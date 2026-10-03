using System.ComponentModel;
using System.Runtime.InteropServices;
using ComputeWeave;

namespace CausticTransport;

internal sealed class CausticTransportPipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly CausticTransportPipelineHost _host;
    private readonly ReadWriteBuffer<int> _syncBuffer;
    private int _gridWidth;
    private int _gridHeight;
    private int _accumulatorCapacity;
    private ReadWriteTexture2D<Bgra32, Float4>? _packedSource;
    private ReadWriteTexture2D<Bgra32, Float4>? _packedOutput;
    private int _packedWidth;
    private int _packedHeight;

    private CausticTransportPipeline(GraphicsDevice device, CausticTransportPipelineHost host)
    {
        _device = device;
        _host = host;
        _syncBuffer = device.AllocateReadWriteBuffer<int>(1);
    }

    internal void WaitForCompletion() => SynchronizeDevice();

    private void SynchronizeDevice()
    {
        using ComputeContext context = _device.CreateComputeContext();
        context.Clear(_syncBuffer);
    }

    public static CausticTransportPipeline? TryCreate()
    {
        try
        {
            return TryCreate(GraphicsDevice.GetDefault());
        }
        catch
        {
            return null;
        }
    }

    public static CausticTransportPipeline? TryCreate(GraphicsDevice device)
    {
        CausticTransportPipelineHost? host = null;
        try
        {
            host = CausticTransportPipelineHost.Create(device, CausticTransportSettings.MaximumPendingSubmissions);
            var pipeline = new CausticTransportPipeline(device, host);
            host = null;
            return pipeline;
        }
        catch (Win32Exception)
        {
            return null;
        }
        finally
        {
            host?.Dispose();
            host?.WaitForDisposal();
        }
    }

    public void Process(ReadOnlySpan<int> source, Span<int> destination, int width, int height, in Parameters parameters)
    {
        var pixelCount = checked(width * height);
        EnsureResources(width, height, parameters.Quality);
        EnsurePackedTextures(width, height);
        var sourceTexture = _packedSource!;
        var outputTexture = _packedOutput!;
        sourceTexture.CopyFrom(MemoryMarshal.Cast<int, Bgra32>(source[..pixelCount]));
        SubmitFullPipeline(sourceTexture, outputTexture, width, height, in parameters).Wait();
        outputTexture.CopyTo(MemoryMarshal.Cast<int, Bgra32>(destination[..pixelCount]));
    }

    public void Process(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> destination,
        int width,
        int height,
        in Parameters parameters)
    {
        EnsureResources(width, height, parameters.Quality);
        _ = SubmitFullPipeline(source, destination, width, height, in parameters);
    }

    internal void ProcessSharedAndWait(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> destination,
        int width,
        int height,
        in Parameters parameters)
    {
        EnsureResources(width, height, parameters.Quality);
        SubmitFullPipeline(source, destination, width, height, in parameters).Wait();
    }

    public void Process(
        ComputeResourceBinding<ReadWriteTexture2D<Bgra32, Float4>> source,
        ComputeResourceBinding<ReadWriteTexture2D<Bgra32, Float4>> destination,
        int width,
        int height,
        in Parameters parameters)
    {
        EnsureResources(width, height, parameters.Quality);
        var derived = Derive(width, height, in parameters);
        _host.RecordSharedPipeline(source, destination, width, height, in derived, in parameters).Wait();
    }

    private ComputeSubmission SubmitFullPipeline(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> output,
        int width,
        int height,
        in Parameters parameters)
    {
        var derived = Derive(width, height, in parameters);
        return _host.RecordFullPipeline(source, output, width, height, in derived, in parameters);
    }

    private DerivedValues Derive(int width, int height, in Parameters parameters)
    {
        var settings = CausticTransportSettings.GetQuality(parameters.Quality);
        return new DerivedValues(
            _gridWidth,
            _gridHeight,
            CausticTransportSettings.GetLevelCount(_gridWidth, _gridHeight),
            settings.TransportIterations,
            settings.JacobiIterations,
            CausticTransportSettings.GetColorFixedScale(checked(width * height)));
    }

    private void EnsureResources(int width, int height, CausticTransportQuality quality)
    {
        var settings = CausticTransportSettings.GetQuality(quality);
        var (gridWidth, gridHeight) = CausticTransportSettings.GetGridSize(width, height, settings.GridResolution);
        var pixelCount = checked(width * height);
        var accumulatorCapacity = Math.Max(_accumulatorCapacity, pixelCount);
        var gridLength = gridWidth * gridHeight;
        var levelCount = CausticTransportSettings.GetLevelCount(gridWidth, gridHeight);
        if (levelCount > CausticTransportSettings.MaximumLevelCount)
            throw new InvalidOperationException();

        Span<int> levelLengths = stackalloc int[CausticTransportSettings.MaximumLevelCount];
        levelLengths.Fill(1);
        for (var level = 0; level < levelCount; level++)
        {
            var (levelWidth, levelHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level);
            levelLengths[level] = levelWidth * levelHeight;
        }

        if (!_host.TryEnsureGrid(
                new CausticTransportGridResources.Plan(
                    accumulatorLength: checked(accumulatorCapacity * 4),
                    densityLength: gridLength,
                    displacementLength: gridLength,
                    phiA0Length: levelLengths[0],
                    phiA1Length: levelLengths[1],
                    phiA2Length: levelLengths[2],
                    phiA3Length: levelLengths[3],
                    phiA4Length: levelLengths[4],
                    phiB0Length: levelLengths[0],
                    phiB1Length: levelLengths[1],
                    phiB2Length: levelLengths[2],
                    phiB3Length: levelLengths[3],
                    phiB4Length: levelLengths[4],
                    residual0Length: levelLengths[0],
                    residual1Length: levelLengths[1],
                    residual2Length: levelLengths[2],
                    residual3Length: levelLengths[3],
                    residual4Length: levelLengths[4],
                    rowSumsLength: gridHeight,
                    scalesLength: 1,
                    sigmaLength: gridLength,
                    warpedFixedLength: gridLength),
                out _))
            throw new InvalidOperationException();

        _gridWidth = gridWidth;
        _gridHeight = gridHeight;
        _accumulatorCapacity = accumulatorCapacity;
    }

    private void EnsurePackedTextures(int width, int height)
    {
        if (_packedWidth == width && _packedHeight == height)
            return;

        _packedSource?.Dispose();
        _packedOutput?.Dispose();
        _packedSource = _device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        _packedOutput = _device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        _packedWidth = width;
        _packedHeight = height;
    }

    public void Dispose()
    {
        _host.Dispose();
        _host.WaitForDisposal();
        _gridWidth = 0;
        _gridHeight = 0;
        _accumulatorCapacity = 0;
        _packedSource?.Dispose();
        _packedOutput?.Dispose();
        _packedSource = null;
        _packedOutput = null;
        _packedWidth = 0;
        _packedHeight = 0;
        _syncBuffer.Dispose();
    }

    internal readonly record struct DerivedValues(
        int GridWidth,
        int GridHeight,
        int LevelCount,
        int TransportIterations,
        int JacobiIterations,
        int ColorScale);

    internal readonly record struct Parameters(
        int Shape,
        CausticTransportQuality Quality,
        float Focus,
        float Aperture,
        float Dispersion,
        float Roughness,
        int Seed);
}
