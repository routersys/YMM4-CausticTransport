using System.Runtime.InteropServices;
using ComputeSharp;

namespace CausticTransport;

internal sealed class CausticTransportPipeline : IDisposable
{
    private readonly GraphicsDevice _device;
    private ReadWriteBuffer<float>? _density;
    private ReadWriteBuffer<float>? _sigma;
    private ReadWriteBuffer<int>? _warpedFixed;
    private ReadWriteBuffer<Float2>? _displacement;
    private ReadWriteBuffer<Float2>? _rowSums;
    private ReadWriteBuffer<Float2>? _scales;
    private ReadWriteBuffer<float>[] _residuals = [];
    private ReadWriteBuffer<float>[] _phiA = [];
    private ReadWriteBuffer<float>[] _phiB = [];
    private (int Width, int Height)[] _levelSizes = [];
    private int _gridWidth;
    private int _gridHeight;
    private ReadWriteBuffer<int>? _accumulator;
    private int _accumulatorCapacity;
    private ReadWriteTexture2D<Bgra32, Float4>? _packedSource;
    private ReadWriteTexture2D<Bgra32, Float4>? _packedOutput;
    private int _packedWidth;
    private int _packedHeight;
    private readonly ReadWriteBuffer<int> _syncBuffer;

    private CausticTransportPipeline(GraphicsDevice device)
    {
        _device = device;
        _syncBuffer = device.AllocateReadWriteBuffer<int>(1);
    }

    internal void WaitForCompletion() => SynchronizeDevice();

    private void SynchronizeDevice()
    {
        using ComputeContext context = _device.CreateComputeContext();
        context.For(1, new ClearIntShader(_syncBuffer, 1));
    }

    public static CausticTransportPipeline? TryCreate()
    {
        try
        {
            return new CausticTransportPipeline(GraphicsDevice.GetDefault());
        }
        catch
        {
            return null;
        }
    }

    public static CausticTransportPipeline? TryCreate(GraphicsDevice device)
    {
        try
        {
            return new CausticTransportPipeline(device);
        }
        catch
        {
            return null;
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
        using (ComputeContext context = _device.CreateComputeContext())
            RecordPipeline(in context, sourceTexture, outputTexture, width, height, in parameters);
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
        using ComputeContext context = _device.CreateComputeContext();
        RecordPipeline(in context, source, destination, width, height, in parameters);
        context.Submit();
    }

    internal void ProcessSharedAndWait(
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> destination,
        int width,
        int height,
        in Parameters parameters)
    {
        EnsureResources(width, height, parameters.Quality);
        using ComputeContext context = _device.CreateComputeContext();
        RecordPipeline(in context, source, destination, width, height, in parameters);
    }

    private void RecordPipeline(
        in ComputeContext context,
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> output,
        int width,
        int height,
        in Parameters parameters)
    {
        var settings = CausticTransportSettings.GetQuality(parameters.Quality);
        var gridWidth = _gridWidth;
        var gridHeight = _gridHeight;
        var gridLength = gridWidth * gridHeight;
        var pixelCount = width * height;
        var colorScale = CausticTransportSettings.GetColorFixedScale(pixelCount);
        var levelCount = _levelSizes.Length;

        var density = _density!;
        var sigma = _sigma!;
        var warpedFixed = _warpedFixed!;
        var displacement = _displacement!;
        var rowSums = _rowSums!;
        var scales = _scales!;
        var accumulator = _accumulator!;

        context.For(gridWidth, gridHeight, new GridDepositShader(source, density, width, height, gridWidth, gridHeight));
        context.For(gridWidth, gridHeight, new LightShapeShader(sigma, gridWidth, gridHeight, parameters.Shape, parameters.Aperture));
        context.Barrier(density);
        context.Barrier(sigma);
        context.For(gridHeight, new GridRowSumShader(density, sigma, rowSums, gridWidth, gridHeight));
        context.Barrier(rowSums);
        context.For(1, new NormalizeScaleShader(rowSums, scales, gridHeight, gridLength));
        context.Barrier(scales);
        context.For(gridWidth, gridHeight, new InitializeDisplacementShader(displacement, gridWidth, gridHeight, parameters.Shape, parameters.Aperture));
        context.For(gridLength, new ClearIntShader(warpedFixed, gridLength));
        context.Barrier(displacement);
        context.Barrier(warpedFixed);

        var coarsest = levelCount - 1;
        for (var iteration = 0; iteration < settings.TransportIterations; iteration++)
        {
            context.For(gridWidth, gridHeight, new PushforwardShader(density, scales, displacement, warpedFixed, gridWidth, gridHeight, CausticTransportSettings.GridFixedScale));
            context.Barrier(warpedFixed);
            context.For(gridWidth, gridHeight, new ResidualShader(warpedFixed, sigma, scales, _residuals[0], gridWidth, gridHeight, 1f / CausticTransportSettings.GridFixedScale));
            context.Barrier(_residuals[0]);
            context.Barrier(warpedFixed);

            for (var level = 1; level < levelCount; level++)
            {
                var (fineWidth, fineHeight) = _levelSizes[level - 1];
                var (coarseWidth, coarseHeight) = _levelSizes[level];
                context.For(coarseWidth, coarseHeight, new RestrictShader(_residuals[level - 1], _residuals[level], fineWidth, fineHeight, coarseWidth, coarseHeight));
                context.Barrier(_residuals[level]);
            }

            var (coarsestWidth, coarsestHeight) = _levelSizes[coarsest];
            context.For(coarsestWidth * coarsestHeight, new ClearFloatShader(_phiA[coarsest], coarsestWidth * coarsestHeight));
            context.Barrier(_phiA[coarsest]);

            for (var level = coarsest; level >= 0; level--)
            {
                var (levelWidth, levelHeight) = _levelSizes[level];
                if (level != coarsest)
                {
                    var (belowWidth, belowHeight) = _levelSizes[level + 1];
                    context.For(levelWidth, levelHeight, new ProlongShader(_phiA[level + 1], _phiA[level], belowWidth, belowHeight, levelWidth, levelHeight));
                    context.Barrier(_phiA[level]);
                }
                for (var step = 0; step < settings.JacobiIterations; step++)
                {
                    var reading = (step & 1) == 0 ? _phiA[level] : _phiB[level];
                    var writing = (step & 1) == 0 ? _phiB[level] : _phiA[level];
                    context.For(levelWidth, levelHeight, new JacobiShader(reading, _residuals[level], writing, levelWidth, levelHeight));
                    context.Barrier(writing);
                }
            }

            context.For(gridWidth, gridHeight, new UpdateDisplacementShader(_phiA[0], displacement, gridWidth, gridHeight, CausticTransportSettings.DisplacementRelaxation, CausticTransportSettings.DisplacementStepLimit));
            context.Barrier(displacement);
        }

        var accumulatorLength = pixelCount * 4;
        context.For(accumulatorLength, new ClearIntShader(accumulator, accumulatorLength));
        context.Barrier(accumulator);
        var movement = 1f - parameters.Focus;
        var jitterAmplitude = parameters.Roughness * CausticTransportSettings.JitterCellAmplitude;
        context.For(width, height, new SplatShader(
            source,
            displacement,
            accumulator,
            width,
            height,
            gridWidth,
            gridHeight,
            movement,
            parameters.Dispersion,
            jitterAmplitude,
            parameters.Seed,
            colorScale));
        context.Barrier(accumulator);
        context.For(width, height, new ResolveShader(accumulator, output, width, height, 1f / colorScale));
    }

    private void EnsureResources(int width, int height, CausticTransportQuality quality)
    {
        var settings = CausticTransportSettings.GetQuality(quality);
        var (gridWidth, gridHeight) = CausticTransportSettings.GetGridSize(width, height, settings.GridResolution);
        EnsureGrid(gridWidth, gridHeight);
        EnsureAccumulator(checked(width * height));
    }

    private void EnsureGrid(int gridWidth, int gridHeight)
    {
        if (_gridWidth == gridWidth && _gridHeight == gridHeight)
            return;

        DisposeGridBuffers();
        var gridLength = gridWidth * gridHeight;
        var levelCount = CausticTransportSettings.GetLevelCount(gridWidth, gridHeight);
        var levelSizes = new (int Width, int Height)[levelCount];
        var residuals = new ReadWriteBuffer<float>[levelCount];
        var phiA = new ReadWriteBuffer<float>[levelCount];
        var phiB = new ReadWriteBuffer<float>[levelCount];
        var (levelWidth, levelHeight) = (gridWidth, gridHeight);
        for (var level = 0; level < levelCount; level++)
        {
            levelSizes[level] = (levelWidth, levelHeight);
            var levelLength = levelWidth * levelHeight;
            residuals[level] = _device.AllocateReadWriteBuffer<float>(levelLength);
            phiA[level] = _device.AllocateReadWriteBuffer<float>(levelLength);
            phiB[level] = _device.AllocateReadWriteBuffer<float>(levelLength);
            (levelWidth, levelHeight) = CausticTransportSettings.GetCoarserLevelSize(levelWidth, levelHeight);
        }

        _density = _device.AllocateReadWriteBuffer<float>(gridLength);
        _sigma = _device.AllocateReadWriteBuffer<float>(gridLength);
        _warpedFixed = _device.AllocateReadWriteBuffer<int>(gridLength);
        _displacement = _device.AllocateReadWriteBuffer<Float2>(gridLength);
        _rowSums = _device.AllocateReadWriteBuffer<Float2>(gridHeight);
        _scales ??= _device.AllocateReadWriteBuffer<Float2>(1);
        _residuals = residuals;
        _phiA = phiA;
        _phiB = phiB;
        _levelSizes = levelSizes;
        _gridWidth = gridWidth;
        _gridHeight = gridHeight;
    }

    private void EnsureAccumulator(int pixelCount)
    {
        if (_accumulatorCapacity >= pixelCount)
            return;

        if (_accumulator is not null)
        {
            SynchronizeDevice();
            _accumulator.Dispose();
        }
        _accumulator = _device.AllocateReadWriteBuffer<int>(pixelCount * 4);
        _accumulatorCapacity = pixelCount;
    }

    private void EnsurePackedTextures(int width, int height)
    {
        if (_packedWidth == width && _packedHeight == height)
            return;

        if (_packedSource is not null)
        {
            SynchronizeDevice();
            _packedSource.Dispose();
            _packedOutput?.Dispose();
        }
        _packedSource = _device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        _packedOutput = _device.AllocateReadWriteTexture2D<Bgra32, Float4>(width, height);
        _packedWidth = width;
        _packedHeight = height;
    }

    private void DisposeGridBuffers()
    {
        if (_gridWidth != 0)
            SynchronizeDevice();
        foreach (var buffer in _residuals)
            buffer.Dispose();
        foreach (var buffer in _phiA)
            buffer.Dispose();
        foreach (var buffer in _phiB)
            buffer.Dispose();
        _residuals = [];
        _phiA = [];
        _phiB = [];
        _levelSizes = [];
        _density?.Dispose();
        _sigma?.Dispose();
        _warpedFixed?.Dispose();
        _displacement?.Dispose();
        _rowSums?.Dispose();
        _density = null;
        _sigma = null;
        _warpedFixed = null;
        _displacement = null;
        _rowSums = null;
        _gridWidth = 0;
        _gridHeight = 0;
    }

    public void Dispose()
    {
        DisposeGridBuffers();
        _scales?.Dispose();
        _scales = null;
        _accumulator?.Dispose();
        _accumulator = null;
        _accumulatorCapacity = 0;
        _packedSource?.Dispose();
        _packedOutput?.Dispose();
        _packedSource = null;
        _packedOutput = null;
        _packedWidth = 0;
        _packedHeight = 0;
        _syncBuffer.Dispose();
    }

    internal readonly record struct Parameters(
        int Shape,
        CausticTransportQuality Quality,
        float Focus,
        float Aperture,
        float Dispersion,
        float Roughness,
        int Seed);
}
