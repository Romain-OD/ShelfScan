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

    /// <summary>Search-as-you-type: every typed word starts a word of the title or author, case and accents ignored.</summary>
    public IEnumerable<Book> Search(string typed)
    {
        HashSet<string> prefixes = Text.Words(typed);
        return books.Where(b =>
        {
            HashSet<string> words = Text.Words($"{b.Title} {b.Author}");
            return prefixes.All(p => words.Any(w => w.StartsWith(p, StringComparison.Ordinal)));
        });
    }

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
        await SaveAsync();
    }

    /// <summary>Take a book off the shelf. False when no book has this key.</summary>
    public async Task<bool> RemoveAsync(string key)
    {
        if (books.RemoveAll(b => b.Key == key) == 0)
        {
            return false;
        }

        await SaveAsync();
        return true;
    }

    private async Task SaveAsync()
    {
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(books, ShelfJson.Default.ListBook));
        // Write aside, then swap in one rename: a crash mid-write never leaves a half-written shelf.
        File.Move(temp, path, overwrite: true);
    }
}
