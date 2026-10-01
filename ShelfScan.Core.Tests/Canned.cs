using System.Net;
using System.Text;

namespace ShelfScan.Core.Tests;

/// <summary>Answers every request with the same body, and keeps the last request to assert on.</summary>
internal sealed class Canned(string body, string mediaType) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        });
    }
}
