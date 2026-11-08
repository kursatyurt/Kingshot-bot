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
