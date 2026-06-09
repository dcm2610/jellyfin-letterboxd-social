using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.LetterboxdSocial.Configuration;
using Jellyfin.Plugin.LetterboxdSocial.Models;

namespace Jellyfin.Plugin.LetterboxdSocial.Services;

/// <summary>
/// Scrapes public Letterboxd RSS feeds and film pages.
/// </summary>
public sealed class LetterboxdScraper
{
    private static readonly Regex FilmSlugRegex = new(
        @"href\s*=\s*[""'](?:/[^/""'#?]+)?/film/(?<slug>[^/""'#?]+)/?(?:\d+/?)?[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FilmListItemRegex = new(
        @"<li\b(?=[^>]*\b(?:griditem|poster-container)\b)[^>]*>(?<item>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FilmDataSlugRegex = new(
        @"data-(?:film|item)-slug\s*=\s*[""'](?<slug>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FilmUrlSlugRegex = new(
        @"(?:https?:)?//(?:www\.)?letterboxd\.com/(?:[^/""'#?]+/)?film/(?<slug>[^/""'#?]+)/?(?:\d+/?)?|/(?:[^/""'#?]+/)?film/(?<slug>[^/""'#?]+)/?(?:\d+/?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescriptionParagraphRegex = new(
        @"<p\b[^>]*>(?<text>.*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ReviewLinkRegex = new(
        @"<a\b(?=[^>]*\b(?:review-micro|icon-review)\b)(?=[^>]*\bhref\s*=\s*[""'](?<path>[^""'#?]+)[""'])[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex OlderFilmsPageRegex = new(
        @"href\s*=\s*[""'][^""']*/films/page/\d+/?[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonLdScriptRegex = new(
        @"<script\b(?=[^>]*\btype\s*=\s*[""']application/ld\+json[""'])[^>]*>(?<json>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ProfileImageMetaRegex = new(
        @"<meta\b(?=[^>]*(?:property|name)\s*=\s*[""'](?:og:image|twitter:image)[""'])(?=[^>]*content\s*=\s*[""'](?<url>[^""']+)[""'])[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RatedClassRegex = new(
        @"\brated-(?<rating>\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleRatingRegex = new(
        @"(?:title|aria-label)\s*=\s*[""'](?<rating>\d(?:\.\d)?)\s*(?:stars?|/5)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TmdbRegex = new(
        @"(?:https?:)?//(?:www\.)?(?:themoviedb|tmdb)\.org/movie/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TmdbDataRegex = new(
        @"data-(?:tmdb|film-tmdb)-id\s*=\s*[""'](?<id>\d+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImdbRegex = new(
        @"(?:https?:)?//(?:www\.)?imdb\.com/title/(?<id>tt\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImdbDataRegex = new(
        @"data-(?:imdb|film-imdb)-id\s*=\s*[""'](?<id>tt\d+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly LetterboxdHttpClientFactory _httpClientFactory;
    private readonly LetterboxdCacheStore _cacheStore;
    private readonly LetterboxdSocialFileLogger _logger;
    private readonly SemaphoreSlim _requestThrottle = new(1, 1);
    private DateTimeOffset _nextLetterboxdRequestAtUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdScraper"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="cacheStore">Cache store.</param>
    /// <param name="logger">File logger.</param>
    public LetterboxdScraper(
        LetterboxdHttpClientFactory httpClientFactory,
        LetterboxdCacheStore cacheStore,
        LetterboxdSocialFileLogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _cacheStore = cacheStore;
        _logger = logger;
    }

    /// <summary>
    /// Scrapes all configured users and stores the cache.
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ScrapeConfiguredUsersAsync(
        PluginConfiguration configuration,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var mappings = configuration.UserMappings
            .Where(static mapping => !string.IsNullOrWhiteSpace(mapping.LetterboxdUsername))
            .ToArray();

        if (mappings.Length == 0)
        {
            _logger.Info("Letterboxd Social scrape skipped because no user mappings are configured.");
            progress.Report(100);
            return;
        }

        var maxPages = Math.Clamp(configuration.MaxDiaryPagesPerUser, 1, 25);
        var requestDelay = TimeSpan.FromMilliseconds(Math.Clamp(configuration.RequestDelayMilliseconds, 0, 30000));
        _logger.Info("Starting Letterboxd Social scrape for " + mappings.Length + " configured user(s). Max film pages per user: " + maxPages + ". Request delay: " + requestDelay.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms.");
        progress.Report(0);

        for (var userIndex = 0; userIndex < mappings.Length; userIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mapping = mappings[userIndex];
            var userProgressStart = (userIndex / (double)mappings.Length) * 100;
            var userProgressWidth = 100d / mappings.Length;
            progress.Report(userProgressStart);

            try
            {
                var records = await ScrapeUserAsync(
                    mapping,
                    maxPages,
                    requestDelay,
                    value => progress.Report(userProgressStart + (userProgressWidth * value)),
                    cancellationToken).ConfigureAwait(false);

                progress.Report(userProgressStart + (userProgressWidth * 0.95d));
                await _cacheStore.SaveUserReviewsAsync(
                    mapping.LetterboxdUsername.Trim(),
                    records,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to scrape Letterboxd account for " + mapping.LetterboxdUsername + ".");
            }

            progress.Report(((userIndex + 1) / (double)mappings.Length) * 100);
        }

        _logger.Info("Finished Letterboxd Social scrape for all configured users.");
    }

    /// <summary>
    /// Scrapes one movie for all configured users. This is used as an on-demand fallback when paginated films pages are blocked.
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="movieTitle">Movie title from Jellyfin.</param>
    /// <param name="productionYear">Production year from Jellyfin, when available.</param>
    /// <param name="lookupIds">Known Jellyfin/TMDB/IMDb lookup ids for validation.</param>
    /// <param name="targetLetterboxdUsernames">Specific Letterboxd usernames to check, or null to check all configured users.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of cache rows saved.</returns>
    public async Task<int> ScrapeConfiguredUsersForMovieAsync(
        PluginConfiguration configuration,
        string movieTitle,
        int? productionYear,
        IReadOnlyCollection<string> lookupIds,
        IReadOnlyCollection<string>? targetLetterboxdUsernames,
        CancellationToken cancellationToken)
    {
        var targetSet = targetLetterboxdUsernames is null
            ? null
            : targetLetterboxdUsernames
                .Where(static username => !string.IsNullOrWhiteSpace(username))
                .Select(static username => username.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = configuration.UserMappings
            .Where(static mapping => !string.IsNullOrWhiteSpace(mapping.LetterboxdUsername))
            .Where(mapping => targetSet is null || targetSet.Contains(mapping.LetterboxdUsername.Trim()))
            .ToArray();

        if (mappings.Length == 0 || string.IsNullOrWhiteSpace(movieTitle))
        {
            return 0;
        }

        var requestDelay = TimeSpan.FromMilliseconds(Math.Clamp(configuration.RequestDelayMilliseconds, 0, 30000));
        var slug = await ResolveLetterboxdSlugForMovieAsync(movieTitle, productionYear, lookupIds, requestDelay, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.Debug("On-demand scrape could not resolve a Letterboxd slug for " + movieTitle + ".");
            return 0;
        }

        var externalIds = await ResolveExternalIdsAsync(slug, requestDelay, cancellationToken).ConfigureAwait(false);
        var checkedUsernames = await _cacheStore.GetCheckedUsernamesForFilmAsync(slug, cancellationToken).ConfigureAwait(false);
        var savedRows = 0;
        var checkedUsers = 0;

        foreach (var mapping in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var username = mapping.LetterboxdUsername.Trim();
            if (checkedUsernames.Contains(username))
            {
                _logger.Debug("Skipping on-demand direct check for " + username + " and " + slug + " because it has already been checked.");
                continue;
            }

            checkedUsers++;
            var displayName = string.IsNullOrWhiteSpace(mapping.DisplayName) ? username : mapping.DisplayName.Trim();
            var userFilmLookup = await FetchUserFilmPageDataAsync(username, slug, requestDelay, cancellationToken).ConfigureAwait(false);
            if (userFilmLookup.Status != UserFilmLookupStatus.Found)
            {
                userFilmLookup = await FetchUserFilmGridEntryAsync(username, slug, requestDelay, cancellationToken).ConfigureAwait(false);
            }

            if (userFilmLookup.Status != UserFilmLookupStatus.Found || userFilmLookup.PageData is null)
            {
                if (userFilmLookup.Status == UserFilmLookupStatus.NotFound)
                {
                    await _cacheStore.SaveUserFilmCheckAsync(username, slug, false, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.Debug("On-demand direct check for " + username + " and " + slug + " was inconclusive, so no miss marker was saved.");
                }

                continue;
            }

            var userFilmPage = userFilmLookup.PageData;
            var avatarUrl = await FetchProfileImageUrlAsync(username, requestDelay, cancellationToken).ConfigureAwait(false);
            var records = BuildLookupIds(externalIds, slug)
                .Select(lookupId => new CachedReviewRecord
                {
                    LookupId = lookupId,
                    TmdbId = externalIds.TmdbId,
                    ImdbId = externalIds.ImdbId,
                    LetterboxdSlug = slug,
                    JellyfinUserId = mapping.JellyfinUserId?.Trim() ?? string.Empty,
                    LetterboxdUsername = username,
                    DisplayName = displayName,
                    AvatarUrl = avatarUrl,
                    StarRating = userFilmPage.RatingValue.HasValue ? FormatRatingText(userFilmPage.RatingValue.Value) : string.Empty,
                    RatingValue = userFilmPage.RatingValue,
                    ReviewText = userFilmPage.ReviewText,
                    ContainsSpoilers = userFilmPage.ContainsSpoilers
                })
                .ToArray();

            await _cacheStore.SaveUserFilmReviewsAsync(username, slug, records, cancellationToken).ConfigureAwait(false);
            savedRows += records.Length;
        }

        _logger.Info("On-demand scrape for " + movieTitle + " (" + slug + ") checked " + checkedUsers + " user(s) and saved " + savedRows + " cache row(s).");
        return savedRows;
    }

    private async Task<IReadOnlyList<CachedReviewRecord>> ScrapeUserAsync(
        LetterboxdUserMapping mapping,
        int maxPages,
        TimeSpan requestDelay,
        Action<double> reportUserProgress,
        CancellationToken cancellationToken)
    {
        var username = mapping.LetterboxdUsername.Trim();
        var displayName = string.IsNullOrWhiteSpace(mapping.DisplayName) ? username : mapping.DisplayName.Trim();
        var records = new List<CachedReviewRecord>();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _logger.Info("Scraping Letterboxd profile, RSS reviews, and film ratings for " + username + ".");
        reportUserProgress(0.01d);

        var avatarUrl = await FetchProfileImageUrlAsync(username, requestDelay, cancellationToken).ConfigureAwait(false);
        reportUserProgress(0.04d);

        var rssReviews = await FetchRssReviewsAsync(username, requestDelay, cancellationToken).ConfigureAwait(false);
        reportUserProgress(0.08d);

        var uniqueEntries = new List<ScrapedFilmEntry>();

        for (var page = 1; page <= maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageStartProgress = 0.08d + (((page - 1) / (double)maxPages) * 0.32d);
            var pageProgressWidth = 0.32d / maxPages;
            reportUserProgress(pageStartProgress);

            var entries = await FetchFilmEntriesPageAsync(username, page, rssReviews, requestDelay, cancellationToken).ConfigureAwait(false);
            _logger.Debug("Parsed " + entries.Length + " watched film entr" + (entries.Length == 1 ? "y" : "ies") + " from films page " + page + " for " + username + ".");
            if (entries.Length == 0)
            {
                if (page == 1)
                {
                    _logger.Warning("No Letterboxd watched films found for " + username + ". The page structure may have changed or the profile has no watched films.");
                }

                break;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!seenSlugs.Add(entry.LetterboxdSlug))
                {
                    _logger.Debug("Skipping duplicate Letterboxd slug " + entry.LetterboxdSlug + " for " + username + ".");
                    continue;
                }

                uniqueEntries.Add(entry);
            }

            reportUserProgress(pageStartProgress + pageProgressWidth);
        }

        _logger.Debug("Resolving " + uniqueEntries.Count + " unique watched film entr" + (uniqueEntries.Count == 1 ? "y" : "ies") + " for " + username + ".");

        var completedEntries = 0;
        using var resolverSemaphore = new SemaphoreSlim(4, 4);
        var resolvedRecordTasks = uniqueEntries.Select(async entry =>
        {
            await resolverSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await BuildCachedRecordsForEntryAsync(
                    mapping,
                    username,
                    displayName,
                    avatarUrl,
                    entry,
                    requestDelay,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                var completed = Interlocked.Increment(ref completedEntries);
                var progressValue = 0.42d + (0.48d * (completed / (double)Math.Max(1, uniqueEntries.Count)));
                reportUserProgress(progressValue);
                resolverSemaphore.Release();
            }
        });

        var resolvedRecords = await Task.WhenAll(resolvedRecordTasks).ConfigureAwait(false);
        records.AddRange(resolvedRecords.SelectMany(static item => item));

        reportUserProgress(0.92d);
        _logger.Info("Scraped " + records.Count + " cache row(s) for " + username + ".");
        return records;
    }

    private async Task<IReadOnlyList<CachedReviewRecord>> BuildCachedRecordsForEntryAsync(
        LetterboxdUserMapping mapping,
        string username,
        string displayName,
        string avatarUrl,
        ScrapedFilmEntry entry,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        _logger.Debug("Processing watched film for " + username + ": slug=" + entry.LetterboxdSlug + ", rating=" + (entry.StarRating.Length == 0 ? "(none)" : entry.StarRating) + ", hasRssReview=" + !string.IsNullOrWhiteSpace(entry.ReviewText) + ", hasReviewPage=" + !string.IsNullOrWhiteSpace(entry.ReviewPath) + ".");

        if (!string.IsNullOrWhiteSpace(entry.ReviewPath))
        {
            var reviewPage = await FetchReviewPageAsync(entry.ReviewPath, requestDelay, cancellationToken).ConfigureAwait(false);
            if (reviewPage is not null)
            {
                if (IsUsefulReviewText(reviewPage.ReviewText))
                {
                    entry.ReviewText = reviewPage.ReviewText;
                }

                entry.ContainsSpoilers = reviewPage.ContainsSpoilers;
                _logger.Debug("Parsed review page for " + username + ": slug=" + entry.LetterboxdSlug + ", hasReviewText=" + !string.IsNullOrWhiteSpace(entry.ReviewText) + ", containsSpoilers=" + entry.ContainsSpoilers + ".");
            }
        }

        var externalIds = await ResolveExternalIdsAsync(entry.LetterboxdSlug, requestDelay, cancellationToken).ConfigureAwait(false);
        _logger.Debug("Resolved external ids for " + entry.LetterboxdSlug + ": TMDB=" + (externalIds.TmdbId ?? "(none)") + ", IMDb=" + (externalIds.ImdbId ?? "(none)") + ".");

        return BuildLookupIds(externalIds, entry.LetterboxdSlug)
            .Select(lookupId => new CachedReviewRecord
            {
                LookupId = lookupId,
                TmdbId = externalIds.TmdbId,
                ImdbId = externalIds.ImdbId,
                LetterboxdSlug = entry.LetterboxdSlug,
                JellyfinUserId = mapping.JellyfinUserId?.Trim() ?? string.Empty,
                LetterboxdUsername = username,
                DisplayName = displayName,
                AvatarUrl = avatarUrl,
                StarRating = entry.StarRating,
                RatingValue = entry.RatingValue,
                ReviewText = entry.ReviewText,
                ContainsSpoilers = entry.ContainsSpoilers,
                WatchedDate = entry.WatchedDate
            })
            .ToArray();
    }

    private async Task<string> FetchProfileImageUrlAsync(
        string username,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        var profilePath = $"{Uri.EscapeDataString(username)}/";
        _logger.Debug("Fetching Letterboxd profile for avatar: " + profilePath + ".");
        var html = await GetStringWithLoggingAsync(profilePath, requestDelay, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var avatarUrl = ExtractProfileImageUrl(html);
        _logger.Debug("Parsed Letterboxd avatar for " + username + ": " + (string.IsNullOrWhiteSpace(avatarUrl) ? "(none)" : avatarUrl) + ".");
        return avatarUrl ?? string.Empty;
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchRssReviewsAsync(
        string username,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        var rssPath = $"{Uri.EscapeDataString(username)}/rss/";
        var xml = await GetStringWithLoggingAsync(rssPath, requestDelay, cancellationToken).ConfigureAwait(false);
        var reviews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(xml))
        {
            _logger.Warning("Letterboxd RSS feed returned no content for " + username + ".");
            return reviews;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var items = document.Descendants().Where(static element => element.Name.LocalName == "item");

            foreach (var item in items)
            {
                var link = item.Elements().FirstOrDefault(static element => element.Name.LocalName == "link")?.Value;
                if (string.IsNullOrWhiteSpace(link))
                {
                    continue;
                }

                var slug = ExtractFilmSlug(link);
                if (string.IsNullOrWhiteSpace(slug))
                {
                    continue;
                }

                var description = item.Elements().FirstOrDefault(static element => element.Name.LocalName == "description")?.Value ?? string.Empty;
                var reviewText = ExtractReviewTextFromRssDescription(description);
                if (!IsUsefulReviewText(reviewText))
                {
                    continue;
                }

                reviews[slug] = reviewText!;
            }

            _logger.Info("Parsed " + reviews.Count + " RSS review(s) for " + username + ".");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to parse Letterboxd RSS feed for " + username + ".");
        }

        return reviews;
    }

    private async Task<ScrapedFilmEntry[]> FetchFilmEntriesPageAsync(
        string username,
        int page,
        IReadOnlyDictionary<string, string> rssReviews,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        var filmsPath = page == 1
            ? $"{Uri.EscapeDataString(username)}/films/"
            : $"{Uri.EscapeDataString(username)}/films/page/{page}/";

        _logger.Debug("Fetching watched films page " + page + " for " + username + ": " + filmsPath + ".");
        var html = await GetStringWithLoggingAsync(filmsPath, requestDelay, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        return ParseFilmEntries(html, rssReviews).ToArray();
    }

    private IEnumerable<ScrapedFilmEntry> ParseFilmEntries(
        string html,
        IReadOnlyDictionary<string, string> rssReviews)
    {
        foreach (Match itemMatch in FilmListItemRegex.Matches(html))
        {
            var item = itemMatch.Groups["item"].Value;
            var slug = ExtractFilmSlug(item);
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            var rating = ExtractRating(item);
            rssReviews.TryGetValue(slug, out var reviewText);
            yield return new ScrapedFilmEntry
            {
                LetterboxdSlug = slug,
                StarRating = rating.ratingText,
                RatingValue = rating.ratingValue,
                ReviewText = reviewText,
                ReviewPath = ExtractReviewPath(item)
            };
        }
    }

    private async Task<ReviewPageData?> FetchReviewPageAsync(
        string reviewPath,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        var relativePath = NormalizeRelativePath(reviewPath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        _logger.Debug("Fetching Letterboxd review page " + relativePath + ".");
        var html = await GetStringWithLoggingAsync(relativePath, requestDelay, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        return new ReviewPageData(
            ExtractReviewTextFromReviewPage(html),
            IsSpoilerReviewPage(html));
    }

    private async Task<UserFilmLookupResult> FetchUserFilmPageDataAsync(
        string username,
        string slug,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        var relativePath = $"{Uri.EscapeDataString(username)}/film/{Uri.EscapeDataString(slug)}/";
        _logger.Debug("Fetching direct Letterboxd user film page " + relativePath + ".");
        var html = await GetStringWithLoggingAsync(relativePath, requestDelay, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            _logger.Debug("Direct Letterboxd user film page was not readable for " + username + " and " + slug + ".");
            return UserFilmLookupResult.Inconclusive();
        }

        return UserFilmLookupResult.Found(new UserFilmPageData(
            ExtractReviewTextFromReviewPage(html),
            IsSpoilerReviewPage(html),
            ExtractJsonLdRatingValue(html)));
    }

    private async Task<UserFilmLookupResult> FetchUserFilmGridEntryAsync(
        string username,
        string slug,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        var filmsPath = $"{Uri.EscapeDataString(username)}/films/";
        _logger.Debug("Searching readable Letterboxd films grid for " + username + " and slug " + slug + ".");
        var html = await GetStringWithLoggingAsync(filmsPath, requestDelay, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return UserFilmLookupResult.Inconclusive();
        }

        var entry = ParseFilmEntries(html, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(item => string.Equals(item.LetterboxdSlug, slug, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            if (!string.IsNullOrWhiteSpace(entry.ReviewPath))
            {
                var reviewPage = await FetchReviewPageAsync(entry.ReviewPath, requestDelay, cancellationToken).ConfigureAwait(false);
                if (reviewPage is not null)
                {
                    if (IsUsefulReviewText(reviewPage.ReviewText))
                    {
                        entry.ReviewText = reviewPage.ReviewText;
                    }

                    entry.ContainsSpoilers = reviewPage.ContainsSpoilers;
                }
            }

            _logger.Debug("Found " + slug + " on readable films grid for " + username + ".");
            return UserFilmLookupResult.Found(new UserFilmPageData(
                entry.ReviewText,
                entry.ContainsSpoilers,
                entry.RatingValue));
        }

        if (HasOlderFilmsPage(html))
        {
            _logger.Debug("Did not find " + slug + " on the first readable films grid for " + username + ", but older films pages exist and may be blocked.");
            return UserFilmLookupResult.Inconclusive();
        }

        _logger.Debug("Did not find " + slug + " on a fully readable films grid for " + username + ".");
        return UserFilmLookupResult.NotFound();
    }

    private async Task<string?> GetStringWithLoggingAsync(
        string relativePath,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await WaitForLetterboxdRequestSlotAsync(requestDelay, cancellationToken).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
                _logger.Debug("Letterboxd HTTP GET " + relativePath + ".");
                using var response = await _httpClientFactory.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < maxAttempts && IsLikelyTemporaryBlock(response.StatusCode))
                    {
                        var retryDelay = GetRetryDelay(response, requestDelay);
                        _logger.Warning("Letterboxd request for " + relativePath + " returned HTTP " + (int)response.StatusCode + ". Waiting " + retryDelay.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms before one retry.");
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.Warning("Letterboxd request for " + relativePath + " returned HTTP " + (int)response.StatusCode + ".");
                    return null;
                }

                _logger.Debug("Letterboxd request for " + relativePath + " returned HTTP " + (int)response.StatusCode + ".");
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Letterboxd request for " + relativePath + " failed.");
                return null;
            }
        }

        return null;
    }

    private static (string ratingText, double? ratingValue) ExtractRating(string row)
    {
        var ratedMatch = RatedClassRegex.Match(row);
        if (ratedMatch.Success && double.TryParse(ratedMatch.Groups["rating"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var halfStars))
        {
            var value = halfStars / 2d;
            return (value.ToString("0.0", CultureInfo.InvariantCulture), value);
        }

        var titleMatch = TitleRatingRegex.Match(row);
        if (titleMatch.Success && double.TryParse(titleMatch.Groups["rating"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var titleRating))
        {
            return (titleRating.ToString("0.0", CultureInfo.InvariantCulture), titleRating);
        }

        const char fullStar = '\u2605';
        const char halfStar = '\u00BD';

        var decoded = WebUtility.HtmlDecode(StripTags(row));
        if (decoded.Contains(fullStar, StringComparison.Ordinal))
        {
            var fullStars = decoded.Count(static character => character == fullStar);
            var hasHalf = decoded.Contains(halfStar, StringComparison.Ordinal);
            var value = fullStars + (hasHalf ? 0.5d : 0d);
            return (value.ToString("0.0", CultureInfo.InvariantCulture), value);
        }

        return (string.Empty, null);
    }

    private async Task<FilmExternalIds> ResolveExternalIdsAsync(
        string slug,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        var cached = await _cacheStore.GetFilmExternalIdsAsync(slug, cancellationToken).ConfigureAwait(false);
        if (cached is not null && (!string.IsNullOrWhiteSpace(cached.TmdbId) || !string.IsNullOrWhiteSpace(cached.ImdbId)))
        {
            _logger.Debug("Using cached external ids for Letterboxd slug " + slug + ".");
            return cached;
        }

        _logger.Debug("Resolving external ids from Letterboxd film page for slug " + slug + ".");
        var html = await GetStringWithLoggingAsync($"film/{Uri.EscapeDataString(slug)}/", requestDelay, cancellationToken).ConfigureAwait(false);
        var externalIds = new FilmExternalIds();

        if (!string.IsNullOrWhiteSpace(html))
        {
            var tmdbMatch = TmdbRegex.Match(html);
            var imdbMatch = ImdbRegex.Match(html);
            var tmdbDataMatch = TmdbDataRegex.Match(html);
            var imdbDataMatch = ImdbDataRegex.Match(html);

            externalIds.TmdbId = tmdbMatch.Success
                ? tmdbMatch.Groups["id"].Value
                : tmdbDataMatch.Success ? tmdbDataMatch.Groups["id"].Value : null;
            externalIds.ImdbId = imdbMatch.Success
                ? imdbMatch.Groups["id"].Value
                : imdbDataMatch.Success ? imdbDataMatch.Groups["id"].Value : null;
        }

        await _cacheStore.SaveFilmExternalIdsAsync(slug, externalIds, cancellationToken).ConfigureAwait(false);
        return externalIds;
    }

    private async Task<string?> ResolveLetterboxdSlugForMovieAsync(
        string movieTitle,
        int? productionYear,
        IReadOnlyCollection<string> lookupIds,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in BuildSlugCandidates(movieTitle, productionYear))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var externalIds = await ResolveExternalIdsAsync(candidate, requestDelay, cancellationToken).ConfigureAwait(false);
            if (ExternalIdsMatch(externalIds, lookupIds))
            {
                _logger.Debug("Resolved " + movieTitle + " to Letterboxd slug " + candidate + " via external id match.");
                return candidate;
            }
        }

        return null;
    }

    private async Task WaitForLetterboxdRequestSlotAsync(
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        await _requestThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var wait = _nextLetterboxdRequestAtUtc - now;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _nextLetterboxdRequestAtUtc = DateTimeOffset.UtcNow + requestDelay;
        }
        finally
        {
            _requestThrottle.Release();
        }
    }

    private static bool IsLikelyTemporaryBlock(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Forbidden
            or (HttpStatusCode)429
            or HttpStatusCode.ServiceUnavailable;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, TimeSpan requestDelay)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : delta;
        }

        var milliseconds = Math.Clamp(
            Math.Max(requestDelay.TotalMilliseconds * 6d, 5000d),
            5000d,
            30000d);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static IEnumerable<string> BuildLookupIds(FilmExternalIds externalIds, string slug)
    {
        if (!string.IsNullOrWhiteSpace(externalIds.TmdbId))
        {
            yield return "tmdb:" + externalIds.TmdbId.Trim();
            yield return externalIds.TmdbId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(externalIds.ImdbId))
        {
            yield return "imdb:" + externalIds.ImdbId.Trim();
            yield return externalIds.ImdbId.Trim();
        }

        yield return "letterboxd:" + slug.Trim();
    }

    private static IEnumerable<string> BuildSlugCandidates(string movieTitle, int? productionYear)
    {
        var baseSlug = SlugifyTitle(movieTitle);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            yield break;
        }

        yield return baseSlug;

        if (productionYear.HasValue)
        {
            yield return baseSlug + "-" + productionYear.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string SlugifyTitle(string title)
    {
        var normalized = WebUtility.HtmlDecode(title)
            .Normalize(NormalizationForm.FormD)
            .ToLowerInvariant()
            .Replace("&", " and ", StringComparison.Ordinal);
        var builder = new StringBuilder(normalized.Length);
        var previousWasSeparator = false;

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-').Normalize(NormalizationForm.FormC);
    }

    private static bool ExternalIdsMatch(FilmExternalIds externalIds, IEnumerable<string> lookupIds)
    {
        var ids = lookupIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .SelectMany(static id =>
            {
                var normalized = id.Trim().ToLowerInvariant();
                var separatorIndex = normalized.IndexOf(':', StringComparison.Ordinal);
                return separatorIndex >= 0
                    ? [normalized, normalized[(separatorIndex + 1)..]]
                    : new[] { normalized };
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (!string.IsNullOrWhiteSpace(externalIds.TmdbId) && ids.Contains(externalIds.TmdbId.Trim()))
            || (!string.IsNullOrWhiteSpace(externalIds.ImdbId) && ids.Contains(externalIds.ImdbId.Trim()));
    }

    private static string FormatRatingText(double ratingValue)
    {
        return ratingValue.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string? ExtractFilmSlug(string htmlOrUrl)
    {
        if (string.IsNullOrWhiteSpace(htmlOrUrl))
        {
            return null;
        }

        var dataSlugMatch = FilmDataSlugRegex.Match(htmlOrUrl);
        if (dataSlugMatch.Success)
        {
            return dataSlugMatch.Groups["slug"].Value;
        }

        var hrefSlugMatch = FilmSlugRegex.Match(htmlOrUrl);
        if (hrefSlugMatch.Success)
        {
            return hrefSlugMatch.Groups["slug"].Value;
        }

        var urlSlugMatch = FilmUrlSlugRegex.Match(htmlOrUrl);
        return urlSlugMatch.Success ? urlSlugMatch.Groups["slug"].Value : null;
    }

    private static string? ExtractReviewPath(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = ReviewLinkRegex.Match(html);
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static string? ExtractProfileImageUrl(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = ProfileImageMetaRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var url = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
        if (string.IsNullOrWhiteSpace(url)
            || url.Contains("default-share", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + url;
        }

        if (url.StartsWith("/", StringComparison.Ordinal))
        {
            return "https://letterboxd.com" + url;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
    }

    private static string NormalizeRelativePath(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.PathAndQuery.TrimStart('/');
        }

        return pathOrUrl.Trim().TrimStart('/');
    }

    private static bool IsUsefulReviewText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return !string.Equals(normalized, "Review", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(normalized, "Read review", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(normalized, "Read the review", StringComparison.OrdinalIgnoreCase)
               && !normalized.StartsWith("This review may contain spoilers.", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractReviewTextFromReviewPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        foreach (Match scriptMatch in JsonLdScriptRegex.Matches(html))
        {
            var json = Regex.Replace(
                scriptMatch.Groups["json"].Value,
                @"/\*.*?\*/",
                string.Empty,
                RegexOptions.Singleline).Trim();

            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(WebUtility.HtmlDecode(json));
                var text = ExtractReviewTextFromJsonElement(document.RootElement);
                if (IsUsefulReviewText(text))
                {
                    return text;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static double? ExtractJsonLdRatingValue(string html)
    {
        foreach (Match scriptMatch in JsonLdScriptRegex.Matches(html))
        {
            var json = Regex.Replace(
                scriptMatch.Groups["json"].Value,
                @"/\*.*?\*/",
                string.Empty,
                RegexOptions.Singleline).Trim();

            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(WebUtility.HtmlDecode(json));
                var rating = ExtractRatingValueFromJsonElement(document.RootElement);
                if (rating.HasValue)
                {
                    return rating.Value;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static double? ExtractRatingValueFromJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = ExtractRatingValueFromJsonElement(item);
                if (value.HasValue)
                {
                    return value.Value;
                }
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object || !IsReviewJsonElement(element))
        {
            return null;
        }

        if (!element.TryGetProperty("reviewRating", out var rating)
            || rating.ValueKind != JsonValueKind.Object
            || !rating.TryGetProperty("ratingValue", out var ratingValue))
        {
            return null;
        }

        return ratingValue.ValueKind switch
        {
            JsonValueKind.Number when ratingValue.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(ratingValue.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static string? ExtractReviewTextFromJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var text = ExtractReviewTextFromJsonElement(item);
                if (IsUsefulReviewText(text))
                {
                    return text;
                }
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!IsReviewJsonElement(element))
        {
            return null;
        }

        if (element.TryGetProperty("reviewBody", out var reviewBody)
            && reviewBody.ValueKind == JsonValueKind.String)
        {
            return CleanPlainText(reviewBody.GetString());
        }

        if (element.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String)
        {
            return CleanPlainText(description.GetString());
        }

        return null;
    }

    private static bool IsReviewJsonElement(JsonElement element)
    {
        if (!element.TryGetProperty("@type", out var type))
        {
            return false;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return string.Equals(type.GetString(), "Review", StringComparison.OrdinalIgnoreCase);
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray()
                .Any(static item => item.ValueKind == JsonValueKind.String
                                    && string.Equals(item.GetString(), "Review", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsSpoilerReviewPage(string html)
    {
        return html.Contains("This review may contain spoilers.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOlderFilmsPage(string html)
    {
        return !string.IsNullOrWhiteSpace(html) && OlderFilmsPageRegex.IsMatch(html);
    }

    private static string? ExtractReviewTextFromRssDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var paragraphs = DescriptionParagraphRegex.Matches(description)
            .Select(static match => match.Groups["text"].Value)
            .Where(static html => !html.Contains("<img", StringComparison.OrdinalIgnoreCase))
            .Select(CleanText)
            .Where(IsUsefulReviewText)
            .Where(static text => !LooksLikeRatingOnlyText(text))
            .ToArray();

        if (paragraphs.Length > 0)
        {
            return string.Join("\n\n", paragraphs);
        }

        var cleaned = CleanText(description);
        return IsUsefulReviewText(cleaned) && !LooksLikeRatingOnlyText(cleaned) ? cleaned : null;
    }

    private static bool LooksLikeRatingOnlyText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        return normalized.All(static character =>
            char.IsWhiteSpace(character)
            || char.IsDigit(character)
            || character is '.' or '/' or '\\' or '-' or ':'
            || character is '\u2605' or '\u00BD');
    }

    private static string CleanText(string html)
    {
        var withoutScripts = Regex.Replace(
            html,
            @"<(script|style)\b[^>]*>.*?</\1>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var withLineBreaks = Regex.Replace(
            withoutScripts,
            @"<\s*(br|/p|/div|/li)\s*/?\s*>",
            "\n",
            RegexOptions.IgnoreCase);

        var stripped = StripTags(withLineBreaks);
        var decoded = WebUtility.HtmlDecode(stripped);
        return Regex.Replace(decoded, @"[ \t\f\v]+", " ")
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .Aggregate(string.Empty, static (current, line) => current.Length == 0 ? line : current + "\n" + line);
    }

    private static string StripTags(string html)
    {
        return Regex.Replace(html, "<.*?>", string.Empty, RegexOptions.Singleline);
    }

    private static string? CleanPlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Regex.Replace(text, @"[ \t\f\v]+", " ")
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .Aggregate(string.Empty, static (current, line) => current.Length == 0 ? line : current + "\n" + line);
    }

    private sealed record ReviewPageData(string? ReviewText, bool ContainsSpoilers);

    private sealed record UserFilmPageData(string? ReviewText, bool ContainsSpoilers, double? RatingValue);

    private enum UserFilmLookupStatus
    {
        Found,
        NotFound,
        Inconclusive
    }

    private sealed record UserFilmLookupResult(UserFilmLookupStatus Status, UserFilmPageData? PageData)
    {
        public static UserFilmLookupResult Found(UserFilmPageData pageData)
        {
            return new UserFilmLookupResult(UserFilmLookupStatus.Found, pageData);
        }

        public static UserFilmLookupResult NotFound()
        {
            return new UserFilmLookupResult(UserFilmLookupStatus.NotFound, null);
        }

        public static UserFilmLookupResult Inconclusive()
        {
            return new UserFilmLookupResult(UserFilmLookupStatus.Inconclusive, null);
        }
    }
}
