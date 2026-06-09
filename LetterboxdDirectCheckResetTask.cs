using Jellyfin.Plugin.LetterboxdSocial.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LetterboxdSocial;

/// <summary>
/// Scheduled task that clears on-demand direct film check markers.
/// </summary>
public sealed class LetterboxdDirectCheckResetTask : IScheduledTask
{
    private readonly LetterboxdCacheStore _cacheStore;
    private readonly LetterboxdSocialFileLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdDirectCheckResetTask"/> class.
    /// </summary>
    /// <param name="cacheStore">Cache store.</param>
    /// <param name="logger">File logger.</param>
    public LetterboxdDirectCheckResetTask(LetterboxdCacheStore cacheStore, LetterboxdSocialFileLogger logger)
    {
        _cacheStore = cacheStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Reset Letterboxd Direct Check Cache";

    /// <inheritdoc />
    public string Key => "ResetLetterboxdDirectCheckCache";

    /// <inheritdoc />
    public string Description => "Clears on-demand direct film check markers so missing friends can be checked again without deleting cached ratings or reviews.";

    /// <inheritdoc />
    public string Category => "Letterboxd Social";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            progress.Report(0);
            _logger.Info("Scheduled task started: " + Name + ".");
            var deletedRows = await _cacheStore.ClearUserFilmChecksAsync(cancellationToken).ConfigureAwait(false);
            _logger.Info("Scheduled task completed: " + Name + ". Cleared " + deletedRows + " direct check marker(s).");
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Letterboxd direct check cache reset was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Letterboxd direct check cache reset failed.");
        }
        finally
        {
            progress.Report(100);
        }
    }
}
