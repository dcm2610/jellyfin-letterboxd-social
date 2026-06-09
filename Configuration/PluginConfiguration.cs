using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LetterboxdSocial.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the configured Letterboxd accounts to display.
    /// </summary>
    public LetterboxdUserMapping[] UserMappings { get; set; } = [];

    /// <summary>
    /// Gets or sets the maximum number of Letterboxd film pages to scan per configured account.
    /// </summary>
    public int MaxDiaryPagesPerUser { get; set; } = 3;

    /// <summary>
    /// Gets or sets the delay between Letterboxd HTTP requests, in milliseconds.
    /// </summary>
    public int RequestDelayMilliseconds { get; set; } = 1000;
}

/// <summary>
/// Configures a public Letterboxd profile to scrape and display.
/// </summary>
public class LetterboxdUserMapping
{
    /// <summary>
    /// Gets or sets the legacy Jellyfin user id. This is retained for compatibility with older saved configurations.
    /// </summary>
    public string JellyfinUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the public Letterboxd username.
    /// </summary>
    public string LetterboxdUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name shown in the Jellyfin web client.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
}
