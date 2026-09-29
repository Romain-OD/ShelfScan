using ShelfScan.Core;

namespace ShelfScan.App;

/// <summary>On-device text recognition: ML Kit on Android, Apple Vision on iOS.</summary>
internal static partial class Ocr
{
    /// <summary>Reads the lines of text in a photo, with the height of each line's box.</summary>
    public static partial Task<IReadOnlyList<OcrLine>> ReadAsync(string path);
}
