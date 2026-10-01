using ShelfScan.Core;

namespace ShelfScan.Core.Tests;

[TestClass]
public sealed class OpenLibraryClientTests
{
    // Trimmed from a real response to search.json?q=moby dick herman melville.
    private const string Response = """
        {"numFound":967,"start":0,"numFoundExact":true,"q":"moby dick herman melville","offset":null,"docs":[
          {"author_name":["Herman Melville"],"cover_i":10544254,"first_publish_year":1851,"key":"/works/OL102749W","title":"Moby Dick"},
          {"key":"/works/OL30237660W","title":"Moby Dick"}]}
        """;

    [TestMethod]
    public async Task SearchAsync_sends_an_identified_request_and_maps_docs_to_books()
    {
        Canned handler = new(Response, "application/json");
        OpenLibraryClient client = new(new HttpClient(handler));

        IReadOnlyList<Book> books = await client.SearchAsync("Moby Dick Herman Melville");

        Assert.AreEqual(
            "https://openlibrary.org/search.json?q=Moby%20Dick%20Herman%20Melville&fields=key,title,author_name,first_publish_year,cover_i&limit=5",
            handler.Request?.RequestUri?.AbsoluteUri);
        Assert.AreEqual(OpenLibraryClient.UserAgent, handler.Request?.Headers.UserAgent.ToString());
        Assert.AreEqual(new Book("/works/OL102749W", "Moby Dick", "Herman Melville", 1851, 10544254), books[0]);
        Assert.AreEqual(new Book("/works/OL30237660W", "Moby Dick", "Unknown author"), books[1]);
    }
}
