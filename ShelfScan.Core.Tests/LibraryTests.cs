using ShelfScan.Core;

namespace ShelfScan.Core.Tests;

[TestClass]
public sealed class LibraryTests
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"shelfscan-{Guid.NewGuid():N}.json");

    [TestCleanup]
    public void DeleteFile() => File.Delete(path);

    [TestMethod]
    public async Task Added_books_are_read_back_by_a_new_library()
    {
        await new Library(path).AddAsync(new Book("/works/OL45804W", "Fantastic Mr Fox", "Roald Dahl", 1970, 6498519));

        Library reopened = new(path);

        Assert.HasCount(1, reopened.Books);
        Assert.AreEqual("Fantastic Mr Fox", reopened.Books[0].Title);
        Assert.AreEqual("https://covers.openlibrary.org/b/id/6498519-M.jpg", reopened.Books[0].CoverUrl);
        Assert.IsTrue(reopened.Owns("/works/OL45804W"));
        Assert.IsFalse(reopened.Owns("/works/OL1W"));
    }

    [TestMethod]
    public async Task FindOwned_matches_cover_text_whatever_the_case_accents_and_extra_words()
    {
        Library library = new(path);
        await library.AddAsync(new Book("/works/OL1", "Le Petit Prince", "Antoine de Saint-Exupéry"));

        Book? found = library.FindOwned("ANTOINE DE SAINT-EXUPERY\nLE PETIT PRINCE\nFolio");

        Assert.AreEqual("Le Petit Prince", found?.Title);
    }

    [TestMethod]
    public async Task FindOwned_needs_the_whole_title_and_the_author()
    {
        Library library = new(path);
        await library.AddAsync(new Book("/works/OL1", "Le Petit Prince", "Antoine de Saint-Exupéry"));

        Assert.IsNull(library.FindOwned("SAINT-EXUPÉRY\nVol de nuit"), "same author, other title");
        Assert.IsNull(library.FindOwned("Le Petit Prince\nJoann Sfar"), "same title, other author");
    }

    [TestMethod]
    public async Task Search_matches_the_start_of_title_or_author_words()
    {
        Library library = new(path);
        await library.AddAsync(new Book("/works/OL1", "Le Petit Prince", "Antoine de Saint-Exupéry"));
        await library.AddAsync(new Book("/works/OL2", "Moby Dick", "Herman Melville"));

        Assert.AreEqual("Le Petit Prince", library.Search("exup").Single().Title);
        Assert.AreEqual("Moby Dick", library.Search("moby MEL").Single().Title);
        Assert.AreEqual(2, library.Search("").Count());
    }

    [TestMethod]
    public async Task Removed_books_leave_the_shelf_file()
    {
        Library library = new(path);
        await library.AddAsync(new Book("/works/OL1", "Le Petit Prince", "Antoine de Saint-Exupéry"));
        await library.AddAsync(new Book("/works/OL2", "Moby Dick", "Herman Melville"));

        Assert.IsTrue(await library.RemoveAsync("/works/OL1"));
        Assert.IsFalse(await library.RemoveAsync("/works/OL1"), "already gone");

        Library reopened = new(path);
        Assert.AreEqual("Moby Dick", reopened.Books.Single().Title);
        Assert.IsFalse(reopened.Owns("/works/OL1"));
    }
}
