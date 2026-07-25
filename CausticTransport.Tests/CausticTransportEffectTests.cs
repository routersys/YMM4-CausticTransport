using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.Interop;
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

    [Theory]
    [InlineData(CausticTransportQuality.Balanced, 128, 4, 12)]
    [InlineData(CausticTransportQuality.High, 192, 6, 16)]
    [InlineData(CausticTransportQuality.Ultra, 256, 8, 20)]
    public void QualitySettingsMatchSpecification(CausticTransportQuality quality, int resolution, int transport, int jacobi)
    {
        var settings = CausticTransportSettings.GetQuality(quality);

        Assert.Equal(resolution, settings.GridResolution);
        Assert.Equal(transport, settings.TransportIterations);
        Assert.Equal(jacobi, settings.JacobiIterations);
    }

    [Theory]
    [InlineData(1920, 1080, 192, 192, 108)]
    [InlineData(1080, 1920, 192, 108, 192)]
    [InlineData(8, 8, 192, 8, 8)]
    [InlineData(4096, 16, 128, 128, 4)]
    [InlineData(100, 100, 256, 100, 100)]
    public void GridSizeKeepsAspectAndBounds(int width, int height, int resolution, int expectedWidth, int expectedHeight)
    {
        var (gridWidth, gridHeight) = CausticTransportSettings.GetGridSize(width, height, resolution);

        Assert.Equal(expectedWidth, gridWidth);
        Assert.Equal(expectedHeight, gridHeight);
    }

    [Theory]
    [InlineData(8, 8, 1)]
    [InlineData(16, 16, 1)]
    [InlineData(17, 17, 2)]
    [InlineData(192, 108, 5)]
    [InlineData(256, 256, 5)]
    public void LevelCountCoversGridDownToCoarsestSize(int gridWidth, int gridHeight, int expected)
    {
        Assert.Equal(expected, CausticTransportSettings.GetLevelCount(gridWidth, gridHeight));
    }

    [Fact]
    public void ColorFixedScaleStaysWithinSafeBounds()
    {
        Assert.Equal(65536, CausticTransportSettings.GetColorFixedScale(64));
        Assert.Equal(345, CausticTransportSettings.GetColorFixedScale(3840 * 2160));
        Assert.True((double)CausticTransportSettings.GetColorFixedScale(3840 * 2160) * 3840 * 2160 <= uint.MaxValue);
    }

    [Fact]
    public void MaximumPixelCountKeepsColorAccumulationWithinUnsignedRange()
    {
        var scale = CausticTransportSettings.GetColorFixedScale(CausticTransportSettings.MaximumPixelCount);

        Assert.Equal(CausticTransportSettings.MinimumColorFixedScale, scale);
        Assert.True((double)CausticTransportSettings.MaximumPixelCount * scale <= uint.MaxValue);
        Assert.True(CausticTransportSettings.MaximumPixelCount >= 4096 * 2160);
    }

    [Theory]
    [InlineData(1920d, 1080d, true)]
    [InlineData(3840d, 2160d, true)]
    [InlineData(4096d, 2160d, true)]
    [InlineData(8192d, 1365d, true)]
    [InlineData(8193d, 1365d, false)]
    [InlineData(4096d, 2731d, false)]
    [InlineData(0d, 1080d, false)]
    [InlineData(double.NaN, 1080d, false)]
    [InlineData(double.PositiveInfinity, 1080d, false)]
    public void SupportedSizeCoversUpTo4KAndRejectsLarger(double width, double height, bool expected)
    {
        Assert.Equal(expected, CausticTransportSettings.IsSupportedSize(width, height));
    }

    [Theory]
    [InlineData(0f, 0f, 0)]
    [InlineData(0.5f, 0.3f, 7)]
    [InlineData(0.5f, 0f, 0)]
    public void FullConvergenceReconstructsSourceWithinQuantization(float roughness, float dispersion, int seed)
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 16;
        const int height = 12;
        var source = CreateSourcePixels(width, height);
        var destination = new int[source.Length];
        var parameters = new CausticTransportPipeline.Parameters(0, CausticTransportQuality.Balanced, 1f, 0.5f, dispersion, roughness, seed);

        pipeline.Process(source, destination, width, height, in parameters);

        for (var index = 0; index < source.Length; index++)
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var expected = (source[index] >> shift) & 255;
                var actual = (destination[index] >> shift) & 255;
                Assert.InRange(actual, Math.Max(expected - 1, 0), Math.Min(expected + 1, 255));
            }
        }
    }

    [Fact]
    public void GpuPipelineIsDeterministic()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 24;
        const int height = 16;
        var source = CreateSourcePixels(width, height);
        var first = new int[source.Length];
        var second = new int[source.Length];
        var parameters = new CausticTransportPipeline.Parameters(1, CausticTransportQuality.Balanced, 0.4f, 0.4f, 0.6f, 0.5f, 42);

        pipeline.Process(source, first, width, height, in parameters);
        pipeline.Process(source, second, width, height, in parameters);

        Assert.Equal(first, second);
    }

    [Fact]
    public void FullHdFrameIsProcessedWithoutDispatchGroupOverflow()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 1920;
        const int height = 1080;
        var source = CreateSourcePixels(width, height);
        var destination = new int[source.Length];
        var parameters = new CausticTransportPipeline.Parameters(0, CausticTransportQuality.Balanced, 0.5f, 0.5f, 0.3f, 0.2f, 5);

        pipeline.Process(source, destination, width, height, in parameters);

        Assert.Contains(destination, pixel => pixel != 0);
    }

    [Fact]
    public void OutputAlphaStaysPremultipliedAndBounded()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 24;
        const int height = 16;
        var source = CreateSourcePixels(width, height);
        var destination = new int[source.Length];
        var parameters = new CausticTransportPipeline.Parameters(1, CausticTransportQuality.Balanced, 0.3f, 0.3f, 0.5f, 0.4f, 3);

        pipeline.Process(source, destination, width, height, in parameters);

        foreach (var pixel in destination)
        {
            var alpha = (pixel >> 24) & 255;
            Assert.InRange((pixel >> 16) & 255, 0, alpha);
            Assert.InRange((pixel >> 8) & 255, 0, alpha);
            Assert.InRange(pixel & 255, 0, alpha);
        }
    }

    [Fact]
    public void MassIsConservedDuringTransport()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 64;
        const int height = 48;
        var source = new int[width * height];
        for (var index = 0; index < source.Length; index++)
        {
            var value = 16 + (index * 7) % 48;
            source[index] = 64 << 24 | value << 16 | value << 8 | value;
        }
        var destination = new int[source.Length];
        var parameters = new CausticTransportPipeline.Parameters(0, CausticTransportQuality.Balanced, 0.5f, 0.5f, 0f, 0f, 0);

        pipeline.Process(source, destination, width, height, in parameters);

        double sourceSum = 0;
        double destinationSum = 0;
        foreach (var pixel in source)
            sourceSum += (pixel >> 24) & 255;
        foreach (var pixel in destination)
            destinationSum += (pixel >> 24) & 255;

        Assert.InRange(destinationSum, sourceSum * 0.98, sourceSum * 1.02);
    }

    [Fact]
    public void TransparentInputYieldsTransparentOutput()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 16;
        const int height = 16;
        var source = new int[width * height];
        var destination = new int[source.Length];
        Array.Fill(destination, -1);
        var parameters = new CausticTransportPipeline.Parameters(1, CausticTransportQuality.Balanced, 0.5f, 0.5f, 0.5f, 0.5f, 0);

        pipeline.Process(source, destination, width, height, in parameters);

        Assert.All(destination, pixel => Assert.Equal(0, pixel));
    }

    [Fact]
    public void UniformImageWithPlaneLightIsFixedPointOfTransport()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 32;
        const int height = 32;
        const int expected = unchecked((int)0xFF808080);
        var source = new int[width * height];
        Array.Fill(source, expected);
        var destination = new int[source.Length];
        var parameters = new CausticTransportPipeline.Parameters(0, CausticTransportQuality.Balanced, 0f, 1f, 0f, 0f, 0);

        pipeline.Process(source, destination, width, height, in parameters);

        foreach (var pixel in destination)
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var expectedChannel = (expected >> shift) & 255;
                var actual = (pixel >> shift) & 255;
                Assert.InRange(actual, expectedChannel - 1, expectedChannel + 1);
            }
        }
    }

    [Fact]
    public void GpuPipelineDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 16;
        const int height = 16;
        var source = CreateSourcePixels(width, height);
        var destination = new int[source.Length];
        var parameters = new CausticTransportPipeline.Parameters(1, CausticTransportQuality.Balanced, 0.5f, 0.5f, 0.5f, 0.5f, 1);
        pipeline.Process(source, destination, width, height, in parameters);
        pipeline.Process(source, destination, width, height, in parameters);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        pipeline.Process(source, destination, width, height, in parameters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SharedTexturePipelineMatchesPackedBufferPipeline()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 24;
        const int height = 18;
        var source = CreateSourcePixels(width, height);
        var expected = new int[source.Length];
        var parameters = new CausticTransportPipeline.Parameters(1, CausticTransportQuality.Balanced, 0.35f, 0.4f, 0.5f, 0.3f, 11);
        pipeline.Process(source, expected, width, height, in parameters);

        var device = GraphicsDevice.GetDefault();
        using var sourceTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        using var outputTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        var sourcePixels = new Bgra32[source.Length];
        for (var index = 0; index < source.Length; index++)
            sourcePixels[index].PackedValue = unchecked((uint)source[index]);
        sourceTexture.CopyFrom(sourcePixels);
        pipeline.ProcessSharedAndWait(sourceTexture, outputTexture, width, height, in parameters);
        var result = new Bgra32[source.Length];
        outputTexture.CopyTo(result);

        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(unchecked((uint)expected[index]), result[index].PackedValue);
    }

    [Fact]
    public void SubmittedSharedTexturePipelineDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        if (pipeline is null)
        {
            Assert.Skip("Direct3D 12 is unavailable.");
            return;
        }

        const int width = 16;
        const int height = 16;
        var device = GraphicsDevice.GetDefault();
        using var source = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        using var destination = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, width, height);
        var parameters = new CausticTransportPipeline.Parameters(0, CausticTransportQuality.Balanced, 0.5f, 0.5f, 0.3f, 0.2f, 0);
        for (var iteration = 0; iteration < 4; iteration++)
            pipeline.Process(source, destination, width, height, in parameters);
        pipeline.WaitForCompletion();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        pipeline.Process(source, destination, width, height, in parameters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        pipeline.WaitForCompletion();

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Direct2DInteropRoundTripReconstructsPixelsAtFullConvergence()
    {
        using var devices = new GraphicsDevices();
        using var graphicsContext = devices.CreateContext();
        using var interop = CausticTransportGpuInterop.TryCreate(graphicsContext);
        if (interop is null)
        {
            Assert.Skip("Direct3D 11 and Direct3D 12 sharing is unavailable.");
            return;
        }

        using var pipeline = CausticTransportPipeline.TryCreate(interop.Device);
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

        Assert.True(interop.EnsureResources(width, height));
        var bounds = new RawRectF(0f, 0f, width, height);
        var parameters = new CausticTransportPipeline.Parameters(0, CausticTransportQuality.Balanced, 1f, 0.5f, 0f, 0f, 0);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            interop.RenderInput(inputBitmap, bounds);
            interop.BeginCompute();
            try
            {
                pipeline!.Process(interop.SourceTexture, interop.OutputTexture, width, height, in parameters);
            }
            finally
            {
                interop.EndCompute();
            }
        }
        interop.WaitForIdle();

        using var staging = graphicsContext.DeviceContext.CreateBitmap(
            new SizeI(width, height),
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        staging.CopyFromBitmap(interop.OutputBitmap);
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
