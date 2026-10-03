using System.ComponentModel;
using System.Runtime.InteropServices;
using ComputeWeave;

namespace CausticTransport;

internal sealed class CausticTransportPipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly CausticTransportPipelineHost _host;
    private readonly ReadWriteBuffer<int> _syncBuffer;
    private readonly ReadWriteBuffer<int> _sourceHash;
    private readonly ReadBackBuffer<int> _sourceHashReadBack;
    private TransportKey? _transportKey;
    private ComputeSubmission _pendingSourceHash;
    private bool _hasPendingSourceHash;
    private ContentHash? _lastSourceHash;
    private bool _sourceChanging;
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
        _sourceHash = device.AllocateReadWriteBuffer<int>(CausticTransportSettings.SourceHashLength);
        _sourceHashReadBack = device.AllocateReadBackBuffer<int>(CausticTransportSettings.SourceHashLength);
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
        _ = Simulate(source, width, height, in parameters);
        Render(source, destination, width, height, in parameters);
    }

    internal bool Simulate(
        ComputeResourceBinding<ReadWriteTexture2D<Bgra32, Float4>> source,
        int width,
        int height,
        in Parameters parameters)
    {
        EnsureResources(width, height, parameters.Quality);
        var derived = Derive(width, height, in parameters);
        ResolvePendingSourceHash();
        var deposited = _host.RecordSharedDeposit(source, _sourceHash, width, height, in derived);
        var transportParameters = new TransportParameters(
            width,
            height,
            parameters.Quality,
            parameters.Shape,
            parameters.Shape == (int)CausticLightShape.Plane ? 0f : parameters.Aperture);
        ContentHash? sourceHash = null;
        if (!_sourceChanging && _transportKey is { Source: not null } current && current.Parameters == transportParameters)
        {
            deposited.Wait();
            sourceHash = ReadSourceHash();
            if (current.Source == sourceHash)
                return false;
        }
        else
        {
            _pendingSourceHash = deposited;
            _hasPendingSourceHash = true;
        }

        _ = _host.RecordTransport(in derived, in parameters);
        _transportKey = new TransportKey(transportParameters, sourceHash);
        return true;
    }

    private void ResolvePendingSourceHash()
    {
        if (!_hasPendingSourceHash)
            return;

        _hasPendingSourceHash = false;
        _pendingSourceHash.Wait();
        var sourceHash = ReadSourceHash();
        if (_transportKey is { Source: null } key)
            _transportKey = key with { Source = sourceHash };
    }

    private ContentHash ReadSourceHash()
    {
        _sourceHashReadBack.CopyFrom(_sourceHash);
        var hashed = _sourceHashReadBack.Span;
        var sourceHash = new ContentHash(hashed[CausticTransportSettings.SourceHashSum], hashed[CausticTransportSettings.SourceHashMix]);
        _sourceChanging = _lastSourceHash is { } last && last != sourceHash;
        _lastSourceHash = sourceHash;
        return sourceHash;
    }

    internal void Render(
        ComputeResourceBinding<ReadWriteTexture2D<Bgra32, Float4>> source,
        ComputeResourceBinding<ReadWriteTexture2D<Bgra32, Float4>> output,
        int width,
        int height,
        in Parameters parameters)
    {
        var derived = Derive(width, height, in parameters);
        _ = _host.RecordSharedSplat(source, output, width, height, in derived, in parameters);
    }

    private ComputeSubmission SubmitFullPipeline(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> output,
        int width,
        int height,
        in Parameters parameters)
    {
        _transportKey = null;
        _hasPendingSourceHash = false;
        _lastSourceHash = null;
        _sourceChanging = false;
        var derived = Derive(width, height, in parameters);
        return _host.RecordFullPipeline(source, output, _sourceHash, width, height, in derived, in parameters);
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
                out var changed))
            throw new InvalidOperationException();

        if (changed)
            _transportKey = null;
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
        _transportKey = null;
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
        _sourceHash.Dispose();
        _sourceHashReadBack.Dispose();
    }

    internal readonly record struct ContentHash(int Sum, int Mix);

    private readonly record struct TransportParameters(
        int Width,
        int Height,
        CausticTransportQuality Quality,
        int Shape,
        float Aperture);

    private readonly record struct TransportKey(
        TransportParameters Parameters,
        ContentHash? Source);

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
