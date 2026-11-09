using System.IO;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>Screens the locator once reported as "lost" (2026-11-08), checked the way LocatorService checks them.</summary>
    public class LostScreenTests
    {
        private static byte[] Shot(string f) => File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, f));

        private static bool IsCloud(byte[] s)
        {
            using var mat = OpenCvSharp.Cv2.ImDecode(s, OpenCvSharp.ImreadModes.Grayscale);
            OpenCvSharp.Cv2.MeanStdDev(mat, out var mean, out var sd);
            return mean.Val0 > 190 && sd.Val0 < 25;
        }

        private static bool IsChat(byte[] s) => new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService())
            .ExtractBrightTextFromScreenArea(s, new System.Drawing.Rectangle(90, 15, 130, 50), 190)?.Contains("Chat", System.StringComparison.OrdinalIgnoreCase) == true;

        [Test]
        public void ActivityTriumphRowAndPage()
        {
            var ocr = new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService());
            var daily = Shot("fountain-norow2.png");
            var rows = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(Bot.Core.Tasks.Modules.SidePanelStatus.RowDots(daily),
                y => ocr.ExtractBrightTextFromScreenArea(daily, new System.Drawing.Rectangle(70, y - 8, 300, 32), 190).Contains("Triumph")));
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0] + 18, Is.InRange(700, 735)); // the row's ✓ button
            Assert.That(ocr.ExtractBrightTextFromScreenArea(Shot("activity-triumph.png"), new System.Drawing.Rectangle(90, 15, 300, 50), 190), Does.Contain("Triumph"));
        }

        [Test]
        public void FountainRowScore()
        {
            using var img = OpenCvSharp.Cv2.ImDecode(Shot("fountain-norow.png"), OpenCvSharp.ImreadModes.Color);
            using var tpl = OpenCvSharp.Cv2.ImRead(Path.Combine(TestContext.CurrentContext.TestDirectory, "templates", "images", "pets", "fountain-row.png"));
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(0, 290, 120, 570));
            using var res = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.MatchTemplate(roi, tpl, res, OpenCvSharp.TemplateMatchModes.CCoeffNormed);
            OpenCvSharp.Cv2.MinMaxLoc(res, out _, out double max, out _, out var loc);
            Assert.That(max, Is.GreaterThan(0.9)); // the full Daily tab (2,160/2,160) the task once missed while it slid in
            var m = new Bot.Core.ImageDetection.UnifiedTemplateMatchingService(new Bot.Core.Logging.LogService());
            foreach (var f in System.Linq.Enumerable.Select(Directory.GetFiles(TestContext.CurrentContext.TestDirectory, "*.png"), Path.GetFileName))
            {
                var r = m.MatchTemplate(Shot(f), Path.Combine(TestContext.CurrentContext.TestDirectory, "templates", "images", "pets", "fountain-row.png"), 1, 0.9, searchArea: new System.Drawing.Rectangle(0, 290, 120, 570));
                var h = m.MatchTemplate(Shot(f), Path.Combine(TestContext.CurrentContext.TestDirectory, "templates", "images", "locator", "side-panel-handle.png"), 1, 0.5, searchArea: new System.Drawing.Rectangle(400, 460, 140, 170));
                // An open panel's handle scores 0.90-0.995 (it is see-through); no other screen tops 0.81
                TestContext.Progress.WriteLine($"handle {f}: {h.confidence:F3} {h.matchRect}");
                Assert.That(h.confidence >= 0.85, Is.EqualTo(f.StartsWith("panel-") || f.StartsWith("daily-") || f.StartsWith("fountain-norow")), f);
            }
        }

        [Test]
        public void GovernorProfileIsRecognised()
        {
            static string Title(byte[] s) => new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService())
                .ExtractBrightTextFromScreenArea(s, new System.Drawing.Rectangle(90, 15, 250, 50), 190) ?? "";
            Assert.That(Title(Shot("lost-profile.png")), Does.Contain("Governor"));
            Assert.That(Title(Shot("panel-town.png")), Does.Not.Contain("Governor"));
        }

        [Test]
        public void LoadingScreenIsRecognised()
        {
            static bool Loading(byte[] s) => new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService())
                .ExtractBrightTextFromScreenArea(s, new System.Drawing.Rectangle(200, 1145, 320, 40), 190) is { } t
                && (t.Contains('%') || t.Contains("version", System.StringComparison.OrdinalIgnoreCase));
            Assert.That(Loading(Shot("lost-loading.png")), Is.True);
            Assert.That(Loading(Shot("lost-loading-version.png")), Is.True);
            Assert.That(Loading(Shot("panel-town.png")), Is.False);
            Assert.That(Loading(Shot("lost-emporium.png")), Is.False);
            Assert.That(Loading(Shot("lost-chat.png")), Is.False);
        }

        [Test]
        public void EmporiumIsRecognised()
        {
            static string Title(byte[] s) => new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService())
                .ExtractBrightTextFromScreenArea(s, new System.Drawing.Rectangle(90, 95, 350, 50), 190) ?? "";
            Assert.That(Title(Shot("lost-emporium.png")), Does.Contain("Wishful"));
            Assert.That(Title(Shot("panel-town.png")), Does.Not.Contain("Wishful"));
        }

        [Test]
        public void ConnectionLostIsRecognised()
        {
            static string Title(byte[] s) => new Bot.Core.Utils.OCRService(new Bot.Core.Logging.LogService())
                .ExtractTextFromScreenArea(s, new System.Drawing.Rectangle(100, 570, 520, 70)) ?? "";
            Assert.That(Title(Shot("lost-connection.png")), Does.Contain("connect"));
            Assert.That(Title(Shot("panel-town.png")), Does.Not.Contain("connect"));
            Assert.That(Title(Shot("lost-chat.png")), Does.Not.Contain("connect"));
        }

        [Test]
        public void CloudAndChatAreRecognised()
        {
            Assert.That(IsCloud(Shot("lost-cloud.png")), Is.True);
            Assert.That(IsCloud(Shot("lost-chat.png")), Is.False);
            Assert.That(IsCloud(Shot("panel-town.png")), Is.False);
            Assert.That(IsChat(Shot("lost-chat.png")), Is.True);
            Assert.That(IsChat(Shot("panel-town.png")), Is.False);
        }
    }
}
