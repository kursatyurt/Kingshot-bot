using Bot.Core.LDPlayer;
using NUnit.Framework;
using OpenCvSharp;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class ScreenScalerTests
    {
        private static byte[] Png(int width, int height)
        {
            using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(0));
            // A white square at base coordinates (100,200)-(200,300), scaled to this size
            double sx = (double)width / ScreenScaler.BaseWidth, sy = (double)height / ScreenScaler.BaseHeight;
            Cv2.Rectangle(mat, new Rect((int)(100 * sx), (int)(200 * sy), (int)(100 * sx), (int)(100 * sy)), Scalar.All(255), -1);
            return mat.ToBytes(".png");
        }

        [Test]
        public void Normalize_BaseResolution_ReturnsSameBytesAndIdentityTaps()
        {
            var png = Png(720, 1280);
            Assert.AreSame(png, ScreenScaler.Normalize("test-base", png));
            Assert.AreEqual((360, 640), ScreenScaler.ToDevice("test-base", 360, 640));
        }

        [Test]
        public void Normalize_LargerScreen_ResizesToBaseAndScalesTaps()
        {
            var normalized = ScreenScaler.Normalize("test-1080", Png(1080, 1920));

            using var mat = Cv2.ImDecode(normalized, ImreadModes.Color);
            Assert.AreEqual(720, mat.Width);
            Assert.AreEqual(1280, mat.Height);
            Assert.AreEqual(255, mat.At<Vec3b>(250, 150).Item0, "square should land at base coordinates");
            Assert.AreEqual(0, mat.At<Vec3b>(400, 400).Item0);

            Assert.AreEqual((540, 960), ScreenScaler.ToDevice("test-1080", 360, 640));
            Assert.AreEqual((1080, 1920), ScreenScaler.ToDevice("test-1080", 720, 1280));
        }

        [Test]
        public void ToDevice_UnknownDevice_LeavesCoordinatesUnchanged()
        {
            Assert.AreEqual((10, 20), ScreenScaler.ToDevice("never-seen", 10, 20));
        }

        [Test]
        public void Normalize_NotAPng_ReturnsInputUnchanged()
        {
            var bytes = new byte[] { 1, 2, 3 };
            Assert.AreSame(bytes, ScreenScaler.Normalize("test-garbage", bytes));
        }
    }
}
