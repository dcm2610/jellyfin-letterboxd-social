using Jellyfin.Plugin.LetterboxdSocial.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LetterboxdSocial;

/// <summary>
/// Scheduled task that refreshes Letterboxd ratings and reviews.
/// </summary>
public sealed class LetterboxdScraperTask : IScheduledTask
{
    private readonly LetterboxdScraper _scraper;
    private readonly LetterboxdSocialFileLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdScraperTask"/> class.
    /// </summary>
    /// <param name="scraper">Letterboxd scraper.</param>
    /// <param name="logger">File logger.</param>
    public LetterboxdScraperTask(LetterboxdScraper scraper, LetterboxdSocialFileLogger logger)
    {
        _scraper = scraper;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh Letterboxd Social Reviews";

    /// <inheritdoc />
    public string Key => "RefreshLetterboxdSocialReviews";

    /// <inheritdoc />
    public string Description => "Scrapes configured public Letterboxd RSS feeds and film pages to refresh the local ratings/reviews cache.";

    /// <inheritdoc />
    public string Category => "Letterboxd Social";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            var configuration = Plugin.Instance?.Configuration;
            if (configuration is null)
            {
                _logger.Warning("Letterboxd Social plugin instance is not available; skipping scheduled scrape.");
                progress.Report(100);
                return;
            }

            _logger.Info("Scheduled task started: " + Name + ".");
            await _scraper.ScrapeConfiguredUsersAsync(configuration, progress, cancellationToken).ConfigureAwait(false);
            _logger.Info("Scheduled task completed: " + Name + ".");
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Letterboxd Social scrape was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Letterboxd Social scheduled scrape failed.");
        }
        finally
        {
            progress.Report(100);
        }
    }
}
