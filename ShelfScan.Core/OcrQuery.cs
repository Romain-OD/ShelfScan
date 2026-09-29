namespace ShelfScan.Core;

/// <summary>One line of text read from a photo; <paramref name="Height"/> is its box height in any unit.</summary>
public sealed record OcrLine(string Text, float Height);

public static class OcrQuery
{
    /// <summary>
    /// Turns a cover's OCR lines into an Open Library query: the big text (title, author) in reading order.
    /// Open Library needs every query word to match, so a small-print tagline would sink the search.
    /// </summary>
    // ponytail: "big text is title and author" is a heuristic; the query stays editable in the UI.
    public static string FromLines(IReadOnlyList<OcrLine> lines)
    {
        OcrLine[] words = [.. lines.Where(l => l.Text.Any(char.IsLetter))];
        if (words.Length == 0)
        {
            return "";
        }

        float tallest = words.Max(l => l.Height);
        string text = string.Join(' ', words.Where(l => l.Height >= tallest * 0.4f).Select(l => l.Text));

        // Keep letters, digits and apostrophes; ':' '-' '"' mean something to the search engine.
        char[] plain = text.Select(c => char.IsLetterOrDigit(c) || c is '\'' or '’' ? c : ' ').ToArray();
        return string.Join(' ', new string(plain).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
