using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.LetterboxdSocial.Middleware;

/// <summary>
/// Adds the frontend injection middleware to the Jellyfin ASP.NET Core pipeline.
/// </summary>
public sealed class LetterboxdSocialStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<FrontendInjectionMiddleware>();
            next(app);
        };
    }
}
