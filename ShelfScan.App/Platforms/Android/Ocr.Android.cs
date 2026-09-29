using Android.Gms.Extensions;
using ShelfScan.Core;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;
using MlKitText = Xamarin.Google.MLKit.Vision.Text.Text;

namespace ShelfScan.App;

internal static partial class Ocr
{
    // One recognizer for the app's lifetime, as ML Kit recommends.
    private static readonly ITextRecognizer Recognizer = TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions);

    public static async partial Task<IReadOnlyList<OcrLine>> ReadAsync(string path)
    {
        // FromFilePath applies the photo's EXIF rotation, so a portrait shot isn't read sideways.
        InputImage image = InputImage.FromFilePath(Platform.AppContext, Android.Net.Uri.FromFile(new Java.IO.File(path))!);
        MlKitText text = await Recognizer.Process(image).AsAsync<MlKitText>();

        return [.. text.TextBlocks
            .SelectMany(block => block.Lines)
            .Select(line => new OcrLine(line.Text, line.BoundingBox?.Height() ?? 0))];
    }
}
