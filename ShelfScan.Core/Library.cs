using System.Text.Json;

namespace ShelfScan.Core;

/// <summary>The books you own, kept in one JSON file.</summary>
// ponytail: the whole file is rewritten on every add. Fine for a personal shelf;
// move to SQLite if a library ever passes ~10k books.
public sealed class Library(string path)
{
    private readonly List<Book> books = File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), ShelfJson.Default.ListBook) ?? []
        : [];

    public IReadOnlyList<Book> Books => books;

    /// <summary>True if a book with this Open Library work key is already on the shelf.</summary>
    public bool Owns(string key) => books.Exists(b => b.Key == key);

    /// <summary>
    /// Offline check straight from the cover: the owned book whose title words all appear in
    /// the OCR text and whose author has at least one word there. Case, accents and punctuation are ignored.
    /// </summary>
    public Book? FindOwned(string ocrText)
    {
        HashSet<string> seen = Text.Words(ocrText);
        return books.Find(b =>
        {
            HashSet<string> author = Text.Words(b.Author);
            return Text.Words(b.Title).IsSubsetOf(seen) && (author.Count == 0 || author.Overlaps(seen));
        });
    }

    public async Task AddAsync(Book book)
    {
        books.Add(book);
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(books, ShelfJson.Default.ListBook));
        // Write aside, then swap in one rename: a crash mid-write never leaves a half-written shelf.
        File.Move(temp, path, overwrite: true);
    }
}
