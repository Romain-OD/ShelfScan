using ShelfScan.Core;

namespace ShelfScan.Core.Tests;

[TestClass]
public sealed class BookTests
{
    // Real results for a scan of "Émile est invisible": the book itself shares every word, the near misses one or none.
    [TestMethod]
    [DataRow("EMILE est invisible", "Émile est invisible", "Vincent Cuvellier", 3)]
    [DataRow("EMILE est invisible", "Pierre-Émile et son double", "Klaus-Peter Wolf", 1)]
    [DataRow("EMILE est invisible", "El viaje y la percepción del otro", "Ricarda Musser", 0)]
    [DataRow("Émile est invisible Vincent Cuvellier Ronan Badel", "Émile est invisible", "Vincent Cuvellier", 5)]
    public void WordsInCommon_counts_search_words_found_in_title_and_author(string search, string title, string author, int expected) =>
        Assert.AreEqual(expected, new Book("ark:/12148/cb0", title, author).WordsInCommon(search));

    [TestMethod]
    [DataRow("ark:/12148/cb47569857w", "47569857w")] // a BnF ark ends in the ISBN of its edition
    [DataRow("/works/OL45804W", "/works/OL45804W")] // Open Library works have no ISBN: the key stands in
    [DataRow("local:0123456789abcdef", "local:0123456789abcdef")]
    public void Isbn_is_the_ark_suffix_or_the_whole_key(string key, string expected) =>
        Assert.AreEqual(expected, new Book(key, "Fantastic Mr Fox", "Roald Dahl").Isbn);
}
