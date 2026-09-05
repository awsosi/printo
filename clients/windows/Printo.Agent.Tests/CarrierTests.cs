using Printo.Agent.Core.Routing;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Carrier resolution, mirroring <c>packages/routing-engine/tests/carrier.test.ts</c>.
/// </summary>
public sealed class CarrierTests
{
    private static PageFeatures Page(
        string? text = null,
        IReadOnlyList<DetectedBarcode>? barcodes = null,
        IReadOnlyList<OcrRegion>? ocr = null) => new()
        {
            PageNumber = 1,
            PageCount = 1,
            PageWidthMm = 297,
            PageHeightMm = 210,
            Orientation = PageOrientation.Landscape,
            Rotation = 0,
            Text = text,
            InkBox = new InkBox { XMm = 34, YMm = 15, WidthMm = 92, HeightMm = 180, Aspect = 1.96, Coverage = 0.065 },
            Barcodes = barcodes ?? [],
            OcrRegions = ocr,
        };

    [Fact]
    public void DoesNotReadADhlLabelAsGlsBecauseOfTheCertifiedLabelFooter()
    {
        // This exact string appears on every MyDHL label in the corpus.
        var resolution = CarrierResolver.Resolve(Page(
            "EXPRESS WORLDWIDE\n2026-08-20 MyDHL API 1.0 / *GLS certified label* WPX\nFrom : CTD - see data"));

        Assert.Equal("DHL", resolution.Carrier);
        Assert.DoesNotContain(resolution.Scores, score => score.Carrier == "GLS");
    }

    [Fact]
    public void AttributesADomesticExpressLabelToDhl()
    {
        // The page that actually broke: none of the old worker's DHL patterns matched it,
        // so it fell through to the GLS keyword inside the certified-label footer.
        var resolution = CarrierResolver.Resolve(Page(
            "DOMESTIC EXPRESS\n2026-08-19 MyDHL API 1.0 / *GLS certified label* DOM\nWAYBILL"));

        Assert.Equal("DHL", resolution.Carrier);
    }

    [Fact]
    public void StillRecognisesAGenuineGlsShipment()
    {
        var resolution = CarrierResolver.Resolve(Page(
            "General Logistics Systems Germany GmbH\nGLS ParcelShop\nwww.gls-group.eu"));

        Assert.Equal("GLS", resolution.Carrier);
    }

    [Fact]
    public void ADecodedWaybillNumberOutranksABareKeyword()
    {
        var resolution = CarrierResolver.Resolve(Page(
            "Ref No: collected by UPS driver",
            [
                new DetectedBarcode
                {
                    Symbology = "Code128",
                    Value = "JD014600009354770923",
                    XMm = 10, YMm = 150, WidthMm = 70, HeightMm = 15,
                },
            ]));

        Assert.Equal("DHL", resolution.Carrier);
        Assert.True(resolution.Confidence >= 0.9);
        Assert.Contains(resolution.Evidence, evidence => evidence.Source == "barcode");
    }

    [Fact]
    public void ResolvesUpsFromAMaxiCodePlusA1ZTrackingNumber()
    {
        var resolution = CarrierResolver.Resolve(Page(
            barcodes:
            [
                new DetectedBarcode { Symbology = "MaxiCode", Value = "[)>", XMm = 10, YMm = 10, WidthMm = 25, HeightMm = 25 },
                new DetectedBarcode { Symbology = "Code128", Value = "1Z7273X60155210490", XMm = 10, YMm = 60, WidthMm = 70, HeightMm = 15 },
            ]));

        Assert.Equal("UPS", resolution.Carrier);
        Assert.True(resolution.Confidence >= 0.9);
    }

    [Fact]
    public void ReadsCarrierEvidenceFromOcrWhenThereIsNoTextLayer()
    {
        var resolution = CarrierResolver.Resolve(Page(
            ocr:
            [
                new OcrRegion
                {
                    Key = "34.0,15.0,92.0,180.0",
                    Rect = new RectMm { XMm = 34, YMm = 15, WidthMm = 92, HeightMm = 180 },
                    Text = "ECONOMY SELECT\nCTD see data\nWAYBILL 83 0121 8004",
                },
            ]));

        Assert.Equal("DHL", resolution.Carrier);
    }

    [Fact]
    public void ReportsNoCarrierRatherThanGuessing()
    {
        var resolution = CarrierResolver.Resolve(Page("Sales Invoice\nTotal amount 177.62"));

        Assert.Null(resolution.Carrier);
        Assert.Equal(0, resolution.Confidence);
    }

    [Fact]
    public void LowersConfidenceWhenTwoCarriersScoreCloseTogether()
    {
        var resolution = CarrierResolver.Resolve(Page(
            "Handled by DPD on behalf of GLS ParcelShop network"));

        Assert.True(resolution.Confidence <= 0.5);
        Assert.True(resolution.Scores.Count > 1);
    }
}
