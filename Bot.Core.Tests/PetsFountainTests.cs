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

        private static byte[] Shot(string name) => System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, name));

        [Test]
        public void PicksOnlyTheLynxWhileTheWolfIsInAdventure()
        {
            var list = Shot("select-pet.png");
            Assert.That(PetsFountainTask.IsPetFree(list, new System.Drawing.Point(142, 657)), Is.True);
            Assert.That(PetsFountainTask.IsPetFree(list, new System.Drawing.Point(286, 657)), Is.False); // "In Adventure"
            Assert.That(PetsFountainTask.IsPetFree(list, new System.Drawing.Point(430, 657)), Is.False); // empty slot
        }

        [Test]
        public void FindsEveryTreasureSpotAndReadsAttempts()
        {
            var tpl = System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../templates/images/pets/adventure-chest.png");
            var spots = PetsFountainTask.FindAll(Shot("pet-adventure.png"), tpl, 0.8, new System.Drawing.Rectangle(0, 150, 720, 1000));
            Assert.That(spots, Has.Some.Matches<System.Drawing.Rectangle>(r => r.Contains(483, 622)));   // free spot
            Assert.That(spots, Has.Some.Matches<System.Drawing.Rectangle>(r => r.Contains(98, 420)));    // Lynx's spot
            var tick = tpl.Replace("adventure-chest.png", "adventure-done-tick.png");
            Assert.That(PetsFountainTask.FindAll(Shot("pet-adventure-done.png"), tick, 0.85, new System.Drawing.Rectangle(0, 150, 720, 1000)), Has.Count.EqualTo(1));
            Assert.That(PetsFountainTask.FindAll(Shot("pet-adventure.png"), tick, 0.85, new System.Drawing.Rectangle(0, 150, 720, 1000)), Is.Empty);
            Assert.That(PetsFountainTask.ParseAttempts("Remaining attempts today: 0"), Is.EqualTo(0));
            Assert.That(PetsFountainTask.ParseAttempts("Pet Adventure"), Is.Null);
        }
    }
}
