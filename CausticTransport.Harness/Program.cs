using System.Diagnostics;
using CausticTransport;
using ComputeSharp;

var width = 1280;
var height = 720;
var outputDirectory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "harness-output");
Directory.CreateDirectory(outputDirectory);

using var pipeline = CausticTransportPipeline.TryCreate();
if (pipeline is null)
{
    Console.WriteLine("Direct3D 12 is unavailable.");
    return 1;
}

var source = CreateTestImage(width, height);
var destination = new int[source.Length];

var reconstruction = new CausticTransportPipeline.Parameters(0, CausticTransportQuality.High, 1f, 0.5f, 0.5f, 0.5f, 7);
pipeline.Process(source, destination, width, height, in reconstruction);
var maxError = 0;
for (var index = 0; index < source.Length; index++)
{
    for (var shift = 0; shift < 32; shift += 8)
        maxError = Math.Max(maxError, Math.Abs(((source[index] >> shift) & 255) - ((destination[index] >> shift) & 255)));
}
Console.WriteLine($"reconstruction max channel error at focus=1: {maxError}");

foreach (var quality in new[] { CausticTransportQuality.Balanced, CausticTransportQuality.High, CausticTransportQuality.Ultra })
{
    var parameters = new CausticTransportPipeline.Parameters(1, quality, 0.5f, 0.5f, 0.3f, 0.2f, 7);
    pipeline.Process(source, destination, width, height, in parameters);
    pipeline.Process(source, destination, width, height, in parameters);
    var stopwatch = Stopwatch.StartNew();
    const int frames = 10;
    for (var frame = 0; frame < frames; frame++)
        pipeline.Process(source, destination, width, height, in parameters);
    stopwatch.Stop();
    Console.WriteLine($"{quality}: {stopwatch.Elapsed.TotalMilliseconds / frames:F2} ms/frame ({width}x{height})");
}

{
    var device = ComputeSharp.GraphicsDevice.GetDefault();
    foreach (var (benchWidth, benchHeight) in new[] { (1024, 1000), (1920, 1080), (3840, 2160) })
    {
        using var sourceTexture = ComputeSharp.Interop.InteropServices.AllocateSharedReadWriteTexture2D<ComputeSharp.Bgra32, ComputeSharp.Float4>(device, benchWidth, benchHeight);
        using var outputTexture = ComputeSharp.Interop.InteropServices.AllocateSharedReadWriteTexture2D<ComputeSharp.Bgra32, ComputeSharp.Float4>(device, benchWidth, benchHeight);
        var benchSource = CreateTestImage(benchWidth, benchHeight);
        var pixels = new ComputeSharp.Bgra32[benchSource.Length];
        for (var index = 0; index < benchSource.Length; index++)
            pixels[index].PackedValue = unchecked((uint)benchSource[index]);
        sourceTexture.CopyFrom(pixels);
        foreach (var shape in new[] { 0, 1 })
        {
            var parameters = new CausticTransportPipeline.Parameters(shape, CausticTransportQuality.High, 0.5f, 0.5f, 0.3f, 0.2f, 7);
            var median = MeasureMedian(pipeline, sourceTexture, outputTexture, benchWidth, benchHeight, in parameters);
            Console.WriteLine($"shared shape={shape}: {median:F2} ms/frame ({benchWidth}x{benchHeight})");
        }
    }
}

foreach (var shape in new[] { 0, 1, 2, 3 })
{
    foreach (var focus in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
    {
        var parameters = new CausticTransportPipeline.Parameters(shape, CausticTransportQuality.High, focus, 0.4f, 0.4f, 0.25f, 7);
        pipeline.Process(source, destination, width, height, in parameters);
        var stats = ComputeStats(source, destination);
        Console.WriteLine($"shape={shape} focus={focus:F2} massRatio={stats.MassRatio:F4} spreadStdDev={stats.SpreadStdDev:F4}");
        WriteBmp(Path.Combine(outputDirectory, $"shape{shape}_focus{(int)(focus * 100):D3}.bmp"), destination, width, height);
    }
}

WriteBmp(Path.Combine(outputDirectory, "source.bmp"), source, width, height);
Console.WriteLine($"images written to {outputDirectory}");
return 0;

static double MeasureMedian(
    CausticTransportPipeline pipeline,
    ReadWriteTexture2D<Bgra32, Float4> source,
    ReadWriteTexture2D<Bgra32, Float4> destination,
    int width,
    int height,
    in CausticTransportPipeline.Parameters parameters)
{
    for (var warmup = 0; warmup < 5; warmup++)
        pipeline.Process(source, destination, width, height, in parameters);
    pipeline.WaitForCompletion();

    var samples = new double[12];
    for (var sample = 0; sample < samples.Length; sample++)
    {
        const int frames = 20;
        var stopwatch = Stopwatch.StartNew();
        for (var frame = 0; frame < frames; frame++)
            pipeline.Process(source, destination, width, height, in parameters);
        pipeline.WaitForCompletion();
        stopwatch.Stop();
        samples[sample] = stopwatch.Elapsed.TotalMilliseconds / frames;
    }

    Array.Sort(samples);
    return (samples[5] + samples[6]) * 0.5;
}

static int[] CreateTestImage(int width, int height)
{
    var pixels = new int[width * height];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var nx = x / (double)width;
            var ny = y / (double)height;
            var red = (int)(255 * nx);
            var green = (int)(255 * ny);
            var blue = (int)(255 * (0.5 + 0.5 * Math.Sin(nx * 12) * Math.Cos(ny * 9)));
            var inCircle = Math.Pow(nx - 0.3, 2) + Math.Pow(ny - 0.35, 2) < 0.02;
            var inBar = Math.Abs(nx - 0.7) < 0.04 && ny > 0.15 && ny < 0.85;
            if (inCircle)
            {
                red = 255;
                green = 240;
                blue = 210;
            }
            else if (inBar)
            {
                red = 40;
                green = 220;
                blue = 255;
            }
            var alpha = 255;
            if (nx < 0.05 || ny < 0.05 || nx > 0.95 || ny > 0.95)
                alpha = 0;
            red = red * alpha / 255;
            green = green * alpha / 255;
            blue = blue * alpha / 255;
            pixels[y * width + x] = alpha << 24 | red << 16 | green << 8 | blue;
        }
    }
    return pixels;
}

static (double MassRatio, double SpreadStdDev) ComputeStats(int[] source, int[] destination)
{
    double sourceMass = 0;
    double destinationMass = 0;
    foreach (var pixel in source)
        sourceMass += Luminance(pixel);
    foreach (var pixel in destination)
        destinationMass += Luminance(pixel);

    var mean = destinationMass / destination.Length;
    double variance = 0;
    foreach (var pixel in destination)
    {
        var difference = Luminance(pixel) - mean;
        variance += difference * difference;
    }
    return (sourceMass > 0 ? destinationMass / sourceMass : 0, Math.Sqrt(variance / destination.Length));
}

static double Luminance(int pixel)
    => (0.2126 * ((pixel >> 16) & 255) + 0.7152 * ((pixel >> 8) & 255) + 0.0722 * (pixel & 255)) / 255.0;

static void WriteBmp(string path, int[] pixels, int width, int height)
{
    var stride = width * 3;
    var padding = (4 - stride % 4) % 4;
    var dataSize = (stride + padding) * height;
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(stream);
    writer.Write((byte)'B');
    writer.Write((byte)'M');
    writer.Write(54 + dataSize);
    writer.Write(0);
    writer.Write(54);
    writer.Write(40);
    writer.Write(width);
    writer.Write(height);
    writer.Write((short)1);
    writer.Write((short)24);
    writer.Write(0);
    writer.Write(dataSize);
    writer.Write(2835);
    writer.Write(2835);
    writer.Write(0);
    writer.Write(0);
    var pad = new byte[padding];
    for (var y = height - 1; y >= 0; y--)
    {
        for (var x = 0; x < width; x++)
        {
            var pixel = pixels[y * width + x];
            writer.Write((byte)(pixel & 255));
            writer.Write((byte)((pixel >> 8) & 255));
            writer.Write((byte)((pixel >> 16) & 255));
        }
        writer.Write(pad);
    }
}
