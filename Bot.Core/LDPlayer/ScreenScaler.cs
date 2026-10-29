using System.Buffers.Binary;
using System.Collections.Concurrent;
using OpenCvSharp;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Makes the bot resolution-independent. Templates and every hard-coded coordinate assume a
    /// 720x1280 screen, so screenshots are scaled to that size and taps/swipes are scaled back
    /// to the device's real size. Devices already at 720x1280 (LDPlayer, which the bot forces
    /// to that size) pass through untouched.
    /// </summary>
    public static class ScreenScaler
    {
        public const int BaseWidth = 720;
        public const int BaseHeight = 1280;

        // ponytail: per-axis scaling only, so devices with a different aspect ratio than 9:16
        // (e.g. 1080x2400) get stretched templates; add letterboxing if such devices matter.
        private static readonly ConcurrentDictionary<string, (double X, double Y)> _scales = new();

        /// <summary>
        /// Records the device's screen size from a PNG screenshot and returns the screenshot at 720x1280.
        /// </summary>
        public static byte[] Normalize(string serial, byte[] png)
        {
            if (!TryReadPngSize(png, out int width, out int height))
                return png;

            _scales[serial] = ((double)width / BaseWidth, (double)height / BaseHeight);
            if (width == BaseWidth && height == BaseHeight)
                return png;

            using var image = Cv2.ImDecode(png, ImreadModes.Unchanged);
            using var resized = new Mat();
            Cv2.Resize(image, resized, new Size(BaseWidth, BaseHeight), 0, 0, InterpolationFlags.Area);
            return resized.ToBytes(".png");
        }

        /// <summary>
        /// Maps a 720x1280 coordinate to the device's real screen, using the size seen in its last screenshot.
        /// </summary>
        public static (int X, int Y) ToDevice(string serial, int x, int y)
        {
            if (!_scales.TryGetValue(serial, out var scale))
                return (x, y);
            return ((int)Math.Round(x * scale.X), (int)Math.Round(y * scale.Y));
        }

        private static bool TryReadPngSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            // 8-byte signature, then the IHDR chunk: length(4) "IHDR"(4) width(4) height(4), big-endian
            if (png.Length < 24 || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
                return false;
            width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            return width > 0 && height > 0;
        }
    }
}
