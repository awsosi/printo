using Printo.Agent.Core.Routing;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Placement maths, mirroring <c>packages/routing-engine/tests/transform.test.ts</c>.
/// </summary>
/// <remarks>
/// The conformance fixtures cover routing decisions but never reach the placement maths,
/// because a fixture stops at "which printer". These assert the part that decides whether the
/// label lands on the stock correctly — the thing a wrong answer only reveals on a physical
/// printer, which is exactly where we cannot test cheaply.
/// </remarks>
public sealed class TransformTests
{
    /// <summary>The measured DHL crop: 92x180mm portrait.</summary>
    private static readonly RectMm DhlCrop = new() { XMm = 33.8, YMm = 15, WidthMm = 92, HeightMm = 180 };

    /// <summary>The measured FedEx crop: a true 4x6in label.</summary>
    private static readonly RectMm FedExCrop = new() { XMm = 34.3, YMm = 19.3, WidthMm = 101.1, HeightMm = 149.9 };

    private static readonly MediaSize Media100X150 = new() { WidthMm = 100, HeightMm = 150 };

    private static readonly MediaSize Media100X200 = new() { WidthMm = 100, HeightMm = 200 };

    [Theory]
    [InlineData("100x150mm", 100, 150)]
    [InlineData("100 x 150", 100, 150)]
    [InlineData("105x148 mm", 105, 148)]
    [InlineData("101.6x152.4mm", 101.6, 152.4)]
    [InlineData("100,5x150", 100.5, 150)]
    [InlineData("A4", 210, 297)]
    [InlineData("letter", 215.9, 279.4)]
    public void ParsesFreeAndNamedMedia(string value, double width, double height)
    {
        var media = MediaSizes.Parse(value);
        Assert.NotNull(media);
        Assert.Equal(width, media!.WidthMm, 6);
        Assert.Equal(height, media.HeightMm, 6);
    }

    [Theory]
    [InlineData("huge")]
    [InlineData("0x0")]
    [InlineData("")]
    [InlineData(null)]
    public void ReturnsNullRatherThanGuessing(string? value) => Assert.Null(MediaSizes.Parse(value));

    [Fact]
    public void FormatsMediaBackToTheCanonicalForm()
    {
        Assert.Equal("100x150mm", MediaSizes.Format(Media100X150));
        Assert.Equal(MediaSizes.DefaultThermal.WidthMm, MediaSizes.Parse(MediaSizes.Format(MediaSizes.DefaultThermal))!.WidthMm);
    }

    [Fact]
    public void MediaPrecedenceTakesTheMostSpecificLayerAndNamesIt()
    {
        var resolved = Placements.ResolveMedia(new MediaResolutionInput
        {
            AgentPrinterMedia = "100x200mm",
            CentralProfileMedia = "100x150mm",
            ProductDefault = MediaSizes.DefaultThermal,
        });

        Assert.Equal(SettingLayer.AgentPrinter, resolved.Layer);
        Assert.Equal(200, resolved.Value.HeightMm, 6);
    }

    [Fact]
    public void RuleMediaOverridesEveryOtherLayer()
    {
        var resolved = Placements.ResolveMedia(new MediaResolutionInput
        {
            RuleMedia = "105x148mm",
            AgentPrinterMedia = "100x200mm",
            CentralPrinterMedia = "100x150mm",
            ProductDefault = MediaSizes.DefaultThermal,
        });

        Assert.Equal(SettingLayer.Rule, resolved.Layer);
        Assert.Equal(105, resolved.Value.WidthMm, 6);
    }

    [Fact]
    public void UnparseableMediaFallsThroughToTheNextLayer()
    {
        var resolved = Placements.ResolveMedia(new MediaResolutionInput
        {
            RuleMedia = "not-a-size",
            CentralProfileMedia = "100x200mm",
            ProductDefault = MediaSizes.DefaultThermal,
        });

        Assert.Equal(SettingLayer.CentralProfile, resolved.Layer);
        Assert.Equal(200, resolved.Value.HeightMm, 6);
    }

    [Fact]
    public void ProductDefaultIsUsedWhenNothingIsConfigured()
    {
        var resolved = Placements.ResolveMedia(new MediaResolutionInput
        {
            ProductDefault = MediaSizes.DefaultThermal,
        });

        Assert.Equal(SettingLayer.ProductDefault, resolved.Layer);
        Assert.Equal(100, resolved.Value.WidthMm, 6);
        Assert.Equal(150, resolved.Value.HeightMm, 6);
    }

    [Fact]
    public void AutoRotationKeepsAPortraitCropUprightOnPortraitStock()
    {
        Assert.Equal(0, Placements.ResolveRotation(RotateSpec.Auto, DhlCrop, Media100X150));
        Assert.Equal(0, Placements.ResolveRotation(RotateSpec.Auto, FedExCrop, Media100X150));
    }

    [Fact]
    public void AutoRotationTurnsAPortraitCropOntoLandscapeStock() =>
        Assert.Equal(90, Placements.ResolveRotation(
            RotateSpec.Auto, DhlCrop, new MediaSize { WidthMm = 200, HeightMm = 100 }));

    [Fact]
    public void ExplicitRotationIsHonoured() =>
        Assert.Equal(180, Placements.ResolveRotation(RotateSpec.Fixed(180), DhlCrop, Media100X150));

    [Fact]
    public void DhlCropScalesDownToFit100X150Centred()
    {
        var placement = Placements.Compute(
            new TransformSpec { Source = RectSpec.InkBox, Rotate = RotateSpec.Auto, Fit = "contain" },
            DhlCrop,
            Media100X150);

        // Height binds: 150/180 = 0.8333.
        Assert.Equal(0, placement.Rotation);
        Assert.Equal(150.0 / 180.0, placement.ScaleX, 9);
        Assert.Equal(150, placement.Destination.HeightMm, 6);
        Assert.Equal(92 * (150.0 / 180.0), placement.Destination.WidthMm, 6);
        Assert.Equal((100 - (92 * (150.0 / 180.0))) / 2, placement.Destination.XMm, 6);
        Assert.Equal(0, placement.Destination.YMm, 6);
        Assert.True(placement.Reduced);
        Assert.False(placement.Clipped);
    }

    [Fact]
    public void DhlCropLandsNearOneToOneOn100X200()
    {
        var placement = Placements.Compute(
            new TransformSpec { Source = RectSpec.InkBox, Rotate = RotateSpec.Auto, Fit = "contain" },
            DhlCrop,
            Media100X200);

        Assert.Equal(100.0 / 92.0, placement.ScaleX, 9);
        Assert.Equal(100, placement.Destination.WidthMm, 6);
        Assert.False(placement.Clipped);
    }

    [Fact]
    public void FedExCropFits100X150WithAlmostNoReduction()
    {
        var placement = Placements.Compute(
            new TransformSpec { Rotate = RotateSpec.Auto, Fit = "contain" },
            FedExCrop,
            Media100X150);

        Assert.Equal(Math.Min(100 / 101.1, 150 / 149.9), placement.ScaleX, 9);
        Assert.True(placement.ScaleX > 0.98);
        Assert.False(placement.Clipped);
    }

    [Fact]
    public void CoverFillsTheMediaAndReportsTheOverflow()
    {
        var placement = Placements.Compute(
            new TransformSpec { Fit = "cover", Rotate = RotateSpec.Fixed(0) },
            DhlCrop,
            Media100X150);

        Assert.Equal(Math.Max(100 / 92.0, 150 / 180.0), placement.ScaleX, 9);
        Assert.True(placement.Clipped);
    }

    [Fact]
    public void ActualKeepsOneToOneRegardlessOfMedia()
    {
        var placement = Placements.Compute(
            new TransformSpec { Fit = "actual", Rotate = RotateSpec.Fixed(0) },
            DhlCrop,
            Media100X200);

        Assert.Equal(1, placement.ScaleX, 9);
        Assert.Equal(92, placement.Destination.WidthMm, 6);
        Assert.Equal(180, placement.Destination.HeightMm, 6);
    }

    [Fact]
    public void StretchFillsBothAxesIndependently()
    {
        var placement = Placements.Compute(
            new TransformSpec { Fit = "stretch", Rotate = RotateSpec.Fixed(0) },
            DhlCrop,
            Media100X150);

        Assert.Equal(100 / 92.0, placement.ScaleX, 9);
        Assert.Equal(150 / 180.0, placement.ScaleY, 9);
    }

    [Fact]
    public void ZoomAndPanApplyOnTopOfTheFit()
    {
        var placement = Placements.Compute(
            new TransformSpec { Fit = "contain", Rotate = RotateSpec.Fixed(0), ZoomPercent = 50, PanXMm = 5, PanYMm = -3 },
            DhlCrop,
            Media100X150);

        Assert.Equal(150.0 / 180.0 * 0.5, placement.ScaleX, 9);
        var width = 92 * (150.0 / 180.0) * 0.5;
        Assert.Equal(((100 - width) / 2) + 5, placement.Destination.XMm, 6);
    }
}
