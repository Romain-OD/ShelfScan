using System.Net.Http.Json;

namespace ShelfScan.Core;

/// <summary>Book search against the Open Library API: https://openlibrary.org/dev/docs/api/search.</summary>
public sealed class OpenLibraryClient(HttpClient http)
{
    /// <summary>Open Library asks every app to identify itself: https://openlibrary.org/developers/api.</summary>
    // ponytail: repo URL instead of a contact email keeps a personal address out of a public repo.
    // Unidentified limit is 1 request/s, and one scan makes one request; add an email to get 3/s.
    public const string UserAgent = "ShelfScan/1.0 (+https://github.com/Romain-OD/ShelfScan)";

    /// <summary>Top five works for a free-text query (title and/or author words).</summary>
    public async Task<IReadOnlyList<Book>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(query)}" +
            "&fields=key,title,author_name,first_publish_year,cover_i&limit=5");
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        SearchResponse? body = await response.Content.ReadFromJsonAsync(ShelfJson.Default.SearchResponse, cancellationToken);

        return body?.Docs
            .Select(d => new Book(d.Key, d.Title, d.AuthorName?.FirstOrDefault() ?? "Unknown author", d.FirstPublishYear, d.CoverI))
            .ToList() ?? [];
    }
}

internal sealed record SearchResponse(List<SearchDoc> Docs);

internal sealed record SearchDoc(string Key, string Title, string[]? AuthorName, int? FirstPublishYear, int? CoverI);
