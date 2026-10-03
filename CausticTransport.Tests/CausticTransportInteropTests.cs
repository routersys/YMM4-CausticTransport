using System.Numerics;
using ComputeWeave;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;

namespace CausticTransport.Tests;

[Collection("Direct2D")]
public sealed class CausticTransportInteropTests
{
    static readonly Bgra Gray = Bgra.Opaque(192, 192, 192);

    static CausticTransportPipeline.Parameters Parameters(float focus = 1f)
        => new((int)CausticLightShape.Circle, CausticTransportQuality.Balanced, focus, 0.5f, 0.3f, 0.2f, 5);

    static Bgra Gradient(int x, int y)
        => new((byte)(32 + x * 3 % 200), (byte)(96 + y * 5 % 150), (byte)(160 + (x + y) % 90), (byte)(96 + (x + y) * 2 % 159));

    static Func<int, int, Bgra> CenteredSquare(int size, int square)
    {
        var start = (size - square) / 2;
        return (x, y) => x >= start && x < start + square && y >= start && y < start + square ? Gray : Bgra.Transparent;
    }

    sealed class Interop : IDisposable
    {
        readonly ComputeExternalQueueScheduler scheduler;
        readonly CausticTransportInteropProvider provider;
        readonly ComputeInteropDomain domain;

        public CausticTransportResourceSet Resources { get; }

        public CausticTransportPipeline Pipeline { get; }

        public GraphicsDevice Device { get; }

        Interop(ComputeExternalQueueScheduler scheduler, CausticTransportInteropProvider provider, GraphicsDevice device)
        {
            this.scheduler = scheduler;
            this.provider = provider;
            Device = device;
            domain = device.RegisterExternalDomain(provider);
            Resources = CausticTransportResourceSet.Create(device, domain);
            Pipeline = CausticTransportPipeline.TryCreate(device)!;
        }

        public static Interop Create(IGraphicsDevicesAndContext devices)
        {
            var scheduler = ComputeExternalQueueScheduler.Create();
            var provider = CausticTransportInteropProvider.TryCreate(devices, scheduler, out var device);
            if (provider is null || device is null)
            {
                scheduler.Dispose();
                Assert.Skip("Direct3D 11 and Direct3D 12 sharing is unavailable.");
            }

            return new Interop(scheduler, provider, device);
        }

        public void Draw(ID2D1Image image)
        {
            var context = provider.RenderContext;
            using var borrow = Resources.BeginSourceExternalOperation();
            var previousTarget = context.Target;
            using var target = new ID2D1Bitmap1(borrow.DangerousGetView().AddRefBitmap());
            context.Target = target;
            context.BeginDraw();
            context.Clear(null);
            context.DrawImage(image, Vector2.Zero, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
            context.EndDraw();
            context.Target = previousTarget;
        }

        public void Process(int width, int height, CausticTransportPipeline.Parameters parameters)
            => Pipeline.Process(Resources.GetSourceComputeBinding(), Resources.GetOutputComputeBinding(), width, height, in parameters);

        public Rendering CaptureOutput(IGraphicsDevicesAndContext devices, out int width, out int height)
        {
            using var lease = Resources.AcquireOutputExternalViewLease();
            width = lease.Width;
            height = lease.Height;
            using var bitmap = new ID2D1Bitmap1(lease.DangerousGetView().AddRefBitmap());
            return Rendering.Capture(devices, bitmap);
        }

        public void Dispose()
        {
            Pipeline.Dispose();
            Resources.Dispose();
            Resources.WaitForDisposal();
            domain.Dispose();
            domain.WaitForDisposal();
            provider.Dispose();
            scheduler.Dispose();
        }
    }

    [Fact]
    public void AProviderCannotBeMadeWithoutAScheduler()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();

        Assert.Throws<ArgumentNullException>(() => CausticTransportInteropProvider.TryCreate(context, null!, out _));
    }

    [Fact]
    public void TheSourceIsReproducedAtFullFocus()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var interop = Interop.Create(context);
        using var source = new SourceImage(context, 24, 18, Gradient);
        Assert.True(interop.Resources.TryEnsureSource(24, 18, out _));
        Assert.True(interop.Resources.TryEnsureOutput(24, 18, out _));

        interop.Draw(source.Bitmap);
        interop.Process(24, 18, Parameters(focus: 1f));
        var output = interop.CaptureOutput(context, out _, out _);

        AssertShows(output, Pixels(source), 24, 18, tolerance: 1);
    }

    [Fact]
    public void ThePictureMatchesWhatThePipelineMakesFromThePackedPixels()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var interop = Interop.Create(context);
        using var source = new SourceImage(context, 48, 32, Gradient);
        var parameters = Parameters(focus: 0.3f);
        var expected = new int[48 * 32];
        interop.Pipeline.Process(Pixels(source), expected, 48, 32, in parameters);
        Assert.True(interop.Resources.TryEnsureSource(48, 32, out _));
        Assert.True(interop.Resources.TryEnsureOutput(48, 32, out _));

        interop.Draw(source.Bitmap);
        interop.Process(48, 32, parameters);
        var output = interop.CaptureOutput(context, out _, out _);

        AssertShows(output, expected, 48, 32, tolerance: 0);
        Assert.NotEqual(Pixels(source), expected);
    }

    [Fact]
    public void ASourceDrawnAgainReplacesTheLightOfTheOneBefore()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var interop = Interop.Create(context);
        using var first = new SourceImage(context, 48, 48, CenteredSquare(48, 16));
        using var second = new SourceImage(context, 48, 48, (x, y) => x < 16 && y < 16 ? Gray : Bgra.Transparent);
        var parameters = Parameters(focus: 0.3f);
        var expected = new int[48 * 48];
        interop.Pipeline.Process(Pixels(second), expected, 48, 48, in parameters);
        Assert.True(interop.Resources.TryEnsureSource(48, 48, out _));
        Assert.True(interop.Resources.TryEnsureOutput(48, 48, out _));
        interop.Draw(first.Bitmap);
        interop.Process(48, 48, parameters);

        interop.Draw(second.Bitmap);
        interop.Process(48, 48, parameters);
        var output = interop.CaptureOutput(context, out _, out _);

        AssertShows(output, expected, 48, 48, tolerance: 0);
    }

    [Fact]
    public void ThePictureIsDrawnAfterTheOutputGrowsToFullHd()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var interop = Interop.Create(context);
        using var source = new SourceImage(context, 96, 96, CenteredSquare(96, 32));
        var parameters = Parameters(focus: 0.3f);
        var expected = new int[96 * 96];
        interop.Pipeline.Process(Pixels(source), expected, 96, 96, in parameters);
        Assert.True(interop.Resources.TryEnsureSource(96, 96, out _));
        Assert.True(interop.Resources.TryEnsureOutput(96, 96, out _));
        interop.Draw(source.Bitmap);
        interop.Process(96, 96, parameters);
        interop.CaptureOutput(context, out var firstWidth, out var firstHeight);

        Assert.True(interop.Resources.TryEnsureOutput(1920, 1080, out var outputChanged));
        interop.Draw(source.Bitmap);
        interop.Process(96, 96, parameters);
        var output = interop.CaptureOutput(context, out var secondWidth, out var secondHeight);

        Assert.Equal((96, 96), (firstWidth, firstHeight));
        Assert.True(outputChanged);
        Assert.Equal((1920, 1080), (secondWidth, secondHeight));
        AssertShows(output, expected, 96, 96, tolerance: 0);
    }

    [Fact]
    public void ThePictureIsDrawnAfterTheSourceIsReplacedByALargerOne()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var interop = Interop.Create(context);
        var parameters = Parameters(focus: 0.3f);

        foreach (var (size, square) in new[] { (64, 24), (128, 48) })
        {
            using var source = new SourceImage(context, size, size, CenteredSquare(size, square));
            var expected = new int[size * size];
            interop.Pipeline.Process(Pixels(source), expected, size, size, in parameters);
            Assert.True(interop.Resources.TryEnsureSource(size, size, out var sourceChanged));
            Assert.True(interop.Resources.TryEnsureOutput(size, size, out _));
            interop.Draw(source.Bitmap);
            interop.Process(size, size, parameters);
            var output = interop.CaptureOutput(context, out var width, out var height);

            Assert.True(sourceChanged);
            Assert.Equal((size, size), (width, height));
            AssertShows(output, expected, size, size, tolerance: 0);
        }
    }

    [Fact]
    public void EveryFrameOfASequenceMatchesWhatAnotherPipelineMakesFromThePackedPixels()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var interop = Interop.Create(context);
        using var reference = CausticTransportPipeline.TryCreate(interop.Device)!;
        using var first = new SourceImage(context, 48, 32, Gradient);
        using var second = new SourceImage(context, 48, 32, (x, y) => x < 20 && y >= 8 ? Gray : Bgra.Transparent);
        var circle = Parameters(focus: 0.3f);
        var plane = circle with { Shape = (int)CausticLightShape.Plane, Aperture = 0.8f };
        var frames = new (SourceImage Source, CausticTransportPipeline.Parameters Parameters)[]
        {
            (first, circle),
            (first, circle),
            (first, circle with { Focus = 0.6f }),
            (first, circle with { Dispersion = 0f }),
            (first, circle with { Roughness = 0f }),
            (first, circle with { Roughness = 0f, Seed = 9 }),
            (first, circle with { Aperture = 0.8f }),
            (second, circle with { Aperture = 0.8f }),
            (second, circle with { Aperture = 0.8f }),
            (second, circle with { Aperture = 0.8f }),
            (second, circle with { Shape = (int)CausticLightShape.HorizontalSlit, Aperture = 0.8f }),
            (second, plane),
            (second, plane with { Aperture = 0.3f }),
            (second, plane with { Quality = CausticTransportQuality.High }),
            (first, plane with { Quality = CausticTransportQuality.High }),
            (first, circle),
        };
        Assert.True(interop.Resources.TryEnsureSource(48, 32, out _));
        Assert.True(interop.Resources.TryEnsureOutput(48, 32, out _));

        for (var index = 0; index < frames.Length; index++)
        {
            var (source, parameters) = frames[index];
            var expected = new int[48 * 32];
            reference.Process(Pixels(source), expected, 48, 32, in parameters);

            interop.Draw(source.Bitmap);
            interop.Process(48, 32, parameters);
            var output = interop.CaptureOutput(context, out _, out _);

            AssertShows(output, expected, 48, 32, tolerance: 0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTransportIsReusedOnceTheSourceAndTheLightSettle(bool sourceChanges)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var interop = Interop.Create(context);
        using var reference = CausticTransportPipeline.TryCreate(interop.Device)!;
        using var first = new SourceImage(context, 48, 32, Gradient);
        using var second = new SourceImage(context, 48, 32, (x, y) => x < 20 && y >= 8 ? Gray : Bgra.Transparent);
        var before = Parameters(focus: 0.3f);
        var after = sourceChanges ? before : before with { Aperture = 0.8f };
        var settled = sourceChanges ? second : first;
        Assert.True(interop.Resources.TryEnsureSource(48, 32, out _));
        Assert.True(interop.Resources.TryEnsureOutput(48, 32, out _));
        interop.Draw(first.Bitmap);
        interop.Process(48, 32, before);

        var recomputed = new List<bool>();
        for (var frame = 0; frame < 3; frame++)
        {
            interop.Draw(settled.Bitmap);
            recomputed.Add(interop.Pipeline.Simulate(interop.Resources.GetSourceComputeBinding(), 48, 32, in after));
            interop.Pipeline.Render(interop.Resources.GetSourceComputeBinding(), interop.Resources.GetOutputComputeBinding(), 48, 32, in after);
        }
        var expected = new int[48 * 32];
        reference.Process(Pixels(settled), expected, 48, 32, in after);
        var output = interop.CaptureOutput(context, out _, out _);

        Assert.True(recomputed[0]);
        Assert.False(recomputed[2]);
        AssertShows(output, expected, 48, 32, tolerance: 0);
    }

    [Fact(Timeout = 60000)]
    public async Task AReclaimedGridIsTransportedAgain()
    {
        await Task.Run(() =>
        {
            using var devices = new GraphicsDevices();
            using var context = devices.CreateContext();
            using var interop = Interop.Create(context);
            using var source = new SourceImage(context, 48, 32, Gradient);
            var parameters = Parameters(focus: 0.3f);
            Assert.True(interop.Resources.TryEnsureSource(48, 32, out _));
            bool Simulate()
            {
                interop.Draw(source.Bitmap);
                return interop.Pipeline.Simulate(interop.Resources.GetSourceComputeBinding(), 48, 32, in parameters);
            }

            var first = Simulate();
            var repeated = Simulate();
            interop.Device.TrimMemory();
            var reclaimed = Simulate();

            Assert.True(first);
            Assert.False(repeated);
            Assert.True(reclaimed);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ASharedPipelineWhoseGridWasReclaimedDrawsLikeAnotherPipeline()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var interop = Interop.Create(context);
        using var reference = CausticTransportPipeline.TryCreate(interop.Device)!;
        using var source = new SourceImage(context, 48, 32, Gradient);
        var parameters = Parameters(focus: 0.3f);
        var expected = new int[48 * 32];
        reference.Process(Pixels(source), expected, 48, 32, in parameters);
        Assert.True(interop.Resources.TryEnsureSource(48, 32, out _));
        Assert.True(interop.Resources.TryEnsureOutput(48, 32, out _));
        interop.Draw(source.Bitmap);
        interop.Process(48, 32, parameters);
        interop.Device.TrimMemory();

        interop.Draw(source.Bitmap);
        interop.Process(48, 32, parameters);
        var output = interop.CaptureOutput(context, out _, out _);

        AssertShows(output, expected, 48, 32, tolerance: 0);
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

    static void AssertShows(Rendering rendering, int[] expected, int width, int height, int tolerance)
    {
        Assert.All(Enumerable.Range(0, height), y =>
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = rendering[x, y];
                var value = pixel.Alpha << 24 | pixel.Red << 16 | pixel.Green << 8 | pixel.Blue;
                var other = expected[y * width + x];
                var close = Math.Abs(pixel.Alpha - ((other >> 24) & 255)) <= tolerance
                    && Math.Abs(pixel.Red - ((other >> 16) & 255)) <= tolerance
                    && Math.Abs(pixel.Green - ((other >> 8) & 255)) <= tolerance
                    && Math.Abs(pixel.Blue - (other & 255)) <= tolerance;
                Assert.True(close, $"({x}, {y}) {value:X8} {other:X8}");
            }
        });
    }
}
