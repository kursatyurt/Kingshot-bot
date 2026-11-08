using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>One row of the city side panel: its title and, when shown in white, a countdown or "Idle".</summary>
    public record PanelRow(string Title, TimeSpan? Timer, bool Idle)
    {
        public bool Is(string word) => Title.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The city side panel lists what every queue is doing: Town (building queues, troop training, research),
    /// Wilderness (marches) and Daily (feast, online rewards, conquest income, fountain, pet adventure). One scan per
    /// account per cycle tells the tasks whether they have anything to do. Green and red statuses ("Completed",
    /// "Available", "Purchase Queue") are not read: such rows have neither a timer nor Idle.
    /// </summary>
    public class SidePanelStatus : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AccountDetection; // not scheduled itself
        public override string Name => "Side Panel Scan";
        protected override string GetImageFolderName() => "locator";

        /// <summary>Rows and the titles of rows carrying a red dot (the game's "something to do here").</summary>
        public record Scan(DateTime At, List<PanelRow> Town, List<PanelRow> Wilderness, List<PanelRow> Daily, List<string> Dots)
        {
            /// <summary>Fountain of Life "Available: have/cap" (green text on the Daily tab).</summary>
            public (int Have, int Cap)? Fountain { get; init; }
            public bool HasDot(string word) => Dots.Any(d => d.Contains(word, StringComparison.OrdinalIgnoreCase));
            /// <summary>A dotted Town row that is a building queue (not a camp, not research).</summary>
            public bool BuildingDot => Dots.Any(d => d.StartsWith("Town:") && !new[] { "Infantry", "Cavalry", "Archer", "Research" }.Any(w => d.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        private static readonly ConcurrentDictionary<int, Scan> _latest = new();
        private static readonly Rectangle PanelText = new(70, 300, 300, 560);

        /// <summary>The scan of this cycle, if it is recent enough to trust.</summary>
        public static Scan? Latest(int instanceNumber) =>
            _latest.TryGetValue(instanceNumber, out var s) && DateTime.UtcNow - s.At < TimeSpan.FromMinutes(15) ? s : null; // a first cycle after a restart can take 6+ min

        public static List<PanelRow> Parse(string text)
        {
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var rows = new List<PanelRow>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (TimerOf(lines[i]) != null || IsIdle(lines[i])) continue;
                var next = i + 1 < lines.Count ? lines[i + 1] : "";
                rows.Add(new PanelRow(lines[i], TimerOf(next), IsIdle(next)));
            }
            return rows;
        }

        /// <summary>Top edges of the red dots on the rows' arrow buttons (15 px blobs at x≈410; red status text is smaller).</summary>
        public static List<int> RowDots(byte[] screenshot)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(395, 290, 50, 580));
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(roi, new OpenCvSharp.Scalar(0, 0, 190), new OpenCvSharp.Scalar(90, 90, 255), mask);
            OpenCvSharp.Cv2.FindContours(mask, out var contours, out _, OpenCvSharp.RetrievalModes.External, OpenCvSharp.ContourApproximationModes.ApproxSimple);
            return contours.Select(OpenCvSharp.Cv2.BoundingRect).Where(b => b.Width is >= 12 and <= 20 && b.Height is >= 12 and <= 20)
                .Select(b => b.Y + 290).OrderBy(y => y).ToList();
        }

        /// <summary>Green status text of the panel column (Available, Completed): white and grey drop out.</summary>
        public static string ReadGreenText(byte[] screenshot, OCRService ocr)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(PanelText.X, PanelText.Y, PanelText.Width, PanelText.Height));
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(roi, new OpenCvSharp.Scalar(0, 170, 0), new OpenCvSharp.Scalar(140, 255, 140), mask);
            using var inverted = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.BitwiseNot(mask, inverted);
            using var big = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.Resize(inverted, big, new OpenCvSharp.Size(0, 0), 3, 3, OpenCvSharp.InterpolationFlags.Cubic);
            using var bgr = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(big, bgr, OpenCvSharp.ColorConversionCodes.GRAY2BGR);
            return ocr.ExtractTextFromMat(bgr);
        }

        /// <summary>"Available: 962/2.160" → (962, 2160); thousands separators read as '.' or ','.</summary>
        public static (int, int)? ParseFountain(string text)
        {
            // OCR can put a space after the separator ("1, 800") or drop a digit ("216"): a capacity that is not a
        // multiple of 360, or below the amount, is a misread
            var m = Regex.Match(text, @"Available\W*(\d[\d.,]*)\s*/\s*(\d+(?:[.,]\s?\d{3})*)", RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(Regex.Replace(m.Groups[1].Value, @"\D", ""), out var have)
                && int.TryParse(Regex.Replace(m.Groups[2].Value, @"\D", ""), out var cap) && cap % 360 == 0 && cap >= have ? (have, cap) : null; // capacities seen: 1,440 / 1,800 / 2,160
        }

        private static TimeSpan? TimerOf(string line)
        {
            var m = Regex.Match(line, @"(\d{1,2}):(\d{2}):(\d{2})");
            return m.Success ? new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }

        private static bool IsIdle(string line) => Regex.IsMatch(line, @"^\W*[Il1]?dle\W*$", RegexOptions.IgnoreCase);

        public async Task ScanAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            int n = account.InstanceNumber;
            await InitializeAsync(logger, cancellationToken);
            if (!await OpenSidePanelAsync(n, logger, cancellationToken)) return; // opens on the Town tab
            var ocr = new OCRService(logger);
            var dots = new List<string>();
            (int, int)? fountain = null;
            async Task<List<PanelRow>> ReadTab(Point tab, string name)
            {
                await ClickAsync(n, logger, tab);
                await Pace.Delay(800, cancellationToken);
                var shot = await TakeScreenshotAsync(n, logger);
                if (shot == null) return new();
                foreach (var y in RowDots(shot)) // the row's title sits just below its arrow's dot
                    dots.Add($"{name}:{ocr.ExtractBrightTextFromScreenArea(shot, new Rectangle(70, y - 8, 300, 32), 190).Trim()}");
                if (name == "Daily") fountain = ParseFountain(ReadGreenText(shot, ocr));
                return Parse(ocr.ExtractBrightTextFromScreenArea(shot, PanelText, 190));
            }
            var scan = new Scan(DateTime.UtcNow, await ReadTab(new Point(80, 268), "Town"), await ReadTab(new Point(225, 268), "Wilderness"), await ReadTab(new Point(370, 268), "Daily"), dots) { Fountain = fountain };
            await ClickAsync(n, logger, new Point(464, 544)); // close the panel by its ◁ handle
            await Pace.Delay(600, cancellationToken);
            _latest[n] = scan;
            string Show(List<PanelRow> rows) => string.Join(", ", rows.Where(r => r.Timer != null || r.Idle).Select(r => $"{r.Title}={(r.Idle ? "Idle" : r.Timer.ToString())}"));
            logger.LogInfo($"[{account.AccountName}] 📋 Panel - Town: {Show(scan.Town)} | Wilderness: {Show(scan.Wilderness)} | Daily: {Show(scan.Daily)} | 🔴 {string.Join(", ", dots)} | 💧 {(fountain is { } f ? $"{f.Item1}/{f.Item2}" : "?")}");
        }

        protected override Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
            => Task.FromResult(new TaskExecutionDetails(true));
    }
}
