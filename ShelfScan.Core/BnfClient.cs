using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ShelfScan.Core;

/// <summary>
/// Book search in the general catalogue of the Bibliothèque nationale de France (BnF), through its SRU API:
/// https://api.bnf.fr/fr/api-sru-catalogue-general. It's free, needs no key, and legal deposit puts nearly every
/// book published in France in it, including the children's books Open Library doesn't have.
/// </summary>
/// <remarks>
/// The records are under the Licence Ouverte 2.0: reusing them means naming the source, "Bibliothèque nationale de
/// France", and the date of the last update (https://api.bnf.fr/fr/conditions-generales-dutilisation-du-site-bnf-api-et-jeux-de-donnees,
/// section 4.2). Each result carries that date.
/// </remarks>
public sealed partial class BnfClient(HttpClient http)
{
    private static readonly XNamespace Srw = "http://www.loc.gov/zing/srw/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ixm = "http://catalogue.bnf.fr/namespaces/InterXMarc";

    /// <summary>The five printed books that best match a free-text query (title and/or author words).</summary>
    public async Task<IReadOnlyList<BnfRecord>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        // "any" ranks by relevance: one word misread on the cover would make "all" return nothing.
        // Doctype "a" is printed text, so no audiobooks, films or scores. A quote would end the CQL string.
        string cql = $"bib.anywhere any \"{query.Replace('"', ' ').Replace('\\', ' ')}\" and bib.doctype any \"a\"";
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            "https://catalogue.bnf.fr/api/SRU?version=1.2&operation=searchRetrieve&recordSchema=dublincore" +
            $"&maximumRecords=5&query={Uri.EscapeDataString(cql)}");
        request.Headers.UserAgent.ParseAdd(OpenLibraryClient.UserAgent);

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        // SendAsync has already read the few KB of the response into memory, so Load doesn't wait on the network.
        // LoadAsync would make the APK 116 KB bigger: trimming would keep the async XML reader.
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        XDocument xml = XDocument.Load(body);

        return [.. xml.Descendants(Srw + "record").Select(ToRecord)];
    }

    private static BnfRecord ToRecord(XElement record)
    {
        string? First(XName name) => (string?)record.Descendants(name).FirstOrDefault();

        string key = First(Srw + "recordIdentifier") ?? throw new FormatException("A BnF record without an ark.");
        // "Tout ça finira mal / Mr Tan ; [dessins], Miss Prickly" or "Tout ça finira mal (Nouvelle édition) Tan ; dessins Miss Prickly".
        string title = First(Dc + "title")?.Split([" / ", " ("], 2, StringSplitOptions.TrimEntries)[0] ?? "";
        // Some books only credit an adapter or an illustrator: SamSam, T'choupi.
        string? person = First(Dc + "creator") ?? First(Dc + "contributor");
        int? year = int.TryParse(First(Dc + "date"), CultureInfo.InvariantCulture, out int y) ? y : null;
        string updated = (string?)record.Descendants(Ixm + "attr")
            .FirstOrDefault(a => (string?)a.Attribute("name") == "LastModificationDate")
            ?? throw new FormatException("A BnF record without its update date.");

        return new BnfRecord(
            new Book(key, title, person is null ? "Unknown author" : Name(person), year),
            DateOnly.ParseExact(updated, "yyyyMMdd", CultureInfo.InvariantCulture));
    }

    // "Bloch, Serge (1956-....). Auteur adapté" gives "Serge Bloch".
    private static string Name(string heading)
    {
        string name = NameDatesRoles().Match(heading).Groups["name"].Value;
        return name.Split(", ", 2) is [string last, string first] ? $"{first} {last}" : name;
    }

    // The name, optional "(dates)", then any number of ". Role": "Guibert, Emmanuel (1964-....). Chant. Instrumentiste".
    // Lazy, yet initials stay in the name: from the "J" of "Rowling, J. K. (1965-....)", no ". Role" chain reaches the end.
    [GeneratedRegex(@"^(?<name>.+?)(?: \([^)]*\))?(?:\. [^.]+)*$")]
    private static partial Regex NameDatesRoles();
}

/// <summary>A book from the BnF catalogue, and the day the BnF last updated its record.</summary>
public sealed record BnfRecord(Book Book, DateOnly Updated);
