using System.Text.Json.Serialization;

namespace ShelfScan.Core;

/// <summary>
/// A book on the shelf. <see cref="Key"/> is the Open Library work key (<c>/works/OL45804W</c>), the ark of a
/// BnF record (<c>ark:/12148/cb47569857w</c>), or <c>local:…</c> for a book saved by hand.
/// </summary>
public sealed record Book(string Key, string Title, string Author, int? Year = null, int? CoverId = null)
{
    /// <summary>Medium cover image from the Open Library covers API, if the book has one.</summary>
    [JsonIgnore]
    public string? CoverUrl => CoverId is int id ? $"https://covers.openlibrary.org/b/id/{id}-M.jpg" : null;

    /// <summary>How many words of the search are in the title or author, case, accents and punctuation ignored.</summary>
    public int WordsInCommon(string search)
    {
        HashSet<string> common = Text.Words(search);
        common.IntersectWith(Text.Words($"{Title} {Author}"));
        return common.Count;
    }
}
