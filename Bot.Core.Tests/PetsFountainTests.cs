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
        public void AllyIslandHelp()
        {
            var dots = PetsFountainTask.VisitDots(Shot("island-ranking-dots.png"));
            Assert.That(dots, Has.Count.EqualTo(7)); // rows 54-60 all show a dot
            Assert.That(dots[0], Is.InRange(240, 262));
            Assert.That(PetsFountainTask.VisitDots(Shot("island-ranking-top.png")), Is.Empty);
            var log = new Bot.Core.Logging.LogService();
            Assert.That(PetsFountainTask.HelpsLeft(Shot("island-help.png"), log), Is.EqualTo(3));
            Assert.That(PetsFountainTask.HelpsLeft(Shot("island-helped.png"), log), Is.EqualTo(2));
            var m = new Bot.Core.ImageDetection.UnifiedTemplateMatchingService(log);
            var tpl = System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../templates/images/pets/island-help.png");
            var area = new System.Drawing.Rectangle(0, 90, 720, 900);
            Assert.That(m.MatchTemplate(Shot("island-help.png"), tpl, 1, 0.8, searchArea: area).found, Is.True);
            Assert.That(m.MatchTemplate(Shot("island-helped.png"), tpl, 1, 0.8, searchArea: area).found, Is.False);
        }

        [Test]
        public void FindsPartlyFilledWaterBubbles()
        {
            var tpl = System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../templates/images/pets/water-drop.png");
            var area = new System.Drawing.Rectangle(0, 90, 720, 1000);
            var drops = PetsFountainTask.FindAll(Shot("island-partial.png"), tpl, 0.72, area);
            Assert.That(drops, Has.Some.Matches<System.Drawing.Rectangle>(r => r.Contains(601, 410)));  // well
            Assert.That(drops, Has.Some.Matches<System.Drawing.Rectangle>(r => r.Contains(356, 470)));  // fountain, partly full
            Assert.That(drops, Has.Count.EqualTo(2));
            Assert.That(PetsFountainTask.FindAll(Shot("city.png"), tpl, 0.72, area), Is.Empty);
            // Full fountain under the dimmed guide-hand overlay (Kingshot_0, 1,800/1,800)
            var guided = PetsFountainTask.FindAll(Shot("island-guide.png"), tpl, 0.72, area);
            Assert.That(guided, Has.Some.Matches<System.Drawing.Rectangle>(r => r.Contains(603, 405)));
            var green = PetsFountainTask.FindGreenBubble(Shot("island-guide.png"), area, tpl);
            Assert.That(green is { } g && Math.Abs(g.X + g.Width / 2 - 360) < 30 && Math.Abs(g.Y + g.Height / 2 - 495) < 30, Is.True, $"{green}");
            Assert.That(PetsFountainTask.FindGreenBubble(Shot("city.png"), area, tpl), Is.Null);
            Assert.That(PetsFountainTask.FindGreenBubble(Shot("panel-daily.png"), area, tpl), Is.Null);
        }

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
