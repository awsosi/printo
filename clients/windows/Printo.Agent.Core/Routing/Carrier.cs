using System.Text.RegularExpressions;

namespace Printo.Agent.Core.Routing;

/// <summary>Where a piece of carrier evidence was found. Sets the weight ceiling.</summary>
public enum CarrierSignalSource
{
    BarcodeValue,
    BarcodeSymbology,
    Text,
    Ocr,
}

public sealed class CarrierSignal
{
    public CarrierSignalSource Source { get; init; }

    /// <summary>Regex source, or an exact symbology name for <see cref="CarrierSignalSource.BarcodeSymbology"/>.</summary>
    public string Pattern { get; init; } = string.Empty;

    public double Weight { get; init; }

    /// <summary>Short human-readable name used in the trace.</summary>
    public string Label { get; init; } = string.Empty;
}

public sealed class CarrierSignatureSet
{
    public string Carrier { get; init; } = string.Empty;

    public IReadOnlyList<CarrierSignal> Signals { get; init; } = [];
}

/// <summary>
/// Carrier resolution. Mirrors <c>packages/routing-engine/src/carrier.ts</c>.
/// </summary>
/// <remarks>
/// This is its own scored step rather than a keyword match inside a rule, because keyword
/// soup is exactly what breaks today: every DHL MyDHL label in the corpus carries the literal
/// string <c>*GLS certified label*</c>, and a bare <c>\bgls\b</c> therefore mis-attributes 278
/// pages to GLS. The fix is structural, not a bigger keyword list — evidence is weighted by
/// source, the certified-label footer is registered as the DHL artifact it is, the bare GLS
/// keyword only counts outside that phrase, and every score is reported so a near-miss is
/// visible in the trace rather than silently resolved.
/// </remarks>
public static class CarrierResolver
{
    /// <summary>Two carriers scoring within this of each other is reported as ambiguous.</summary>
    private const double AmbiguityMargin = 0.15;

    private static readonly Dictionary<string, Regex> RegexCache = [];

    private static readonly Lock CacheLock = new();

    /// <summary>Built-in carrier signatures; deployments extend these from the bundle.</summary>
    public static IReadOnlyList<CarrierSignatureSet> Builtin { get; } =
    [
        new CarrierSignatureSet
        {
            Carrier = "DHL",
            Signals =
            [
                new() { Source = CarrierSignalSource.BarcodeValue, Pattern = @"^JD\d{16,20}$", Weight = 0.95, Label = "waybill JD number" },
                new() { Source = CarrierSignalSource.BarcodeValue, Pattern = @"^JJD\d{16,20}$", Weight = 0.95, Label = "waybill JJD number" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"EXPRESS\s*WORLDWIDE", Weight = 0.6, Label = "EXPRESS WORLDWIDE" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"ECONOMY\s*SELECT", Weight = 0.6, Label = "ECONOMY SELECT" },
                new() { Source = CarrierSignalSource.Text, Pattern = "MyDHL", Weight = 0.6, Label = "MyDHL" },

                // Only ever printed by MyDHL, on DHL stock. Registering it here is what stops
                // it being read as a GLS shipment.
                new() { Source = CarrierSignalSource.Text, Pattern = @"GLS\s*certified\s*label", Weight = 0.5, Label = "MyDHL certified-label footer" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"\bWAYBILL\b", Weight = 0.3, Label = "WAYBILL" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"\bDHL\b", Weight = 0.35, Label = "DHL" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"EXPRESS\s*WORLDWIDE", Weight = 0.55, Label = "EXPRESS WORLDWIDE (ocr)" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"ECONOMY\s*SELECT", Weight = 0.55, Label = "ECONOMY SELECT (ocr)" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = "MyDHL", Weight = 0.55, Label = "MyDHL (ocr)" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"\bDHL\b", Weight = 0.3, Label = "DHL (ocr)" },
            ],
        },
        new CarrierSignatureSet
        {
            Carrier = "UPS",
            Signals =
            [
                new() { Source = CarrierSignalSource.BarcodeValue, Pattern = "^1Z[0-9A-Z]{16}$", Weight = 0.95, Label = "1Z tracking number" },
                new() { Source = CarrierSignalSource.BarcodeSymbology, Pattern = "MaxiCode", Weight = 0.7, Label = "MaxiCode" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"United\s*Parcel", Weight = 0.6, Label = "United Parcel Service" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"\b1Z\s?[0-9A-Z]{3}\s?[0-9A-Z]{3}\s?[0-9A-Z]{2}\s?\d{4}\s?\d{4}\b", Weight = 0.7, Label = "1Z tracking number (text)" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"ups\.com", Weight = 0.5, Label = "ups.com" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"\bUPS\b", Weight = 0.35, Label = "UPS" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"\bUPS\b", Weight = 0.3, Label = "UPS (ocr)" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"United\s*Parcel", Weight = 0.55, Label = "United Parcel Service (ocr)" },
            ],
        },
        new CarrierSignatureSet
        {
            Carrier = "FEDEX",
            Signals =
            [
                new() { Source = CarrierSignalSource.BarcodeValue, Pattern = @"^\[\)>.*?FDE", Weight = 0.9, Label = "FedEx PDF417 header" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"Fed\s?Ex", Weight = 0.6, Label = "FedEx" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"\bTRK#", Weight = 0.4, Label = "TRK#" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"BILL\s+SENDER", Weight = 0.35, Label = "BILL SENDER" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"Fed\s?Ex", Weight = 0.55, Label = "FedEx (ocr)" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"\bTRK#", Weight = 0.35, Label = "TRK# (ocr)" },
            ],
        },
        new CarrierSignatureSet
        {
            Carrier = "GLS",
            Signals =
            [
                // Deliberately guarded: `*GLS certified label*` is DHL's, not GLS's.
                new() { Source = CarrierSignalSource.Text, Pattern = @"\bGLS\b(?!\s*certified)", Weight = 0.3, Label = "GLS" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"General\s+Logistics\s+Systems", Weight = 0.7, Label = "General Logistics Systems" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"gls-group\.", Weight = 0.6, Label = "gls-group" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"General\s+Logistics\s+Systems", Weight = 0.65, Label = "General Logistics Systems (ocr)" },
            ],
        },
        new CarrierSignatureSet
        {
            Carrier = "DPD",
            Signals =
            [
                new() { Source = CarrierSignalSource.Text, Pattern = @"\bDPD\b", Weight = 0.4, Label = "DPD" },
                new() { Source = CarrierSignalSource.Text, Pattern = @"dpd\.(com|de|pl)", Weight = 0.6, Label = "dpd domain" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"\bDPD\b", Weight = 0.35, Label = "DPD (ocr)" },
            ],
        },
        new CarrierSignatureSet
        {
            Carrier = "INPOST",
            Signals =
            [
                new() { Source = CarrierSignalSource.Text, Pattern = "InPost", Weight = 0.6, Label = "InPost" },
                new() { Source = CarrierSignalSource.Text, Pattern = "Paczkomat", Weight = 0.6, Label = "Paczkomat" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = "InPost", Weight = 0.55, Label = "InPost (ocr)" },
            ],
        },
        new CarrierSignatureSet
        {
            Carrier = "POCZTA_POLSKA",
            Signals =
            [
                new() { Source = CarrierSignalSource.Text, Pattern = @"Poczta\s+Polska", Weight = 0.7, Label = "Poczta Polska" },
                new() { Source = CarrierSignalSource.Text, Pattern = "Pocztex", Weight = 0.6, Label = "Pocztex" },
                new() { Source = CarrierSignalSource.Ocr, Pattern = @"Poczta\s+Polska", Weight = 0.65, Label = "Poczta Polska (ocr)" },
            ],
        },
    ];

    internal static Regex? Compile(string pattern, bool caseSensitive = false)
    {
        var key = (caseSensitive ? "s:" : "i:") + pattern;
        lock (CacheLock)
        {
            if (RegexCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        Regex? compiled;
        try
        {
            var options = RegexOptions.CultureInvariant;
            if (!caseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            compiled = new Regex(pattern, options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            compiled = null;
        }

        lock (CacheLock)
        {
            RegexCache[key] = compiled!;
        }

        return compiled;
    }

    /// <summary>
    /// Scores every carrier against the page and returns the best, with all evidence. Scores
    /// saturate rather than sum without bound: three weak keywords must not out-vote one
    /// decoded waybill number.
    /// </summary>
    public static CarrierResolution Resolve(
        PageFeatures page,
        IReadOnlyList<CarrierSignatureSet>? signatures = null)
    {
        signatures ??= Builtin;
        var text = page.Text ?? string.Empty;
        var ocr = page.OcrRegions is null
            ? string.Empty
            : string.Join("\n", page.OcrRegions.Select(region => region.Text));

        var scored = new List<(string Carrier, double Score, List<CarrierEvidence> Evidence)>();

        foreach (var signature in signatures)
        {
            var evidence = new List<CarrierEvidence>();
            var best = 0.0;
            var total = 0.0;

            foreach (var signal in signature.Signals)
            {
                bool hit;

                if (signal.Source == CarrierSignalSource.BarcodeSymbology)
                {
                    hit = page.Barcodes.Any(barcode =>
                        string.Equals(barcode.Symbology, signal.Pattern, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var regex = Compile(signal.Pattern);
                    if (regex is null)
                    {
                        continue;
                    }

                    hit = signal.Source switch
                    {
                        CarrierSignalSource.BarcodeValue => page.Barcodes.Any(barcode => regex.IsMatch(barcode.Value)),
                        CarrierSignalSource.Text => text.Length > 0 && regex.IsMatch(text),
                        _ => ocr.Length > 0 && regex.IsMatch(ocr),
                    };
                }

                if (!hit)
                {
                    continue;
                }

                evidence.Add(new CarrierEvidence
                {
                    Source = signal.Source is CarrierSignalSource.BarcodeValue or CarrierSignalSource.BarcodeSymbology
                        ? "barcode"
                        : signal.Source == CarrierSignalSource.Text ? "text" : "ocr",
                    Detail = signal.Label,
                    Weight = signal.Weight,
                });
                best = Math.Max(best, signal.Weight);
                total += signal.Weight;
            }

            if (evidence.Count > 0)
            {
                // The strongest single piece of evidence dominates; corroboration adds a little.
                var score = Math.Min(1, best + ((total - best) * 0.25));
                scored.Add((signature.Carrier, score, evidence));
            }
        }

        if (scored.Count == 0)
        {
            return new CarrierResolution();
        }

        scored.Sort((left, right) => right.Score.CompareTo(left.Score));
        var winner = scored[0];
        var ambiguous = scored.Count > 1 && winner.Score - scored[1].Score < AmbiguityMargin;

        return new CarrierResolution
        {
            Carrier = winner.Carrier,

            // An unresolved tie is reported as low confidence rather than as a coin toss.
            Confidence = ambiguous ? Math.Min(winner.Score, 0.5) : winner.Score,
            Evidence = winner.Evidence,
            Scores = scored
                .Select(entry => new CarrierScore
                {
                    Carrier = entry.Carrier,
                    Score = Math.Round(entry.Score, 3),
                })
                .ToList(),
        };
    }
}
