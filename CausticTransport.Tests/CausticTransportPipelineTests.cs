using ComputeWeave;
using ComputeWeave.Interop;

namespace CausticTransport.Tests;

[Collection("Direct3D12")]
public sealed class CausticTransportPipelineTests
{
    const int Opaque = unchecked((int)0xFFC0C0C0);
    const int Plane = (int)CausticLightShape.Plane;
    const int Circle = (int)CausticLightShape.Circle;
    const int HorizontalSlit = (int)CausticLightShape.HorizontalSlit;
    const int VerticalSlit = (int)CausticLightShape.VerticalSlit;

    static readonly bool Direct3D12IsAvailable = GraphicsDevice.EnumerateDevices().Any();

    public static readonly TheoryData<int> Shapes = [Plane, Circle, HorizontalSlit, VerticalSlit];

    static CausticTransportPipeline CreatePipeline()
    {
        if (!Direct3D12IsAvailable)
            Assert.Skip("Direct3D 12 is unavailable.");
        var pipeline = CausticTransportPipeline.TryCreate();
        Assert.NotNull(pipeline);
        return pipeline;
    }

    static CausticTransportPipeline.Parameters Parameters(
        int shape = Plane,
        CausticTransportQuality quality = CausticTransportQuality.Balanced,
        float focus = 0.5f,
        float aperture = 0.5f,
        float dispersion = 0f,
        float roughness = 0f,
        int seed = 0)
        => new(shape, quality, focus, aperture, dispersion, roughness, seed);

    static int Alpha(int pixel) => (pixel >> 24) & 255;

    static int Red(int pixel) => (pixel >> 16) & 255;

    static int Green(int pixel) => (pixel >> 8) & 255;

    static int Blue(int pixel) => pixel & 255;

    static int[] Uniform(int width, int height, int pixel) => Enumerable.Repeat(pixel, width * height).ToArray();

    static int[] Disc(int width, int height, double centerX, double centerY, double radius, int pixel)
    {
        var pixels = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x + 0.5 - centerX;
                var dy = y + 0.5 - centerY;
                if (dx * dx + dy * dy <= radius * radius)
                    pixels[y * width + x] = pixel;
            }
        }

        return pixels;
    }

    static int[] Mixed(int width, int height)
    {
        var source = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var alpha = x == 0 && y == 0 ? 0 : Math.Min(96 + (x + y) * 4, 255);
                var red = (x * 35 % 256) * alpha / 255;
                var green = (y * 35 % 256) * alpha / 255;
                var blue = ((x + y) * 17 % 256) * alpha / 255;
                source[y * width + x] = alpha << 24 | red << 16 | green << 8 | blue;
            }
        }

        return source;
    }

    static int[] Speckled(int width, int height)
    {
        var source = new int[width * height];
        for (var index = 0; index < source.Length; index++)
        {
            var value = 16 + (index * 7) % 48;
            source[index] = 64 << 24 | value << 16 | value << 8 | value;
        }

        return source;
    }

    static int[] Render(CausticTransportPipeline pipeline, int[] source, int width, int height, CausticTransportPipeline.Parameters parameters)
    {
        var destination = new int[source.Length];
        pipeline.Process(source, destination, width, height, in parameters);
        return destination;
    }

    static long Light(int[] pixels) => pixels.Sum(pixel => (long)Alpha(pixel));

    static int LitPixels(int[] pixels) => pixels.Count(pixel => Alpha(pixel) > 0);

    static double DistanceFromCenter(int index, int width, int height)
    {
        var dx = index % width + 0.5 - width / 2.0;
        var dy = index / width + 0.5 - height / 2.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double FarthestLit(int[] pixels, int width, int height)
        => Enumerable.Range(0, pixels.Length).Where(index => Alpha(pixels[index]) > 0).Max(index => DistanceFromCenter(index, width, height));

    static double CentroidX(int[] pixels, int width, Func<int, int> channel)
    {
        double weighted = 0;
        double total = 0;
        for (var index = 0; index < pixels.Length; index++)
        {
            var value = channel(pixels[index]);
            weighted += value * (index % width + 0.5);
            total += value;
        }

        return weighted / total;
    }

    static void AssertWithinOne(int[] expected, int[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var expectedChannel = (expected[index] >> shift) & 255;
                var actualChannel = (actual[index] >> shift) & 255;
                Assert.True(Math.Abs(expectedChannel - actualChannel) <= 1, $"{index} {shift} {expectedChannel} {actualChannel}");
            }
        }
    }

    static void Upload(ReadWriteTexture2D<Bgra32, Float4> texture, int[] pixels)
        => texture.CopyFrom(pixels.Select(pixel => new Bgra32 { PackedValue = unchecked((uint)pixel) }).ToArray());

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ATransparentSourceProducesNoLight(int shape)
    {
        using var pipeline = CreatePipeline();
        var destination = Enumerable.Repeat(-1, 32 * 32).ToArray();
        var parameters = Parameters(shape);

        pipeline.Process(new int[32 * 32], destination, 32, 32, in parameters);

        Assert.All(destination, pixel => Assert.Equal(0, pixel));
    }

    [Fact]
    public void ATransparentSourceAfterABrightOneLeavesNoLightBehind()
    {
        using var pipeline = CreatePipeline();
        var parameters = Parameters(Circle, focus: 0.2f);
        Assert.True(LitPixels(Render(pipeline, Uniform(32, 32, Opaque), 32, 32, parameters)) > 0);

        var rendering = Render(pipeline, new int[32 * 32], 32, 32, parameters);

        Assert.All(rendering, pixel => Assert.Equal(0, pixel));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheOutputStaysPremultipliedAndBounded(int shape)
    {
        using var pipeline = CreatePipeline();
        var parameters = Parameters(shape, focus: 0.3f, dispersion: 0.5f, roughness: 0.4f, seed: 3);

        var rendering = Render(pipeline, Mixed(24, 16), 24, 16, parameters);

        Assert.All(rendering, pixel =>
        {
            Assert.InRange(Red(pixel), 0, Alpha(pixel));
            Assert.InRange(Green(pixel), 0, Alpha(pixel));
            Assert.InRange(Blue(pixel), 0, Alpha(pixel));
        });
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void AColorWithoutLightStaysDark(float dispersion)
    {
        using var pipeline = CreatePipeline();
        var red = Disc(64, 48, 24, 24, 8, unchecked((int)0xFFFF0000));
        var parameters = Parameters(Circle, focus: 0.4f, dispersion: dispersion, roughness: 0.3f, seed: 3);

        var rendering = Render(pipeline, red, 64, 48, parameters);

        Assert.True(LitPixels(rendering) > 0);
        Assert.All(rendering, pixel =>
        {
            Assert.Equal(0, Green(pixel));
            Assert.Equal(0, Blue(pixel));
        });
    }

    [Theory]
    [InlineData(Plane, 0f, 0f, 0)]
    [InlineData(Plane, 0.5f, 0.3f, 7)]
    [InlineData(Circle, 0f, 0f, 0)]
    [InlineData(Circle, 1f, 0.6f, 5)]
    [InlineData(HorizontalSlit, 0.5f, 0.3f, 7)]
    [InlineData(VerticalSlit, 0.5f, 0.3f, 7)]
    public void AFullFocusReproducesTheSourceWithinQuantization(int shape, float roughness, float dispersion, int seed)
    {
        using var pipeline = CreatePipeline();
        var source = Mixed(16, 12);
        var parameters = Parameters(shape, focus: 1f, dispersion: dispersion, roughness: roughness, seed: seed);

        var rendering = Render(pipeline, source, 16, 12, parameters);

        AssertWithinOne(source, rendering);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void AUniformImageIsAFixedPointOfThePlaneLight(float focus)
    {
        using var pipeline = CreatePipeline();
        var source = Uniform(32, 32, unchecked((int)0xFF808080));
        var parameters = Parameters(Plane, focus: focus, aperture: 1f);

        var rendering = Render(pipeline, source, 32, 32, parameters);

        AssertWithinOne(source, rendering);
    }

    [Fact]
    public void ThePlaneLightIgnoresTheAperture()
    {
        using var pipeline = CreatePipeline();
        var source = Mixed(32, 24);

        var small = Render(pipeline, source, 32, 24, Parameters(Plane, focus: 0.3f, aperture: 0.1f));
        var large = Render(pipeline, source, 32, 24, Parameters(Plane, focus: 0.3f, aperture: 1f));

        Assert.Equal(small, large);
    }

    [Fact]
    public void TheCircleGathersTheLightOntoItsDisc()
    {
        using var pipeline = CreatePipeline();

        var rendering = Render(pipeline, Uniform(96, 96, Opaque), 96, 96, Parameters(Circle, focus: 0f, aperture: 0.5f));

        Assert.Equal(255, Alpha(rendering[48 * 96 + 48]));
        Assert.All(new[] { 0, 95, 95 * 96, 96 * 96 - 1 }, corner => Assert.Equal(0, Alpha(rendering[corner])));
        Assert.True(FarthestLit(rendering, 96, 96) <= 32d);
    }

    [Fact]
    public void AHorizontalSlitGathersTheLightOntoItsBand()
    {
        using var pipeline = CreatePipeline();

        var rendering = Render(pipeline, Uniform(96, 96, Opaque), 96, 96, Parameters(HorizontalSlit, focus: 0f, aperture: 0.5f));

        Assert.Equal(255, Alpha(rendering[48 * 96]));
        Assert.Equal(255, Alpha(rendering[48 * 96 + 95]));
        Assert.All(Enumerable.Range(0, 96), x =>
        {
            Assert.Equal(0, Alpha(rendering[x]));
            Assert.Equal(0, Alpha(rendering[95 * 96 + x]));
            Assert.Equal(0, Alpha(rendering[10 * 96 + x]));
            Assert.Equal(0, Alpha(rendering[85 * 96 + x]));
        });
    }

    [Fact]
    public void AVerticalSlitGathersTheLightOntoItsBand()
    {
        using var pipeline = CreatePipeline();

        var rendering = Render(pipeline, Uniform(96, 96, Opaque), 96, 96, Parameters(VerticalSlit, focus: 0f, aperture: 0.5f));

        Assert.Equal(255, Alpha(rendering[48]));
        Assert.Equal(255, Alpha(rendering[95 * 96 + 48]));
        Assert.All(Enumerable.Range(0, 96), y =>
        {
            Assert.Equal(0, Alpha(rendering[y * 96]));
            Assert.Equal(0, Alpha(rendering[y * 96 + 95]));
            Assert.Equal(0, Alpha(rendering[y * 96 + 10]));
            Assert.Equal(0, Alpha(rendering[y * 96 + 85]));
        });
    }

    [Fact]
    public void ALargerApertureSpreadsTheLightWider()
    {
        using var pipeline = CreatePipeline();
        var source = Uniform(96, 96, Opaque);

        var narrow = Render(pipeline, source, 96, 96, Parameters(Circle, focus: 0f, aperture: 0.5f));
        var wide = Render(pipeline, source, 96, 96, Parameters(Circle, focus: 0f, aperture: 1f));

        Assert.True(FarthestLit(wide, 96, 96) > FarthestLit(narrow, 96, 96) + 10d);
        Assert.True(FarthestLit(wide, 96, 96) > 44d);
        Assert.True(LitPixels(wide) > LitPixels(narrow) * 2);
    }

    [Fact]
    public void LessFocusGathersTheLightMore()
    {
        using var pipeline = CreatePipeline();
        var source = Uniform(64, 48, Opaque);

        var focused = LitPixels(Render(pipeline, source, 64, 48, Parameters(Circle, focus: 1f)));
        var halfway = LitPixels(Render(pipeline, source, 64, 48, Parameters(Circle, focus: 0.5f)));
        var gathered = LitPixels(Render(pipeline, source, 64, 48, Parameters(Circle, focus: 0f)));

        Assert.True(focused > halfway && halfway > gathered);
        Assert.Equal(64 * 48, focused);
    }

    [Fact]
    public void ThePlaneLightConservesTheLightItTransports()
    {
        using var pipeline = CreatePipeline();
        var source = Speckled(64, 48);

        var rendering = Render(pipeline, source, 64, 48, Parameters(Plane, focus: 0.5f));

        Assert.InRange(Light(rendering), Light(source) * 0.98, Light(source) * 1.02);
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(4096, 2730)]
    public void ALargeFrameConservesTheLightItTransports(int width, int height)
    {
        using var pipeline = CreatePipeline();
        var source = Speckled(width, height);

        var rendering = Render(pipeline, source, width, height, Parameters(Plane, focus: 0.5f));

        Assert.InRange(Light(rendering), Light(source) * 0.98, Light(source) * 1.02);
    }

    [Fact]
    public void LightSpreadFarBeyondItsSourceIsConservedAndStaysBalanced()
    {
        using var pipeline = CreatePipeline();
        var source = Disc(96, 64, 48, 32, 16, Opaque);

        var rendering = Render(pipeline, source, 96, 64, Parameters(Plane, focus: 0f));

        var left = rendering.Where((_, index) => index % 96 < 48).Sum(pixel => (long)Alpha(pixel));
        var right = rendering.Where((_, index) => index % 96 >= 48).Sum(pixel => (long)Alpha(pixel));
        Assert.InRange(Light(rendering), Light(source) * 0.98, Light(source) * 1.02);
        Assert.InRange(left, right * 0.98, right * 1.02);
        Assert.True(LitPixels(rendering) > LitPixels(source) * 2);
    }

    [Fact]
    public void WithoutDispersionEveryColorTravelsTogether()
    {
        using var pipeline = CreatePipeline();
        var source = Disc(64, 48, 16, 24, 8, Opaque);

        var rendering = Render(pipeline, source, 64, 48, Parameters(Circle, focus: 0.4f, dispersion: 0f));

        Assert.True(LitPixels(rendering) > 0);
        Assert.All(rendering, pixel =>
        {
            Assert.Equal(Red(pixel), Green(pixel));
            Assert.Equal(Green(pixel), Blue(pixel));
        });
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void DispersionKeepsTheLightItTransports(float dispersion)
    {
        using var pipeline = CreatePipeline();
        var source = Speckled(64, 48);

        var rendering = Render(pipeline, source, 64, 48, Parameters(Plane, focus: 0.5f, dispersion: dispersion));

        Assert.InRange(Light(rendering), Light(source) * 0.98, Light(source) * 1.02);
    }

    [Fact]
    public void DispersionCarriesRedFartherThanBlue()
    {
        using var pipeline = CreatePipeline();
        var source = Disc(64, 48, 16, 24, 8, Opaque);

        double Split(float dispersion)
        {
            var rendering = Render(pipeline, source, 64, 48, Parameters(Circle, focus: 0.4f, dispersion: dispersion));
            return CentroidX(rendering, 64, Red) - CentroidX(rendering, 64, Blue);
        }

        var none = Split(0f);
        var some = Split(0.5f);
        var most = Split(1f);

        Assert.InRange(none, -0.01d, 0.01d);
        Assert.True(some > 1d);
        Assert.True(most > some + 1d);
    }

    [Fact]
    public void TheSameSettingsAlwaysProduceTheSamePicture()
    {
        using var pipeline = CreatePipeline();
        var source = Mixed(24, 16);
        var parameters = Parameters(Circle, focus: 0.4f, aperture: 0.4f, dispersion: 0.6f, roughness: 0.5f, seed: 42);

        var first = Render(pipeline, source, 24, 16, parameters);
        var second = Render(pipeline, source, 24, 16, parameters);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(Plane)]
    [InlineData(Circle)]
    public void WithoutRoughnessTheSeedChangesNothing(int shape)
    {
        using var pipeline = CreatePipeline();
        var source = Disc(64, 48, 16, 24, 8, Opaque);

        var first = Render(pipeline, source, 64, 48, Parameters(shape, focus: 0.4f, roughness: 0f, seed: 0));
        var second = Render(pipeline, source, 64, 48, Parameters(shape, focus: 0.4f, roughness: 0f, seed: 1));

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(Plane)]
    [InlineData(Circle)]
    public void ADifferentSeedScattersTheLightDifferently(int shape)
    {
        using var pipeline = CreatePipeline();
        var source = Disc(64, 48, 16, 24, 8, Opaque);

        var first = Render(pipeline, source, 64, 48, Parameters(shape, focus: 0.4f, roughness: 0.5f, seed: 0));
        var second = Render(pipeline, source, 64, 48, Parameters(shape, focus: 0.4f, roughness: 0.5f, seed: 1));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void RoughnessScattersTheLight()
    {
        using var pipeline = CreatePipeline();
        var source = Disc(64, 48, 16, 24, 8, Opaque);

        var smooth = Render(pipeline, source, 64, 48, Parameters(Circle, focus: 0.4f, roughness: 0f));
        var rough = Render(pipeline, source, 64, 48, Parameters(Circle, focus: 0.4f, roughness: 1f));

        Assert.NotEqual(smooth, rough);
        Assert.InRange(Light(rough), Light(smooth) * 0.95, Light(smooth) * 1.05);
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(9, 1)]
    [InlineData(1, 9)]
    [InlineData(7, 7)]
    [InlineData(8, 9)]
    [InlineData(15, 16)]
    [InlineData(16, 17)]
    [InlineData(17, 15)]
    [InlineData(65, 63)]
    public void FramesThatDoNotFillWholeThreadGroupsAreReproduced(int width, int height)
    {
        using var pipeline = CreatePipeline();
        var source = Mixed(width, height);
        var parameters = Parameters(Circle, focus: 1f, dispersion: 0.3f, roughness: 0.2f, seed: 5);

        var rendering = Render(pipeline, source, width, height, parameters);

        AssertWithinOne(source, rendering);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(4096, 2160)]
    [InlineData(4096, 2730)]
    public void ALargeFrameIsProcessedWithoutDispatchOverflow(int width, int height)
    {
        Assert.True(CausticTransportSettings.IsSupportedSize(width, height));
        using var pipeline = CreatePipeline();

        var rendering = Render(pipeline, Speckled(width, height), width, height, Parameters(Plane, focus: 0.5f, dispersion: 0.3f, roughness: 0.2f, seed: 5));

        Assert.Contains(rendering, pixel => pixel != 0);
    }

    [Theory]
    [InlineData(96, 64, 64, 48)]
    [InlineData(64, 48, 96, 64)]
    [InlineData(64, 48, 64, 64)]
    [InlineData(64, 64, 64, 48)]
    [InlineData(64, 64, 48, 64)]
    [InlineData(48, 64, 64, 64)]
    public void APipelineUsedAtAnotherSizeDrawsLikeAFreshOne(int firstWidth, int firstHeight, int width, int height)
    {
        using var pipeline = CreatePipeline();
        using var fresh = CreatePipeline();
        var parameters = Parameters(Circle, focus: 0.3f, dispersion: 0.4f, roughness: 0.3f, seed: 7);
        Render(pipeline, Mixed(firstWidth, firstHeight), firstWidth, firstHeight, parameters);
        var source = Mixed(width, height);

        var reused = Render(pipeline, source, width, height, parameters);
        var expected = Render(fresh, source, width, height, parameters);

        Assert.Equal(expected, reused);
    }

    [Fact]
    public void APipelineWhoseFrameGrowsOnTheSameGridDrawsLikeAFreshOne()
    {
        using var pipeline = CreatePipeline();
        using var fresh = CreatePipeline();
        var parameters = Parameters(Plane, focus: 0.3f);
        Render(pipeline, Mixed(1000, 500), 1000, 500, parameters);
        var source = Mixed(1001, 500);

        var reused = Render(pipeline, source, 1001, 500, parameters);
        var expected = Render(fresh, source, 1001, 500, parameters);

        Assert.Equal(expected, reused);
    }

    [Fact]
    public void SharedTexturesAreProcessedByAPipelineThatHasNotDrawnYet()
    {
        using var packed = CreatePipeline();
        using var pipeline = CreatePipeline();
        var source = Mixed(24, 18);
        var parameters = Parameters(Circle, focus: 0.35f, dispersion: 0.5f);
        var expected = Render(packed, source, 24, 18, parameters);
        var device = GraphicsDevice.GetDefault();
        using var sourceTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, 24, 18);
        using var outputTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, 24, 18);
        Upload(sourceTexture, source);

        pipeline.ProcessSharedAndWait(sourceTexture, outputTexture, 24, 18, in parameters);
        var result = new Bgra32[source.Length];
        outputTexture.CopyTo(result);

        Assert.Equal(expected.Select(pixel => unchecked((uint)pixel)), result.Select(pixel => pixel.PackedValue));
    }

    [Fact]
    public void AFrameWhosePixelCountOverflowsIsRejectedBeforeAnythingIsAllocated()
    {
        using var pipeline = CreatePipeline();
        var device = GraphicsDevice.GetDefault();
        using var source = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, 1, 1);
        using var destination = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, 1, 1);
        var parameters = Parameters(Circle);

        Assert.Throws<OverflowException>(() => pipeline.Process(Array.Empty<int>(), Array.Empty<int>(), 65536, 65536, in parameters));
        Assert.Throws<OverflowException>(() => pipeline.ProcessSharedAndWait(source, destination, 65536, 65536, in parameters));
    }

    [Fact]
    public void AFrameWhoseAccumulatorCannotBeIndexedIsRejectedBeforeAnythingIsAllocated()
    {
        using var pipeline = CreatePipeline();
        var parameters = Parameters(Circle);

        Assert.Throws<OverflowException>(() => pipeline.Process(Array.Empty<int>(), Array.Empty<int>(), 30000, 20000, in parameters));
    }

    [Fact]
    public void ALargeFrameGatheredOntoASmallApertureDoesNotOverflowTheAccumulator()
    {
        using var pipeline = CreatePipeline();
        var source = Uniform(3840, 2160, Opaque);

        var rendering = Render(pipeline, source, 3840, 2160, Parameters(Circle, focus: 0f, aperture: 0.05f));

        var center = rendering[1080 * 3840 + 1920];
        Assert.Equal(255, Alpha(center));
        Assert.InRange(Red(center), 191, 193);
        Assert.InRange(Green(center), 191, 193);
        Assert.InRange(Blue(center), 191, 193);
    }

    [Theory]
    [InlineData(CausticTransportQuality.Balanced, CausticTransportQuality.Ultra)]
    [InlineData(CausticTransportQuality.Ultra, CausticTransportQuality.Balanced)]
    public void APipelineUsedAtAnotherQualityDrawsLikeAFreshOne(CausticTransportQuality first, CausticTransportQuality second)
    {
        using var pipeline = CreatePipeline();
        using var fresh = CreatePipeline();
        var source = Mixed(160, 100);
        Render(pipeline, source, 160, 100, Parameters(Circle, first, focus: 0.3f));
        var parameters = Parameters(Circle, second, focus: 0.3f);

        var reused = Render(pipeline, source, 160, 100, parameters);
        var expected = Render(fresh, source, 160, 100, parameters);

        Assert.Equal(expected, reused);
    }

    [Fact]
    public void SharedTexturesProduceTheSamePicturesAsPackedBuffers()
    {
        using var pipeline = CreatePipeline();
        var source = Mixed(24, 18);
        var parameters = Parameters(Circle, focus: 0.35f, aperture: 0.4f, dispersion: 0.5f, roughness: 0.3f, seed: 11);
        var expected = Render(pipeline, source, 24, 18, parameters);
        var device = GraphicsDevice.GetDefault();
        using var sourceTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, 24, 18);
        using var outputTexture = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, 24, 18);
        Upload(sourceTexture, source);

        pipeline.ProcessSharedAndWait(sourceTexture, outputTexture, 24, 18, in parameters);
        var result = new Bgra32[source.Length];
        outputTexture.CopyTo(result);

        Assert.Equal(expected.Select(pixel => unchecked((uint)pixel)), result.Select(pixel => pixel.PackedValue));
    }

    [Fact]
    public void APipelineWhoseGridWasReclaimedDrawsLikeAFreshOne()
    {
        using var pipeline = CreatePipeline();
        using var fresh = CreatePipeline();
        var source = Mixed(48, 32);
        var parameters = Parameters(Circle, focus: 0.3f, dispersion: 0.5f);
        Render(pipeline, source, 48, 32, parameters);
        GraphicsDevice.GetDefault().TrimMemory();

        var reused = Render(pipeline, source, 48, 32, parameters);
        var expected = Render(fresh, source, 48, 32, parameters);

        Assert.Equal(expected, reused);
    }

    [Fact]
    public void AWarmPipelineAllocatesNoManagedMemory()
    {
        using var pipeline = CreatePipeline();
        var source = Mixed(16, 16);
        var destination = new int[source.Length];
        var parameters = Parameters(Circle, dispersion: 0.5f, roughness: 0.5f, seed: 1);
        for (var iteration = 0; iteration < 4; iteration++)
            pipeline.Process(source, destination, 16, 16, in parameters);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var minimum = long.MaxValue;
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            pipeline.Process(source, destination, 16, 16, in parameters);
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, minimum);
    }

    [Fact]
    public void RepeatedSharedTextureSubmissionsAllocateNoManagedMemory()
    {
        using var pipeline = CreatePipeline();
        var device = GraphicsDevice.GetDefault();
        using var source = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, 16, 16);
        using var destination = InteropServices.AllocateSharedReadWriteTexture2D<Bgra32, Float4>(device, 16, 16);
        var parameters = Parameters(Circle, aperture: 0.3f);
        for (var iteration = 0; iteration < 4; iteration++)
            pipeline.Process(source, destination, 16, 16, in parameters);
        pipeline.WaitForCompletion();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var minimum = long.MaxValue;
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            pipeline.Process(source, destination, 16, 16, in parameters);
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        pipeline.WaitForCompletion();

        Assert.Equal(0, minimum);
    }
}
