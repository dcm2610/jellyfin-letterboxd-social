using System.Globalization;
using System.Net;
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
        _logger.Info("Starting Letterboxd scrape for " + mappings.Length + " user(s); up to " + maxPages + " film page(s) each, " + requestDelay.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms between requests.");
        progress.Report(0);

        await _cacheStore.PruneUnconfiguredUsersAsync(
            mappings.Select(static mapping => mapping.LetterboxdUsername).ToArray(),
            cancellationToken).ConfigureAwait(false);

        for (var userIndex = 0; userIndex < mappings.Length; userIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mapping = mappings[userIndex];
            var userProgressStart = (userIndex / (double)mappings.Length) * 100;
            var userProgressWidth = 100d / mappings.Length;
            progress.Report(userProgressStart);

            try
            {
                var result = await ScrapeUserAsync(
                    mapping,
                    maxPages,
                    requestDelay,
                    value => progress.Report(userProgressStart + (userProgressWidth * value)),
                    cancellationToken).ConfigureAwait(false);

                await _cacheStore.SaveUserReviewsAsync(
                    mapping.LetterboxdUsername.Trim(),
                    result.Records,
                    result.ScrapeWasComplete,
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

        _logger.Info("Finished Letterboxd scrape for all configured users.");
    }

    private async Task<UserScrapeResult> ScrapeUserAsync(
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

        reportUserProgress(0.01d);

        var avatarUrl = await FetchProfileImageUrlAsync(username, requestDelay, cancellationToken).ConfigureAwait(false);
        reportUserProgress(0.04d);

        var rssReviews = await FetchRssReviewsAsync(username, requestDelay, cancellationToken).ConfigureAwait(false);
        reportUserProgress(0.08d);

        var uniqueEntries = new List<ScrapedFilmEntry>();
        var scrapeWasComplete = false;

        for (var page = 1; page <= maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportUserProgress(0.08d + (((page - 1) / (double)maxPages) * 0.32d));

            var escapedUsername = Uri.EscapeDataString(username);
            var filmsPath = page == 1
                ? $"{escapedUsername}/films/"
                : $"{escapedUsername}/films/page/{page}/";
            var referer = page <= 1
                ? $"https://letterboxd.com/{escapedUsername}/"
                : page == 2
                    ? $"https://letterboxd.com/{escapedUsername}/films/"
                    : $"https://letterboxd.com/{escapedUsername}/films/page/{page - 1}/";

            var html = await GetStringWithLoggingAsync(filmsPath, requestDelay, cancellationToken, referer).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                _logger.Warning("Films page " + page + " for " + username + " was not readable; previously cached films will be kept.");
                break;
            }

            var entries = ParseFilmEntries(html, rssReviews).ToArray();
            if (entries.Length == 0 && page == 1)
            {
                _logger.Warning("No Letterboxd watched films found for " + username + ". The page structure may have changed or the profile has no watched films.");
            }

            foreach (var entry in entries)
            {
                if (seenSlugs.Add(entry.LetterboxdSlug))
                {
                    uniqueEntries.Add(entry);
                }
            }

            if (!HasOlderFilmsPage(html))
            {
                scrapeWasComplete = true;
                break;
            }

            if (page == maxPages)
            {
                _logger.Info("Stopped at the configured limit of " + maxPages + " film page(s) for " + username + "; more pages exist.");
            }
        }

        for (var entryIndex = 0; entryIndex < uniqueEntries.Count; entryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = await BuildCachedRecordForEntryAsync(
                mapping,
                username,
                displayName,
                avatarUrl,
                uniqueEntries[entryIndex],
                requestDelay,
                cancellationToken).ConfigureAwait(false);
            records.Add(record);
            reportUserProgress(0.42d + (0.56d * ((entryIndex + 1) / (double)Math.Max(1, uniqueEntries.Count))));
        }

        var reviewCount = records.Count(static record => !string.IsNullOrWhiteSpace(record.ReviewText));
        _logger.Info("Scraped " + records.Count + " film(s) for " + username + " (" + reviewCount + " with review text, scrape " + (scrapeWasComplete ? "complete" : "partial") + ").");
        return new UserScrapeResult(records, scrapeWasComplete);
    }

    private async Task<CachedReviewRecord> BuildCachedRecordForEntryAsync(
        LetterboxdUserMapping mapping,
        string username,
        string displayName,
        string avatarUrl,
        ScrapedFilmEntry entry,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
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

        var externalIds = await ResolveExternalIdsAsync(entry.LetterboxdSlug, requestDelay, cancellationToken).ConfigureAwait(false);

        return new CachedReviewRecord
        {
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
        };
    }

    private async Task<string> FetchProfileImageUrlAsync(
        string username,
        TimeSpan requestDelay,
        CancellationToken cancellationToken)
    {
        var profilePath = $"{Uri.EscapeDataString(username)}/";
        var html = await GetStringWithLoggingAsync(profilePath, requestDelay, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        return ExtractProfileImageUrl(html) ?? string.Empty;
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
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to parse Letterboxd RSS feed for " + username + ".");
        }

        return reviews;
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

        var html = await GetStringWithLoggingAsync(relativePath, requestDelay, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        return new ReviewPageData(
            ExtractReviewTextFromReviewPage(html),
            IsSpoilerReviewPage(html));
    }

    private async Task<string?> GetStringWithLoggingAsync(
        string relativePath,
        TimeSpan requestDelay,
        CancellationToken cancellationToken,
        string? referer = null)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await WaitForLetterboxdRequestSlotAsync(requestDelay, cancellationToken).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
                request.Headers.Add("Upgrade-Insecure-Requests", "1");
                request.Headers.Add("Sec-Fetch-Dest", "document");
                request.Headers.Add("Sec-Fetch-Mode", "navigate");
                request.Headers.Add("Sec-Fetch-Site", string.IsNullOrEmpty(referer) ? "none" : "same-origin");
                request.Headers.Add("Sec-Fetch-User", "?1");
                if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                {
                    request.Headers.Referrer = refererUri;
                }

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

        const char fullStar = '★';
        const char halfStar = '½';

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
            return cached;
        }

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
            || character is '★' or '½');
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

    private sealed record UserScrapeResult(IReadOnlyList<CachedReviewRecord> Records, bool ScrapeWasComplete);
}
