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

        public record Scan(DateTime At, List<PanelRow> Town, List<PanelRow> Wilderness, List<PanelRow> Daily);

        private static readonly ConcurrentDictionary<int, Scan> _latest = new();
        private static readonly Rectangle PanelText = new(70, 300, 300, 560);

        /// <summary>The scan of this cycle, if it is recent enough to trust.</summary>
        public static Scan? Latest(int instanceNumber) =>
            _latest.TryGetValue(instanceNumber, out var s) && DateTime.UtcNow - s.At < TimeSpan.FromMinutes(5) ? s : null;

        /// <summary>Forget the scan once a task has changed what it showed (started a research, a building...).</summary>
        public static void Invalidate(int instanceNumber) => _latest.TryRemove(instanceNumber, out _);

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
            async Task<List<PanelRow>> ReadTab(Point tab)
            {
                await ClickAsync(n, logger, tab);
                await Pace.Delay(800, cancellationToken);
                var shot = await TakeScreenshotAsync(n, logger);
                return shot == null ? new() : Parse(ocr.ExtractBrightTextFromScreenArea(shot, PanelText, 190));
            }
            var scan = new Scan(DateTime.UtcNow, await ReadTab(new Point(80, 268)), await ReadTab(new Point(225, 268)), await ReadTab(new Point(370, 268)));
            await ClickAsync(n, logger, new Point(464, 544)); // close the panel by its ◁ handle
            await Pace.Delay(600, cancellationToken);
            _latest[n] = scan;
            string Show(List<PanelRow> rows) => string.Join(", ", rows.Where(r => r.Timer != null || r.Idle).Select(r => $"{r.Title}={(r.Idle ? "Idle" : r.Timer.ToString())}"));
            logger.LogInfo($"[{account.AccountName}] 📋 Panel - Town: {Show(scan.Town)} | Wilderness: {Show(scan.Wilderness)} | Daily: {Show(scan.Daily)}");
        }

        protected override Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
            => Task.FromResult(new TaskExecutionDetails(true));
    }
}
