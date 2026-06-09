using Jellyfin.Plugin.LetterboxdSocial.Middleware;
using Jellyfin.Plugin.LetterboxdSocial.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LetterboxdSocial;

/// <summary>
/// Registers services used by the Letterboxd Social plugin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LetterboxdSocialFileLogger>();
        serviceCollection.AddSingleton<LetterboxdHttpClientFactory>();
        serviceCollection.AddSingleton<LetterboxdCacheStore>();
        serviceCollection.AddSingleton<LetterboxdScraper>();
        serviceCollection.AddTransient<IScheduledTask, LetterboxdScraperTask>();
        serviceCollection.AddTransient<IScheduledTask, LetterboxdDirectCheckResetTask>();
        serviceCollection.AddSingleton<IStartupFilter, LetterboxdSocialStartupFilter>();
    }
}
