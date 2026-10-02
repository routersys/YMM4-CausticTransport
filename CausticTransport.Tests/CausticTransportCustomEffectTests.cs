using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

namespace CausticTransport.Tests;

[Collection("Direct2D")]
public sealed class CausticTransportCustomEffectTests
{
    const int AmountIndex = 0;
    const int Width = 40;
    const int Height = 24;

    static readonly Bgra Blue = Bgra.Opaque(255, 0, 0);
    static readonly Bgra HalfLight = new(224, 192, 160, 128);

    static Rendering Render(IGraphicsDevicesAndContext devices, ID2D1Image source, ID2D1Image caustic, float amount)
    {
        using var effect = new CausticTransportCustomEffect(devices);
        effect.SetInput(0, source, true);
        effect.SetInput(1, caustic, true);
        effect.Amount = amount;
        using var output = effect.Output;
        return Rendering.Capture(devices, output);
    }

    static AffineTransform2D Translate(IGraphicsDevicesAndContext devices, ID2D1Image image, float dx, float dy)
    {
        var transform = new AffineTransform2D(devices.DeviceContext)
        {
            InterPolationMode = AffineTransform2DInterpolationMode.NearestNeighbor,
            BorderMode = BorderMode.Hard,
            TransformMatrix = Matrix3x2.CreateTranslation(dx, dy),
        };
        transform.SetInput(0, image, true);
        return transform;
    }

    static ID2D1Bitmap1 Premultiplied(IGraphicsDevicesAndContext devices, int width, int height, Bgra color)
    {
        var stride = width * SourceImage.BytesPerPixel;
        var pixels = new byte[stride * height];
        for (var offset = 0; offset < pixels.Length; offset += SourceImage.BytesPerPixel)
        {
            pixels[offset] = color.Blue;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Red;
            pixels[offset + 3] = color.Alpha;
        }

        var bitmap = devices.DeviceContext.CreateBitmap(
            new SizeI(width, height), nint.Zero, stride,
            new BitmapProperties1(SourceImage.PixelFormat, SourceImage.Dpi, SourceImage.Dpi, BitmapOptions.None));
        bitmap.CopyFromMemory(pixels, stride);
        return bitmap;
    }

    static bool WithinRounding(Bgra expected, Bgra actual)
        => Math.Abs(expected.Blue - actual.Blue) <= 1 && Math.Abs(expected.Green - actual.Green) <= 1 && Math.Abs(expected.Red - actual.Red) <= 1 && Math.Abs(expected.Alpha - actual.Alpha) <= 1;

    static Bgra Mix(Bgra source, Bgra caustic, float amount)
    {
        byte Channel(byte sourceValue, byte causticValue) => (byte)Math.Round(sourceValue + (causticValue - sourceValue) * (double)amount, MidpointRounding.AwayFromZero);
        return new Bgra(Channel(source.Blue, caustic.Blue), Channel(source.Green, caustic.Green), Channel(source.Red, caustic.Red), Channel(source.Alpha, caustic.Alpha));
    }

    [Fact]
    public void TheEffectIsEnabledOnceCreated()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var effect = new CausticTransportCustomEffect(context);

        Assert.True(effect.IsEnabled);
    }

    [Fact]
    public void TheAmountStartsFromZero()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var effect = new CausticTransportCustomEffect(context);

        Assert.Equal(0f, effect.GetFloatValue(AmountIndex));
    }

    [Theory]
    [InlineData(-0.5f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.4f, 0.4f)]
    [InlineData(1f, 1f)]
    [InlineData(1.5f, 1f)]
    [InlineData(float.MaxValue, 1f)]
    public void TheAmountIsClampedToTheUnitRange(float value, float expected)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var effect = new CausticTransportCustomEffect(context);

        effect.Amount = value;

        Assert.Equal(expected, effect.GetFloatValue(AmountIndex));
    }

    [Fact]
    public void TheOutputBoundsFollowTheSourceAlone()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = SourceImage.Solid(context, Width, Height, Blue);
        using var caustic = SourceImage.Solid(context, 8, 8, HalfLight);
        using var moved = Translate(context, caustic.Bitmap, 50f, -6f);
        using var movedOutput = moved.Output;
        using var effect = new CausticTransportCustomEffect(context);
        effect.SetInput(0, source.Bitmap, true);
        effect.SetInput(1, movedOutput, true);
        using var output = effect.Output;

        var bounds = context.DeviceContext.GetImageLocalBounds(output);

        Assert.Equal(0f, bounds.Left);
        Assert.Equal(0f, bounds.Top);
        Assert.Equal((float)Width, bounds.Right);
        Assert.Equal((float)Height, bounds.Bottom);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void WithoutAmountTheSourcePassesThroughUntouched(float amount)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = SourceImage.Solid(context, Width, Height, Blue);
        using var caustic = SourceImage.Solid(context, Width, Height, HalfLight);

        var rendering = Render(context, source.Bitmap, caustic.Bitmap, amount);

        Assert.All(rendering.Coordinates(), point => Assert.Equal(source[point.X, point.Y], rendering[point.X, point.Y]));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void TheSourceFadesIntoTheCausticByTheAmount(float amount)
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = SourceImage.Solid(context, Width, Height, Blue);
        using var caustic = SourceImage.Solid(context, Width, Height, HalfLight);
        var expected = Mix(Blue.Premultiplied(), HalfLight.Premultiplied(), amount);

        var rendering = Render(context, source.Bitmap, caustic.Bitmap, amount);

        Assert.All(rendering.Coordinates(), point => Assert.True(WithinRounding(expected, rendering[point.X, point.Y]), $"({point.X}, {point.Y}) {rendering[point.X, point.Y]}"));
    }

    [Fact]
    public void AnAmountChangedAfterADrawReachesTheNextDraw()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = SourceImage.Solid(context, Width, Height, Blue);
        using var caustic = SourceImage.Solid(context, Width, Height, HalfLight);
        using var effect = new CausticTransportCustomEffect(context);
        effect.SetInput(0, source.Bitmap, true);
        effect.SetInput(1, caustic.Bitmap, true);
        using var output = effect.Output;
        effect.Amount = 0.25f;
        var first = Rendering.Capture(context, output);

        effect.Amount = 0.75f;
        var second = Rendering.Capture(context, output);

        var expectedFirst = Mix(Blue.Premultiplied(), HalfLight.Premultiplied(), 0.25f);
        var expectedSecond = Mix(Blue.Premultiplied(), HalfLight.Premultiplied(), 0.75f);
        Assert.All(first.Coordinates(), point => Assert.True(WithinRounding(expectedFirst, first[point.X, point.Y])));
        Assert.All(second.Coordinates(), point => Assert.True(WithinRounding(expectedSecond, second[point.X, point.Y])));
    }

    [Fact]
    public void ColorsBrighterThanTheirAlphaAreHeldToTheAlpha()
    {
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        using var source = SourceImage.Solid(context, Width, Height, Bgra.Transparent);
        using var caustic = Premultiplied(context, Width, Height, new Bgra(240, 200, 255, 128));

        var rendering = Render(context, source.Bitmap, caustic, 1f);

        Assert.All(rendering.Coordinates(), point => Assert.True(WithinRounding(new Bgra(128, 128, 128, 128), rendering[point.X, point.Y]), $"({point.X}, {point.Y}) {rendering[point.X, point.Y]}"));
        caustic.Dispose();
    }
}
