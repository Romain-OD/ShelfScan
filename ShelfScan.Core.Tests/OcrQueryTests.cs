using ShelfScan.Core;

namespace ShelfScan.Core.Tests;

[TestClass]
public sealed class OcrQueryTests
{
    [TestMethod]
    public void FromLines_keeps_the_big_text_in_reading_order()
    {
        OcrLine[] cover =
        [
            new("PENGUIN CLASSICS", 18),
            new("FRANKENSTEIN", 120),
            new("or, The Modern Prometheus", 30),
            new("MARY SHELLEY", 60),
            new("The classic tale of terror that has haunted readers", 20),
            new("£8.99", 16),
        ];

        Assert.AreEqual("FRANKENSTEIN MARY SHELLEY", OcrQuery.FromLines(cover));
    }

    [TestMethod]
    public void FromLines_strips_search_syntax_but_keeps_apostrophes()
    {
        OcrLine[] cover = [new("MOBY-DICK;", 100), new("- \"Herman\" Melville’s -", 50)];

        Assert.AreEqual("MOBY DICK Herman Melville’s", OcrQuery.FromLines(cover));
    }

    [TestMethod]
    public void FromLines_without_letters_is_empty() =>
        Assert.AreEqual("", OcrQuery.FromLines([new("12345", 80)]));
}
