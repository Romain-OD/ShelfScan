using Foundation;
using ShelfScan.Core;
using Vision;

namespace ShelfScan.App;

internal static partial class Ocr
{
    // Vision's defaults are already right for covers: Accurate level, language correction on.
    public static partial Task<IReadOnlyList<OcrLine>> ReadAsync(string path) => Task.Run<IReadOnlyList<OcrLine>>(() =>
    {
        // English and French covers; ML Kit's Latin model reads both without configuration.
        using VNRecognizeTextRequest request = new(null) { RecognitionLanguages = ["en-US", "fr-FR"] };
        using VNImageRequestHandler handler = new(NSUrl.FromFilename(path), new VNImageOptions());

        // Perform is synchronous and CPU-heavy, hence Task.Run.
        if (!handler.Perform([request], out NSError? error))
        {
            throw new NSErrorException(error!);
        }

        // Boxes are normalized with the origin at the bottom-left: highest Y is the top of the cover.
        return [.. (request.Results ?? [])
            .OrderByDescending(o => o.BoundingBox.Y)
            .SelectMany(o => o.TopCandidates(1).Select(c => new OcrLine(c.String, (float)o.BoundingBox.Height)))];
    });
}
