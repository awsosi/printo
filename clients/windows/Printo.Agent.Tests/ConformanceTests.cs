using System.Globalization;
using System.Text.Json;
using Printo.Agent.Core.Routing;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Runs every shared conformance fixture through the C# engine.
/// </summary>
/// <remarks>
/// The same files are run by the TypeScript engine in
/// <c>packages/routing-engine/tests/conformance.test.ts</c>. When the two disagree the build
/// fails on both sides, which is the point: a workstation and the server that route the same
/// page differently is a defect nobody would otherwise notice until a parcel shipped with the
/// wrong label on it.
/// </remarks>
public sealed class ConformanceTests
{
    public static TheoryData<string> FixtureFiles()
    {
        var data = new TheoryData<string>();
        var directory = RepositoryPaths.Conformance;
        if (directory is null)
        {
            // xunit rejects an empty theory, so a checkout without the shared fixtures still
            // runs one case, which asserts nothing but keeps the suite reportable.
            data.Add(string.Empty);
            return data;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(directory, file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void FixtureMatches(string relativePath)
    {
        var directory = RepositoryPaths.Conformance;
        if (directory is null || relativePath.Length == 0)
        {
            return;
        }

        var suite = RoutingJson.Deserialize<ConformanceSuite>(
            File.ReadAllText(Path.Combine(directory, relativePath)));

        Assert.NotEmpty(suite.Fixtures);

        foreach (var fixture in suite.Fixtures)
        {
            RunFixture(relativePath, fixture);
        }
    }

    private static void RunFixture(string file, ConformanceFixture fixture)
    {
        var where = $"{file} :: {fixture.Name}" +
            (fixture.Rationale is null ? string.Empty : $" ({fixture.Rationale})");
        var profile = fixture.ResolveProfile();
        var evaluation = RoutingEngine.EvaluateDocument(profile, fixture.Document);

        if (fixture.ExpectNeedsOcr is { Count: > 0 })
        {
            Assert.True(evaluation.NeedsFeatures, $"{where}: expected the engine to request OCR");
            var actual = evaluation.Ocr
                .Select(request => $"{request.PageNumber}:{request.Key}")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var expected = fixture.ExpectNeedsOcr
                .Select(request => $"{request.PageNumber}:{request.Key}")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(expected, actual);

            foreach (var request in fixture.ExpectNeedsOcr.Where(entry => entry.RuleId is not null))
            {
                var match = evaluation.Ocr.FirstOrDefault(entry =>
                    entry.PageNumber == request.PageNumber && entry.Key == request.Key);
                Assert.NotNull(match);
                Assert.Equal(request.RuleId, match!.RuleId);
            }

            return;
        }

        Assert.False(
            evaluation.NeedsFeatures,
            $"{where}: engine asked for OCR the fixture does not supply: " +
            string.Join(", ", evaluation.Ocr.Select(request => $"p{request.PageNumber} {request.Key}")));

        var decision = evaluation.Document!;

        if (fixture.Expect.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (fixture.Expect.TryGetProperty("pages", out var pages))
        {
            foreach (var expected in pages.EnumerateArray())
            {
                AssertPage(where, decision, expected);
            }
        }

        if (fixture.Expect.TryGetProperty("document", out var document))
        {
            AssertDocument(where, decision, document);
        }
    }

    private static void AssertPage(string where, DocumentDecision decision, JsonElement expected)
    {
        var pageNumber = expected.GetProperty("pageNumber").GetInt32();
        var actual = decision.Pages.FirstOrDefault(page => page.PageNumber == pageNumber);
        Assert.True(actual is not null, $"{where}: no decision for page {pageNumber}");
        var page = actual!;
        var at = $"{where} page {pageNumber}";

        if (expected.TryGetProperty("route", out var route))
        {
            Assert.True(
                route.GetString() == page.Route,
                $"{at}: expected route {route.GetString()}, got {page.Route} (rule {page.RuleId ?? "none"})");
        }

        if (expected.TryGetProperty("ruleId", out var ruleId))
        {
            var wanted = ruleId.ValueKind == JsonValueKind.Null ? null : ruleId.GetString();
            Assert.True(wanted == page.RuleId, $"{at}: expected rule {wanted ?? "none"}, got {page.RuleId ?? "none"}");
        }

        if (expected.TryGetProperty("confidence", out var confidence))
        {
            Assert.True(
                Math.Abs(confidence.GetDouble() - page.Confidence) < 1e-9,
                $"{at}: expected confidence {confidence.GetDouble()}, got {page.Confidence}");
        }

        if (expected.TryGetProperty("hold", out var hold))
        {
            Assert.True(hold.GetBoolean() == page.Hold, $"{at}: expected hold {hold.GetBoolean()}, got {page.Hold}");
        }

        if (expected.TryGetProperty("fallbackReason", out var reason))
        {
            var wanted = reason.ValueKind == JsonValueKind.Null ? null : reason.GetString();
            var got = page.Fallback is null ? null : FallbackReasons.ToWire(page.Fallback.Reason);
            Assert.True(wanted == got, $"{at}: expected fallback {wanted ?? "none"}, got {got ?? "none"}");
        }

        if (expected.TryGetProperty("carrier", out var carrier))
        {
            var wanted = carrier.ValueKind == JsonValueKind.Null ? null : carrier.GetString();
            Assert.True(
                wanted == page.Trace.Carrier.Carrier,
                $"{at}: expected carrier {wanted ?? "none"}, got {page.Trace.Carrier.Carrier ?? "none"}");
        }

        if (expected.TryGetProperty("ocrRectsUsed", out var rects))
        {
            var wanted = rects.EnumerateArray()
                .Select(entry => entry.GetString() ?? string.Empty)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var got = page.Trace.OcrRectsUsed.OrderBy(value => value, StringComparer.Ordinal).ToList();
            Assert.True(
                wanted.SequenceEqual(got, StringComparer.Ordinal),
                $"{at}: expected OCR rects [{string.Join(", ", wanted)}], got [{string.Join(", ", got)}]");
        }
    }

    private static void AssertDocument(string where, DocumentDecision decision, JsonElement expected)
    {
        if (expected.TryGetProperty("fallbackReason", out var reason))
        {
            var wanted = reason.ValueKind == JsonValueKind.Null ? null : reason.GetString();
            var got = decision.Fallback is null ? null : FallbackReasons.ToWire(decision.Fallback.Reason);
            Assert.True(wanted == got, $"{where}: expected document fallback {wanted ?? "none"}, got {got ?? "none"}");
        }

        if (expected.TryGetProperty("candidatePages", out var candidates))
        {
            var wanted = candidates.EnumerateArray().Select(entry => entry.GetInt32()).ToList();
            var got = decision.Fallback?.CandidatePages.ToList() ?? [];
            Assert.True(
                wanted.SequenceEqual(got),
                $"{where}: expected candidate pages [{string.Join(", ", wanted)}], " +
                $"got [{string.Join(", ", got.Select(value => value.ToString(CultureInfo.InvariantCulture)))}]");
        }
    }
}
