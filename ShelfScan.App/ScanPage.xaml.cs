using ShelfScan.Core;

namespace ShelfScan.App;

/// <summary>One scanned cover: read it, check the shelf, look it up, add it.</summary>
public partial class ScanPage : ContentPage
{
    private readonly Library library;
    private readonly OpenLibraryClient openLibrary;
    private readonly string photoPath;

    // Data comes in through the constructor: no route, no Shell [QueryProperty] (which won't work under Native AOT).
    public ScanPage(Library library, OpenLibraryClient openLibrary, string photoPath)
    {
        InitializeComponent();
        this.library = library;
        this.openLibrary = openLibrary;
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

        try
        {
            Status.Text = "Searching Open Library…";
            IReadOnlyList<Book> found = await openLibrary.SearchAsync(Query.Text);
            BindableLayout.SetItemsSource(Results, found.Select(b => new Candidate(b, library.Owns(b.Key))).ToList());
            Status.Text = found.FirstOrDefault(b => library.Owns(b.Key)) is Book mine
                ? $"✔ Already on your shelf: {mine.Title} by {mine.Author}"
                : found.Count == 0 ? "Nothing on Open Library. Edit the search or save it by hand." : "Pick your edition:";
        }
        catch (Exception ex)
        {
            Status.Text = $"Open Library didn't answer ({ex.Message}). You can save it by hand.";
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

/// <summary>An Open Library result, flagged when you already own that work.</summary>
public sealed record Candidate(Book Book, bool Owned)
{
    public string Action => Owned ? "Owned" : "Add";

    public bool CanAdd => !Owned;
}
