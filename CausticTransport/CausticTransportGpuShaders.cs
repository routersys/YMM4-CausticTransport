using ComputeSharp;

namespace CausticTransport;

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct InitializeDisplacementShader(
    ReadWriteBuffer<Float2> displacement,
    int gridWidth,
    int gridHeight,
    int shape,
    float aperture) : IComputeShader
{
    private readonly ReadWriteBuffer<Float2> displacement = displacement;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;
    private readonly int shape = shape;
    private readonly float aperture = aperture;

    public void Execute()
    {
        var gx = ThreadIds.X;
        var gy = ThreadIds.Y;
        if (gx >= gridWidth || gy >= gridHeight)
            return;

        var centerX = gridWidth * 0.5f;
        var centerY = gridHeight * 0.5f;
        var x = gx + 0.5f;
        var y = gy + 0.5f;
        var minDim = Hlsl.Min(gridWidth, gridHeight);
        var radius = aperture * 0.5f * minDim;

        var targetX = x;
        var targetY = y;
        if (shape == 1)
        {
            var a = Hlsl.Clamp(x / gridWidth * 2f - 1f, -1f, 1f);
            var b = Hlsl.Clamp(y / gridHeight * 2f - 1f, -1f, 1f);
            var r = 0f;
            var theta = 0f;
            if (a * a > b * b)
            {
                r = a;
                theta = 0.785398163f * (b / a);
            }
            else if (b != 0f)
            {
                r = b;
                theta = 1.570796327f - 0.785398163f * (a / b);
            }
            targetX = centerX + r * radius * Hlsl.Cos(theta);
            targetY = centerY + r * radius * Hlsl.Sin(theta);
        }
        else if (shape == 2)
        {
            targetY = centerY + (y - centerY) * (2f * radius / gridHeight);
        }
        else if (shape == 3)
        {
            targetX = centerX + (x - centerX) * (2f * radius / gridWidth);
        }

        displacement[gy * gridWidth + gx] = new Float2(targetX - x, targetY - y);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct GridDepositShader(
    ReadWriteTexture2D<Bgra32, Float4> source,
    ReadWriteBuffer<float> density,
    int width,
    int height,
    int gridWidth,
    int gridHeight) : IComputeShader
{
    private readonly ReadWriteTexture2D<Bgra32, Float4> source = source;
    private readonly ReadWriteBuffer<float> density = density;
    private readonly int width = width;
    private readonly int height = height;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;

    public void Execute()
    {
        var gx = ThreadIds.X;
        var gy = ThreadIds.Y;
        if (gx >= gridWidth || gy >= gridHeight)
            return;

        var x0 = gx * width / gridWidth;
        var x1 = (gx + 1) * width / gridWidth;
        var y0 = gy * height / gridHeight;
        var y1 = (gy + 1) * height / gridHeight;

        var sum = 0f;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var pixel = source[new Int2(x, y)];
                sum += 0.2126f * pixel.X + 0.7152f * pixel.Y + 0.0722f * pixel.Z + 0.01f * pixel.W;
            }
        }
        density[gy * gridWidth + gx] = sum;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct LightShapeShader(
    ReadWriteBuffer<float> sigma,
    int gridWidth,
    int gridHeight,
    int shape,
    float aperture) : IComputeShader
{
    private readonly ReadWriteBuffer<float> sigma = sigma;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;
    private readonly int shape = shape;
    private readonly float aperture = aperture;

    public void Execute()
    {
        var gx = ThreadIds.X;
        var gy = ThreadIds.Y;
        if (gx >= gridWidth || gy >= gridHeight)
            return;

        var px = gx + 0.5f - gridWidth * 0.5f;
        var py = gy + 0.5f - gridHeight * 0.5f;
        var minDim = Hlsl.Min(gridWidth, gridHeight);
        var radius = aperture * 0.5f * minDim;
        var feather = Hlsl.Max(minDim * 0.05f, 1.5f);

        var metric = 0f;
        if (shape == 1)
            metric = Hlsl.Sqrt(px * px + py * py);
        else if (shape == 2)
            metric = Hlsl.Abs(py);
        else if (shape == 3)
            metric = Hlsl.Abs(px);

        var density = shape == 0 ? 1f : 1f - Hlsl.SmoothStep(radius - feather, radius + feather, metric);
        sigma[gy * gridWidth + gx] = Hlsl.Max(density, 0.004f);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct GridRowSumShader(
    ReadWriteBuffer<float> density,
    ReadWriteBuffer<float> sigma,
    ReadWriteBuffer<Float2> rowSums,
    int gridWidth,
    int gridHeight) : IComputeShader
{
    private readonly ReadWriteBuffer<float> density = density;
    private readonly ReadWriteBuffer<float> sigma = sigma;
    private readonly ReadWriteBuffer<Float2> rowSums = rowSums;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;

    public void Execute()
    {
        var row = ThreadIds.X;
        if (row >= gridHeight)
            return;

        var densitySum = 0f;
        var sigmaSum = 0f;
        var offset = row * gridWidth;
        for (var x = 0; x < gridWidth; x++)
        {
            densitySum += density[offset + x];
            sigmaSum += sigma[offset + x];
        }
        rowSums[row] = new Float2(densitySum, sigmaSum);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct NormalizeScaleShader(
    ReadWriteBuffer<Float2> rowSums,
    ReadWriteBuffer<Float2> scales,
    int gridHeight,
    int gridLength) : IComputeShader
{
    private readonly ReadWriteBuffer<Float2> rowSums = rowSums;
    private readonly ReadWriteBuffer<Float2> scales = scales;
    private readonly int gridHeight = gridHeight;
    private readonly int gridLength = gridLength;

    public void Execute()
    {
        if (ThreadIds.X != 0)
            return;

        var densitySum = 0f;
        var sigmaSum = 0f;
        for (var row = 0; row < gridHeight; row++)
        {
            var sums = rowSums[row];
            densitySum += sums.X;
            sigmaSum += sums.Y;
        }

        var valid = densitySum > 1e-9f && sigmaSum > 1e-9f;
        scales[0] = new Float2(valid ? gridLength / densitySum : 0f, valid ? gridLength / sigmaSum : 0f);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct PushforwardShader(
    ReadWriteBuffer<float> density,
    ReadWriteBuffer<Float2> scales,
    ReadWriteBuffer<Float2> displacement,
    ReadWriteBuffer<int> warpedFixed,
    int gridWidth,
    int gridHeight,
    float fixedScale) : IComputeShader
{
    private readonly ReadWriteBuffer<float> density = density;
    private readonly ReadWriteBuffer<Float2> scales = scales;
    private readonly ReadWriteBuffer<Float2> displacement = displacement;
    private readonly ReadWriteBuffer<int> warpedFixed = warpedFixed;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;
    private readonly float fixedScale = fixedScale;

    public void Execute()
    {
        var gx = ThreadIds.X;
        var gy = ThreadIds.Y;
        if (gx >= gridWidth || gy >= gridHeight)
            return;

        var index = gy * gridWidth + gx;
        var mass = density[index] * scales[0].X;
        if (mass <= 0f)
            return;

        var offset = displacement[index];
        var posX = Hlsl.Clamp(gx + 0.5f + offset.X, 0.5f, gridWidth - 0.5f);
        var posY = Hlsl.Clamp(gy + 0.5f + offset.Y, 0.5f, gridHeight - 0.5f);
        var px = posX - 0.5f;
        var py = posY - 0.5f;
        var ix0 = (int)px;
        var iy0 = (int)py;
        var fx = px - ix0;
        var fy = py - iy0;
        var ix1 = Hlsl.Min(ix0 + 1, gridWidth - 1);
        var iy1 = Hlsl.Min(iy0 + 1, gridHeight - 1);

        var scaled = mass * fixedScale;
        Hlsl.InterlockedAdd(ref warpedFixed[iy0 * gridWidth + ix0], (int)Hlsl.Round(scaled * (1f - fx) * (1f - fy)));
        Hlsl.InterlockedAdd(ref warpedFixed[iy0 * gridWidth + ix1], (int)Hlsl.Round(scaled * fx * (1f - fy)));
        Hlsl.InterlockedAdd(ref warpedFixed[iy1 * gridWidth + ix0], (int)Hlsl.Round(scaled * (1f - fx) * fy));
        Hlsl.InterlockedAdd(ref warpedFixed[iy1 * gridWidth + ix1], (int)Hlsl.Round(scaled * fx * fy));
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct ResidualShader(
    ReadWriteBuffer<int> warpedFixed,
    ReadWriteBuffer<float> sigma,
    ReadWriteBuffer<Float2> scales,
    ReadWriteBuffer<float> residual,
    int gridWidth,
    int gridHeight,
    float inverseFixedScale) : IComputeShader
{
    private readonly ReadWriteBuffer<int> warpedFixed = warpedFixed;
    private readonly ReadWriteBuffer<float> sigma = sigma;
    private readonly ReadWriteBuffer<Float2> scales = scales;
    private readonly ReadWriteBuffer<float> residual = residual;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;
    private readonly float inverseFixedScale = inverseFixedScale;

    public void Execute()
    {
        var gx = ThreadIds.X;
        var gy = ThreadIds.Y;
        if (gx >= gridWidth || gy >= gridHeight)
            return;

        var index = gy * gridWidth + gx;
        residual[index] = warpedFixed[index] * inverseFixedScale - sigma[index] * scales[0].Y;
        warpedFixed[index] = 0;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct RestrictShader(
    ReadWriteBuffer<float> fineResidual,
    ReadWriteBuffer<float> coarseResidual,
    int fineWidth,
    int fineHeight,
    int coarseWidth,
    int coarseHeight) : IComputeShader
{
    private readonly ReadWriteBuffer<float> fineResidual = fineResidual;
    private readonly ReadWriteBuffer<float> coarseResidual = coarseResidual;
    private readonly int fineWidth = fineWidth;
    private readonly int fineHeight = fineHeight;
    private readonly int coarseWidth = coarseWidth;
    private readonly int coarseHeight = coarseHeight;

    public void Execute()
    {
        var cx = ThreadIds.X;
        var cy = ThreadIds.Y;
        if (cx >= coarseWidth || cy >= coarseHeight)
            return;

        var fx0 = Hlsl.Min(cx * 2, fineWidth - 1);
        var fx1 = Hlsl.Min(cx * 2 + 1, fineWidth - 1);
        var fy0 = Hlsl.Min(cy * 2, fineHeight - 1);
        var fy1 = Hlsl.Min(cy * 2 + 1, fineHeight - 1);

        coarseResidual[cy * coarseWidth + cx] =
            fineResidual[fy0 * fineWidth + fx0] +
            fineResidual[fy0 * fineWidth + fx1] +
            fineResidual[fy1 * fineWidth + fx0] +
            fineResidual[fy1 * fineWidth + fx1];
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct JacobiShader(
    ReadWriteBuffer<float> phiIn,
    ReadWriteBuffer<float> residual,
    ReadWriteBuffer<float> phiOut,
    int gridWidth,
    int gridHeight) : IComputeShader
{
    private readonly ReadWriteBuffer<float> phiIn = phiIn;
    private readonly ReadWriteBuffer<float> residual = residual;
    private readonly ReadWriteBuffer<float> phiOut = phiOut;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;

    public void Execute()
    {
        var gx = ThreadIds.X;
        var gy = ThreadIds.Y;
        if (gx >= gridWidth || gy >= gridHeight)
            return;

        var index = gy * gridWidth + gx;
        var center = phiIn[index];
        var left = gx > 0 ? phiIn[index - 1] : center;
        var right = gx < gridWidth - 1 ? phiIn[index + 1] : center;
        var up = gy > 0 ? phiIn[index - gridWidth] : center;
        var down = gy < gridHeight - 1 ? phiIn[index + gridWidth] : center;
        phiOut[index] = (left + right + up + down - residual[index]) * 0.25f;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct ProlongShader(
    ReadWriteBuffer<float> coarsePhi,
    ReadWriteBuffer<float> finePhi,
    int coarseWidth,
    int coarseHeight,
    int fineWidth,
    int fineHeight) : IComputeShader
{
    private readonly ReadWriteBuffer<float> coarsePhi = coarsePhi;
    private readonly ReadWriteBuffer<float> finePhi = finePhi;
    private readonly int coarseWidth = coarseWidth;
    private readonly int coarseHeight = coarseHeight;
    private readonly int fineWidth = fineWidth;
    private readonly int fineHeight = fineHeight;

    public void Execute()
    {
        var fx = ThreadIds.X;
        var fy = ThreadIds.Y;
        if (fx >= fineWidth || fy >= fineHeight)
            return;

        var px = Hlsl.Clamp((fx + 0.5f) * coarseWidth / fineWidth - 0.5f, 0f, coarseWidth - 1f);
        var py = Hlsl.Clamp((fy + 0.5f) * coarseHeight / fineHeight - 0.5f, 0f, coarseHeight - 1f);
        var ix0 = (int)px;
        var iy0 = (int)py;
        var wx = px - ix0;
        var wy = py - iy0;
        var ix1 = Hlsl.Min(ix0 + 1, coarseWidth - 1);
        var iy1 = Hlsl.Min(iy0 + 1, coarseHeight - 1);

        var top = Hlsl.Lerp(coarsePhi[iy0 * coarseWidth + ix0], coarsePhi[iy0 * coarseWidth + ix1], wx);
        var bottom = Hlsl.Lerp(coarsePhi[iy1 * coarseWidth + ix0], coarsePhi[iy1 * coarseWidth + ix1], wx);
        finePhi[fy * fineWidth + fx] = Hlsl.Lerp(top, bottom, wy);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct UpdateDisplacementShader(
    ReadWriteBuffer<float> phi,
    ReadWriteBuffer<Float2> displacement,
    int gridWidth,
    int gridHeight,
    float relaxation,
    float stepLimit) : IComputeShader
{
    private readonly ReadWriteBuffer<float> phi = phi;
    private readonly ReadWriteBuffer<Float2> displacement = displacement;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;
    private readonly float relaxation = relaxation;
    private readonly float stepLimit = stepLimit;

    public void Execute()
    {
        var gx = ThreadIds.X;
        var gy = ThreadIds.Y;
        if (gx >= gridWidth || gy >= gridHeight)
            return;

        var index = gy * gridWidth + gx;
        var centerX = gx + 0.5f;
        var centerY = gy + 0.5f;
        var offset = displacement[index];
        var posX = Hlsl.Clamp(centerX + offset.X, 0.5f, gridWidth - 0.5f);
        var posY = Hlsl.Clamp(centerY + offset.Y, 0.5f, gridHeight - 0.5f);

        var gradX = relaxation * (SamplePhi(posX + 0.5f, posY) - SamplePhi(posX - 0.5f, posY));
        var gradY = relaxation * (SamplePhi(posX, posY + 0.5f) - SamplePhi(posX, posY - 0.5f));
        var gradLength = Hlsl.Sqrt(gradX * gradX + gradY * gradY);
        if (gradLength > stepLimit)
        {
            var stepScale = stepLimit / gradLength;
            gradX *= stepScale;
            gradY *= stepScale;
        }

        var newX = Hlsl.Clamp(posX + gradX, 0.5f, gridWidth - 0.5f);
        var newY = Hlsl.Clamp(posY + gradY, 0.5f, gridHeight - 0.5f);
        displacement[index] = new Float2(newX - centerX, newY - centerY);
    }

    private float SamplePhi(float x, float y)
    {
        var px = Hlsl.Clamp(x, 0.5f, gridWidth - 0.5f) - 0.5f;
        var py = Hlsl.Clamp(y, 0.5f, gridHeight - 0.5f) - 0.5f;
        var ix0 = (int)px;
        var iy0 = (int)py;
        var wx = px - ix0;
        var wy = py - iy0;
        var ix1 = Hlsl.Min(ix0 + 1, gridWidth - 1);
        var iy1 = Hlsl.Min(iy0 + 1, gridHeight - 1);

        var top = Hlsl.Lerp(phi[iy0 * gridWidth + ix0], phi[iy0 * gridWidth + ix1], wx);
        var bottom = Hlsl.Lerp(phi[iy1 * gridWidth + ix0], phi[iy1 * gridWidth + ix1], wx);
        return Hlsl.Lerp(top, bottom, wy);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct SplatShader(
    ReadWriteTexture2D<Bgra32, Float4> source,
    ReadWriteBuffer<Float2> displacement,
    ReadWriteBuffer<int> accumulator,
    int width,
    int height,
    int gridWidth,
    int gridHeight,
    float movement,
    float dispersion,
    float jitterAmplitude,
    int seed,
    float colorScale) : IComputeShader
{
    private readonly ReadWriteTexture2D<Bgra32, Float4> source = source;
    private readonly ReadWriteBuffer<Float2> displacement = displacement;
    private readonly ReadWriteBuffer<int> accumulator = accumulator;
    private readonly int width = width;
    private readonly int height = height;
    private readonly int gridWidth = gridWidth;
    private readonly int gridHeight = gridHeight;
    private readonly float movement = movement;
    private readonly float dispersion = dispersion;
    private readonly float jitterAmplitude = jitterAmplitude;
    private readonly int seed = seed;
    private readonly float colorScale = colorScale;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var pixel = source[ThreadIds.XY];
        if (pixel.X <= 0f && pixel.Y <= 0f && pixel.Z <= 0f && pixel.W <= 0f)
            return;

        var transport = SampleDisplacement(x, y);
        var baseX = x + 0.5f;
        var baseY = y + 0.5f;
        if (dispersion <= 0f)
        {
            SplatAll(baseX + transport.X * movement, baseY + transport.Y * movement, pixel);
        }
        else
        {
            var spread = dispersion * 0.35f;
            var movementRed = Hlsl.Saturate(movement * (1f + spread));
            var movementBlue = Hlsl.Saturate(movement * (1f - spread));
            var alphaShare = pixel.W * (1f / 3f);
            SplatChannel(baseX + transport.X * movementRed, baseY + transport.Y * movementRed, 0, pixel.X, alphaShare);
            SplatChannel(baseX + transport.X * movement, baseY + transport.Y * movement, 1, pixel.Y, alphaShare);
            SplatChannel(baseX + transport.X * movementBlue, baseY + transport.Y * movementBlue, 2, pixel.Z, alphaShare);
        }
    }

    private Float2 SampleDisplacement(int x, int y)
    {
        var px = Hlsl.Clamp((x + 0.5f) * gridWidth / width - 0.5f, 0f, gridWidth - 1f);
        var py = Hlsl.Clamp((y + 0.5f) * gridHeight / height - 0.5f, 0f, gridHeight - 1f);
        var ix0 = (int)px;
        var iy0 = (int)py;
        var wx = px - ix0;
        var wy = py - iy0;
        var ix1 = Hlsl.Min(ix0 + 1, gridWidth - 1);
        var iy1 = Hlsl.Min(iy0 + 1, gridHeight - 1);

        var top = Hlsl.Lerp(CellValue(ix0, iy0), CellValue(ix1, iy0), wx);
        var bottom = Hlsl.Lerp(CellValue(ix0, iy1), CellValue(ix1, iy1), wx);
        var cell = Hlsl.Lerp(top, bottom, wy);
        return new Float2(cell.X * width / gridWidth, cell.Y * height / gridHeight);
    }

    private Float2 CellValue(int cellX, int cellY)
    {
        var index = cellY * gridWidth + cellX;
        var value = displacement[index];
        if (jitterAmplitude > 0f)
        {
            var hash = (uint)index * 0x9e3779b9u ^ (uint)seed * 0x85ebca6bu;
            value.X += (Hash01(hash) * 2f - 1f) * jitterAmplitude;
            value.Y += (Hash01(hash ^ 0x68bc21ebu) * 2f - 1f) * jitterAmplitude;
        }
        return value;
    }

    private void SplatAll(float x, float y, Float4 value)
    {
        var px = Hlsl.Clamp(x, 0.5f, width - 0.5f) - 0.5f;
        var py = Hlsl.Clamp(y, 0.5f, height - 0.5f) - 0.5f;
        var ix0 = (int)px;
        var iy0 = (int)py;
        var wx = px - ix0;
        var wy = py - iy0;
        var ix1 = Hlsl.Min(ix0 + 1, width - 1);
        var iy1 = Hlsl.Min(iy0 + 1, height - 1);

        SplatAllTap((iy0 * width + ix0) * 4, value, (1f - wx) * (1f - wy));
        SplatAllTap((iy0 * width + ix1) * 4, value, wx * (1f - wy));
        SplatAllTap((iy1 * width + ix0) * 4, value, (1f - wx) * wy);
        SplatAllTap((iy1 * width + ix1) * 4, value, wx * wy);
    }

    private void SplatAllTap(int index4, Float4 value, float weight)
    {
        var scaled = weight * colorScale;
        Hlsl.InterlockedAdd(ref accumulator[index4], (int)Hlsl.Round(value.X * scaled));
        Hlsl.InterlockedAdd(ref accumulator[index4 + 1], (int)Hlsl.Round(value.Y * scaled));
        Hlsl.InterlockedAdd(ref accumulator[index4 + 2], (int)Hlsl.Round(value.Z * scaled));
        Hlsl.InterlockedAdd(ref accumulator[index4 + 3], (int)Hlsl.Round(value.W * scaled));
    }

    private void SplatChannel(float x, float y, int channel, float value, float alphaShare)
    {
        var px = Hlsl.Clamp(x, 0.5f, width - 0.5f) - 0.5f;
        var py = Hlsl.Clamp(y, 0.5f, height - 0.5f) - 0.5f;
        var ix0 = (int)px;
        var iy0 = (int)py;
        var wx = px - ix0;
        var wy = py - iy0;
        var ix1 = Hlsl.Min(ix0 + 1, width - 1);
        var iy1 = Hlsl.Min(iy0 + 1, height - 1);

        SplatChannelTap((iy0 * width + ix0) * 4, channel, value, alphaShare, (1f - wx) * (1f - wy));
        SplatChannelTap((iy0 * width + ix1) * 4, channel, value, alphaShare, wx * (1f - wy));
        SplatChannelTap((iy1 * width + ix0) * 4, channel, value, alphaShare, (1f - wx) * wy);
        SplatChannelTap((iy1 * width + ix1) * 4, channel, value, alphaShare, wx * wy);
    }

    private void SplatChannelTap(int index4, int channel, float value, float alphaShare, float weight)
    {
        var scaled = weight * colorScale;
        Hlsl.InterlockedAdd(ref accumulator[index4 + channel], (int)Hlsl.Round(value * scaled));
        Hlsl.InterlockedAdd(ref accumulator[index4 + 3], (int)Hlsl.Round(alphaShare * scaled));
    }

    private float Hash01(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return value * 2.3283064e-10f;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct ResolveShader(
    ReadWriteBuffer<int> accumulator,
    ReadWriteTexture2D<Bgra32, Float4> output,
    int width,
    int height,
    float inverseColorScale) : IComputeShader
{
    private readonly ReadWriteBuffer<int> accumulator = accumulator;
    private readonly ReadWriteTexture2D<Bgra32, Float4> output = output;
    private readonly int width = width;
    private readonly int height = height;
    private readonly float inverseColorScale = inverseColorScale;

    public void Execute()
    {
        var x = ThreadIds.X;
        var y = ThreadIds.Y;
        if (x >= width || y >= height)
            return;

        var index4 = (y * width + x) * 4;
        var red = Hlsl.Max(accumulator[index4], 0) * inverseColorScale;
        var green = Hlsl.Max(accumulator[index4 + 1], 0) * inverseColorScale;
        var blue = Hlsl.Max(accumulator[index4 + 2], 0) * inverseColorScale;
        var alpha = Hlsl.Max(accumulator[index4 + 3], 0) * inverseColorScale;

        var outAlpha = Hlsl.Min(alpha, 1f);
        var scale = alpha > 1f ? outAlpha / alpha : 1f;
        red = Hlsl.Min(red * scale, outAlpha);
        green = Hlsl.Min(green * scale, outAlpha);
        blue = Hlsl.Min(blue * scale, outAlpha);
        output[ThreadIds.XY] = new Float4(red, green, blue, outAlpha);
    }
}
