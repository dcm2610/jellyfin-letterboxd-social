using System.Globalization;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.LetterboxdSocial.Services;

/// <summary>
/// Writes Letterboxd Social diagnostics to a plugin-owned log file.
/// </summary>
public sealed class LetterboxdSocialFileLogger
{
    private const long MaxLogSizeBytes = 5 * 1024 * 1024;
    private readonly IApplicationPaths _applicationPaths;
    private readonly string _logFilePath;
    private readonly Lock _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdSocialFileLogger"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    public LetterboxdSocialFileLogger(IApplicationPaths applicationPaths)
    {
        _applicationPaths = applicationPaths;
        _logFilePath = CreateLogFilePath();
    }

    /// <summary>
    /// Writes an information log entry.
    /// </summary>
    /// <param name="message">Message.</param>
    public void Info(string message)
    {
        Write("INF", message, null);
    }

    /// <summary>
    /// Writes a warning log entry.
    /// </summary>
    /// <param name="message">Message.</param>
    public void Warning(string message)
    {
        Write("WRN", message, null);
    }

    /// <summary>
    /// Writes a warning log entry with an exception.
    /// </summary>
    /// <param name="exception">Exception.</param>
    /// <param name="message">Message.</param>
    public void Warning(Exception exception, string message)
    {
        Write("WRN", message, exception);
    }

    /// <summary>
    /// Writes an error log entry.
    /// </summary>
    /// <param name="message">Message.</param>
    public void Error(string message)
    {
        Write("ERR", message, null);
    }

    /// <summary>
    /// Writes an error log entry with an exception.
    /// </summary>
    /// <param name="exception">Exception.</param>
    /// <param name="message">Message.</param>
    public void Error(Exception exception, string message)
    {
        Write("ERR", message, exception);
    }

    /// <summary>
    /// Gets the current log file path.
    /// </summary>
    /// <returns>Log file path.</returns>
    public string GetLogFilePath()
    {
        return _logFilePath;
    }

    private string CreateLogFilePath()
    {
        var logFolder = _applicationPaths.LogDirectoryPath
            ?? Plugin.Instance?.DataFolderPath
            ?? Path.Combine(AppContext.BaseDirectory, "letterboxd-social");

        Directory.CreateDirectory(logFolder);
        return Path.Combine(logFolder, "letterboxd-social.log");
    }

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_writeLock)
            {
                var path = GetLogFilePath();
                RotateIfNeeded(path);

                var line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.UtcNow:O} [{level}] {message}{Environment.NewLine}");

                File.AppendAllText(path, line);

                if (exception is not null)
                {
                    File.AppendAllText(path, exception + Environment.NewLine);
                }
            }
        }
        catch
        {
            // Logging must never break Jellyfin request handling or scheduled tasks.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length < MaxLogSizeBytes)
        {
            return;
        }

        var archivePath = path + ".1";
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        File.Move(path, archivePath);
    }
}
