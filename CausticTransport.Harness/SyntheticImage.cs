namespace CausticTransport.Harness;

internal static class SyntheticImage
{
    public static byte[] Create(int width, int height)
    {
        var pixels = new byte[width * height * HarnessImage.BytesPerPixel];
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

                var offset = (y * width + x) * HarnessImage.BytesPerPixel;
                pixels[offset] = (byte)(blue * alpha / 255);
                pixels[offset + 1] = (byte)(green * alpha / 255);
                pixels[offset + 2] = (byte)(red * alpha / 255);
                pixels[offset + 3] = (byte)alpha;
            }
        }

        return pixels;
    }
}
