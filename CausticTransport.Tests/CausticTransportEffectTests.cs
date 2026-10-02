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

    [Fact]
    public void Direct2DInteropRoundTripReconstructsPixelsAtFullConvergence()
    {
        using var devices = new GraphicsDevices();
        using var graphicsContext = devices.CreateContext();
        using var scheduler = ComputeExternalQueueScheduler.Create();
        using var provider = CausticTransportInteropProvider.TryCreate(graphicsContext, scheduler, out var interopDevice);
        if (provider is null || interopDevice is null)
        {
            Assert.Skip("Direct3D 11 and Direct3D 12 sharing is unavailable.");
            return;
        }

        using var domain = interopDevice.RegisterExternalDomain(provider);
        using var resourceSet = CausticTransportResourceSet.Create(interopDevice, domain);
        using var pipeline = CausticTransportPipeline.TryCreate(interopDevice);
        Assert.NotNull(pipeline);

        const int width = 16;
        const int height = 12;
        const int expected = unchecked((int)0xC0302010);
        var pixels = Enumerable.Repeat(expected, width * height).ToArray();
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        using var inputBitmap = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.None));
        try
        {
            inputBitmap.CopyFromMemory(handle.AddrOfPinnedObject(), width * sizeof(int));
        }
        finally
        {
            handle.Free();
        }

        Assert.True(resourceSet.TryEnsureSource(width, height, out _));
        Assert.True(resourceSet.TryEnsureOutput(width, height, out _));
        var parameters = new CausticTransportPipeline.Parameters(0, CausticTransportQuality.Balanced, 1f, 0.5f, 0f, 0f, 0);
        var renderContext = provider.RenderContext;
        for (var iteration = 0; iteration < 2; iteration++)
        {
            using (var borrow = resourceSet.BeginSourceExternalOperation())
            {
                var previousTarget = renderContext.Target;
                using var sourceBitmap = new ID2D1Bitmap1(borrow.DangerousGetView().AddRefBitmap());
                renderContext.Target = sourceBitmap;
                renderContext.BeginDraw();
                renderContext.Clear(null);
                renderContext.DrawImage(
                    inputBitmap,
                    System.Numerics.Vector2.Zero,
                    null,
                    InterpolationMode.NearestNeighbor,
                    CompositeMode.SourceCopy);
                renderContext.EndDraw();
                renderContext.Target = previousTarget;
            }

            pipeline!.Process(
                resourceSet.GetSourceComputeBinding(),
                resourceSet.GetOutputComputeBinding(),
                width,
                height,
                in parameters);
        }

        using var outputLease = resourceSet.AcquireOutputExternalViewLease();
        using var outputBitmap = new ID2D1Bitmap1(outputLease.DangerousGetView().AddRefBitmap());
        using var staging = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        staging.CopyFromBitmap(outputBitmap);
        var mapped = staging.Map(MapOptions.Read);
        try
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var actual = Marshal.ReadInt32(mapped.Bits + (nint)(y * mapped.Pitch + x * sizeof(int)));
                    for (var shift = 0; shift < 32; shift += 8)
                    {
                        var expectedChannel = (expected >> shift) & 255;
                        var actualChannel = (actual >> shift) & 255;
                        Assert.InRange(actualChannel, expectedChannel - 1, expectedChannel + 1);
                    }
                }
            }
        }
        finally
        {
            staging.Unmap();
        }
    }

    private static int[] CreateSourcePixels(int width, int height)
    {
        var source = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var alpha = x == 0 && y == 0 ? 0 : 96 + (x + y) * 4;
                alpha = Math.Min(alpha, 255);
                var red = (x * 35 % 256) * alpha / 255;
                var green = (y * 35 % 256) * alpha / 255;
                var blue = ((x + y) * 17 % 256) * alpha / 255;
                source[y * width + x] = alpha << 24 | red << 16 | green << 8 | blue;
            }
        }
        return source;
    }
}
