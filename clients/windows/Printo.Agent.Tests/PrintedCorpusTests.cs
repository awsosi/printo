using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Printo.Agent.Core.Routing;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The whole corpus as the virtual printer delivers it, through the agent's engine.
/// </summary>
/// <remarks>
/// <para>
/// The file-path corpus runs cannot see the print path: there a page keeps its frame, and the
/// geometry fast paths claim it before a content rule is reached. A return-label rule that sent
/// 148 of 423 outgoing FedEx labels to A4 once printed passed all of them, and passed the seven
/// real captures too, because none of those carries the marking it misread.
/// </para>
/// <para>
/// The TypeScript engine runs the same file in <c>golden-corpus.test.ts</c>, so the server's
/// worker and the workstation are held to the same answer.
/// </para>
/// <para>
/// And every print-dialog variant in <c>tests/corpus/printed-variants</c> - the queue at 203 dpi,
/// the application's scale from 80% to 125%, fitted to the sheet - is held to the same answer
/// too, because routing that changes with those settings is what the operators reported.
/// </para>
/// </remarks>
public sealed class PrintedCorpusTests
{
    public static TheoryData<string, WaybillHandling> FeatureSets()
    {
        var data = new TheoryData<string, WaybillHandling>();
        var sets = new List<string> { "as measured" };
        if (RepositoryPaths.PrintedCorpusVariants is { } variants)
        {
            sets.AddRange(Directory.EnumerateFiles(variants, "*.jsonl.gz")
                .Select(path => Path.GetFileName(path)[..^".jsonl.gz".Length])
                .Order(StringComparer.Ordinal));
        }

        foreach (var set in sets)
        {
            foreach (var handling in new[] { WaybillHandling.Route, WaybillHandling.A4, WaybillHandling.Thermal, WaybillHandling.Skip })
            {
                data.Add(set, handling);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FeatureSets))]
    public void RoutesEveryPrintedPageCorrectlyWithoutAskingAnybody(string set, WaybillHandling handling)
    {
        var featuresPath = set == "as measured"
            ? RepositoryPaths.PrintedCorpusFeatures
            : RepositoryPaths.PrintedCorpusVariants is { } variants ? Path.Combine(variants, set + ".jsonl.gz") : null;
        if (featuresPath is null || !File.Exists(featuresPath) || RepositoryPaths.CorpusExpected is not { } expectedPath)
        {
            return;
        }

        var expected = JsonNode.Parse(File.ReadAllText(expectedPath))!["pages"]!.AsArray()
            .ToDictionary(
                page => (page!["doc"]!.GetValue<string>(), page["pageNumber"]!.GetValue<int>()),
                page => (
                    Class: page!["pageClass"]!.GetValue<string>(),
                    Route: ExpectedRoute(
                        page["route"]!.GetValue<string>(),
                        page["waybill"]?.GetValue<bool>() ?? false,
                        handling)));

        var mismatches = new List<string>();
        var prompted = new List<string>();
        var pages = 0;

        foreach (var document in Load(featuresPath))
        {
            var evaluation = RoutingEngine.EvaluateDocument(
                BuiltinProfiles.OneClickPrint,
                document,
                new EngineOptions { WaybillHandling = handling });
            Assert.False(
                evaluation.NeedsFeatures,
                $"{document.FileName}: the engine asked for something the printed corpus did not record");

            var decision = evaluation.Document!;
            if (decision.Fallback is not null)
            {
                prompted.Add($"{document.FileName}: {FallbackReasons.ToWire(decision.Fallback.Reason)}");
            }

            foreach (var page in decision.Pages)
            {
                pages++;
                var want = expected[(document.FileName, page.PageNumber)];
                if (page.Fallback is not null)
                {
                    prompted.Add(
                        $"{document.FileName} p{page.PageNumber}: {FallbackReasons.ToWire(page.Fallback.Reason)} via {page.RuleId}");
                }

                if (page.Route != want.Route)
                {
                    mismatches.Add(
                        $"{document.FileName} p{page.PageNumber} [{want.Class}]: expected {want.Route}, "
                            + $"got {page.Route} via {page.RuleId ?? "(no rule)"}");
                }
            }
        }

        Assert.Equal(expected.Count, pages);
        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} printed pages misrouted:{Environment.NewLine}{string.Join(Environment.NewLine, mismatches.Take(20))}");
        Assert.True(
            prompted.Count == 0,
            $"{prompted.Count} printed pages asked about:{Environment.NewLine}{string.Join(Environment.NewLine, prompted.Take(20))}");
    }

    /// <summary>
    /// Where a page belongs under a waybill handling: the reviewed ground truth, except that every
    /// handling but Route moves the waybill copies - and nothing else.
    /// </summary>
    private static string ExpectedRoute(string route, bool waybill, WaybillHandling handling) =>
        !waybill || handling == WaybillHandling.Route
            ? route
            : handling switch
            {
                WaybillHandling.A4 => RoutingProfileRules.RouteA4,
                WaybillHandling.Thermal => RoutingProfileRules.RouteThermal,
                _ => RoutingProfileRules.RouteSkip,
            };

    private static List<DocumentFeatures> Load(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        var byDocument = new Dictionary<string, List<PageFeatures>>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var record = JsonNode.Parse(line)!.AsObject();
            var doc = record["doc"]!.GetValue<string>();
            record.Remove("doc");

            var page = record.Deserialize<PageFeatures>(RoutingJson.Options)
                ?? throw new InvalidDataException($"unreadable page in {doc}");

            if (!byDocument.TryGetValue(doc, out var list))
            {
                byDocument[doc] = list = [];
            }

            list.Add(page);
        }

        return byDocument
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new DocumentFeatures
            {
                FileName = entry.Key,
                PageCount = entry.Value.Count,
                Pages = entry.Value.OrderBy(page => page.PageNumber).ToList(),
            })
            .ToList();
    }
}
