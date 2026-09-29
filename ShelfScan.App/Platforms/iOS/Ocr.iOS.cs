using ShelfScan.Core;

namespace ShelfScan.App;

internal static partial class Ocr
{
    // Apple Vision arrives in the next step; this keeps the iOS build green until then.
    public static partial Task<IReadOnlyList<OcrLine>> ReadAsync(string path) =>
        throw new PlatformNotSupportedException("Apple Vision OCR arrives in step 07.");
}
