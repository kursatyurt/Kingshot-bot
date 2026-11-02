using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Uses the daily Arena of Glory challenges: side panel Daily tab → Arena row → Challenge → an opponent whose power
    /// is shown green (weaker than us) → Quick Deploy → Fight → "Tap anywhere to exit" back to the Challenge List.
    /// Never taps the "+" (buys attempts) and refreshes the list only while the button says Free.
    /// </summary>
    public class ArenaTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.Arena;
        public override string Name => "Arena";
        protected override string GetImageFolderName() => "arena";

        private static readonly ConcurrentDictionary<int, DateTime> _nextCheck = new();
        private static readonly int[] RowY = { 360, 493, 626, 758, 891 }; // swords buttons of the 5 Challenge List rows

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (_nextCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next)
                return new TaskExecutionDetails(true, message: "Arena checked recently");
            _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(3);

            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            if (!await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken)) return TaskExecutionDetails.Failed("Side panel did not open");
            await ClickAsync(account.InstanceNumber, logger, new Point(370, 269)); // Daily tab
            await Task.Delay(800, cancellationToken);
            await SwipeAsync(account.InstanceNumber, logger, new Point(220, 750), new Point(220, 300), 800);
            await Task.Delay(1500, cancellationToken);

            var panel = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (panel == null) return TaskExecutionDetails.Failed("No screenshot");
            var (rowShown, row, _) = _templateMatcher.MatchTemplate(panel, Path.Combine(ImageTemplateFolder, "arena-row-icon.png"),
                account.InstanceNumber, threshold: 0.92, searchArea: new Rectangle(0, 290, 120, 570));
            if (!rowShown)
            {
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                return new TaskExecutionDetails(true, message: "Arena row not in the Daily tab");
            }
            var rowY = row.Y + row.Height / 2;
            var attemptsText = new OCRService(logger).ExtractTextFromScreenArea(panel, new Rectangle(90, rowY, 290, 32)) ?? "";
            var m = Regex.Match(attemptsText, @"(\d+)\s*/\s*\d+");
            var attempts = m.Success ? int.Parse(m.Groups[1].Value) : 5;
            if (attempts == 0)
            {
                _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(6);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                return new TaskExecutionDetails(true, message: "No Arena attempts left");
            }

            logger.LogInfo($"[{account.AccountName}] 🏟️ Arena: {attempts} challenge(s) left");
            await ClickAsync(account.InstanceNumber, logger, new Point(404, rowY)); // row's arrow
            await Task.Delay(3000, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(360, 1214)); // Challenge
            await Task.Delay(2500, cancellationToken);

            int won = 0, fought = 0;
            bool refreshed = false;
            while (fought < attempts && !cancellationToken.IsCancellationRequested)
            {
                var list = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (list == null) break;
                var target = Array.FindIndex(RowY, y => IsGreenText(list, new Rectangle(185, y - 15, 90, 60))); // power box sits lower when a # line shows
                if (target < 0)
                {
                    var refresh = new OCRService(logger).ExtractTextFromScreenArea(list, new Rectangle(220, 1010, 280, 70)) ?? "";
                    if (refreshed || !refresh.Contains("Free", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInfo($"[{account.AccountName}] Arena: no weaker opponent to challenge");
                        break;
                    }
                    await ClickAsync(account.InstanceNumber, logger, new Point(360, 1046)); // Free Refresh
                    refreshed = true;
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }

                await ClickAsync(account.InstanceNumber, logger, new Point(624, RowY[target])); // that row's swords
                await Task.Delay(3000, cancellationToken);
                await ClickAsync(account.InstanceNumber, logger, new Point(190, 1200)); // Quick Deploy
                await Task.Delay(1500, cancellationToken);
                await ClickAsync(account.InstanceNumber, logger, new Point(528, 1200)); // Fight
                fought++;

                // The fight plays itself; its result page ends with "Tap anywhere to exit"
                var start = DateTime.UtcNow;
                while (DateTime.UtcNow - start < TimeSpan.FromMinutes(3))
                {
                    await Task.Delay(5000, cancellationToken);
                    var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (shot == null) continue;
                    var ocr = new OCRService(logger);
                    if (ocr.ExtractTextFromScreenArea(shot, new Rectangle(180, 1200, 360, 50))?.Contains("exit", StringComparison.OrdinalIgnoreCase) != true) continue;
                    var victory = ocr.ExtractTextFromScreenArea(shot, new Rectangle(150, 330, 420, 100))?.Contains("Victory", StringComparison.OrdinalIgnoreCase) == true;
                    if (victory) won++;
                    logger.LogInfo($"[{account.AccountName}] Arena fight {fought}: {(victory ? "won" : "lost")}");
                    await ClickAsync(account.InstanceNumber, logger, new Point(360, 1100));
                    await Task.Delay(2500, cancellationToken);
                    break;
                }
            }

            if (fought >= attempts) _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(6);
            // Close the Challenge List, then leave the Arena page with its back arrow (the locator would call this lost)
            await ClickAsync(account.InstanceNumber, logger, new Point(668, 192));
            await Task.Delay(1000, cancellationToken);
            await FindAndClickImageAsync("back-arrow.png", account.InstanceNumber, logger, threshold: 0.85, searchArea: new Rectangle(0, 0, 160, 90));
            await Task.Delay(1500, cancellationToken);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: $"Arena: won {won}/{fought}");
        }

        /// <summary>The Challenge List shows an opponent's power in green when it is below ours, red above.</summary>
        private static bool IsGreenText(byte[] screenshot, Rectangle rect)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return false;
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(rect.X, rect.Y, rect.Width, rect.Height));
            int green = 0;
            for (int y = 0; y < roi.Rows; y++)
                for (int x = 0; x < roi.Cols; x++)
                {
                    var p = roi.At<OpenCvSharp.Vec3b>(y, x); // BGR
                    if (p.Item1 > 150 && p.Item2 < 120 && p.Item0 < 150) green++;
                }
            return green > 300;
        }
    }
}
