using System.Globalization;
using Jellyfin.Plugin.LetterboxdSocial.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LetterboxdSocial;

/// <summary>
/// Main Letterboxd Social plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The stable plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("449b09f6-a9b5-4cf0-ba7d-433c11e9c0ad");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Letterboxd Social";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var assemblyNamespace = GetType().Namespace;

        return
        [
            new PluginPageInfo
            {
                Name = "letterboxdSocialConfiguration",
                DisplayName = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    assemblyNamespace)
            },
            new PluginPageInfo
            {
                Name = "letterboxdSocialConfigurationJs",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.js",
                    assemblyNamespace)
            }
        ];
    }
}
