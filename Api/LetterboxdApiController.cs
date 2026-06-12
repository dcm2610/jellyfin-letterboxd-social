using System.Reflection;
using Jellyfin.Plugin.LetterboxdSocial.Models;
using Jellyfin.Plugin.LetterboxdSocial.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LetterboxdSocial.Api;

/// <summary>
/// REST API for Letterboxd Social frontend data.
/// </summary>
[ApiController]
[Route("ScheduledLetterboxd")]
public sealed class LetterboxdApiController : ControllerBase
{
    private const string FrontendResourceName = "Jellyfin.Plugin.LetterboxdSocial.Web.FrontendInjection.js";
    private readonly LetterboxdCacheStore _cacheStore;
    private readonly ILibraryManager _libraryManager;
    private readonly LetterboxdSocialFileLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdApiController"/> class.
    /// </summary>
    /// <param name="cacheStore">Cache store.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">File logger.</param>
    public LetterboxdApiController(
        LetterboxdCacheStore cacheStore,
        ILibraryManager libraryManager,
        LetterboxdSocialFileLogger logger)
    {
        _cacheStore = cacheStore;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets cached Letterboxd friend ratings/reviews for a movie.
    /// </summary>
    /// <param name="movieId">TMDB id, IMDb id, Letterboxd slug, or Jellyfin internal item id.</param>
    /// <param name="cachedOnly">Ignored. Retained so older cached frontend scripts keep working.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Friend ratings/reviews.</returns>
    [Authorize]
    [HttpGet("Reviews")]
    [Produces("application/json")]
    public async Task<ActionResult<IReadOnlyList<LetterboxdReviewResponse>>> GetReviews(
        [FromQuery] string? movieId,
        [FromQuery] bool cachedOnly,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(movieId))
        {
            return Ok(Array.Empty<LetterboxdReviewResponse>());
        }

        try
        {
            var lookupIds = ResolveLookupIds(movieId);
            var reviews = await _cacheStore.GetReviewsAsync(lookupIds, cancellationToken).ConfigureAwait(false);

            var configuration = Plugin.Instance?.Configuration;
            if (configuration is not null)
            {
                var configuredUsernames = configuration.UserMappings
                    .Where(static mapping => !string.IsNullOrWhiteSpace(mapping.LetterboxdUsername))
                    .Select(static mapping => mapping.LetterboxdUsername.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                reviews = reviews
                    .Where(review => configuredUsernames.Contains(review.Username))
                    .ToArray();
            }

            return Ok(reviews);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to return Letterboxd Social reviews for movie id " + movieId + ".");
            return Ok(Array.Empty<LetterboxdReviewResponse>());
        }
    }

    /// <summary>
    /// Serves the frontend injection script as an embedded plugin resource.
    /// </summary>
    /// <returns>JavaScript content.</returns>
    [AllowAnonymous]
    [HttpGet("FrontendInjection.js")]
    [Produces("application/javascript")]
    public IActionResult GetFrontendInjectionScript()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(FrontendResourceName);
            if (stream is null)
            {
                _logger.Error("Embedded frontend resource " + FrontendResourceName + " was not found.");
                return NotFound();
            }

            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), "application/javascript");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to serve Letterboxd Social frontend script.");
            return StatusCode(500);
        }
    }

    private IReadOnlyCollection<string> ResolveLookupIds(string movieId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedInput = movieId.Trim();
        ids.Add(normalizedInput);

        try
        {
            if (Guid.TryParse(normalizedInput, out var guid))
            {
                var item = _libraryManager.GetItemById(guid);
                AddProviderIds(ids, item);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Unable to resolve Jellyfin provider ids for " + movieId + ". " + ex.GetType().Name + ": " + ex.Message);
        }

        return ids;
    }

    private static void AddProviderIds(ISet<string> ids, BaseItem? item)
    {
        if (item?.ProviderIds is null)
        {
            return;
        }

        foreach (var providerId in item.ProviderIds)
        {
            if (string.IsNullOrWhiteSpace(providerId.Value))
            {
                continue;
            }

            if (string.Equals(providerId.Key, "Tmdb", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add("tmdb:" + providerId.Value.Trim());
            }
            else if (string.Equals(providerId.Key, "Imdb", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add("imdb:" + providerId.Value.Trim());
            }
        }
    }

}
