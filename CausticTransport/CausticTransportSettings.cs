namespace CausticTransport;

internal static class CausticTransportSettings
{
    public const float GridFixedScale = 16384f;
    public const float DisplacementRelaxation = 0.7f;
    public const float DisplacementStepLimit = 3f;
    public const float JitterCellAmplitude = 0.5f;
    public const int CoarsestLevelSize = 16;
    public const int MinimumGridSize = 4;

    public static QualitySettings GetQuality(CausticTransportQuality quality)
        => quality switch
        {
            CausticTransportQuality.Balanced => new QualitySettings(128, 4, 12),
            CausticTransportQuality.Ultra => new QualitySettings(256, 8, 20),
            _ => new QualitySettings(192, 6, 16),
        };

    public static (int Width, int Height) GetGridSize(int width, int height, int resolution)
    {
        var longSide = Math.Max(width, height);
        var shortSide = Math.Max(Math.Min(width, height), 1);
        var gridLong = Math.Max(Math.Min(resolution, longSide), MinimumGridSize);
        var gridShort = Math.Clamp((int)Math.Round((double)gridLong * shortSide / longSide), MinimumGridSize, gridLong);
        return width >= height ? (gridLong, gridShort) : (gridShort, gridLong);
    }

    public static int GetLevelCount(int gridWidth, int gridHeight)
    {
        var count = 1;
        var (width, height) = (gridWidth, gridHeight);
        while (Math.Max(width, height) > CoarsestLevelSize)
        {
            (width, height) = GetCoarserLevelSize(width, height);
            count++;
        }
        return count;
    }

    public static (int Width, int Height) GetCoarserLevelSize(int width, int height)
        => (Math.Max((width + 1) / 2, MinimumGridSize), Math.Max((height + 1) / 2, MinimumGridSize));

    public static int GetColorFixedScale(int pixelCount)
        => (int)Math.Clamp(int.MaxValue / (1.5 * Math.Max(pixelCount, 1)), 256d, 65536d);

    internal readonly record struct QualitySettings(int GridResolution, int TransportIterations, int JacobiIterations);
}
