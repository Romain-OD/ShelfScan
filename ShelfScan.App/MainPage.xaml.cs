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
