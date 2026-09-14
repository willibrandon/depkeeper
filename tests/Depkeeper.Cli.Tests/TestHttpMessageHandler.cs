using System.Net;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Supplies deterministic HTTP responses to registry clients without external network access.
/// </summary>
internal sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    /// <summary>
    /// Creates a handler from the response callback.
    /// </summary>
    /// <param name="respond">The deterministic response callback.</param>
    internal TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>
    /// Gets the number of requests sent through the handler.
    /// </summary>
    internal int Requests { get; private set; }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(_respond(request));
    }

    /// <summary>
    /// Creates a successful JSON response.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <returns>The HTTP response.</returns>
    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };
}
