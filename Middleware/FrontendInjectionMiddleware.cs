using System.IO.Compression;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.LetterboxdSocial.Services;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.LetterboxdSocial.Middleware;

/// <summary>
/// Injects the Letterboxd Social frontend script into Jellyfin Web HTML responses.
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

        _logger.Debug("Inspecting Jellyfin Web HTML response for " + context.Request.Path + ".");
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            if (!ShouldInject(context.Response))
            {
                _logger.Debug("Skipping frontend injection for " + context.Request.Path + ". Status=" + context.Response.StatusCode + ", ContentType=" + (context.Response.ContentType ?? "(none)") + ".");
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var encoding = GetContentEncoding(context.Response);
            var originalBytes = buffer.ToArray();
            var html = await ReadResponseHtmlAsync(originalBytes, encoding, context.RequestAborted).ConfigureAwait(false);
            if (html is null)
            {
                _logger.Warning("Skipping frontend injection because response encoding could not be decoded. Encoding=" + (encoding ?? "(none)") + ".");
                await WriteBytesAsync(originalBody, originalBytes, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("Frontend injection marker already present for " + context.Request.Path + ".");
                var existingBytes = await EncodeResponseHtmlAsync(html, encoding, context.RequestAborted).ConfigureAwait(false);
                context.Response.ContentLength = existingBytes.Length;
                await WriteBytesAsync(originalBody, existingBytes, context.RequestAborted).ConfigureAwait(false);
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
            var injectedHtml = html.Contains("</body>", StringComparison.OrdinalIgnoreCase)
                ? ReplaceLastBodyClose(html, scriptTag)
                : html + scriptTag;

            var injectedBytes = await EncodeResponseHtmlAsync(injectedHtml, encoding, context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.Remove("ETag");
            context.Response.ContentLength = injectedBytes.Length;
            await WriteBytesAsync(originalBody, injectedBytes, context.RequestAborted).ConfigureAwait(false);
            _logger.Debug("Injected Letterboxd Social frontend script into " + context.Request.Path + ". Encoding=" + (encoding ?? "(none)") + ".");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to inject Letterboxd Social frontend script.");
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
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
        return path.EndsWith("/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/", StringComparison.Ordinal);
    }

    private static bool ShouldInject(HttpResponse response)
    {
        return response.StatusCode == StatusCodes.Status200OK
               && response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? GetContentEncoding(HttpResponse response)
    {
        var value = response.Headers["Content-Encoding"].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(',')[0].Trim().ToLowerInvariant();
    }

    private static async Task<string?> ReadResponseHtmlAsync(
        byte[] bytes,
        string? encoding,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new MemoryStream(bytes);
            await using var decoded = new MemoryStream();

            if (string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                await using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: false);
                await gzip.CopyToAsync(decoded, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(encoding, "br", StringComparison.OrdinalIgnoreCase))
            {
                await using var brotli = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: false);
                await brotli.CopyToAsync(decoded, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(encoding, "deflate", StringComparison.OrdinalIgnoreCase))
            {
                await using var deflate = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: false);
                await deflate.CopyToAsync(decoded, cancellationToken).ConfigureAwait(false);
            }
            else if (encoding is null)
            {
                decoded.Write(bytes, 0, bytes.Length);
            }
            else
            {
                return null;
            }

            return Encoding.UTF8.GetString(decoded.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<byte[]> EncodeResponseHtmlAsync(
        string html,
        string? encoding,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(html);

        if (encoding is null)
        {
            return bytes;
        }

        await using var output = new MemoryStream();

        if (string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                await gzip.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (string.Equals(encoding, "br", StringComparison.OrdinalIgnoreCase))
        {
            await using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                await brotli.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (string.Equals(encoding, "deflate", StringComparison.OrdinalIgnoreCase))
        {
            await using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                await deflate.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            return bytes;
        }

        return output.ToArray();
    }

    private static string ReplaceLastBodyClose(string html, string scriptTag)
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
