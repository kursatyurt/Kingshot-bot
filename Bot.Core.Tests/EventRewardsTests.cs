using System;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    public class EventRewardsTests
    {
        [Test]
        public void ReadsNextChestTimer()
        {
            Assert.That(EventRewardsTask.ParseTimer("Next Chest Ready In:\n00:59:57"), Is.EqualTo(new TimeSpan(0, 59, 57)));
            Assert.That(EventRewardsTask.ParseTimer("garbage"), Is.Null);
        }

        [Test]
        public void OcrReadsTheRedTimerOnTheChestPopup()
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "next-chest.png"));
            string text;
            try { text = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractBrightTextFromScreenArea(shot, new System.Drawing.Rectangle(220, 1090, 280, 55), 100); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(EventRewardsTask.ParseTimer(text), Is.EqualTo(new TimeSpan(0, 59, 57)), text);
        }

        [Test]
        public void OcrReadsThePetAdventureRowTimer()
        {
            var shot = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "daily-pet-timer.png"));
            string text;
            try { text = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService()).ExtractBrightTextFromScreenArea(shot, new System.Drawing.Rectangle(80, 715, 290, 32)); }
            catch (InvalidOperationException) { Assert.Ignore("Tesseract native libraries not next to the test binaries"); return; }
            Assert.That(EventRewardsTask.ParseTimer(text), Is.EqualTo(new TimeSpan(0, 52, 51)), text);
        }
    }
}
