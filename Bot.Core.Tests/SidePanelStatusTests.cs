using System;
using System.Linq;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>Real side panel screenshots (Kingshot_1, 2026-11-08) read the way the scan reads them.</summary>
    public class SidePanelStatusTests
    {
        private static System.Collections.Generic.List<PanelRow> Read(string file)
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, file));
            try { return SidePanelStatus.Parse(new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractBrightTextFromScreenArea(shot, new System.Drawing.Rectangle(70, 300, 300, 560), 190)); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return null!; }
        }

        [Test]
        public void TownTabShowsQueuesTroopsAndIdleResearch()
        {
            var town = Read("panel-town.png");
            Assert.That(town.Single(r => r.Is("Embassy")).Timer, Is.EqualTo(new TimeSpan(5, 41, 43)));
            Assert.That(town.Where(r => r.Is("Infantry") || r.Is("Cavalry") || r.Is("Archer")).Select(r => r.Timer),
                Is.EqualTo(new TimeSpan?[] { new(0, 20, 33), new(0, 21, 1), new(0, 21, 28) }));
            Assert.That(town.Single(r => r.Is("Research") && r.Idle), Is.Not.Null);
        }

        [Test]
        public void WildernessTabShowsEveryMarchTimer()
        {
            var marches = Read("panel-wilderness.png").Where(r => r.Timer != null).ToList();
            Assert.That(marches, Has.Count.EqualTo(4));
            Assert.That(marches.Min(r => r.Timer), Is.EqualTo(new TimeSpan(0, 0, 35)));   // Returning
        }

        [Test]
        public void DailyTabShowsPetTimerAndOnlineChest()
        {
            var daily = Read("panel-daily.png");
            Assert.That(daily.Single(r => r.Is("Pet Adventure") && r.Timer != null).Timer, Is.EqualTo(new TimeSpan(4, 51, 22)));
            Assert.That(daily.Any(r => r.Is("Online Rewards")), Is.False);
            Assert.That(Read("panel-daily-online.png").Any(r => r.Is("Online Rewards")), Is.True);
        }

        [TestCase("panel-town.png", "Research")]
        [TestCase("panel-daily-online.png", "Online Rewards")]
        [TestCase("panel-daily-pets-done.png", "Adventure")]
        [TestCase("panel-daily.png", null)]
        [TestCase("panel-wilderness.png", null)]
        public void RedDotsMarkTheRowsWithSomethingToDo(string file, string? row)
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, file));
            var dots = SidePanelStatus.RowDots(shot);
            Assert.That(dots, Has.Count.EqualTo(row == null ? 0 : 1));
            if (row == null) return;
            string title;
            try { title = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractBrightTextFromScreenArea(shot, new System.Drawing.Rectangle(70, dots[0] - 8, 300, 32), 190); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(title, Does.Contain(row));
        }

        [TestCase("panel-daily.png", 962, 2160)]
        [TestCase("panel-daily-online.png", 17, 2160)]
        [TestCase("panel-daily-pets-done.png", 152, 1800)]
        public void ReadsTheFountainFill(string file, int have, int cap)
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, file));
            string text;
            try { text = SidePanelStatus.ReadGreenText(shot, new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService())); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(SidePanelStatus.ParseFountain(text), Is.EqualTo(((int, int)?)(have, cap)), text);
        }

        [TestCase("Available: 292/1, 800", 292, 1800)]
        [TestCase("Available: 962/2.160", 962, 2160)]
        [TestCase("Available: 17/2160", 17, 2160)]
        [TestCase("Available: 292/1", null, null)]   // capacity cut off: no reading rather than "full"
        [TestCase("Available: 141/216", null, null)] // a dropped digit
        public void ParsesFountainFillOrRejectsAMisread(string text, int? have, int? cap)
        {
            var parsed = SidePanelStatus.ParseFountain(text);
            Assert.That(parsed, Is.EqualTo(have == null ? null : ((int, int)?)(have.Value, cap!.Value)));
        }

        [TestCase("tap-anywhere.png")]  // new troop tier: text low on the screen
        [TestCase("victory.png")]       // beast Victory rewards: text higher up
        public void TapAnywhereScreensAreRecognised(string file) // what the locator reads to dismiss them
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, file));
            string text;
            try { text = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractBrightTextFromScreenArea(shot, new System.Drawing.Rectangle(60, 980, 600, 290), 190); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(text, Does.Contain("anywhere").IgnoreCase);
        }

        [Test]
        public void LeftoverDeployScreenReadsDeploy() // what AutoHunt reads before looking for its hunt button
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "deploy-screen.png"));
            string text;
            try { text = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractBrightTextFromScreenArea(shot, new System.Drawing.Rectangle(80, 15, 240, 50), 190); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(text, Does.Contain("Deploy").IgnoreCase);
            Assert.That(new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractBrightTextFromScreenArea(System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "city.png")), new System.Drawing.Rectangle(80, 15, 240, 50), 190), Does.Not.Contain("Deploy").IgnoreCase);
        }

        [Test]
        public void FindsTheRescueClaimButtonWhereverThePopupSits()
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "rescue-claim.png"));
            var claim = Bot.Core.Tasks.Modules.AutoHunt.AutoHuntCoordinator.FindClaimButton(shot);
            Assert.That(claim, Is.Not.Null);
            Assert.That(claim!.Value.Y, Is.InRange(845, 890));
            Assert.That(Bot.Core.Tasks.Modules.AutoHunt.AutoHuntCoordinator.FindClaimButton(System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "city.png"))), Is.Null);
        }

        [Test]
        public void IntelMissionCardShowsExpires() // what AutoHunt reads to close a card it left open
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "intel-card.png"));
            string text;
            try { text = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractTextFromScreenArea(shot, new System.Drawing.Rectangle(180, 840, 360, 45)); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(text, Does.Contain("Expires"));
        }

        [Test]
        public void FirstPurchasePromoIsRecognised() // the locator closes it with Back
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "topup-promo.png"));
            string text;
            try { text = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractBrightTextFromScreenArea(shot, new System.Drawing.Rectangle(60, 120, 600, 120), 190); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(text, Does.Contain("first purchas").IgnoreCase);
        }

        [Test]
        public void ReadsTheRebelAssaultCountdown()
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "rebel-countdown.png"));
            TimeSpan? wait;
            try { wait = Bot.Core.Tasks.Modules.RebelAssaultTask.ReadCountdown(shot, new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService())); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(wait, Is.EqualTo(new TimeSpan(0, 44, 59)));
        }

        [Test]
        public void MarchQueueUpsellIsRecognised() // Farming and AutoHunt close it by its X, never Buy
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "march-queue-popup.png"));
            string text;
            try { text = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractTextFromScreenArea(shot, new System.Drawing.Rectangle(200, 315, 320, 55)); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(text, Does.Contain("March"));
        }

        [Test]
        public void ParsesTitleThenStatusLines()
        {
            var rows = SidePanelStatus.Parse("Building Queue\nEmbassy Upgrading\n05:41:43 |\nTech Research\nIdle\n");
            Assert.That(rows.Single(r => r.Is("Embassy")).Timer, Is.EqualTo(new TimeSpan(5, 41, 43)));
            Assert.That(rows.Single(r => r.Is("Tech Research")).Idle, Is.True);
            Assert.That(rows.Single(r => r.Is("Building Queue")).Timer, Is.Null);
        }
    }
}
