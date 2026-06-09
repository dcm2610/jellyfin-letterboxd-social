using System.Net;

namespace Jellyfin.Plugin.LetterboxdSocial.Services;

/// <summary>
/// Provides a configured HTTP client for Letterboxd requests.
/// </summary>
public sealed class LetterboxdHttpClientFactory : IDisposable
{
    private readonly HttpClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdHttpClientFactory"/> class.
    /// </summary>
    public LetterboxdHttpClientFactory()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AllowAutoRedirect = true
        };

        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://letterboxd.com/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <summary>
    /// Gets the configured HTTP client.
    /// </summary>
    public HttpClient Client => _client;

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
    }
}
