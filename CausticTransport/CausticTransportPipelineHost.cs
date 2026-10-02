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
        int width,
        int height,
        in CausticTransportPipeline.DerivedValues derived,
        in CausticTransportPipeline.Parameters parameters)
    {
        _ = _device;

        RecordStages(in context, grid, source, output, width, height, in derived, in parameters);
    }

    private static void RecordStages(
        in ComputeContext context,
        CausticTransportGridResources grid,
        ReadWriteTexture2D<Bgra32, Float4> source,
        ReadWriteTexture2D<Bgra32, Float4> output,
        int width,
        int height,
        in CausticTransportPipeline.DerivedValues derived,
        in CausticTransportPipeline.Parameters parameters)
    {
        var gridWidth = derived.GridWidth;
        var gridHeight = derived.GridHeight;
        var gridLength = gridWidth * gridHeight;
        var levelCount = derived.LevelCount;

        context.For(gridWidth, gridHeight, new GridDepositShader(source, grid.Density, width, height, gridWidth, gridHeight));
        context.For(gridWidth, gridHeight, new LightShapeShader(grid.Sigma, gridWidth, gridHeight, parameters.Shape, parameters.Aperture));
        context.Barrier(grid.Density);
        context.Barrier(grid.Sigma);
        context.For(gridHeight, new GridRowSumShader(grid.Density, grid.Sigma, grid.RowSums, gridWidth, gridHeight));
        context.Barrier(grid.RowSums);
        context.For(1, new NormalizeScaleShader(grid.RowSums, grid.Scales, gridHeight, gridLength));
        context.Barrier(grid.Scales);
        context.For(gridWidth, gridHeight, new InitializeDisplacementShader(grid.Displacement, gridWidth, gridHeight, parameters.Shape, parameters.Aperture));
        context.Clear(grid.WarpedFixed);
        context.Barrier(grid.Displacement);
        context.Barrier(grid.WarpedFixed);

        var coarsest = levelCount - 1;
        for (var iteration = 0; iteration < derived.TransportIterations; iteration++)
        {
            context.For(gridWidth, gridHeight, new PushforwardShader(grid.Density, grid.Scales, grid.Displacement, grid.WarpedFixed, gridWidth, gridHeight, CausticTransportSettings.GridFixedScale));
            context.Barrier(grid.WarpedFixed);
            context.For(gridWidth, gridHeight, new ResidualShader(grid.WarpedFixed, grid.Sigma, grid.Scales, grid.Residual0, gridWidth, gridHeight, 1f / CausticTransportSettings.GridFixedScale));
            context.Barrier(grid.Residual0);
            context.Barrier(grid.WarpedFixed);

            for (var level = 1; level < levelCount; level++)
            {
                var (fineWidth, fineHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level - 1);
                var (coarseWidth, coarseHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level);
                context.For(coarseWidth, coarseHeight, new RestrictShader(grid.GetResidual(level - 1), grid.GetResidual(level), fineWidth, fineHeight, coarseWidth, coarseHeight));
                context.Barrier(grid.GetResidual(level));
            }

            context.Clear(grid.GetPhiA(coarsest));
            context.Barrier(grid.GetPhiA(coarsest));

            for (var level = coarsest; level >= 0; level--)
            {
                var (levelWidth, levelHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level);
                if (level != coarsest)
                {
                    var (belowWidth, belowHeight) = CausticTransportSettings.GetLevelSize(gridWidth, gridHeight, level + 1);
                    context.For(levelWidth, levelHeight, new ProlongShader(grid.GetPhiA(level + 1), grid.GetPhiA(level), belowWidth, belowHeight, levelWidth, levelHeight));
                    context.Barrier(grid.GetPhiA(level));
                }
                for (var step = 0; step < derived.JacobiIterations; step++)
                {
                    var reading = (step & 1) == 0 ? grid.GetPhiA(level) : grid.GetPhiB(level);
                    var writing = (step & 1) == 0 ? grid.GetPhiB(level) : grid.GetPhiA(level);
                    context.For(levelWidth, levelHeight, new JacobiShader(reading, grid.GetResidual(level), writing, levelWidth, levelHeight));
                    context.Barrier(writing);
                }
            }

            context.For(gridWidth, gridHeight, new UpdateDisplacementShader(grid.PhiA0, grid.Displacement, gridWidth, gridHeight, CausticTransportSettings.DisplacementRelaxation, CausticTransportSettings.DisplacementStepLimit));
            context.Barrier(grid.Displacement);
        }

        context.Clear(grid.Accumulator);
        context.Barrier(grid.Accumulator);
        var movement = 1f - parameters.Focus;
        var jitterAmplitude = parameters.Roughness * CausticTransportSettings.JitterCellAmplitude;
        context.For(
            CausticTransportSettings.GetSplatDispatchSize(width),
            CausticTransportSettings.GetSplatDispatchSize(height),
            new SplatShader(
                source,
                grid.Displacement,
                grid.Accumulator,
                width,
                height,
                gridWidth,
                gridHeight,
                movement,
                parameters.Dispersion,
                jitterAmplitude,
                parameters.Seed,
                derived.ColorScale));
        context.Barrier(grid.Accumulator);
        context.For(width, height, new ResolveShader(grid.Accumulator, output, width, height, 1f / derived.ColorScale));
    }
}
