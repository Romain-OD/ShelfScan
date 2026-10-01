using System.Globalization;
using ShelfScan.Core;

namespace ShelfScan.App;

/// <summary>One scanned cover: read it, check the shelf, look it up, add it.</summary>
public partial class ScanPage : ContentPage
{
    private readonly Library library;
    private readonly OpenLibraryClient openLibrary;
    private readonly BnfClient bnf;
    private readonly string photoPath;

    // Data comes in through the constructor: no route, no Shell [QueryProperty] (which won't work under Native AOT).
    public ScanPage(Library library, OpenLibraryClient openLibrary, BnfClient bnf, string photoPath)
    {
        InitializeComponent();
        this.library = library;
        this.openLibrary = openLibrary;
        this.bnf = bnf;
        this.photoPath = photoPath;
        Photo.Source = ImageSource.FromFile(photoPath);
        Loaded += async (_, _) => await ReadCoverAsync();
    }

    private async Task ReadCoverAsync()
    {
        try
        {
            Status.Text = "Reading the cover…";
            IReadOnlyList<OcrLine> lines = await Ocr.ReadAsync(photoPath);
            Query.Text = OcrQuery.FromLines(lines);

            // If you end up saving by hand, the two biggest lines are the best guess for title and author.
            OcrLine[] biggest = [.. lines.OrderByDescending(l => l.Height).Take(2)];
            ManualTitle.Text = biggest.ElementAtOrDefault(0)?.Text;
            ManualAuthor.Text = biggest.ElementAtOrDefault(1)?.Text;

            // Offline first: when the shelf already answers, no request is sent.
            if (library.FindOwned(string.Join('\n', lines.Select(l => l.Text))) is Book owned)
            {
                Status.Text = $"✔ Already on your shelf: {owned.Title} by {owned.Author}";
                return;
            }

            await SearchAsync();
        }
        catch (Exception ex)
        {
            Status.Text = $"Couldn't read the cover: {ex.Message}";
        }
    }

    private async void OnSearchClicked(object? sender, EventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query.Text))
        {
            Status.Text = "No text found. Type the title and author, then Search.";
            return;
        }

        string query = Query.Text;
        List<string> failures = [];
        Status.Text = "Searching Open Library and the BnF…";

        // Both catalogues at once. When one fails, what the other found still shows.
        IReadOnlyList<Candidate>[] answers = await Task.WhenAll(
            AskAsync("Open Library", async () => [.. (await openLibrary.SearchAsync(query)).Select(b => Found(b, "Open Library"))]),
            AskAsync("The BnF", async () => [.. (await bnf.SearchAsync(query)).Select(r => Found(r.Book,
                $"BnF, updated {r.Updated.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"))]));
        // Closest to the search first: each catalogue ranks its own way, and a title-only search for "Émile est invisible"
        // listed it sixth. OrderBy is stable, so ties keep Open Library first, then the BnF, each in its own order.
        List<Candidate> found = [.. answers.SelectMany(a => a).OrderByDescending(c => c.Book.WordsInCommon(query))];

        BindableLayout.SetItemsSource(Results, found);
        string status = found.FirstOrDefault(c => c.Owned) is Candidate mine
            ? $"✔ Already on your shelf: {mine.Book.Title} by {mine.Book.Author}"
            : found.Count > 0 ? "Pick your edition:"
            : failures.Count == 0 ? "Nothing on Open Library or the BnF. Edit the search or save it by hand."
            : "Nothing found. You can save it by hand.";
        Status.Text = string.Join('\n', [status, .. failures]);

        Candidate Found(Book book, string source) => new(book, library.Owns(book.Key), source);

        async Task<IReadOnlyList<Candidate>> AskAsync(string catalogue, Func<Task<IReadOnlyList<Candidate>>> search)
        {
            try
            {
                return await search();
            }
            catch (Exception ex)
            {
                failures.Add($"{catalogue} didn't answer ({ex.Message}).");
                return [];
            }
        }
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: Candidate candidate })
        {
            await SaveAsync(candidate.Book);
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ManualTitle.Text))
        {
            await SaveAsync(new Book($"local:{Guid.NewGuid():N}", ManualTitle.Text.Trim(), ManualAuthor.Text?.Trim() ?? ""));
        }
    }

    private async Task SaveAsync(Book book)
    {
        try
        {
            await library.AddAsync(book);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            Status.Text = $"Couldn't save: {ex.Message}";
        }
    }
}

/// <summary>A search result, flagged when you already own it, and the catalogue it came from.</summary>
public sealed record Candidate(Book Book, bool Owned, string Source)
{
    public string Action => Owned ? "Owned" : "Add";

    public bool CanAdd => !Owned;

    // "2024 · BnF, updated 7 Jan 2025": the BnF's licence asks for the source and the date of the last update.
    public string Details => Book.Year is int year ? $"{year} · {Source}" : Source;
}
