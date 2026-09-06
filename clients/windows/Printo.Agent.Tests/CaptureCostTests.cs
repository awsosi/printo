using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Printo.Agent.Core.Routing;
using Printo.Agent.Ocr;
using Printo.Agent.Render;
using Xunit;
using Xunit.Abstractions;

namespace Printo.Agent.Tests;

/// <summary>What one stage of the pipeline cost on one page, in milliseconds.</summary>
public sealed record StageCost(string Capture, int PageNumber, string Stage, double Milliseconds, string Detail);

/// <summary>
/// What routing a printed job actually costs, stage by stage, on the captured jobs.
/// </summary>
/// <remarks>
/// Section 5.0a established that the print path deletes the text layer from every page, which
/// moves OCR, barcodes and picture matching from optimisations onto the critical path of every
/// job. That changes the shape of the rule set, and the choice between rule-set shapes should be
/// made against measured cost rather than an assumption about what OCR costs - so this measures
/// the ingredients any of those shapes would be built from.
///
/// Two costs are distinguished, because different things pay them:
///
/// <list type="bullet">
/// <item><b>Always paid.</b> Geometry and the text layer, plus barcode decoding, which the
/// service configures for every page (<c>AgentService</c> builds the extractor with a
/// <see cref="ZxingBarcodeDecoder"/>). Every page of every job pays this before a single rule
/// is evaluated.</item>
/// <item><b>Paid on escalation.</b> OCR and picture matching, which the engine requests lazily,
/// for the pages and the rectangles a rule actually asks about.</item>
/// </list>
///
/// Cold cost is reported separately from warm. The first job after a service start pays for
/// PDFium's and the recogniser's initialisation, and folding that into a per-page average would
/// flatter the steady state and understate the first job a user actually waits for.
///
/// The assertions are deliberately loose - a ceiling with a large multiple of headroom, not a
/// benchmark - because the numbers are the point, and a timing test that fails on a busy machine
/// gets deleted within a week. What a loose ceiling still catches is the defect described in plan
/// section 10.5, where an exhaustive template search took 35 seconds a page and returned the
/// right answer, so every functional test passed.
/// </remarks>
public sealed class CaptureCostTests(ITestOutputHelper output)
{
    /// <summary>Resolution the console cuts reference images at; see <c>ConsoleTemplateTests</c>.</summary>
    private const double TemplateDpi = 150;

    /// <summary>Recognition resolution, matching <c>WindowsOcrEngine</c>'s own.</summary>
    private const double OcrDpi = 250;

    /// <summary>Timed repeats per stage; the median is reported, so one scheduling hiccup cannot skew it.</summary>
    private const int Repeats = 3;

    /// <summary>Ceiling for the cost every page pays before any rule is evaluated.</summary>
    private const double AlwaysPaidCeilingMs = 8000;

    /// <summary>Ceiling for one lazily requested measurement.</summary>
    private const double EscalationCeilingMs = 8000;

    /// <summary>
    /// Whether to run the measurements. Off unless PRINTO_MEASURE_COST is set.
    /// </summary>
    /// <remarks>
    /// These are a benchmark, not a correctness check, and they behave like one: four minutes of
    /// CPU-saturating rendering, recognition and correlation. Run in the default suite they add
    /// that to every run and, because xUnit runs collections in parallel, they starve the
    /// timing-sensitive tests alongside them - <c>TrayIpcTests</c> waits 4 seconds to connect to
    /// a named pipe it started itself, which is ample until a machine is fully committed
    /// elsewhere. Opt-in keeps the suite fast and deterministic and the numbers reproducible on
    /// demand, which is what a benchmark is for.
    /// </remarks>
    private static bool Enabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PRINTO_MEASURE_COST"));

    [Fact]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void MeasuresWhatEachStageCostsOnRealPrintedJobs()
    {
        if (!Enabled || RepositoryPaths.Captures is not { } directory || RepositoryPaths.Root is null)
        {
            return;
        }

        var fixtures = Manifest();
        if (fixtures.Count == 0)
        {
            return;
        }

        var ocr = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            ? WindowsOcrEngine.TryCreate()
            : null;
        var template = LoadTemplate();
        var decoder = new ZxingBarcodeDecoder();

        var cold = MeasureCold(directory, fixtures[0].Capture, decoder, ocr, template);
        var costs = new List<StageCost>();

        foreach (var fixture in fixtures)
        {
            var path = Path.Combine(directory, fixture.Capture);
            if (!File.Exists(path))
            {
                continue;
            }

            using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
            for (var index = 0; index < pdf.PageCount; index++)
            {
                using var page = pdf.OpenPage(index);
                costs.AddRange(
                    MeasurePage(fixture.Capture, page, index + 1, pdf.PageCount, decoder, ocr, template));
            }
        }

        Report(costs, cold, ocr?.Language);
        Persist(costs, cold, ocr?.Language);

        Assert.NotEmpty(costs);

        foreach (var cost in costs)
        {
            var ceiling = IsAlwaysPaid(cost.Stage) ? AlwaysPaidCeilingMs : EscalationCeilingMs;
            Assert.True(
                cost.Milliseconds < ceiling,
                $"{cost.Capture} p{cost.PageNumber} {cost.Stage} took {cost.Milliseconds:0} ms, "
                    + $"over the {ceiling:0} ms ceiling");
        }
    }

    /// <summary>
    /// How much text the recogniser returns from a page's ink box at each quarter turn.
    /// </summary>
    /// <remarks>
    /// The cost measurement above turned up something that reads like an impossibility: on
    /// several captured pages, OCR of the ink box returned <em>fewer</em> characters than OCR of
    /// the whole page, and on one - the FedEx label that the print path turned to portrait - it
    /// returned none at all against 51 characters for the page. The ink box contains all of the
    /// ink by construction, and both are rendered at the same 250 dpi, so the difference is not
    /// resolution and not the crop losing content.
    ///
    /// It is orientation. The Windows recogniser decides which way up a bitmap's text runs from
    /// the bitmap it is given, and a label that arrives turned 90 degrees inside a portrait sheet
    /// reads as vertical text in a landscape crop. Given the whole sheet it sometimes guesses
    /// again and recovers a line or two, which is why the page score is higher - not because the
    /// page holds more.
    ///
    /// That matters for the rule set, because section 5.0a made OCR load-bearing on this input
    /// path and 10 of the 22 captured pages arrive turned. This measures the recovery and its
    /// cost so the decision is made on numbers.
    ///
    /// Only the text is examined here. <see cref="WindowsOcrEngine.RecogniseRaster"/> maps line
    /// boxes back into page millimetres assuming the raster is the right way up, so a rotated
    /// raster's coordinates are meaningless - a real implementation has to rotate them back.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void MeasuresWhatTurningTheInkBoxRecoversForTheRecogniser()
    {
        if (!Enabled || RepositoryPaths.Captures is not { } directory)
        {
            return;
        }

        var ocr = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            ? WindowsOcrEngine.TryCreate()
            : null;
        if (ocr is null)
        {
            return;
        }

        var extractor = new PageFeatureExtractor();
        var rows = new List<(string Capture, int Page, RectMm Ink, int[] Chars, double[] Ms)>();

        foreach (var fixture in Manifest())
        {
            var path = Path.Combine(directory, fixture.Capture);
            if (!File.Exists(path))
            {
                continue;
            }

            using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
            for (var index = 0; index < pdf.PageCount; index++)
            {
                using var page = pdf.OpenPage(index);
                var features = extractor.ExtractPage(page, index + 1, pdf.PageCount);
                if (features.InkBox is not { } box)
                {
                    continue;
                }

                var region = new RectMm
                {
                    XMm = box.XMm,
                    YMm = box.YMm,
                    WidthMm = box.WidthMm,
                    HeightMm = box.HeightMm,
                };

                var upright = PageRenderer.RenderRegion(page, region, OcrDpi);
                var chars = new int[4];
                var timings = new double[4];

                for (var quarter = 0; quarter < 4; quarter++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var lines = ocr.RecogniseRaster(upright.Rotate(quarter * 90), region, OcrDpi);
                    stopwatch.Stop();

                    chars[quarter] = lines.Sum(line => line.Text.Length);
                    timings[quarter] = stopwatch.Elapsed.TotalMilliseconds;
                }

                rows.Add((fixture.Capture, index + 1, region, chars, timings));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        output.WriteLine($"characters recognised from the ink box at each quarter turn ({OcrDpi:0} dpi)");
        output.WriteLine($"  {"page",-34} {"ink box",-16} {"0",6} {"90",6} {"180",6} {"270",6}  best");

        var improved = 0;
        foreach (var row in rows)
        {
            var best = Array.IndexOf(row.Chars, row.Chars.Max());
            if (row.Chars[best] > row.Chars[0])
            {
                improved++;
            }

            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {Path.GetFileNameWithoutExtension(row.Capture) + " p" + row.Page,-34} {Describe(row.Ink),-16} "
                    + $"{row.Chars[0],6} {row.Chars[1],6} {row.Chars[2],6} {row.Chars[3],6}  "
                    + $"{best * 90}deg"));
        }

        var uprightTotal = rows.Sum(row => row.Ms[0]);
        var allFour = rows.Sum(row => row.Ms.Sum());

        output.WriteLine(string.Empty);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{improved} of {rows.Count} pages yield more text turned than upright. "
                + $"Upright only: {uprightTotal:0} ms total, {uprightTotal / rows.Count:0.0} ms/page. "
                + $"All four turns: {allFour:0} ms total, {allFour / rows.Count:0.0} ms/page."));

        Assert.NotEmpty(rows);

        // The turn does not have to be searched for. Every document in this corpus is
        // portrait-native content - labels are tall, invoices are tall - so ink that measures
        // wider than it is tall is ink the print path turned, and turning it back is the one
        // quarter turn worth trying. Pinned as an assertion because it is the difference
        // between 246 ms a page and 1724 ms: searching all four turns costs seven times as
        // much and finds the same answer.
        var mispredicted = new List<string>();
        foreach (var row in rows)
        {
            var best = Array.IndexOf(row.Chars, row.Chars.Max());
            var predicted = row.Ink.WidthMm > row.Ink.HeightMm ? 1 : 0;

            if (best != predicted)
            {
                mispredicted.Add(
                    $"{Path.GetFileNameWithoutExtension(row.Capture)} p{row.Page} ink {Describe(row.Ink)}: "
                        + $"predicted {predicted * 90}deg, best was {best * 90}deg "
                        + $"({string.Join("/", row.Chars)} chars)");
            }
        }

        Assert.True(
            mispredicted.Count == 0,
            $"{mispredicted.Count} of {rows.Count} pages did not read best at the turn their ink box "
                + $"predicts:{Environment.NewLine}{string.Join(Environment.NewLine, mispredicted)}");
    }


    private static bool IsAlwaysPaid(string stage) => stage is "geometry" or "barcodes";

    private static List<StageCost> MeasurePage(
        string capture,
        PdfPage page,
        int pageNumber,
        int pageCount,
        ZxingBarcodeDecoder decoder,
        IOcrEngine? ocr,
        byte[]? template)
    {
        var plain = new PageFeatureExtractor();
        var costs = new List<StageCost>();
        PageFeatures? features = null;

        costs.Add(Measure(capture, pageNumber, "geometry", () =>
        {
            features = plain.ExtractPage(page, pageNumber, pageCount);
            return $"ink {Describe(features.InkBox)}, text {features.Text?.Length ?? 0} chars";
        }));

        costs.Add(Measure(capture, pageNumber, "barcodes", () =>
        {
            var found = decoder.Decode(page);
            return found.Count == 0
                ? "none"
                : string.Join(",", found.Select(barcode => barcode.Symbology).Distinct());
        }));

        // How much of the stages above and below is rasterisation rather than analysis. Each
        // stage renders the page for itself - the barcode decoder at 200 and again at 300 dpi,
        // OCR at 250, the matcher at the template's dpi - so this is the cost a shared raster
        // would be trading against.
        costs.Add(Measure(capture, pageNumber, "render-200", () =>
        {
            var (_, width, height) = PageRenderer.RenderGray8(page, 200);
            return $"{width}x{height}px";
        }));

        costs.Add(Measure(capture, pageNumber, "render-300", () =>
        {
            var (_, width, height) = PageRenderer.RenderGray8(page, 300);
            return $"{width}x{height}px";
        }));

        var whole = new RectMm { XMm = 0, YMm = 0, WidthMm = page.WidthMm, HeightMm = page.HeightMm };
        var ink = features?.InkBox is { } box
            ? new RectMm { XMm = box.XMm, YMm = box.YMm, WidthMm = box.WidthMm, HeightMm = box.HeightMm }
            : whole;

        if (ocr is not null)
        {
            costs.Add(Measure(capture, pageNumber, "ocr-ink", () =>
                $"{ocr.Recognise(page, ink).Text.Length} chars over {Describe(ink)}"));

            costs.Add(Measure(capture, pageNumber, "ocr-page", () =>
                $"{ocr.Recognise(page, whole).Text.Length} chars over the whole page"));
        }

        if (template is not null)
        {
            costs.Add(Measure(capture, pageNumber, "template-ink", () =>
                Describe(TemplateMatcher.Match(page, ink, template, TemplateDpi))));

            costs.Add(Measure(capture, pageNumber, "template-page", () =>
                Describe(TemplateMatcher.Match(page, whole, template, TemplateDpi))));
        }

        return costs;
    }

    /// <summary>
    /// The first-job cost: PDFium, the recogniser and the decoder all initialising.
    /// </summary>
    /// <remarks>
    /// Taken on the first captured document and then discarded, so the per-page table that
    /// follows describes the steady state a workstation spends its day in.
    /// </remarks>
    private static List<StageCost> MeasureCold(
        string directory,
        string capture,
        ZxingBarcodeDecoder decoder,
        IOcrEngine? ocr,
        byte[]? template)
    {
        var path = Path.Combine(directory, capture);
        if (!File.Exists(path))
        {
            return [];
        }

        using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
        using var page = pdf.OpenPage(0);

        var whole = new RectMm { XMm = 0, YMm = 0, WidthMm = page.WidthMm, HeightMm = page.HeightMm };
        var costs = new List<StageCost>
        {
            Once(capture, "geometry", () => new PageFeatureExtractor().ExtractPage(page, 1, pdf.PageCount)),
            Once(capture, "barcodes", () => decoder.Decode(page)),
        };

        if (ocr is not null)
        {
            costs.Add(Once(capture, "ocr-page", () => ocr.Recognise(page, whole)));
        }

        if (template is not null)
        {
            costs.Add(Once(capture, "template-page", () => TemplateMatcher.Match(page, whole, template, TemplateDpi)));
        }

        return costs;
    }

    private static StageCost Once(string capture, string stage, Func<object?> work)
    {
        var stopwatch = Stopwatch.StartNew();
        work();
        stopwatch.Stop();
        return new StageCost(capture, 1, stage, stopwatch.Elapsed.TotalMilliseconds, "first call");
    }

    private static StageCost Measure(string capture, int pageNumber, string stage, Func<string> work)
    {
        var timings = new List<double>(Repeats);
        var detail = string.Empty;

        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var stopwatch = Stopwatch.StartNew();
            detail = work();
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        return new StageCost(capture, pageNumber, stage, timings[timings.Count / 2], detail);
    }

    private void Report(IReadOnlyList<StageCost> costs, IReadOnlyList<StageCost> cold, string? language)
    {
        output.WriteLine($"OCR recogniser: {language ?? "(none installed)"}");
        output.WriteLine($"median of {Repeats} timed runs per stage, in milliseconds");
        output.WriteLine(string.Empty);

        output.WriteLine("cold, first call after start:");
        foreach (var cost in cold)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  {cost.Stage,-14} {cost.Milliseconds,8:0.0}"));
        }

        output.WriteLine(string.Empty);
        output.WriteLine("per page:");
        foreach (var group in costs.GroupBy(cost => cost.Capture))
        {
            output.WriteLine("  " + group.Key);
            foreach (var page in group.GroupBy(cost => cost.PageNumber).OrderBy(page => page.Key))
            {
                foreach (var cost in page)
                {
                    output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"    p{cost.PageNumber,-2} {cost.Stage,-14} {cost.Milliseconds,8:0.0}  {cost.Detail}"));
                }
            }
        }

        output.WriteLine(string.Empty);
        output.WriteLine("by stage, across every captured page:");
        output.WriteLine($"  {"stage",-14} {"n",3} {"min",8} {"median",8} {"p90",8} {"max",8} {"total",9}");
        foreach (var group in costs.GroupBy(cost => cost.Stage))
        {
            var values = group.Select(cost => cost.Milliseconds).OrderBy(value => value).ToList();
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {group.Key,-14} {values.Count,3} {values[0],8:0.0} {Percentile(values, 0.5),8:0.0} "
                    + $"{Percentile(values, 0.9),8:0.0} {values[^1],8:0.0} {values.Sum(),9:0.0}"));
        }

        var pages = costs.Select(cost => (cost.Capture, cost.PageNumber)).Distinct().Count();
        var alwaysPaid = costs.Where(cost => IsAlwaysPaid(cost.Stage)).Sum(cost => cost.Milliseconds);
        var ocrInk = costs.Where(cost => cost.Stage == "ocr-ink").Sum(cost => cost.Milliseconds);

        output.WriteLine(string.Empty);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{pages} pages: always paid {alwaysPaid:0} ms ({alwaysPaid / pages:0.0} ms/page); "
                + $"OCR of the ink box on every one of them a further {ocrInk:0} ms ({ocrInk / pages:0.0} ms/page)"));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        var index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>Writes the raw measurements where PRINTO_COST_REPORT points, for the plan's table.</summary>
    private static void Persist(IReadOnlyList<StageCost> costs, IReadOnlyList<StageCost> cold, string? language)
    {
        var path = Environment.GetEnvironmentVariable("PRINTO_COST_REPORT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var report = new
        {
            recogniser = language,
            repeats = Repeats,
            cold,
            pages = costs,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static byte[]? LoadTemplate()
    {
        var path = Path.Combine(RepositoryPaths.Root!, "tests", "templates", "console-cut-logo.png");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static string Describe(RectMm? rect) => rect is null
        ? "(none)"
        : string.Create(CultureInfo.InvariantCulture, $"{rect.WidthMm:0.#}x{rect.HeightMm:0.#}mm");

    private static string Describe(TemplateMatchResult? match) => match is null
        ? "(not attempted)"
        : string.Create(CultureInfo.InvariantCulture, $"score {match.Score:0.000} at {Describe(match.Rect)}");

    private static IReadOnlyList<CaptureFixture> Manifest()
    {
        var directory = RepositoryPaths.Captures;
        if (directory is null)
        {
            return [];
        }

        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<CaptureFixture>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
}
