using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Printo.Agent.Core.Routing;
using Printo.Agent.Ocr;
using Printo.Agent.Render;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Regenerates <c>tests/corpus/printed-features.jsonl.gz</c> from print-simulated corpus copies.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, because it is an export rather than a check: PRINTO_PRINTED_CORPUS_DIR must name the
/// output of <c>tools/corpus/simulate_print.py</c>. It records what the agent itself measures on
/// each page - geometry, every barcode its decoder finds, and its recogniser's reading of the ink
/// box - so the printed corpus can be routed by both engines without a recogniser, and so the
/// file changes only when the agent's measurement does.
/// </para>
/// <para>
/// Every page is recognised and decoded, not only those a rule asks about today. A rule added
/// tomorrow must be provable against the same file.
/// </para>
/// </remarks>
public sealed class PrintedCorpusExport
{
    [Fact]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void ExportsThePrintedCorpusFeatures()
    {
        var source = Environment.GetEnvironmentVariable("PRINTO_PRINTED_CORPUS_DIR");
        if (string.IsNullOrWhiteSpace(source) || RepositoryPaths.PrintedCorpusFeatures is not { } target)
        {
            return;
        }

        var ocr = WindowsOcrEngine.TryCreate()
            ?? throw new InvalidOperationException("the export needs a Windows OCR language installed");
        var decoder = new ZxingBarcodeDecoder();
        var extractor = new PageFeatureExtractor();

        using var file = File.Create(target);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(gzip) { NewLine = "\n" };

        foreach (var path in Directory.EnumerateFiles(source, "*.pdf", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var doc = Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');
            using var pdf = PdfDocument.Load(File.ReadAllBytes(path));

            foreach (var page in extractor.Extract(pdf, doc).Pages)
            {
                using var rendered = pdf.OpenPage(page.PageNumber - 1);
                var measured = PageFeatureExtractor.WithBarcodes(page, rendered, decoder);

                if (page.InkBox is { } box)
                {
                    var rect = new RectMm { XMm = box.XMm, YMm = box.YMm, WidthMm = box.WidthMm, HeightMm = box.HeightMm };
                    measured = PageFeatureExtractor.WithOcr(
                        measured,
                        rendered,
                        [new OcrRequest { PageNumber = page.PageNumber, Key = Geometry.OcrRegionKey(rect), Rect = rect }],
                        ocr);
                }

                writer.WriteLine(Record(doc, measured));
            }
        }
    }

    /// <summary>One page in the shape of <c>features.jsonl</c>, with the bulky parts left out.</summary>
    /// <remarks>
    /// Line boxes are dropped - no rule reads them and they would triple the file - and so are the
    /// derived rectangle edges, which the readers compute. The text layer is recorded as null
    /// explicitly, because that is the measurement: printing removed it.
    /// </remarks>
    private static string Record(string doc, PageFeatures page)
    {
        var node = JsonNode.Parse(RoutingJson.Serialize(page))!.AsObject();
        node.Remove("textLines");
        node["text"] = null;

        foreach (var region in node["ocrRegions"]?.AsArray() ?? [])
        {
            region!["lines"] = new JsonArray();
            region["rect"]?.AsObject().Remove("right");
            region["rect"]?.AsObject().Remove("bottom");
        }

        node["inkBox"]?.AsObject().Remove("right");
        node["inkBox"]?.AsObject().Remove("bottom");

        var record = new JsonObject { ["doc"] = doc };
        foreach (var (key, value) in node.ToList())
        {
            node.Remove(key);
            record[key] = value;
        }

        return record.ToJsonString();
    }
}
