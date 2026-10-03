using System.Globalization;
using System.Numerics;
using ComputeWeave;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Json;
using YukkuriMovieMaker.Player.Video;

namespace CausticTransport.Tests;

[Collection("Direct2D")]
public sealed class CausticTransportEffectProcessorTests
{
    const int Size = 64;
    const int Length = 30;

    static readonly Bgra Gray = Bgra.Opaque(192, 192, 192);

    static readonly Bgra Amber = Bgra.Opaque(80, 200, 255);

    static readonly Bgra Azure = Bgra.Opaque(255, 120, 60);

    static Bgra Scene(int x, int y)
    {
        var dx = x - 22;
        var dy = y - 30;
        if (dx * dx + dy * dy <= 144)
            return Amber;
        return x is >= 44 and < 52 && y is >= 40 and < 48 ? Azure : Bgra.Transparent;
    }

    static Func<int, int, Bgra> Filled(Bgra color) => (x, y) => x is >= 16 and < 48 && y is >= 16 and < 48 ? color : Bgra.Transparent;

    static void RequireInterop(IGraphicsDevicesAndContext devices)
    {
        using var scheduler = ComputeExternalQueueScheduler.Create();
        using var provider = CausticTransportInteropProvider.TryCreate(devices, scheduler, out var device);
        if (provider is null || device is null)
            Assert.Skip("Direct3D 11 and Direct3D 12 sharing is unavailable.");
    }

    static Animation Linear(double from, double to)
        => Json.LoadFromText<Animation>(string.Create(CultureInfo.InvariantCulture, $$"""{"AnimationType":"直線移動","Values":[{"Value":{{from}}},{"Value":{{to}}}]}"""))!;

    static Rendering RenderFrame(IGraphicsDevicesAndContext devices, IVideoEffectProcessor processor, int frame)
    {
        processor.Update(EffectDescriptions.At(frame, Length));
        return Rendering.Capture(devices, processor.Output);
    }

    static Rendering RenderFresh(IGraphicsDevicesAndContext devices, CausticTransportEffect effect, SourceImage source, int frame = 0)
    {
        using var processor = effect.CreateVideoEffect(devices);
        processor.SetInput(source.Bitmap);
        return RenderFrame(devices, processor, frame);
    }

    static void AssertSameAsSource(IGraphicsDevicesAndContext devices, Rendering rendering, SourceImage source, string? setting = null)
    {
        var expected = Rendering.Capture(devices, source.Bitmap);

        Assert.Equal((0, 0, source.Width, source.Height), (rendering.Left, rendering.Top, rendering.Width, rendering.Height));
        Assert.True(rendering.SamePixelsAs(expected), setting);
    }

    static int[] Pixels(SourceImage source)
    {
        var pixels = new int[source.Width * source.Height];
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source[x, y];
                pixels[y * source.Width + x] = pixel.Alpha << 24 | pixel.Red << 16 | pixel.Green << 8 | pixel.Blue;
            }
        }

        return pixels;
    }

    static int[] ThroughThePipeline(SourceImage source, CausticTransportPipeline.Parameters parameters)
    {
        using var pipeline = CausticTransportPipeline.TryCreate();
        Assert.NotNull(pipeline);
        var destination = new int[source.Width * source.Height];
        pipeline.Process(Pixels(source), destination, source.Width, source.Height, in parameters);
        return destination;
    }

    static bool Close(Bgra pixel, int expected)
        => Math.Abs(pixel.Alpha - ((expected >> 24) & 255)) <= 1
            && Math.Abs(pixel.Red - ((expected >> 16) & 255)) <= 1
            && Math.Abs(pixel.Green - ((expected >> 8) & 255)) <= 1
            && Math.Abs(pixel.Blue - (expected & 255)) <= 1;

    static bool Differs(Rendering rendering, SourceImage source)
        => rendering.Coordinates().Any(point => rendering[point.X, point.Y] != source[point.X, point.Y]);

    [Fact]
    public void TheProcessorHandsTheDrawDescriptionBackUnchanged()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = new SourceImage(context, Size, Size, Scene);
        using var processor = new CausticTransportEffect().CreateVideoEffect(context);
        processor.SetInput(source.Bitmap);
        var description = EffectDescriptions.At(0, Length);

        var draw = processor.Update(description);

        Assert.Same(description.DrawDescription, draw);
    }

    public static readonly TheoryData<string, Action<CausticTransportEffect>, CausticLightShape, CausticTransportQuality, float, float, float, float, int> Settings = new()
    {
        { "Default", _ => { }, CausticLightShape.Plane, CausticTransportQuality.High, 0.5f, 0.5f, 0.3f, 0.2f, 0 },
        { nameof(CausticTransportEffect.Focus), effect => effect.Focus.Values[0].Value = 20d, CausticLightShape.Plane, CausticTransportQuality.High, 0.2f, 0.5f, 0.3f, 0.2f, 0 },
        { nameof(CausticTransportEffect.LightShape), effect => effect.LightShape = CausticLightShape.Circle, CausticLightShape.Circle, CausticTransportQuality.High, 0.5f, 0.5f, 0.3f, 0.2f, 0 },
        { nameof(CausticTransportEffect.Quality), effect => effect.Quality = CausticTransportQuality.Balanced, CausticLightShape.Plane, CausticTransportQuality.Balanced, 0.5f, 0.5f, 0.3f, 0.2f, 0 },
        { nameof(CausticTransportEffect.ApertureSize), effect => { effect.LightShape = CausticLightShape.Circle; effect.ApertureSize.Values[0].Value = 100d; }, CausticLightShape.Circle, CausticTransportQuality.High, 0.5f, 1f, 0.3f, 0.2f, 0 },
        { nameof(CausticTransportEffect.Dispersion), effect => effect.Dispersion.Values[0].Value = 100d, CausticLightShape.Plane, CausticTransportQuality.High, 0.5f, 0.5f, 1f, 0.2f, 0 },
        { nameof(CausticTransportEffect.Roughness), effect => effect.Roughness.Values[0].Value = 100d, CausticLightShape.Plane, CausticTransportQuality.High, 0.5f, 0.5f, 0.3f, 1f, 0 },
        { nameof(CausticTransportEffect.Seed), effect => effect.Seed = 9, CausticLightShape.Plane, CausticTransportQuality.High, 0.5f, 0.5f, 0.3f, 0.2f, 9 },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void EverySettingReachesThePipeline(string setting, Action<CausticTransportEffect> configure, CausticLightShape shape, CausticTransportQuality quality, float focus, float aperture, float dispersion, float roughness, int seed)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);
        var effect = new CausticTransportEffect();
        configure(effect);
        var expected = ThroughThePipeline(source, new CausticTransportPipeline.Parameters((int)shape, quality, focus, aperture, dispersion, roughness, seed));

        var rendering = RenderFresh(context, effect, source);

        Assert.Equal((0, 0, Size, Size), (rendering.Left, rendering.Top, rendering.Width, rendering.Height));
        Assert.All(rendering.Coordinates(), point => Assert.True(Close(rendering[point.X, point.Y], expected[point.Y * Size + point.X]), $"{setting} ({point.X}, {point.Y})"));
        Assert.True(Differs(rendering, source), setting);
    }

    [Fact]
    public void TheLightSpreadsPastTheShapeOfTheSourceAndStaysPremultiplied()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);

        var rendering = RenderFresh(context, new CausticTransportEffect(), source);

        Assert.All(rendering.Coordinates().Where(point => rendering[point.X, point.Y].Alpha > 0), point =>
        {
            var pixel = rendering[point.X, point.Y];
            Assert.InRange(pixel.Blue, 0, pixel.Alpha);
            Assert.InRange(pixel.Green, 0, pixel.Alpha);
            Assert.InRange(pixel.Red, 0, pixel.Alpha);
        });
        Assert.Contains(rendering.Coordinates(), point => rendering[point.X, point.Y].Alpha > 0 && source[point.X, point.Y].Alpha == 0);
    }

    public static readonly TheoryData<string, Action<CausticTransportEffect>> PassThroughSettings = new()
    {
        { nameof(CausticTransportEffect.Amount), effect => effect.Amount.Values[0].Value = 0d },
        { nameof(CausticTransportEffect.Focus), effect => effect.Focus.Values[0].Value = 100d },
    };

    [Theory]
    [MemberData(nameof(PassThroughSettings))]
    public void AZeroAmountOrAFullFocusPassesTheImageThrough(string setting, Action<CausticTransportEffect> configure)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = new SourceImage(context, Size, Size, Scene);
        var effect = new CausticTransportEffect();
        configure(effect);

        var rendering = RenderFresh(context, effect, source);

        AssertSameAsSource(context, rendering, source, setting);
    }

    [Theory]
    [InlineData(8193, 2)]
    [InlineData(2, 8193)]
    [InlineData(4096, 2731)]
    public void AnImageBeyondTheSupportedSizePassesThroughUntouched(int width, int height)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, width, height, (x, y) => x < width / 2 ? Gray : Bgra.Transparent);

        var rendering = RenderFresh(context, new CausticTransportEffect(), source);

        AssertSameAsSource(context, rendering, source);
    }

    [Theory]
    [InlineData(8192, 8)]
    [InlineData(8, 8192)]
    public void AnImageAtTheLongestSupportedSideIsStillTransported(int width, int height)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, width, height, (x, y) => x * y == 0 && x + y < 300 ? Gray : Bgra.Transparent);

        var rendering = RenderFresh(context, new CausticTransportEffect(), source);

        Assert.Equal((0, 0, width, height), (rendering.Left, rendering.Top, rendering.Width, rendering.Height));
        Assert.False(rendering.SamePixelsAs(Rendering.Capture(context, source.Bitmap)));
    }

    [Theory]
    [InlineData(100, 50)]
    [InlineData(-37, 21)]
    public void TheLightTravelsWithTheImage(int dx, int dy)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);
        using var moved = new AffineTransform2D(context.DeviceContext)
        {
            InterPolationMode = AffineTransform2DInterpolationMode.NearestNeighbor,
            BorderMode = BorderMode.Hard,
            TransformMatrix = Matrix3x2.CreateTranslation(dx, dy),
        };
        moved.SetInput(0, source.Bitmap, true);
        using var movedOutput = moved.Output;
        using var inPlaceProcessor = new CausticTransportEffect().CreateVideoEffect(context);
        inPlaceProcessor.SetInput(source.Bitmap);
        var inPlace = RenderFrame(context, inPlaceProcessor, 0);
        using var travelledProcessor = new CausticTransportEffect().CreateVideoEffect(context);
        travelledProcessor.SetInput(movedOutput);

        var travelled = RenderFrame(context, travelledProcessor, 0);

        Assert.Equal((inPlace.Left + dx, inPlace.Top + dy, inPlace.Width, inPlace.Height), (travelled.Left, travelled.Top, travelled.Width, travelled.Height));
        Assert.All(inPlace.Coordinates(), point => Assert.True(inPlace[point.X, point.Y] == travelled[point.X + dx, point.Y + dy], $"({point.X}, {point.Y})"));
        Assert.True(Differs(inPlace, source));
    }

    [Fact]
    public void ReturningToAFrameReproducesItExactly()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);
        var effect = new CausticTransportEffect();
        effect.Focus.CopyFrom(Linear(10d, 90d));
        using var processor = effect.CreateVideoEffect(context);
        processor.SetInput(source.Bitmap);

        var first = RenderFrame(context, processor, 4);
        var other = RenderFrame(context, processor, 20);
        var again = RenderFrame(context, processor, 4);

        Assert.False(first.SamePixelsAs(other));
        Assert.True(first.SamePixelsAs(again));
    }

    [Fact]
    public void TheNextFrameOfAnUnchangedSettingIsDrawnTheSame()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);
        using var processor = new CausticTransportEffect().CreateVideoEffect(context);
        processor.SetInput(source.Bitmap);

        var first = RenderFrame(context, processor, 0);
        var next = RenderFrame(context, processor, 1);

        Assert.True(Differs(first, source));
        Assert.True(first.SamePixelsAs(next));
    }

    [Fact]
    public void ANewProcessorDrawsTheSameLight()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);
        var effect = new CausticTransportEffect { Seed = 9 };

        var first = RenderFresh(context, effect, source);
        var second = RenderFresh(context, effect, source);

        Assert.True(first.SamePixelsAs(second));
    }

    [Fact]
    public void AnimatedFocusIsReadAtEachFrame()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);
        var effect = new CausticTransportEffect();
        effect.Focus.CopyFrom(Linear(100d, 0d));
        using var processor = effect.CreateVideoEffect(context);
        processor.SetInput(source.Bitmap);

        var start = RenderFrame(context, processor, 0);
        var end = RenderFrame(context, processor, Length - 1);
        var back = RenderFrame(context, processor, 0);

        AssertSameAsSource(context, start, source);
        Assert.True(Differs(end, source));
        AssertSameAsSource(context, back, source);
    }

    [Theory]
    [InlineData(100d, 30d)]
    [InlineData(30d, 100d)]
    [InlineData(30d, 60d)]
    public void AProcessorThatDrewAnotherFocusDrawsLikeAFreshOne(double firstFocus, double secondFocus)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);
        var effect = new CausticTransportEffect();
        effect.Focus.Values[0].Value = firstFocus;
        using var processor = effect.CreateVideoEffect(context);
        processor.SetInput(source.Bitmap);
        RenderFrame(context, processor, 0);
        effect.Focus.Values[0].Value = secondFocus;
        var expected = RenderFresh(context, effect, source);

        var reused = RenderFrame(context, processor, 0);

        Assert.True(reused.SamePixelsAs(expected));
    }

    public static readonly TheoryData<string, Action<CausticTransportEffect>> LaterChanges = new()
    {
        { nameof(CausticTransportEffect.Amount), effect => effect.Amount.Values[0].Value = 50d },
        { nameof(CausticTransportEffect.Focus), effect => effect.Focus.Values[0].Value = 20d },
        { nameof(CausticTransportEffect.Quality), effect => effect.Quality = CausticTransportQuality.Balanced },
        { nameof(CausticTransportEffect.LightShape), effect => effect.LightShape = CausticLightShape.Circle },
        { nameof(CausticTransportEffect.ApertureSize), effect => { effect.LightShape = CausticLightShape.Circle; effect.ApertureSize.Values[0].Value = 100d; } },
        { nameof(CausticTransportEffect.Dispersion), effect => effect.Dispersion.Values[0].Value = 100d },
        { nameof(CausticTransportEffect.Roughness), effect => effect.Roughness.Values[0].Value = 100d },
        { nameof(CausticTransportEffect.Seed), effect => effect.Seed = 9 },
    };

    [Theory]
    [MemberData(nameof(LaterChanges))]
    public void EverySettingChangedAfterTheFirstFrameReachesTheEffect(string setting, Action<CausticTransportEffect> change)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, Size, Size, Scene);
        var effect = new CausticTransportEffect();
        using var processor = effect.CreateVideoEffect(context);
        processor.SetInput(source.Bitmap);

        var before = RenderFrame(context, processor, 0);
        change(effect);
        var after = RenderFrame(context, processor, 0);
        var expected = RenderFresh(context, effect, source);

        Assert.False(before.SamePixelsAs(after), setting);
        Assert.True(after.SamePixelsAs(expected), setting);
    }

    [Theory]
    [InlineData(32, 128)]
    [InlineData(128, 32)]
    [InlineData(64, 48)]
    public void AResizedSourceIsDrawnLikeAFreshOne(int firstSize, int secondSize)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var first = new SourceImage(context, firstSize, firstSize, (x, y) => (x - 8) * (x - 8) + (y - 8) * (y - 8) <= 36 ? Amber : Bgra.Transparent);
        using var second = new SourceImage(context, secondSize, secondSize, (x, y) => (x - 20) * (x - 20) + (y - 12) * (y - 12) <= 64 ? Azure : Bgra.Transparent);
        var effect = new CausticTransportEffect();
        using var processor = effect.CreateVideoEffect(context);
        processor.SetInput(first.Bitmap);
        RenderFrame(context, processor, 0);
        var expected = RenderFresh(context, effect, second);

        processor.SetInput(second.Bitmap);
        var after = RenderFrame(context, processor, 0);

        Assert.Equal((0, 0, secondSize, secondSize), (after.Left, after.Top, after.Width, after.Height));
        Assert.True(Differs(after, second));
        Assert.True(after.SamePixelsAs(expected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LightAppearsOnceATransparentSourceTakesShape(bool lightWasDrawnBefore)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var empty = SourceImage.Solid(context, Size, Size, Bgra.Transparent);
        using var source = new SourceImage(context, Size, Size, Scene);
        var effect = new CausticTransportEffect();
        var expected = RenderFresh(context, effect, source);
        using var processor = effect.CreateVideoEffect(context);
        if (lightWasDrawnBefore)
        {
            processor.SetInput(source.Bitmap);
            RenderFrame(context, processor, 0);
        }
        processor.SetInput(empty.Bitmap);
        var before = RenderFrame(context, processor, 0);

        processor.SetInput(source.Bitmap);
        var after = RenderFrame(context, processor, 0);

        Assert.All(before.Coordinates(), point => Assert.Equal(0, before[point.X, point.Y].Alpha));
        Assert.True(Differs(after, source));
        Assert.True(after.SamePixelsAs(expected));
    }

    [Fact]
    public void ARecoloredImageIsDrawnLikeAFreshOne()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var amber = new SourceImage(context, Size, Size, Filled(Amber));
        using var azure = new SourceImage(context, Size, Size, Filled(Azure));
        var effect = new CausticTransportEffect();
        using var processor = effect.CreateVideoEffect(context);
        processor.SetInput(amber.Bitmap);
        RenderFrame(context, processor, 0);
        var expected = RenderFresh(context, effect, azure);

        processor.SetInput(azure.Bitmap);
        var recolored = RenderFrame(context, processor, 0);

        Assert.True(recolored.SamePixelsAs(expected));
    }

    [Fact]
    public void EveryFrameOfAnInputChangingInPlaceIsDrawnLikeAFreshProcessor()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var amber = new SourceImage(context, Size, Size, Scene);
        using var azure = new SourceImage(context, Size, Size, Filled(Azure));
        using var passThrough = new AffineTransform2D(context.DeviceContext);
        using var image = passThrough.Output;
        var effect = new CausticTransportEffect();
        using var processor = effect.CreateVideoEffect(context);
        processor.SetInput(image);
        var frames = new (SourceImage Source, Action<CausticTransportEffect> Change)[]
        {
            (amber, _ => { }),
            (amber, _ => { }),
            (azure, _ => { }),
            (azure, effect => effect.Focus.Values[0].Value = 30d),
            (amber, _ => { }),
            (amber, effect => effect.Amount.Values[0].Value = 50d),
            (amber, effect => effect.Roughness.Values[0].Value = 0d),
            (amber, effect => effect.Seed = 9),
            (amber, effect => effect.Roughness.Values[0].Value = 20d),
            (amber, effect => effect.Seed = 3),
            (amber, effect => effect.Dispersion.Values[0].Value = 70d),
        };

        for (var index = 0; index < frames.Length; index++)
        {
            var (source, change) = frames[index];
            change(effect);
            passThrough.SetInput(0, source.Bitmap, true);

            var drawn = RenderFrame(context, processor, 0);
            using var fresh = effect.CreateVideoEffect(context);
            fresh.SetInput(image);
            var expected = RenderFrame(context, fresh, 0);

            Assert.True(drawn.SamePixelsAs(expected), $"frame {index}");
        }
    }

    [Fact]
    public void AProcessorWithoutAnInputHandsTheDrawDescriptionBack()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var processor = new CausticTransportEffect().CreateVideoEffect(context);
        var description = EffectDescriptions.At(0, Length);

        var draw = processor.Update(description);

        Assert.Same(description.DrawDescription, draw);
    }

    [Fact]
    public void LightReturnsOnceAnUnsupportedFrameIsFollowedByASupportedOne()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        RequireInterop(context);
        using var source = new SourceImage(context, 8193, Size, Scene);
        using var crop = new Crop(context.DeviceContext) { Rectangle = new Vector4(0f, 0f, Size, Size) };
        crop.SetInput(0, source.Bitmap, true);
        using var cropOutput = crop.Output;
        using var processor = new CausticTransportEffect().CreateVideoEffect(context);
        processor.SetInput(cropOutput);
        var first = RenderFrame(context, processor, 0);

        crop.Rectangle = new Vector4(0f, 0f, 8193f, Size);
        var unsupported = RenderFrame(context, processor, 1);
        crop.Rectangle = new Vector4(0f, 0f, Size, Size);
        var again = RenderFrame(context, processor, 2);

        Assert.Equal((0, 0, 8193, Size), (unsupported.Left, unsupported.Top, unsupported.Width, unsupported.Height));
        Assert.True(unsupported.SamePixelsAs(Rendering.Capture(context, source.Bitmap)));
        Assert.False(first.SamePixelsAs(Rendering.Capture(context, cropOutput)));
        Assert.True(first.SamePixelsAs(again));
    }

    [Fact]
    public void AFailureWhileUpdatingIsNotSwallowed()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var processor = new CausticTransportEffect().CreateVideoEffect(context);

        Assert.ThrowsAny<Exception>(() => processor.Update(null!));
    }
}
