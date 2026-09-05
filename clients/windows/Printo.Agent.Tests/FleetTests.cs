using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;
using Printo.Agent.Render;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The agent's half of the fleet API: enrolment, bundle sync, the three decision modes and
/// what happens to each of them when the server is not there.
/// </summary>
/// <remarks>
/// Driven through a scripted <see cref="HttpMessageHandler"/> rather than a live server. The
/// contract being tested here is the agent's behaviour - which endpoint it calls, what it does
/// with each answer, and above all what it does with no answer at all. That the server produces
/// those answers is asserted on the other side, against a real database.
/// </remarks>
public sealed class FleetTests : IDisposable
{
    private readonly string root;

    public FleetTests()
    {
        root = Path.Combine(Path.GetTempPath(), "printo-fleet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Not a test failure.
        }
    }

    // ---------------------------------------------------------------------------------------
    // A scripted server
    // ---------------------------------------------------------------------------------------

    /// <summary>Answers requests from a script, and records what was asked.</summary>
    private sealed class ScriptedServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<string, HttpResponseMessage>> routes = new(StringComparer.Ordinal);

        public List<(string Method, string Path, string Body)> Requests { get; } = [];

        public List<string?> Keys { get; } = [];

        /// <summary>When set, every request fails as though the network were down.</summary>
        public bool Offline { get; set; }

        public ScriptedServer On(string methodAndPath, Func<string, HttpResponseMessage> answer)
        {
            routes[methodAndPath] = answer;
            return this;
        }

        public ScriptedServer OnJson(string methodAndPath, object body, HttpStatusCode status = HttpStatusCode.OK) =>
            On(methodAndPath, _ => Json(body, status));

        public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body, HttpServerClient.Json),
                    Encoding.UTF8,
                    MediaTypeHeaderValue.Parse("application/json")),
            };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("no route to host");
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            Requests.Add((request.Method.Method, path, body));
            Keys.Add(request.Headers.TryGetValues("x-printo-agent-key", out var values)
                ? values.FirstOrDefault()
                : null);

            var key = $"{request.Method.Method} {path}";
            if (routes.TryGetValue(key, out var exact))
            {
                return exact(body);
            }

            // Query strings are part of the request but rarely part of what a test pins.
            var withoutQuery = $"{request.Method.Method} {path.Split('?')[0]}";
            return routes.TryGetValue(withoutQuery, out var loose)
                ? loose(body)
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "{\"error\":\"NO_ROUTE\"}",
                        Encoding.UTF8,
                        MediaTypeHeaderValue.Parse("application/json")),
                };
        }
    }

    private static HttpServerClient Client(ScriptedServer server, string? apiKey = "key-1") =>
        new("https://printo.test/", () => apiKey, server);

    /// <summary>A bundle payload and the checksum the server would have published with it.</summary>
    private static (JsonElement Payload, string Checksum) BundlePayload(params RoutingProfileRules[] profiles)
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                schemaVersion = 1,
                profiles = profiles.Length > 0 ? profiles : [BuiltinProfiles.OneClickPrint],
            },
            HttpServerClient.Json);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            payload.WriteTo(writer);
        }

        return (payload, Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
    }

    // ---------------------------------------------------------------------------------------
    // Enrolment
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EnrolsOnceAndKeepsTheCredentialAcrossRestarts()
    {
        var server = new ScriptedServer()
            .OnJson(
                "POST agents/enroll",
                new { agent = new { id = "agent-1", machineName = "WS-001" }, apiKey = "issued-key" },
                HttpStatusCode.Created)
            .OnJson("POST agents/me/heartbeat", new { ok = true, bundleVersion = (long?)null })
            .On("GET agents/me/bundle", _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var sync = NewSync(server, token: "enrol-token");

        var first = sync.RunOnce();
        Assert.True(first.Enrolled);
        Assert.True(first.HeartbeatSent);
        Assert.Equal("issued-key", sync.CurrentIdentity.ApiKey);

        // A second pass must not enrol again: the token is single-use server-side, and a second
        // attempt would leave the machine with a key the server has already superseded.
        var second = NewSync(server, token: "enrol-token").RunOnce();
        Assert.False(second.Enrolled);
        Assert.True(second.HeartbeatSent);
        Assert.Single(server.Requests, request => request.Path == "agents/enroll");

        // The install id survives, so a restart is the same machine rather than a new agent.
        Assert.Equal(sync.CurrentIdentity.InstallId, NewSync(server).CurrentIdentity.InstallId);
    }

    [Fact]
    public void RunsUnenrolledWithoutATokenAndNeverBlocks()
    {
        var server = new ScriptedServer();
        var sync = NewSync(server, token: null);

        var result = sync.RunOnce();

        Assert.False(result.Enrolled);
        Assert.False(result.HeartbeatSent);
        Assert.Empty(server.Requests);

        // And it still has rules: the profiles the agent shipped with.
        Assert.True(sync.CurrentBundle.IsBuiltin);
        Assert.NotEmpty(sync.CurrentBundle.Profiles);
    }

    [Fact]
    public void DropsACredentialTheServerHasRevoked()
    {
        var server = new ScriptedServer()
            .OnJson(
                "POST agents/enroll",
                new { agent = new { id = "agent-1", machineName = "WS-001" }, apiKey = "issued-key" },
                HttpStatusCode.Created)
            .OnJson("POST agents/me/heartbeat", new { error = "INVALID_AGENT_KEY" }, HttpStatusCode.Unauthorized);

        var sync = NewSync(server, token: "enrol-token");
        var result = sync.RunOnce();

        Assert.NotNull(result.Unreachable);

        // Retrying forever with a key the server has retired would never recover. Dropping it
        // means a freshly issued enrolment token is all an administrator has to do.
        Assert.False(sync.CurrentIdentity.IsEnrolled);
        Assert.NotEmpty(sync.CurrentIdentity.InstallId);
    }

    [Fact]
    public void KeepsTheEnrolmentFileOutOfOrdinaryUsersReach()
    {
        var path = Path.Combine(root, "identity.json");
        new AgentIdentity { InstallId = "abc", ApiKey = "secret", AgentId = "agent-1" }.Save(path);

        Assert.True(File.Exists(path));
        Assert.Contains("secret", File.ReadAllText(path));

        if (OperatingSystem.IsWindows())
        {
            var rules = new FileInfo(path)
                .GetAccessControl()
                .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));

            // ProgramData grants authenticated users read by default, so the inherited rules
            // must be gone rather than merely supplemented. The only accounts left are the
            // system, administrators, and whichever account the agent itself runs as.
            var allowed = new[]
            {
                "S-1-5-18",
                "S-1-5-32-544",
                System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
            };

            Assert.All(
                rules.Cast<System.Security.AccessControl.FileSystemAccessRule>(),
                rule => Assert.Contains(
                    ((System.Security.Principal.SecurityIdentifier)rule.IdentityReference).Value,
                    allowed));

            // And "Users" is not one of them.
            Assert.DoesNotContain(
                rules.Cast<System.Security.AccessControl.FileSystemAccessRule>(),
                rule => ((System.Security.Principal.SecurityIdentifier)rule.IdentityReference)
                    .IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinUsersSid));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Bundle sync
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DownloadsCachesAndReusesTheBundle()
    {
        var (payload, checksum) = BundlePayload();
        var server = Enrolled()
            .OnJson("POST agents/me/heartbeat", new { ok = true, bundleVersion = 7L })
            .OnJson("GET agents/me/bundle", new { version = 7L, payload, checksum });

        var cachePath = Path.Combine(root, "bundle.json");
        var sync = NewSync(server, token: "enrol-token", cachePath: cachePath);

        var first = sync.RunOnce();
        Assert.True(first.BundleUpdated);
        Assert.Equal(7L, sync.CurrentBundle.Version);
        Assert.False(sync.CurrentBundle.IsBuiltin);

        // Told the version on every heartbeat, the agent does not re-download an unchanged one.
        var downloads = server.Requests.Count(request => request.Path.StartsWith("agents/me/bundle"));
        Assert.Equal(1, downloads);

        var again = sync.RunOnce();
        Assert.False(again.BundleUpdated);
        Assert.Equal(downloads, server.Requests.Count(request => request.Path.StartsWith("agents/me/bundle")));

        // A restart reads the cache rather than the network, which is what keeps an offline
        // workstation routing on the published rules.
        var restarted = NewSync(new ScriptedServer(), cachePath: cachePath);
        Assert.Equal(7L, restarted.CurrentBundle.Version);
    }

    [Fact]
    public void RejectsABundleWhoseChecksumDoesNotMatch()
    {
        var (payload, _) = BundlePayload();
        var server = Enrolled()
            .OnJson("POST agents/me/heartbeat", new { ok = true, bundleVersion = 3L })
            .OnJson(
                "GET agents/me/bundle",
                new { version = 3L, payload, checksum = new string('0', 64) });

        var sync = NewSync(server, token: "enrol-token");
        var result = sync.RunOnce();

        Assert.False(result.BundleUpdated);

        // Corruption in transit must not switch the machine to a different rule set mid-shift:
        // it keeps whatever it was routing with.
        Assert.True(sync.CurrentBundle.IsBuiltin);
    }

    [Fact]
    public void CarriesCarrierSignaturesFromTheBundleIntoTheEngine()
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                schemaVersion = 1,
                profiles = new[] { BuiltinProfiles.OneClickPrint },
                carrierSignatures = new[]
                {
                    new
                    {
                        carrier = "POCZTA",
                        signals = new[]
                        {
                            new { source = "text", pattern = "Poczta\\s*Polska", weight = 0.9, label = "Poczta Polska" },
                        },
                    },
                },
            },
            HttpServerClient.Json);

        var cache = new BundleCache(Path.Combine(root, "bundle.json"));
        var stored = cache.Store(new ServerBundle { Version = 4, Payload = payload });

        Assert.Equal(4, stored.Version);
        var signatures = stored.ToEngineOptions().CarrierSignatures;
        Assert.NotNull(signatures);
        Assert.Equal("POCZTA", Assert.Single(signatures!).Carrier);
    }

    // ---------------------------------------------------------------------------------------
    // Decision modes
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void LocalModeDecidesWithoutTouchingTheNetwork()
    {
        var server = new ScriptedServer();
        var decider = new LocalDecider(() => RuleBundle.Builtin);

        var decision = decider.Decide(LabelDocument(), NoOcr.Instance);

        Assert.Equal(DecisionStatus.Decided, decision.Status);
        Assert.Equal("local", decision.DecidedBy);
        Assert.Equal(RoutingProfileRules.RouteThermal, decision.Document!.Pages[0].Route);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void ServerModeSendsFeaturesAndNotTheDocument()
    {
        var features = LabelDocument();
        var server = new ScriptedServer().OnJson(
            "POST agents/me/decide",
            new
            {
                status = "decided",
                bundleVersion = 9L,
                decision = new DocumentDecision
                {
                    Profile = "OneClickPrint",
                    Pages = [new PageDecision { PageNumber = 1, Route = RoutingProfileRules.RouteThermal, Confidence = 1 }],
                },
            });

        using var client = Client(server);
        var decider = new ServerDecider(client) { Bundle = () => RuleBundle.Builtin };

        var decision = decider.Decide(features, NoOcr.Instance);

        Assert.Equal(DecisionStatus.Decided, decision.Status);
        Assert.Equal("server", decision.DecidedBy);
        Assert.Equal(9L, decision.BundleVersion);

        var request = Assert.Single(server.Requests);
        Assert.Equal("agents/me/decide", request.Path);

        // Measured features, not the file: the workstation stays the only place the customer's
        // document is rendered.
        Assert.Contains("\"pageWidthMm\"", request.Body);
        Assert.DoesNotContain("%PDF", request.Body);
        Assert.Equal("key-1", Assert.Single(server.Keys));
    }

    [Fact]
    public void ServerModeAnswersTheServersOcrRequestLocally()
    {
        var answered = false;
        var server = new ScriptedServer().On("POST agents/me/decide", body =>
        {
            if (!answered)
            {
                answered = true;
                return ScriptedServer.Json(new
                {
                    status = "needs-features",
                    ocr = new[] { new { pageNumber = 1, key = "0.0,0.0,10.0,10.0", ruleId = "r", rect = new { xMm = 0.0, yMm = 0.0, widthMm = 10.0, heightMm = 10.0 } } },
                });
            }

            Assert.Contains("secondPass\":true", body.Replace(" ", string.Empty));
            return ScriptedServer.Json(new
            {
                status = "decided",
                decision = new DocumentDecision
                {
                    Profile = "OneClickPrint",
                    Pages = [new PageDecision { PageNumber = 1, Route = RoutingProfileRules.RouteA4, Confidence = 1 }],
                },
            });
        });

        using var client = Client(server);
        var filler = new RecordingOcrFiller();
        var decider = new ServerDecider(client) { Bundle = () => RuleBundle.Builtin };

        var decision = decider.Decide(LabelDocument(), filler);

        Assert.Equal(DecisionStatus.Decided, decision.Status);
        Assert.Equal(RoutingProfileRules.RouteA4, decision.Document!.Pages[0].Route);

        // Only the workstation holds the pixels, so a server that asks is answered from here.
        Assert.Equal(1, filler.Calls);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public void ServerModeRefusesToLoopWhenTheRulesAskForOcrTwice()
    {
        var server = new ScriptedServer().On("POST agents/me/decide", _ => ScriptedServer.Json(new
        {
            status = "needs-features",
            ocr = new[] { new { pageNumber = 1, key = "k", ruleId = "r", rect = new { xMm = 0.0, yMm = 0.0, widthMm = 10.0, heightMm = 10.0 } } },
        }));

        using var client = Client(server);
        var decider = new ServerDecider(client) { Bundle = () => RuleBundle.Builtin };

        var decision = decider.Decide(LabelDocument(), new RecordingOcrFiller());

        Assert.Equal(DecisionStatus.RulesAskedOcrTwice, decision.Status);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public void ServerModeReportsAnUnreachableServerRatherThanThrowing()
    {
        var server = new ScriptedServer { Offline = true };
        using var client = Client(server);
        var decider = new ServerDecider(client) { Bundle = () => RuleBundle.Builtin };

        var decision = decider.Decide(LabelDocument(), NoOcr.Instance);

        Assert.Equal(DecisionStatus.ServerUnavailable, decision.Status);
        Assert.Null(decision.Document);
    }

    [Fact]
    public void AutoModeKeepsAConfidentLocalAnswerOffTheNetwork()
    {
        var server = new ScriptedServer();
        using var client = Client(server);

        var decider = new AutoDecider(
            new LocalDecider(() => RuleBundle.Builtin),
            new ServerDecider(client) { Bundle = () => RuleBundle.Builtin },
            confidenceThreshold: 0.75);

        var decision = decider.Decide(LabelDocument(), NoOcr.Instance);

        Assert.Equal(DecisionStatus.Decided, decision.Status);
        Assert.Equal("local", decision.DecidedBy);
        Assert.False(decision.Degraded);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void AutoModeEscalatesAnUncertainDocumentToTheServer()
    {
        var server = new ScriptedServer().OnJson(
            "POST agents/me/decide",
            new
            {
                status = "decided",
                bundleVersion = 11L,
                decision = new DocumentDecision
                {
                    Profile = "OneClickPrint",
                    Pages = [new PageDecision { PageNumber = 1, Route = RoutingProfileRules.RouteThermal, Confidence = 1 }],
                },
            });

        using var client = Client(server);
        var decider = new AutoDecider(
            new LocalDecider(() => RuleBundle.Builtin),
            new ServerDecider(client) { Bundle = () => RuleBundle.Builtin },
            confidenceThreshold: 0.75);

        // An unremarkable A4 page takes the profile default at 0.6 confidence, below the
        // threshold, which is exactly the case `auto` exists to escalate.
        var decision = decider.Decide(PlainDocument(), NoOcr.Instance);

        Assert.Equal("server", decision.DecidedBy);
        Assert.Equal(11L, decision.BundleVersion);
        Assert.Single(server.Requests);
    }

    [Fact]
    public void AutoModeFallsBackToTheLocalAnswerWhenTheServerIsDown()
    {
        var server = new ScriptedServer { Offline = true };
        using var client = Client(server);

        var decider = new AutoDecider(
            new LocalDecider(() => RuleBundle.Builtin),
            new ServerDecider(client) { Bundle = () => RuleBundle.Builtin },
            confidenceThreshold: 0.75);

        var decision = decider.Decide(PlainDocument(), NoOcr.Instance);

        // The document still prints - that is the whole point of `auto` - but the machine says
        // it is running on cached rules, so the drift is visible rather than silent.
        Assert.Equal(DecisionStatus.Decided, decision.Status);
        Assert.Equal("local", decision.DecidedBy);
        Assert.True(decision.Degraded);
        Assert.Contains("server unreachable", decision.Detail);
    }

    [Fact]
    public void AutoModePrefersTheServersVerdictOverRetryingLocally()
    {
        var server = new ScriptedServer().OnJson("POST agents/me/decide", new { status = "no-profile" });
        using var client = Client(server);

        var decider = new AutoDecider(
            new LocalDecider(() => RuleBundle.Builtin),
            new ServerDecider(client) { Bundle = () => RuleBundle.Builtin },
            confidenceThreshold: 0.75);

        var decision = decider.Decide(PlainDocument(), NoOcr.Instance);

        // A real verdict, not a failure to answer: retrying locally would only disagree.
        Assert.Equal(DecisionStatus.NoProfile, decision.Status);
        Assert.Equal("server", decision.DecidedBy);
    }

    // ---------------------------------------------------------------------------------------
    // Reporting
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ReportsWhatTheEngineProposedAndWhatThePersonChose()
    {
        var job = new SpoolJob
        {
            Id = 1,
            JobKey = "folder:abc",
            Source = JobSource.HotFolder,
            FileName = "OneClickPrint_TEST.pdf",
            DocumentSha256 = "abc",
            PayloadPath = "unused",
            PageCount = 2,
        };

        var result = new JobProcessingResult
        {
            Outcome = JobOutcome.Printed,
            Decision = new DocumentDecision
            {
                Profile = "OneClickPrint",
                Pages =
                [
                    new PageDecision { PageNumber = 1, Route = RoutingProfileRules.RouteA4, Confidence = 0.6 },
                    new PageDecision { PageNumber = 2, Route = RoutingProfileRules.RouteThermal, Confidence = 1 },
                ],
            },
            Prompt = new FallbackPrompt
            {
                ReasonCode = "AMBIGUOUS",
                Message = "two pages qualified",
                SuggestedThermalPages = [2],
                PageCount = 2,
                TraceJson = "{\"profile\":\"OneClickPrint\"}",
            },
            BundleVersion = 5,
        };

        var report = JobReporter.Build(
            job,
            result,
            new FallbackAnswer { Selection = new HashSet<int> { 2 }, Elapsed = TimeSpan.FromSeconds(3) });

        Assert.Equal("folder:abc", report.JobKey);
        Assert.Equal("COMPLETED", report.Status);
        Assert.Equal(5, report.BundleVersion);
        Assert.Equal(2, report.Pages.Count);

        var fallback = report.Fallback!;
        Assert.Equal("AMBIGUOUS", fallback.ReasonCode);

        // Both halves, which is what makes an agreement rate computable at all.
        Assert.Equal([2], fallback.EngineSelection);
        Assert.Equal([2], fallback.UserSelection);
        Assert.Equal("print", fallback.Resolution);
        Assert.Equal(3000, fallback.DecisionMs);
        Assert.NotNull(fallback.Trace);
    }

    [Fact]
    public void ReportsAnUnansweredPickerAndAnAllA4Answer()
    {
        var job = new SpoolJob
        {
            Id = 1,
            JobKey = "k",
            Source = JobSource.VirtualPrinter,
            FileName = "x.pdf",
            DocumentSha256 = "d",
            PayloadPath = "unused",
        };

        var result = new JobProcessingResult
        {
            Outcome = JobOutcome.NeedsUser,
            Decision = null,
            Prompt = new FallbackPrompt
            {
                ReasonCode = "OCR_UNAVAILABLE",
                Message = "no recogniser",
                SuggestedThermalPages = [1],
                PageCount = 1,
                TraceJson = "{}",
            },
        };

        Assert.Equal("unanswered", JobReporter.Build(job, result, new FallbackAnswer()).Fallback!.Resolution);
        Assert.Equal("AWAITING_USER", JobReporter.Build(job, result, null).Status);
        Assert.Equal("VirtualPrinter", JobReporter.Build(job, result, null).Source);

        var allA4 = JobReporter.Build(job, result, new FallbackAnswer { Selection = new HashSet<int>() });
        Assert.Equal("allA4", allA4.Fallback!.Resolution);
        Assert.Empty(allA4.Fallback.UserSelection!);
    }

    [Fact]
    public void ReportsADegradedPrintEvenThoughNobodyWasAsked()
    {
        var job = new SpoolJob
        {
            Id = 1,
            JobKey = "k",
            Source = JobSource.HotFolder,
            FileName = "x.pdf",
            DocumentSha256 = "d",
            PayloadPath = "unused",
        };

        var report = JobReporter.Build(
            job,
            new JobProcessingResult
            {
                Outcome = JobOutcome.Printed,
                Decision = new DocumentDecision
                {
                    Profile = "OneClickPrint",
                    Pages = [new PageDecision { PageNumber = 1, Route = RoutingProfileRules.RouteThermal, Confidence = 1 }],
                },
                Degraded = true,
            },
            null);

        // A machine printing all week on a bundle two revisions old is exactly the drift the
        // fallback analytics exist to surface.
        Assert.Equal("SERVER_UNAVAILABLE", report.Fallback!.ReasonCode);
        Assert.Equal([1], report.Fallback.EngineSelection);
        Assert.Equal("print", report.Fallback.Resolution);
    }

    [Fact]
    public void ReportingNeverStopsPrintingWhenTheServerIsDown()
    {
        var server = new ScriptedServer { Offline = true };
        using var client = Client(server);

        var failures = new List<string>();
        var reporter = new JobReporter(client, (code, _) => failures.Add(code));

        var reported = reporter.Report(
            new SpoolJob
            {
                Id = 1,
                JobKey = "k",
                Source = JobSource.HotFolder,
                FileName = "x.pdf",
                DocumentSha256 = "d",
                PayloadPath = "unused",
            },
            new JobProcessingResult { Outcome = JobOutcome.Printed, Decision = null });

        Assert.Null(reported);
        Assert.Equal(["report-failed"], failures);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private FleetSync NewSync(ScriptedServer server, string? token = null, string? cachePath = null) =>
        new(
            new AgentConfiguration { DataDirectory = root, ServerUrl = "https://printo.test/" },
            Path.Combine(root, "identity.json"),
            new BundleCache(cachePath ?? Path.Combine(root, "bundle.json")),
            key => new HttpServerClient("https://printo.test/", key, server))
        {
            EnrolmentToken = token,
        };

    /// <summary>A server that accepts an enrolment, for tests about what happens afterwards.</summary>
    private static ScriptedServer Enrolled() => new ScriptedServer().OnJson(
        "POST agents/enroll",
        new { agent = new { id = "agent-1", machineName = "WS-001" }, apiKey = "issued-key" },
        HttpStatusCode.Created);

    /// <summary>Features for a page the built-in rules route to thermal with confidence.</summary>
    private static DocumentFeatures LabelDocument() => Extract(TestPdf.Build(TestPdf.FedExStyleLabelOnA4Landscape()));

    /// <summary>Features for a page no rule claims, which takes the profile default.</summary>
    private static DocumentFeatures PlainDocument() => Extract(TestPdf.Build(TestPdf.A4Document()));

    private static DocumentFeatures Extract(byte[] pdf)
    {
        using var document = PdfDocument.Load(pdf);
        return new PageFeatureExtractor().Extract(document, "OneClickPrint_TEST.pdf");
    }

    /// <summary>A machine with no recogniser: the OCR gate must refer to a person.</summary>
    private sealed class NoOcr : IOcrFiller
    {
        public static NoOcr Instance { get; } = new();

        public DocumentFeatures? Fill(DocumentFeatures features, IReadOnlyList<OcrRequest> requests) => null;
    }

    /// <summary>Counts the rounds of OCR the engine or the server asked for.</summary>
    private sealed class RecordingOcrFiller : IOcrFiller
    {
        public int Calls { get; private set; }

        public DocumentFeatures? Fill(DocumentFeatures features, IReadOnlyList<OcrRequest> requests)
        {
            Calls++;
            return features;
        }
    }
}
