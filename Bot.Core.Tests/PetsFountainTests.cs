using System.Drawing;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;
using OpenCvSharp;

namespace Bot.Core.Tests
{
    /// <summary>Colors measured on the live game (BGR), 2026-11-07.</summary>
    public class PetsFountainTests
    {
        private static byte[] Solid(double b, double g, double r)
        {
            using var img = new Mat(1280, 720, MatType.CV_8UC3, new Scalar(b, g, r));
            return img.ToBytes(".png");
        }

        [Test]
        public void OnlyALitUseButtonCountsAsCyan()
        {
            var use = new Rectangle(240, 1060, 240, 50);
            Assert.That(PetsFountainTask.IsCyan(Solid(200, 190, 60), use), Is.True);    // lit, like Select Pet
            Assert.That(PetsFountainTask.IsCyan(Solid(125, 116, 110), use), Is.False);  // grey Use
            Assert.That(PetsFountainTask.IsCyan(Solid(196, 204, 231), use), Is.False);  // "Unlocked after…" (locked)
        }
    }
}
