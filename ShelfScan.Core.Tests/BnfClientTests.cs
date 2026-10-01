using ShelfScan.Core;

namespace ShelfScan.Core.Tests;

[TestClass]
public sealed class BnfClientTests
{
    // Four records trimmed from real responses to searches for Mortelle Adèle, Harry Potter, SamSam and Astérix.
    private const string Response = """
        <?xml version="1.0" encoding="UTF-8"?>
        <srw:searchRetrieveResponse xmlns:srw="http://www.loc.gov/zing/srw/" xmlns="http://catalogue.bnf.fr/namespaces/InterXMarc" xmlns:ixm="http://catalogue.bnf.fr/namespaces/InterXMarc" xmlns:mn="http://catalogue.bnf.fr/namespaces/motsnotices">
        <srw:version>1.2</srw:version>
        <srw:numberOfRecords>4</srw:numberOfRecords>
        <srw:records>
        <srw:record>
        <srw:recordSchema>dc</srw:recordSchema>
        <srw:recordPacking>xml</srw:recordPacking>
        <srw:recordData>
        <oai_dc:dc xmlns:oai_dc="http://www.openarchives.org/OAI/2.0/oai_dc/" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <dc:identifier>http://catalogue.bnf.fr/ark:/12148/cb47569857w</dc:identifier>
          <dc:title>Tout ça finira mal / Mr Tan ; [dessins], Miss Prickly</dc:title>
          <dc:creator>Mr Tan (1981-....). Auteur du texte</dc:creator>
          <dc:contributor>Miss Prickly (1982-....). Illustrateur</dc:contributor>
          <dc:publisher>Bayard jeunesse (Montrouge)</dc:publisher>
          <dc:date>2024</dc:date>
        </oai_dc:dc>
        </srw:recordData>
        <srw:recordIdentifier>ark:/12148/cb47569857w</srw:recordIdentifier>
        <srw:recordPosition>1</srw:recordPosition>
        <srw:extraRecordData>
        <ixm:attr name="CreationDate">20241002</ixm:attr>
        <ixm:attr name="LastModificationDate">20250107</ixm:attr>
        <mn:score>49.06495</mn:score>
        </srw:extraRecordData>
        </srw:record>
        <srw:record>
        <srw:recordSchema>dc</srw:recordSchema>
        <srw:recordPacking>xml</srw:recordPacking>
        <srw:recordData>
        <oai_dc:dc xmlns:oai_dc="http://www.openarchives.org/OAI/2.0/oai_dc/" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <dc:identifier>http://catalogue.bnf.fr/ark:/12148/cb45595787x</dc:identifier>
          <dc:title>Harry Potter à l'école des sorciers : Serdaigle (Éd. collector 20e anniversaire) J. K. Rowling ; traduit de l'anglais par Jean-François Ménard</dc:title>
          <dc:creator>Rowling, J. K. (1965-....). Auteur du texte</dc:creator>
          <dc:contributor>Ménard, Jean-François (1948-.... ; romancier pour la jeunesse). Traducteur</dc:contributor>
          <dc:publisher>Gallimard jeunesse (Paris)</dc:publisher>
          <dc:date>2018</dc:date>
        </oai_dc:dc>
        </srw:recordData>
        <srw:recordIdentifier>ark:/12148/cb45595787x</srw:recordIdentifier>
        <srw:recordPosition>2</srw:recordPosition>
        <srw:extraRecordData>
        <ixm:attr name="CreationDate">20181008</ixm:attr>
        <ixm:attr name="LastModificationDate">20210512</ixm:attr>
        <mn:score>34.073025</mn:score>
        </srw:extraRecordData>
        </srw:record>
        <srw:record>
        <srw:recordSchema>dc</srw:recordSchema>
        <srw:recordPacking>xml</srw:recordPacking>
        <srw:recordData>
        <oai_dc:dc xmlns:oai_dc="http://www.openarchives.org/OAI/2.0/oai_dc/" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <dc:identifier>http://catalogue.bnf.fr/ark:/12148/cb41454068x</dc:identifier>
          <dc:title>La grande peur de SamSam / d'après l'oeuvre originale de Serge Bloch</dc:title>
          <dc:contributor>Bloch, Serge (1956-....). Auteur adapté</dc:contributor>
          <dc:publisher>Bayard jeunesse (Montrouge)</dc:publisher>
          <dc:date>2009</dc:date>
        </oai_dc:dc>
        </srw:recordData>
        <srw:recordIdentifier>ark:/12148/cb41454068x</srw:recordIdentifier>
        <srw:recordPosition>3</srw:recordPosition>
        <srw:extraRecordData>
        <ixm:attr name="CreationDate">20090326</ixm:attr>
        <ixm:attr name="LastModificationDate">20151222</ixm:attr>
        <mn:score>19.394028</mn:score>
        </srw:extraRecordData>
        </srw:record>
        <srw:record>
        <srw:recordSchema>dc</srw:recordSchema>
        <srw:recordPacking>xml</srw:recordPacking>
        <srw:recordData>
        <oai_dc:dc xmlns:oai_dc="http://www.openarchives.org/OAI/2.0/oai_dc/" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <dc:identifier>http://catalogue.bnf.fr/ark:/12148/cb38462727v</dc:identifier>
          <dc:title>Astérix &amp; Obélix contre césar : coffret collector</dc:title>
          <dc:creator>Zidi, Claude (1934-....). Réalisateur. Scénariste</dc:creator>
          <dc:creator>Uderzo, Albert (1927-2020). Auteur adapté</dc:creator>
          <dc:date>1999</dc:date>
        </oai_dc:dc>
        </srw:recordData>
        <srw:recordIdentifier>ark:/12148/cb38462727v</srw:recordIdentifier>
        <srw:recordPosition>4</srw:recordPosition>
        <srw:extraRecordData>
        <ixm:attr name="CreationDate">19991213</ixm:attr>
        <ixm:attr name="LastModificationDate">20110210</ixm:attr>
        <mn:score>28.752771</mn:score>
        </srw:extraRecordData>
        </srw:record>
        </srw:records>
        </srw:searchRetrieveResponse>
        """;

    [TestMethod]
    public async Task SearchAsync_sends_an_identified_request_for_printed_books_matching_any_word()
    {
        Canned handler = new(Response, "application/xml");
        BnfClient client = new(new HttpClient(handler));

        // A quote typed in the search box would close the CQL string, so it becomes a space.
        await client.SearchAsync("Tout ça \"finira\" mal");

        Assert.AreEqual(
            "https://catalogue.bnf.fr/api/SRU?version=1.2&operation=searchRetrieve&recordSchema=dublincore&maximumRecords=5" +
            "&query=bib.anywhere%20any%20%22Tout%20%C3%A7a%20%20finira%20%20mal%22%20and%20bib.doctype%20any%20%22a%22",
            handler.Request?.RequestUri?.AbsoluteUri);
        Assert.AreEqual(OpenLibraryClient.UserAgent, handler.Request?.Headers.UserAgent.ToString());
    }

    [TestMethod]
    public async Task SearchAsync_maps_records_to_books_with_the_date_of_their_last_update()
    {
        BnfClient client = new(new HttpClient(new Canned(Response, "application/xml")));

        IReadOnlyList<BnfRecord> records = await client.SearchAsync("Tout ça finira mal");

        CollectionAssert.AreEqual(
            new[]
            {
                new BnfRecord(new Book("ark:/12148/cb47569857w", "Tout ça finira mal", "Mr Tan", 2024), new DateOnly(2025, 1, 7)),
                // Title cut before the edition, initials kept, "Last, First" turned around.
                new BnfRecord(new Book("ark:/12148/cb45595787x", "Harry Potter à l'école des sorciers : Serdaigle", "J. K. Rowling", 2018), new DateOnly(2021, 5, 12)),
                // No creator: the adapter is the best author we have.
                new BnfRecord(new Book("ark:/12148/cb41454068x", "La grande peur de SamSam", "Serge Bloch", 2009), new DateOnly(2015, 12, 22)),
                // A book sold with the film on VHS: its first creator has two roles after the dates.
                new BnfRecord(new Book("ark:/12148/cb38462727v", "Astérix & Obélix contre césar : coffret collector", "Claude Zidi", 1999), new DateOnly(2011, 2, 10)),
            },
            records.ToArray());
    }
}
