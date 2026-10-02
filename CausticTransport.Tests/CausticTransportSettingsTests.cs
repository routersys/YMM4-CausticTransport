using ComputeWeave;

namespace CausticTransport.Tests;

public sealed class CausticTransportSettingsTests
{
    public static readonly TheoryData<CausticTransportQuality> Qualities = [CausticTransportQuality.Balanced, CausticTransportQuality.High, CausticTransportQuality.Ultra];

    [Theory]
    [InlineData(CausticTransportQuality.Balanced, 128, 4, 12)]
    [InlineData(CausticTransportQuality.High, 192, 6, 16)]
    [InlineData(CausticTransportQuality.Ultra, 256, 8, 20)]
    public void EachQualityHasItsGridResolutionAndIterations(CausticTransportQuality quality, int resolution, int transport, int jacobi)
    {
        var settings = CausticTransportSettings.GetQuality(quality);

        Assert.Equal(resolution, settings.GridResolution);
        Assert.Equal(transport, settings.TransportIterations);
        Assert.Equal(jacobi, settings.JacobiIterations);
    }

    [Fact]
    public void AnUnknownQualityFallsBackToHigh()
    {
        var unknown = CausticTransportSettings.GetQuality((CausticTransportQuality)99);

        Assert.Equal(CausticTransportSettings.GetQuality(CausticTransportQuality.High), unknown);
    }

    [Fact]
    public void ABetterQualitySolvesOnAFinerGridWithMoreIterations()
    {
        var balanced = CausticTransportSettings.GetQuality(CausticTransportQuality.Balanced);
        var high = CausticTransportSettings.GetQuality(CausticTransportQuality.High);
        var ultra = CausticTransportSettings.GetQuality(CausticTransportQuality.Ultra);

        Assert.True(balanced.GridResolution < high.GridResolution && high.GridResolution < ultra.GridResolution);
        Assert.True(balanced.TransportIterations < high.TransportIterations && high.TransportIterations < ultra.TransportIterations);
        Assert.True(balanced.JacobiIterations < high.JacobiIterations && high.JacobiIterations < ultra.JacobiIterations);
    }

    [Theory]
    [InlineData(1920, 1080, 192, 192, 108)]
    [InlineData(1080, 1920, 192, 108, 192)]
    [InlineData(1000, 333, 192, 192, 64)]
    [InlineData(333, 1000, 192, 64, 192)]
    [InlineData(101, 100, 256, 101, 100)]
    [InlineData(100, 100, 256, 100, 100)]
    [InlineData(500, 500, 192, 192, 192)]
    [InlineData(8, 8, 192, 8, 8)]
    [InlineData(2, 2, 192, 4, 4)]
    [InlineData(4096, 16, 128, 128, 4)]
    [InlineData(16, 4096, 128, 4, 128)]
    [InlineData(8192, 1, 256, 256, 4)]
    public void TheGridKeepsTheAspectOfTheFrameWithinItsBounds(int width, int height, int resolution, int expectedWidth, int expectedHeight)
    {
        var (gridWidth, gridHeight) = CausticTransportSettings.GetGridSize(width, height, resolution);

        Assert.Equal(expectedWidth, gridWidth);
        Assert.Equal(expectedHeight, gridHeight);
    }

    [Theory]
    [MemberData(nameof(Qualities))]
    public void TheGridNeverExceedsTheResolutionOfTheQuality(CausticTransportQuality quality)
    {
        var resolution = CausticTransportSettings.GetQuality(quality).GridResolution;

        foreach (var (width, height) in new[] { (8192, 8192), (8192, 1), (1, 8192), (3840, 2160), (7, 5), (5, 7) })
        {
            var (gridWidth, gridHeight) = CausticTransportSettings.GetGridSize(width, height, resolution);

            Assert.InRange(gridWidth, CausticTransportSettings.MinimumGridSize, resolution);
            Assert.InRange(gridHeight, CausticTransportSettings.MinimumGridSize, resolution);
        }
    }

    [Theory]
    [InlineData(4, 4, 1)]
    [InlineData(8, 8, 1)]
    [InlineData(16, 16, 1)]
    [InlineData(17, 4, 2)]
    [InlineData(4, 17, 2)]
    [InlineData(17, 17, 2)]
    [InlineData(32, 32, 2)]
    [InlineData(33, 33, 3)]
    [InlineData(192, 108, 5)]
    [InlineData(256, 256, 5)]
    [InlineData(4, 128, 4)]
    [InlineData(128, 4, 4)]
    public void TheLevelsHalveTheGridDownToTheCoarsestSize(int gridWidth, int gridHeight, int expected)
    {
        Assert.Equal(expected, CausticTransportSettings.GetLevelCount(gridWidth, gridHeight));
    }

    [Theory]
    [InlineData(192, 108, 96, 54)]
    [InlineData(7, 5, 4, 4)]
    [InlineData(5, 128, 4, 64)]
    [InlineData(4, 4, 4, 4)]
    [InlineData(255, 255, 128, 128)]
    public void ACoarserLevelHalvesRoundingUpWithoutPassingTheMinimum(int width, int height, int expectedWidth, int expectedHeight)
    {
        var (coarserWidth, coarserHeight) = CausticTransportSettings.GetCoarserLevelSize(width, height);

        Assert.Equal(expectedWidth, coarserWidth);
        Assert.Equal(expectedHeight, coarserHeight);
    }

    [Theory]
    [InlineData(0, 192, 108)]
    [InlineData(1, 96, 54)]
    [InlineData(2, 48, 27)]
    [InlineData(3, 24, 14)]
    [InlineData(4, 12, 7)]
    public void ALevelSizeIsTheGridHalvedOncePerLevel(int level, int expectedWidth, int expectedHeight)
    {
        var (width, height) = CausticTransportSettings.GetLevelSize(192, 108, level);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Theory]
    [MemberData(nameof(Qualities))]
    public void EveryLevelOfEverySupportedFrameFitsTheLevelsTheResourcesHold(CausticTransportQuality quality)
    {
        var resolution = CausticTransportSettings.GetQuality(quality).GridResolution;

        foreach (var (width, height) in new[] { (8192, 8192), (8192, 1), (1, 8192), (1920, 1080), (1080, 1920), (4096, 16), (16, 4096), (17, 17), (1, 1) })
        {
            var (gridWidth, gridHeight) = CausticTransportSettings.GetGridSize(width, height, resolution);

            Assert.InRange(CausticTransportSettings.GetLevelCount(gridWidth, gridHeight), 1, CausticTransportSettings.MaximumLevelCount);
        }
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(7, 8)]
    [InlineData(8, 8)]
    [InlineData(9, 16)]
    [InlineData(1080, 1080)]
    [InlineData(2730, 2736)]
    [InlineData(8191, 8192)]
    [InlineData(8192, 8192)]
    public void TheSplatDispatchCoversTheFrameInWholeThreadGroups(int size, int expected)
    {
        var horizontal = ThreadGroupAlignment.AlignX<SplatShader>(size);
        var vertical = ThreadGroupAlignment.AlignY<SplatShader>(size);

        Assert.Equal(expected, horizontal);
        Assert.Equal(expected, vertical);
        Assert.Equal(0, horizontal % CausticTransportSettings.SplatGroupSize);
        Assert.InRange(horizontal - size, 0, CausticTransportSettings.SplatGroupSize - 1);
    }

    [Fact]
    public void EverySupportedSizeIsRoundedUpToTheNextWholeThreadGroup()
    {
        for (var size = 1; size <= CausticTransportSettings.MaximumCanvasSize; size++)
        {
            var expected = (size + CausticTransportSettings.SplatGroupSize - 1) / CausticTransportSettings.SplatGroupSize * CausticTransportSettings.SplatGroupSize;

            Assert.Equal(expected, ThreadGroupAlignment.AlignX<SplatShader>(size));
            Assert.Equal(expected, ThreadGroupAlignment.AlignY<SplatShader>(size));
        }
    }

    [Fact]
    public void TheSplatTileHoldsFourChannelsPerCell()
    {
        Assert.Equal(CausticTransportSettings.SplatTileSize * CausticTransportSettings.SplatTileSize * 4, CausticTransportSettings.SplatTileLength);
        Assert.True(CausticTransportSettings.SplatTileSize > CausticTransportSettings.SplatGroupSize);
    }

    [Theory]
    [InlineData(1, 65536)]
    [InlineData(64, 65536)]
    [InlineData(43690, 65536)]
    [InlineData(43691, 65535)]
    [InlineData(65536, 43690)]
    [InlineData(1920 * 1080, 1380)]
    [InlineData(3840 * 2160, 345)]
    [InlineData(11184810, 256)]
    [InlineData(int.MaxValue, 256)]
    public void TheColorScaleShrinksWithThePixelCountWithinItsBounds(int pixelCount, int expected)
    {
        Assert.Equal(expected, CausticTransportSettings.GetColorFixedScale(pixelCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AnEmptyFrameIsScaledLikeASinglePixel(int pixelCount)
    {
        Assert.Equal(CausticTransportSettings.GetColorFixedScale(1), CausticTransportSettings.GetColorFixedScale(pixelCount));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(43690)]
    [InlineData(43691)]
    [InlineData(1920 * 1080)]
    [InlineData(3840 * 2160)]
    [InlineData(4096 * 2160)]
    [InlineData(11184810)]
    public void TheColorOfEveryPixelAccumulatesWithinTheUnsignedRangeWithMargin(int pixelCount)
    {
        var scale = CausticTransportSettings.GetColorFixedScale(pixelCount);

        Assert.True((double)scale * pixelCount * CausticTransportSettings.ColorFixedScaleMargin <= uint.MaxValue);
    }

    [Fact]
    public void TheLargestSupportedFrameStillAccumulatesWithinTheUnsignedRange()
    {
        var scale = CausticTransportSettings.GetColorFixedScale(CausticTransportSettings.MaximumPixelCount);

        Assert.Equal(CausticTransportSettings.MinimumColorFixedScale, scale);
        Assert.True((double)CausticTransportSettings.MaximumPixelCount * scale * CausticTransportSettings.ColorFixedScaleMargin <= uint.MaxValue);
        Assert.True((double)(CausticTransportSettings.MaximumPixelCount + 1) * scale * CausticTransportSettings.ColorFixedScaleMargin > uint.MaxValue);
    }

    [Fact]
    public void FourKFramesAreSupported()
    {
        Assert.True(CausticTransportSettings.MaximumPixelCount >= 4096 * 2160);
        Assert.True(CausticTransportSettings.MaximumCanvasSize >= 4096);
    }

    [Theory]
    [InlineData(1d, 1d, true)]
    [InlineData(1920d, 1080d, true)]
    [InlineData(3840d, 2160d, true)]
    [InlineData(4096d, 2160d, true)]
    [InlineData(8192d, 1d, true)]
    [InlineData(1d, 8192d, true)]
    [InlineData(8192d, 1365d, true)]
    [InlineData(8192d, 1366d, false)]
    [InlineData(8193d, 1d, false)]
    [InlineData(1d, 8193d, false)]
    [InlineData(4096d, 2731d, false)]
    [InlineData(0.5d, 100d, false)]
    [InlineData(100d, 0.5d, false)]
    [InlineData(0d, 1080d, false)]
    [InlineData(1920d, 0d, false)]
    [InlineData(-1d, 1080d, false)]
    [InlineData(double.NaN, 1080d, false)]
    [InlineData(1920d, double.NaN, false)]
    [InlineData(double.PositiveInfinity, 1080d, false)]
    [InlineData(1920d, double.PositiveInfinity, false)]
    public void AFrameIsSupportedWhenItsSidesAndPixelCountStayWithinTheLimits(double width, double height, bool expected)
    {
        Assert.Equal(expected, CausticTransportSettings.IsSupportedSize(width, height));
    }
}
