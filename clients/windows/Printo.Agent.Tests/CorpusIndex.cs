using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Printo.Agent.Core.Routing;

namespace Printo.Agent.Tests;

/// <summary>
/// Queries over the extracted corpus, for tests that need a specific kind of page.
/// </summary>
internal static class CorpusIndex
{
    private static readonly Regex WaybillMarkings = new(
        @"WAYBILL\s*DOC|Not\s*to\s*be\s*attached|Hand\s*to\s*Courier",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class Record
    {
        public string Doc { get; init; } = string.Empty;

        public int PageNumber { get; init; }

        public double PageWidthMm { get; init; }

        public double PageHeightMm { get; init; }

        public string? Text { get; init; }

        public InkBox? InkBox { get; init; }
    }

    private sealed class ExpectedPage
    {
        public string Doc { get; init; } = string.Empty;

        public int PageNumber { get; init; }

        public string PageClass { get; init; } = string.Empty;
    }

    private sealed class ExpectedCorpus
    {
        public List<ExpectedPage> Pages { get; init; } = [];
    }

    /// <summary>
    /// Courier sheets whose text layer does *not* carry the markings, so only OCR can identify
    /// them.
    /// </summary>
    /// <remarks>
    /// These are the pages the OCR gate in the shipped rule set exists for: the anonymiser
    /// flattened the static template chrome into an image on this DHL variant, leaving only
    /// the field values in the text layer. Geometry cannot separate them from the parcel
    /// label — 2.3 mm of ink height — so a recogniser that cannot read them means a courier
    /// copy goes on a parcel.
    /// </remarks>
    public static IReadOnlyList<(string Document, int PageNumber, RectMm InkBox)>
        WaybillPagesWithoutTextMarkings(string featuresPath)
    {
        var expectedPath = RepositoryPaths.CorpusExpected;
        if (expectedPath is null)
        {
            return [];
        }

        var expected = JsonSerializer.Deserialize<ExpectedCorpus>(
            File.ReadAllText(expectedPath), RoutingJson.Options);
        if (expected is null)
        {
            return [];
        }

        var courierSheets = expected.Pages
            .Where(page => page.PageClass == "DHL_WAYBILL_DOC")
            .Select(page => (page.Doc, page.PageNumber))
            .ToHashSet();

        var results = new List<(string, int, RectMm)>();

        using var file = File.OpenRead(featuresPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<Record>(line, RoutingJson.Options);
            if (record?.InkBox is null || !courierSheets.Contains((record.Doc, record.PageNumber)))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(record.Text) && WaybillMarkings.IsMatch(record.Text))
            {
                continue; // the text layer already identifies it; OCR is not needed
            }

            results.Add((
                record.Doc,
                record.PageNumber,
                new RectMm
                {
                    XMm = record.InkBox.XMm,
                    YMm = record.InkBox.YMm,
                    WidthMm = record.InkBox.WidthMm,
                    HeightMm = record.InkBox.HeightMm,
                }));
        }

        return results;
    }
}
