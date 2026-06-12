using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LetterboxdSocial.Models;

namespace Jellyfin.Plugin.LetterboxdSocial.Services;

/// <summary>
/// In-memory cache for scraped Letterboxd reviews, persisted as a single JSON file.
/// </summary>
public sealed class LetterboxdCacheStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly LetterboxdSocialFileLogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CacheData? _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdCacheStore"/> class.
    /// </summary>
    /// <param name="logger">File logger.</param>
    public LetterboxdCacheStore(LetterboxdSocialFileLogger logger)
    {
        _logger = logger;
    }

    private static string CacheFilePath
    {
        get
        {
            var dataFolder = Plugin.Instance?.DataFolderPath
                ?? Path.Combine(AppContext.BaseDirectory, "letterboxd-social");

            Directory.CreateDirectory(dataFolder);
            return Path.Combine(dataFolder, "letterboxd-social-cache.json");
        }
    }

    /// <summary>
    /// Gets cached reviews for any of the supplied lookup ids.
    /// </summary>
    /// <param name="lookupIds">Lookup ids (TMDB/IMDb ids or Letterboxd slugs, with or without prefixes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached review response rows.</returns>
    public async Task<IReadOnlyList<LetterboxdReviewResponse>> GetReviewsAsync(
        IEnumerable<string> lookupIds,
        CancellationToken cancellationToken)
    {
        var bareIds = lookupIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(StripLookupPrefix)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (bareIds.Count == 0)
        {
            return [];
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var data = Load();
            var results = new List<LetterboxdReviewResponse>();

            foreach (var user in data.Users.Values)
            {
                foreach (var (slug, film) in user.Films)
                {
                    if (!MatchesAnyId(data, slug, bareIds))
                    {
                        continue;
                    }

                    results.Add(new LetterboxdReviewResponse
                    {
                        JellyfinUserId = user.JellyfinUserId ?? string.Empty,
                        Username = user.Username,
                        DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
                        AvatarUrl = user.AvatarUrl ?? string.Empty,
                        StarRating = film.StarRating ?? string.Empty,
                        RatingValue = film.RatingValue,
                        ReviewText = film.ReviewText,
                        ContainsSpoilers = film.ContainsSpoilers,
                        HasWatched = true,
                        WatchedDate = film.WatchedDate,
                        LetterboxdSlug = slug
                    });
                }
            }

            return results
                .OrderBy(static review => review.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static review => review.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read Letterboxd Social cache.");
            return [];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves a user's scraped cache records. Existing entries are kept when a scrape
    /// is incomplete or comes back empty, so a temporary Letterboxd block can never
    /// wipe previously cached data.
    /// </summary>
    /// <param name="letterboxdUsername">Letterboxd username being refreshed.</param>
    /// <param name="records">Records to save.</param>
    /// <param name="scrapeWasComplete">Whether the scrape covered the user's entire films list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SaveUserReviewsAsync(
        string letterboxdUsername,
        IReadOnlyCollection<CachedReviewRecord> records,
        bool scrapeWasComplete,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(letterboxdUsername))
        {
            return;
        }

        var username = letterboxdUsername.Trim();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var data = Load();
            var userKey = username.ToLowerInvariant();

            if (!data.Users.TryGetValue(userKey, out var user))
            {
                user = new CachedUserData { Username = username };
                data.Users[userKey] = user;
            }

            if (records.Count == 0)
            {
                _logger.Warning("Scrape returned no films for " + username + "; keeping " + user.Films.Count + " previously cached film(s).");
                Persist(data);
                return;
            }

            var first = records.First();
            user.Username = first.LetterboxdUsername;
            user.JellyfinUserId = first.JellyfinUserId;
            user.DisplayName = first.DisplayName;
            if (!string.IsNullOrWhiteSpace(first.AvatarUrl))
            {
                user.AvatarUrl = first.AvatarUrl;
            }

            // A complete scrape saw every films page, so films no longer listed can be dropped.
            // An incomplete one only proves what it saw; merge to protect unreachable pages.
            if (scrapeWasComplete)
            {
                user.Films.Clear();
            }

            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.LetterboxdSlug))
                {
                    continue;
                }

                var slugKey = record.LetterboxdSlug.Trim().ToLowerInvariant();
                user.Films[slugKey] = new CachedUserFilm
                {
                    StarRating = string.IsNullOrWhiteSpace(record.StarRating) ? null : record.StarRating,
                    RatingValue = record.RatingValue,
                    ReviewText = record.ReviewText,
                    ContainsSpoilers = record.ContainsSpoilers,
                    WatchedDate = record.WatchedDate,
                    ScrapedAt = DateTimeOffset.UtcNow.ToString("O")
                };

                if (!string.IsNullOrWhiteSpace(record.TmdbId) || !string.IsNullOrWhiteSpace(record.ImdbId))
                {
                    data.Films[slugKey] = new CachedFilmIds
                    {
                        TmdbId = record.TmdbId?.Trim(),
                        ImdbId = record.ImdbId?.Trim()
                    };
                }
            }

            Persist(data);
            _logger.Info("Saved " + user.Films.Count + " cached film(s) for " + username + " (" + (scrapeWasComplete ? "complete" : "partial") + " scrape).");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save Letterboxd cache for " + username + ".");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes cached users that are no longer configured.
    /// </summary>
    /// <param name="configuredUsernames">Currently configured Letterboxd usernames.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task PruneUnconfiguredUsersAsync(
        IReadOnlyCollection<string> configuredUsernames,
        CancellationToken cancellationToken)
    {
        var keep = configuredUsernames
            .Where(static username => !string.IsNullOrWhiteSpace(username))
            .Select(static username => username.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var data = Load();
            var removed = data.Users.Keys.Where(key => !keep.Contains(key)).ToArray();
            if (removed.Length == 0)
            {
                return;
            }

            foreach (var key in removed)
            {
                data.Users.Remove(key);
            }

            Persist(data);
            _logger.Info("Removed cached data for " + removed.Length + " no-longer-configured user(s): " + string.Join(", ", removed) + ".");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to prune unconfigured users from the Letterboxd cache.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets cached external ids for a Letterboxd film slug.
    /// </summary>
    /// <param name="slug">Letterboxd film slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>External ids, when cached.</returns>
    public async Task<FilmExternalIds?> GetFilmExternalIdsAsync(string slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var data = Load();
            if (!data.Films.TryGetValue(slug.Trim().ToLowerInvariant(), out var ids))
            {
                return null;
            }

            return new FilmExternalIds { TmdbId = ids.TmdbId, ImdbId = ids.ImdbId };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read cached external ids for Letterboxd slug " + slug + ".");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves external ids for a Letterboxd film slug. Kept in memory only until the
    /// next user save persists the whole cache, since ids are cheap to re-resolve.
    /// </summary>
    /// <param name="slug">Letterboxd film slug.</param>
    /// <param name="externalIds">External ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SaveFilmExternalIdsAsync(string slug, FilmExternalIds externalIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var data = Load();
            data.Films[slug.Trim().ToLowerInvariant()] = new CachedFilmIds
            {
                TmdbId = externalIds.TmdbId?.Trim(),
                ImdbId = externalIds.ImdbId?.Trim()
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to save external ids for Letterboxd slug " + slug + ".");
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool MatchesAnyId(CacheData data, string slug, IReadOnlySet<string> bareIds)
    {
        if (bareIds.Contains(slug))
        {
            return true;
        }

        if (!data.Films.TryGetValue(slug, out var ids))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(ids.TmdbId) && bareIds.Contains(ids.TmdbId))
            || (!string.IsNullOrWhiteSpace(ids.ImdbId) && bareIds.Contains(ids.ImdbId));
    }

    private static string StripLookupPrefix(string lookupId)
    {
        var normalized = lookupId.Trim();
        var separatorIndex = normalized.IndexOf(':', StringComparison.Ordinal);
        return separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
    }

    private CacheData Load()
    {
        if (_data is not null)
        {
            return _data;
        }

        try
        {
            var path = CacheFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<CacheData>(json, SerializerOptions);
                if (loaded is not null)
                {
                    NormalizeKeys(loaded);
                    _data = loaded;
                    return _data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load the Letterboxd cache file; starting with an empty cache.");
        }

        _data = new CacheData();
        return _data;
    }

    private void Persist(CacheData data)
    {
        try
        {
            var path = CacheFilePath;
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(data, SerializerOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to write the Letterboxd cache file.");
        }
    }

    private static void NormalizeKeys(CacheData data)
    {
        // JSON round-trips lose the case-insensitive comparers; rebuild with lowercase keys.
        data.Films = data.Films.ToDictionary(
            static pair => pair.Key.ToLowerInvariant(),
            static pair => pair.Value,
            StringComparer.Ordinal);
        data.Users = data.Users.ToDictionary(
            static pair => pair.Key.ToLowerInvariant(),
            static pair => pair.Value,
            StringComparer.Ordinal);

        foreach (var user in data.Users.Values)
        {
            user.Films = user.Films.ToDictionary(
                static pair => pair.Key.ToLowerInvariant(),
                static pair => pair.Value,
                StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// Root persisted cache document.
/// </summary>
internal sealed class CacheData
{
    /// <summary>Gets or sets the cache schema version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the external id map keyed by Letterboxd slug.</summary>
    public Dictionary<string, CachedFilmIds> Films { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets cached user data keyed by lowercase Letterboxd username.</summary>
    public Dictionary<string, CachedUserData> Users { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// External ids for one Letterboxd film.
/// </summary>
internal sealed class CachedFilmIds
{
    /// <summary>Gets or sets the TMDB movie id.</summary>
    public string? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb title id.</summary>
    public string? ImdbId { get; set; }
}

/// <summary>
/// Cached data for one configured Letterboxd user.
/// </summary>
internal sealed class CachedUserData
{
    /// <summary>Gets or sets the Letterboxd username with original casing.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy mapped Jellyfin user id.</summary>
    public string? JellyfinUserId { get; set; }

    /// <summary>Gets or sets the configured display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the profile avatar URL.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Gets or sets watched/rated/reviewed films keyed by lowercase slug.</summary>
    public Dictionary<string, CachedUserFilm> Films { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// One cached watched/rated/reviewed film for a user.
/// </summary>
internal sealed class CachedUserFilm
{
    /// <summary>Gets or sets the rating label.</summary>
    public string? StarRating { get; set; }

    /// <summary>Gets or sets the numeric rating.</summary>
    public double? RatingValue { get; set; }

    /// <summary>Gets or sets the review text.</summary>
    public string? ReviewText { get; set; }

    /// <summary>Gets or sets a value indicating whether the review contains spoilers.</summary>
    public bool ContainsSpoilers { get; set; }

    /// <summary>Gets or sets the watched date text.</summary>
    public string? WatchedDate { get; set; }

    /// <summary>Gets or sets when this entry was scraped.</summary>
    public string? ScrapedAt { get; set; }
}
