using System.Reflection;
using System.Text;
using Jellyfin.Plugin.LetterboxdSocial.Services;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.LetterboxdSocial.Middleware;

/// <summary>
/// Injects the Letterboxd Social frontend script into the Jellyfin Web index page.
/// </summary>
public sealed class FrontendInjectionMiddleware
{
    private const string Marker = "data-letterboxd-social-script";
    private readonly RequestDelegate _next;
    private readonly LetterboxdSocialFileLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrontendInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next request delegate.</param>
    /// <param name="logger">File logger.</param>
    public FrontendInjectionMiddleware(RequestDelegate next, LetterboxdSocialFileLogger logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldInspect(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask downstream for an uncompressed response so the HTML can be edited
        // without any gzip/brotli decode/re-encode round trip, and disable
        // conditional requests so the index is always a 200 with a body to inject
        // into (a 304 would point clients at cached HTML we never saw).
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            var originalBytes = buffer.ToArray();
            if (!ShouldInject(context.Response))
            {
                // Bodyless responses (304/204/HEAD-like) must not be written to at all.
                if (originalBytes.Length > 0)
                {
                    await WriteBytesAsync(originalBody, originalBytes, context.RequestAborted).ConfigureAwait(false);
                }

                return;
            }

            var html = Encoding.UTF8.GetString(originalBytes);
            if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            {
                await WriteBytesAsync(originalBody, originalBytes, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var scriptVersion = Uri.EscapeDataString(
                typeof(FrontendInjectionMiddleware).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion
                ?? typeof(FrontendInjectionMiddleware).Assembly.GetName().Version?.ToString()
                ?? "1");
            var scriptPath = context.Request.PathBase.Add("/ScheduledLetterboxd/FrontendInjection.js").ToString() + "?v=" + scriptVersion;
            var scriptTag = $"<script defer src=\"{scriptPath}\" {Marker}=\"true\"></script>";
            var injectedBytes = Encoding.UTF8.GetBytes(InjectBeforeBodyClose(html, scriptTag));

            context.Response.Headers.Remove("ETag");
            context.Response.ContentLength = injectedBytes.Length;
            await WriteBytesAsync(originalBody, injectedBytes, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to inject Letterboxd Social frontend script.");
            if (buffer.Length > 0)
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool ShouldInspect(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        return string.Equals(path, "/", StringComparison.Ordinal)
               || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldInject(HttpResponse response)
    {
        return response.StatusCode == StatusCodes.Status200OK
               && response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true
               && string.IsNullOrEmpty(response.Headers.ContentEncoding.ToString());
    }

    private static string InjectBeforeBodyClose(string html, string scriptTag)
    {
        var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? html + scriptTag
            : html.Insert(index, scriptTag);
    }

    private static Task WriteBytesAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(bytes, cancellationToken).AsTask();
    }
}
