using System.Runtime.InteropServices;
using ComputeWeave;
using ComputeWeave.Interop;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace CausticTransport.Tests;

public sealed class CausticTransportEffectTests
{
    private static double ValueAt(YukkuriMovieMaker.Commons.Animation animation) => animation.GetValue(0, 1, 30);

    [Fact]
    public void DefaultParameterValuesMatchSpecification()
    {
        var effect = new CausticTransportEffect();

        Assert.Equal(100d, ValueAt(effect.Amount), 6);
        Assert.Equal(50d, ValueAt(effect.Focus), 6);
        Assert.Equal(50d, ValueAt(effect.ApertureSize), 6);
        Assert.Equal(30d, ValueAt(effect.Dispersion), 6);
        Assert.Equal(20d, ValueAt(effect.Roughness), 6);
        Assert.Equal(CausticLightShape.Plane, effect.LightShape);
        Assert.Equal(CausticTransportQuality.High, effect.Quality);
        Assert.Equal(0, effect.Seed);
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1234, 1234)]
    public void SeedClampsNegativeInputToZero(int input, int expected)
    {
        var effect = new CausticTransportEffect { Seed = input };

        Assert.Equal(expected, effect.Seed);
    }

    [Fact]
    public void CreateExoVideoFiltersReturnsEmpty()
    {
        var effect = new CausticTransportEffect();

        Assert.Empty(effect.CreateExoVideoFilters(0, null!));
    }
}
