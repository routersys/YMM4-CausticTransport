using ComputeWeave;

namespace CausticTransport;

[ComputeResourceGroup]
internal sealed partial class CausticTransportGridResources
{
    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> Density { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> Sigma { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<int> WarpedFixed { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<Float2> Displacement { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<Float2> RowSums { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<Float2> Scales { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<uint> Accumulator { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> Residual0 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> Residual1 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> Residual2 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> Residual3 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> Residual4 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiA0 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiA1 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiA2 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiA3 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiA4 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiB0 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiB1 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiB2 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiB3 { get; }

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite)]
    internal ReadWriteBuffer<float> PhiB4 { get; }

    internal ReadWriteBuffer<float> GetResidual(int level) => level switch
    {
        0 => Residual0,
        1 => Residual1,
        2 => Residual2,
        3 => Residual3,
        4 => Residual4,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    internal ReadWriteBuffer<float> GetPhiA(int level) => level switch
    {
        0 => PhiA0,
        1 => PhiA1,
        2 => PhiA2,
        3 => PhiA3,
        4 => PhiA4,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    internal ReadWriteBuffer<float> GetPhiB(int level) => level switch
    {
        0 => PhiB0,
        1 => PhiB1,
        2 => PhiB2,
        3 => PhiB3,
        4 => PhiB4,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}

[ComputePipelineHost("_device", 1)]
internal sealed partial class CausticTransportPipelineHost
{
    private readonly GraphicsDevice _device;

    [ComputePipelineResource(ComputeResourceAccess.ReadWrite, ComputeResourceRecovery.Recompute)]
    private readonly ComputeResourceGroupSlot<CausticTransportGridResources> _grid = new();

    [ComputePipeline]
    private void RecordFullPipeline(
        in ComputeContext context,
        [ComputeOwnedResource(nameof(_grid))] CausticTransportGridResources grid,
        [ComputeResource(ComputeResourceAccess.ReadWrite)] ReadWriteTexture2D<Bgra32, Float4> source,
        [ComputeResource(ComputeResourceAccess.ReadWrite)] ReadWriteTexture2D<Bgra32, Float4> output,
        [ComputeResource(ComputeResourceAccess.ReadWrite)] ReadWriteBuffer<int> sourceHash,
        int width,
        int height,
        in CausticTransportPipeline.DerivedValues derived,
        in CausticTransportPipeline.Parameters parameters)
    {
        _ = _device;

        RecordDepositStage(in context, grid, source, sourceHash, width, height, in derived);
        RecordTransportStage(in context, grid, in derived, in parameters);
        RecordSplatStage(in context, grid, source, output, width, height, in derived, in parameters);
    }

    [ComputePipeline]
    [ComputeInterop]
    private void RecordSharedDeposit(
        in ComputeContext context,
        [ComputeOwnedResource(nameof(_grid))] CausticTransportGridResources grid,
        [ComputeResource(ComputeResourceAccess.ReadWrite, Sharing = ComputeResourceSharing.External)] ReadWriteTexture2D<Bgra32, Float4> source,
        [ComputeResource(ComputeResourceAccess.ReadWrite)] ReadWriteBuffer<int> sourceHash,
        int width,
        int height,
        in CausticTransportPipeline.DerivedValues derived)
    {
        _ = _device;

        RecordDepositStage(in context, grid, source, sourceHash, width, height, in derived);
    }

    [ComputePipeline]
    private void RecordTransport(
        in ComputeContext context,
        [ComputeOwnedResource(nameof(_grid))] CausticTransportGridResources grid,
        in CausticTransportPipeline.DerivedValues derived,
        in CausticTransportPipeline.Parameters parameters)
    {
        _ = _device;

        RecordTransportStage(in context, grid, in derived, in parameters);
    }

    [ComputePipeline]
    [ComputeInterop]
    private void RecordSharedSplat(
        in ComputeContext context,
        [ComputeOwnedResource(nameof(_grid))] CausticTransportGridResources grid,
        [ComputeResource(ComputeResourceAccess.ReadWrite, Sharing = ComputeResourceSharing.External)] ReadWriteTexture2D<Bgra32, Float4> source,
        [ComputeResource(ComputeResourceAccess.ReadWrite, Sharing = ComputeResourceSharing.External)] ReadWriteTexture2D<Bgra32, Float4> output,
        int width,
        int height,
        in CausticTransportPipeline.DerivedValues derived,
        in CausticTransportPipeline.Parameters parameters)
    {
        _ = _device;

        RecordSplatStage(in context, grid, source, output, width, height, in derived, in parameters);
    }

    private static void RecordDepositStage(
        in ComputeContext context,
        CausticTransportGridResources grid,
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteBuffer<int> sourceHash,
        int width,
        int height,
        in CausticTransportPipeline.DerivedValues derived)
    {
        var gridWidth = derived.GridWidth;
        var gridHeight = derived.GridHeight;

        context.Clear(sourceHash);
        context.Barrier(sourceHash);
        context.For(
            ThreadGroupAlignment.AlignX<GridDepositShader>(gridWidth),
            ThreadGroupAlignment.AlignY<GridDepositShader>(gridHeight),
            new GridDepositShader(source, grid.Density, sourceHash, width, height, gridWidth, gridHeight));
        context.Barrier(grid.Density);
    }

    private static void RecordTransportStage(
        in ComputeContext context,
        CausticTransportGridResources grid,
        in CausticTransportPipeline.DerivedValues derived,
        in CausticTransportPipeline.Parameters parameters)
    {
        var gridWidth = derived.GridWidth;
        var gridHeight = derived.GridHeight;
        var gridLength = gridWidth * gridHeight;
        var levelCount = derived.LevelCount;

        context.For(gridWidth, gridHeight, new LightShapeShader(grid.Sigma, gridWidth, gridHeight, parameters.Shape, parameters.Aperture));
        context.Barrier(grid.Sigma);
        context.For(gridHeight, new GridRowSumShader(grid.Density, grid.Sigma, grid.RowSums, gridWidth, gridHeight));
        context.Barrier(grid.RowSums);
        context.For(1, new NormalizeScaleShader(grid.RowSums, grid.Scales, gridHeight, gridLength));
        context.Barrier(grid.Scales);
        context.For(gridWidth, gridHeight, new InitializeDisplacementShader(grid.Displacement, gridWidth, gridHeight, parameters.Shape, parameters.Aperture));
        context.Clear(grid.WarpedFixed);
        context.Barrier(grid.Displacement);
        context.Barrier(grid.WarpedFixed);

        var coarseLevel = CausticTransportSettings.GetCoarseSolveLevel(gridWidth, gridHeight);
        var (coarseLevelWidth, coarseLevelHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, coarseLevel);
        var (coarseFineWidth, coarseFineHeight) = coarseLevel > 0
            ? CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, coarseLevel - 1)
            : (gridWidth, gridHeight);
        for (var iteration = 0; iteration < derived.TransportIterations; iteration++)
        {
            context.For(gridWidth, gridHeight, new PushforwardShader(grid.Density, grid.Scales, grid.Displacement, grid.WarpedFixed, gridWidth, gridHeight, CausticTransportSettings.GridFixedScale));
            context.Barrier(grid.WarpedFixed);
            context.For(gridWidth, gridHeight, new ResidualShader(grid.WarpedFixed, grid.Sigma, grid.Scales, grid.Residual0, gridWidth, gridHeight, 1f / CausticTransportSettings.GridFixedScale));
            context.Barrier(grid.Residual0);
            context.Barrier(grid.WarpedFixed);

            for (var level = 1; level < coarseLevel; level++)
            {
                var (fineWidth, fineHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level - 1);
                var (coarseWidth, coarseHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level);
                context.For(coarseWidth, coarseHeight, new RestrictShader(grid.GetResidual(level - 1), grid.GetResidual(level), fineWidth, fineHeight, coarseWidth, coarseHeight));
                context.Barrier(grid.GetResidual(level));
            }

            context.For(
                CausticTransportSettings.CoarseSolveThreads,
                new CoarseSolveShader(
                    coarseLevel > 0 ? grid.GetResidual(coarseLevel - 1) : grid.Residual0,
                    grid.GetPhiA(coarseLevel),
                    coarseFineWidth,
                    coarseFineHeight,
                    coarseLevelWidth,
                    coarseLevelHeight,
                    levelCount - coarseLevel,
                    derived.JacobiIterations,
                    coarseLevel > 0 ? 1 : 0));
            context.Barrier(grid.GetPhiA(coarseLevel));

            for (var level = coarseLevel - 1; level >= 0; level--)
            {
                var (levelWidth, levelHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level);
                var (belowWidth, belowHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level + 1);
                context.For(levelWidth, levelHeight, new ProlongShader(grid.GetPhiA(level + 1), grid.GetPhiA(level), belowWidth, belowHeight, levelWidth, levelHeight));
                context.Barrier(grid.GetPhiA(level));
                RecordRelaxation(in context, grid, level, levelWidth, levelHeight, derived.JacobiIterations);
            }

            context.For(gridWidth, gridHeight, new UpdateDisplacementShader(grid.PhiA0, grid.Displacement, gridWidth, gridHeight, CausticTransportSettings.DisplacementRelaxation, CausticTransportSettings.DisplacementStepLimit));
            context.Barrier(grid.Displacement);
        }
    }

    private static void RecordRelaxation(
        in ComputeContext context,
        CausticTransportGridResources grid,
        int level,
        int levelWidth,
        int levelHeight,
        int jacobiIterations)
    {
        var blockSteps = CausticTransportSettings.GetJacobiBlockSteps(jacobiIterations);
        var passes = blockSteps > 0 ? CausticTransportSettings.JacobiBlockCount : jacobiIterations;
        for (var pass = 0; pass < passes; pass++)
        {
            var reading = (pass & 1) == 0 ? grid.GetPhiA(level) : grid.GetPhiB(level);
            var writing = (pass & 1) == 0 ? grid.GetPhiB(level) : grid.GetPhiA(level);
            if (blockSteps > 0)
            {
                context.For(
                    ThreadGroupAlignment.AlignX<JacobiBlockShader>(levelWidth),
                    ThreadGroupAlignment.AlignY<JacobiBlockShader>(levelHeight),
                    new JacobiBlockShader(reading, grid.GetResidual(level), writing, levelWidth, levelHeight, blockSteps));
            }
            else
            {
                context.For(levelWidth, levelHeight, new JacobiShader(reading, grid.GetResidual(level), writing, levelWidth, levelHeight));
            }
            context.Barrier(writing);
        }
    }

    private static void RecordSplatStage(
        in ComputeContext context,
        CausticTransportGridResources grid,
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> output,
        int width,
        int height,
        in CausticTransportPipeline.DerivedValues derived,
        in CausticTransportPipeline.Parameters parameters)
    {
        context.Clear(grid.Accumulator);
        context.Barrier(grid.Accumulator);
        var movement = 1f - parameters.Focus;
        var jitterAmplitude = parameters.Roughness * CausticTransportSettings.JitterCellAmplitude;
        context.For(
            ThreadGroupAlignment.AlignX<SplatShader>(width),
            ThreadGroupAlignment.AlignY<SplatShader>(height),
            new SplatShader(
                source,
                grid.Displacement,
                grid.Accumulator,
                width,
                height,
                derived.GridWidth,
                derived.GridHeight,
                movement,
                parameters.Dispersion,
                jitterAmplitude,
                parameters.Seed,
                derived.ColorScale));
        context.Barrier(grid.Accumulator);
        context.For(width, height, new ResolveShader(grid.Accumulator, output, width, height, 1f / derived.ColorScale));
    }
}
