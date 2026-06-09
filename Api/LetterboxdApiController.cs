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
    private readonly LetterboxdScraper _scraper;
    private readonly ILibraryManager _libraryManager;
    private readonly LetterboxdSocialFileLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdApiController"/> class.
    /// </summary>
    /// <param name="cacheStore">Cache store.</param>
    /// <param name="scraper">Letterboxd scraper.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">File logger.</param>
    public LetterboxdApiController(
        LetterboxdCacheStore cacheStore,
        LetterboxdScraper scraper,
        ILibraryManager libraryManager,
        LetterboxdSocialFileLogger logger)
    {
        _cacheStore = cacheStore;
        _scraper = scraper;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets cached Letterboxd friend ratings/reviews for a movie.
    /// </summary>
    /// <param name="movieId">TMDB id, IMDb id, Letterboxd slug, or Jellyfin internal item id.</param>
    /// <param name="cachedOnly">Whether to return only existing cache rows without running on-demand checks.</param>
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
            _logger.Debug("Reviews API called without a movieId. Returning an empty array.");
            return Ok(Array.Empty<LetterboxdReviewResponse>());
        }

        try
        {
            _logger.Debug("Reviews API called for movieId=" + movieId + ".");
            var lookupIds = ResolveLookupIds(movieId, out var item);
            var reviews = await _cacheStore.GetReviewsAsync(lookupIds, cancellationToken).ConfigureAwait(false);
            if (cachedOnly)
            {
                _logger.Debug("Reviews API returning " + reviews.Count + " cached-only review(s) for movieId=" + movieId + ".");
                return Ok(reviews);
            }

            var configuration = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
            var configuredUsernames = configuration.UserMappings
                .Where(static mapping => !string.IsNullOrWhiteSpace(mapping.LetterboxdUsername))
                .Select(static mapping => mapping.LetterboxdUsername.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (ShouldRunOnDemandCheck(reviews, configuredUsernames, item))
            {
                var targetUsernames = await GetUncheckedTargetUsernamesAsync(
                    reviews,
                    configuredUsernames,
                    cancellationToken).ConfigureAwait(false);

                var savedRows = await _scraper.ScrapeConfiguredUsersForMovieAsync(
                    configuration,
                    item!.Name,
                    item.ProductionYear,
                    lookupIds,
                    targetUsernames,
                    cancellationToken).ConfigureAwait(false);

                if (savedRows > 0)
                {
                    reviews = await _cacheStore.GetReviewsAsync(lookupIds, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.Debug("Reviews API returning " + reviews.Count + " review(s) for movieId=" + movieId + ".");
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

    private async Task<IReadOnlyCollection<string>?> GetUncheckedTargetUsernamesAsync(
        IReadOnlyCollection<LetterboxdReviewResponse> reviews,
        IReadOnlyCollection<string> configuredUsernames,
        CancellationToken cancellationToken)
    {
        if (reviews.Count == 0)
        {
            return null;
        }

        var cachedUsernames = reviews
            .Select(static review => review.Username)
            .Where(static username => !string.IsNullOrWhiteSpace(username))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slug = reviews
            .Select(static review => review.LetterboxdSlug)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        var checkedUsernames = string.IsNullOrWhiteSpace(slug)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await _cacheStore.GetCheckedUsernamesForFilmAsync(slug, cancellationToken).ConfigureAwait(false);

        return configuredUsernames
            .Where(username => !cachedUsernames.Contains(username) && !checkedUsernames.Contains(username))
            .ToArray();
    }

    private static bool ShouldRunOnDemandCheck(
        IReadOnlyCollection<LetterboxdReviewResponse> reviews,
        IReadOnlyCollection<string> configuredUsernames,
        BaseItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Name) || configuredUsernames.Count == 0)
        {
            return false;
        }

        var cachedUsernames = reviews
            .Select(static review => review.Username)
            .Where(static username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return cachedUsernames < configuredUsernames.Count;
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

            _logger.Debug("Serving embedded frontend injection script.");
            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), "application/javascript");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to serve Letterboxd Social frontend script.");
            return StatusCode(500);
        }
    }

    private IReadOnlyCollection<string> ResolveLookupIds(string movieId, out BaseItem? resolvedItem)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedInput = movieId.Trim();
        resolvedItem = null;

        AddLookupVariants(ids, normalizedInput);

        try
        {
            if (Guid.TryParse(normalizedInput, out var guid))
            {
                var item = _libraryManager.GetItemById(guid);
                resolvedItem = item;
                AddProviderIds(ids, item);
                _logger.Debug("Resolved Jellyfin item " + movieId + " to lookup ids: " + string.Join(", ", ids) + ".");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug("Unable to resolve Jellyfin provider ids for " + movieId + ". " + ex.GetType().Name + ": " + ex.Message);
        }

        _logger.Debug("Final lookup ids for " + movieId + ": " + string.Join(", ", ids) + ".");
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
                ids.Add(providerId.Value.Trim());
            }
            else if (string.Equals(providerId.Key, "Imdb", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add("imdb:" + providerId.Value.Trim());
                ids.Add(providerId.Value.Trim());
            }
        }
    }

    private static void AddLookupVariants(ISet<string> ids, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        ids.Add(trimmed);

        if (trimmed.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("letterboxd:", StringComparison.OrdinalIgnoreCase))
        {
            ids.Add(trimmed[(trimmed.IndexOf(':', StringComparison.Ordinal) + 1)..]);
            return;
        }

        if (trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            ids.Add("imdb:" + trimmed);
        }
        else if (trimmed.All(char.IsDigit))
        {
            ids.Add("tmdb:" + trimmed);
        }
        else
        {
            ids.Add("letterboxd:" + trimmed);
        }
    }
}
