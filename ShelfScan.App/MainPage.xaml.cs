using ShelfScan.Core;

namespace ShelfScan.App;

public partial class MainPage : ContentPage
{
    private readonly Library library;
    private readonly OpenLibraryClient openLibrary;
    private readonly BnfClient bnf;

    public MainPage(Library library, OpenLibraryClient openLibrary, BnfClient bnf)
    {
        InitializeComponent();
        this.library = library;
        this.openLibrary = openLibrary;
        this.bnf = bnf;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ShowBooks();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ShowBooks();

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: Book book })
        {
            return;
        }

        // Recopying the ISBN is the confirmation: it can't be tapped through by accident.
        string? typed = await DisplayPromptAsync(
            $"Delete {book.Title}?",
            $"ISBN: {book.Isbn}\nWhy are you deleting it (\"I sold this book\"), then retype the ISBN to confirm.",
            placeholder: book.Isbn);
        if (typed is null)
        {
            return; // cancelled
        }

        string[] parts = typed.Split('\n', 2, StringSplitOptions.TrimEntries);
        string? isbn = parts.Length == 2 ? parts[1] : null;
        if (!string.Equals(isbn, book.Isbn, StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlertAsync("Delete", "The ISBN doesn't match: the book stays on your shelf.", "OK");
            return;
        }

        try
        {
            await library.RemoveAsync(book.Key);
            ShowBooks();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Delete", ex.Message, "OK");
        }
    }

    // Newest first.
    private void ShowBooks() => Books.ItemsSource = library.Search(Search.Text ?? "").Reverse().ToList();

    private async void OnScanClicked(object? sender, EventArgs e)
    {
        try
        {
            FileResult? photo = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions
            {
                MaximumWidth = 1600, // plenty for cover text, and OCR runs faster
                MaximumHeight = 1600,
                // Android: MAUI 10.0.20's RotateImage strips the EXIF orientation and leaves the pixels
                // sideways. Keep the tag instead, in a JPEG (quality 95+ makes a PNG): ML Kit and the
                // preview both apply it.
                RotateImage = !OperatingSystem.IsAndroid(), // iOS: upright pixels for Vision
                CompressionQuality = OperatingSystem.IsAndroid() ? 90 : 100,
            });
            if (photo is null)
            {
                return; // cancelled
            }

            // FullPath isn't always a real file, so copy the photo where the OCR engines can read it.
            string path = Path.Combine(FileSystem.CacheDirectory, "scan-" + photo.FileName);
            await using (Stream source = await photo.OpenReadAsync())
            await using (FileStream target = File.Create(path))
            {
                await source.CopyToAsync(target);
            }

            await Navigation.PushAsync(new ScanPage(library, openLibrary, bnf, path));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Camera", ex.Message, "OK");
        }
    }
}
