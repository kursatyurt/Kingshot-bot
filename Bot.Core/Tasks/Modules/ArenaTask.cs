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

        protected override bool OnCooldown(AccountSettings account) => _nextCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (_nextCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next)
                return new TaskExecutionDetails(true, message: "Arena checked recently");
            _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(3);

            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            if (!await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken)) return TaskExecutionDetails.Failed("Side panel did not open");
            await ClickAsync(account.InstanceNumber, logger, new Point(370, 269)); // Daily tab
            await Bot.Core.Utils.Pace.Delay(800, cancellationToken);
            await ClaimDailyRowsAsync(account, logger, cancellationToken);
            await SwipeAsync(account.InstanceNumber, logger, new Point(220, 750), new Point(220, 300), 800);
            await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
            await ClaimDailyRowsAsync(account, logger, cancellationToken);

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
            await Bot.Core.Utils.Pace.Delay(3000, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(360, 1214)); // Challenge
            await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);

            int won = 0, fought = 0;
            bool refreshed = false;
            while (fought < attempts && !cancellationToken.IsCancellationRequested)
            {
                var list = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (list == null) break;
                // The list's own counter is the real one: at 0 every swords button is grey and does nothing
                if (Regex.IsMatch(new OCRService(logger).ExtractTextFromScreenArea(list, new Rectangle(200, 950, 240, 40)) ?? "", @"challenges\W*0\b", RegexOptions.IgnoreCase))
                {
                    _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(6);
                    logger.LogInfo($"[{account.AccountName}] Arena: no daily challenges left");
                    break;
                }
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
                    await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                    continue;
                }

                await ClickAsync(account.InstanceNumber, logger, new Point(624, RowY[target])); // that row's swords
                await Bot.Core.Utils.Pace.Delay(3000, cancellationToken);
                // Without the deploy screen these taps land outside the Challenge List and close it
                var deploy = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (deploy == null || new OCRService(logger).ExtractTextFromScreenArea(deploy, new Rectangle(40, 1160, 640, 80))?.Contains("Fight", StringComparison.OrdinalIgnoreCase) != true)
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
                    await File.WriteAllBytesAsync(Path.Combine(dir, $"arena_list_{account.InstanceNumber}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png"), list, cancellationToken);
                    if (deploy != null) await File.WriteAllBytesAsync(Path.Combine(dir, $"arena_nodeploy_{account.InstanceNumber}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png"), deploy, cancellationToken);
                    logger.LogWarning($"[{account.AccountName}] Arena: row {target + 1}'s swords did not open the deploy screen; stopping");
                    break;
                }
                await ClickAsync(account.InstanceNumber, logger, new Point(190, 1200)); // Quick Deploy
                await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
                await ClickAsync(account.InstanceNumber, logger, new Point(528, 1200)); // Fight
                fought++;

                // The fight plays itself; its result page ends with "Tap anywhere to exit"
                var start = DateTime.UtcNow;
                bool resultRead = false;
                while (DateTime.UtcNow - start < TimeSpan.FromMinutes(3))
                {
                    await Bot.Core.Utils.Pace.Delay(5000, cancellationToken);
                    var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (shot == null) continue;
                    var ocr = new OCRService(logger);
                    if (ocr.ExtractTextFromScreenArea(shot, new Rectangle(180, 1200, 360, 50))?.Contains("exit", StringComparison.OrdinalIgnoreCase) != true) continue;
                    var victory = ocr.ExtractTextFromScreenArea(shot, new Rectangle(150, 330, 420, 100))?.Contains("Victory", StringComparison.OrdinalIgnoreCase) == true;
                    if (victory) won++;
                    logger.LogInfo($"[{account.AccountName}] Arena fight {fought}: {(victory ? "won" : "lost")}");
                    await ClickAsync(account.InstanceNumber, logger, new Point(360, 1100));
                    await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);
                    resultRead = true;
                    break;
                }
                if (!resultRead)
                {
                    // Keep what was on screen: the result page was not recognised, so win or loss is unknown
                    var path = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots", $"arena_{account.InstanceNumber}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                    if (await TakeScreenshotAsync(account.InstanceNumber, logger) is { } stuck) await File.WriteAllBytesAsync(path, stuck, cancellationToken);
                    logger.LogWarning($"[{account.AccountName}] Arena fight {fought}: no result page after 3 min; saved {path}");
                    break;
                }
            }

            if (fought >= attempts) _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(6);
            // Close the Challenge List, then leave the Arena page with its back arrow (the locator would call this lost)
            await ClickAsync(account.InstanceNumber, logger, new Point(668, 192));
            await Bot.Core.Utils.Pace.Delay(1000, cancellationToken);
            await FindAndClickImageAsync("back-arrow.png", account.InstanceNumber, logger, threshold: 0.85, searchArea: new Rectangle(0, 0, 160, 90));
            await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: $"Arena: won {won}/{fought}");
        }

        /// <summary>
        /// Daily-tab rows that are done (Online Rewards "Completed", Daily Activity Triumph "Claimable") show a green
        /// check button where the others have a cyan arrow: tap each green one and close its reward popup.
        /// </summary>
        private async Task ClaimDailyRowsAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 4; i++)
            {
                var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (shot == null) return;
                int? y = null;
                using (var img = OpenCvSharp.Cv2.ImDecode(shot, OpenCvSharp.ImreadModes.Color))
                    for (int cy = 310; cy < 840 && y == null; cy += 6)
                    {
                        using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(395, cy - 8, 18, 16));
                        var m = OpenCvSharp.Cv2.Mean(roi); // BGR
                        if (m.Val0 < 140 && m.Val1 > 200)
                        {
                            // Other rows can have green parts too: only claim when the row says so
                            var label = new OCRService(logger).ExtractTextFromScreenArea(shot, new Rectangle(80, cy - 5, 300, 40)) ?? "";
                            if (label.Contains("Claimable", StringComparison.OrdinalIgnoreCase) || label.Contains("Completed", StringComparison.OrdinalIgnoreCase)) y = cy;
                            else cy += 40; // skip past this button
                        }
                    }
                if (y is not int row) return;
                logger.LogInfo($"[{account.AccountName}] 🎁 Claiming a finished Daily row");
                await ClickAsync(account.InstanceNumber, logger, new Point(404, row + 4));
                await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                if (!await IsSidePanelOpenAsync(account, logger)) // a reward popup covers the panel
                {
                    await PressBackAsync(account.InstanceNumber, logger);
                    await Bot.Core.Utils.Pace.Delay(1200, cancellationToken);
                    if (!await IsSidePanelOpenAsync(account, logger))
                    {
                        // The back closed the panel itself: reopen it on the Daily tab
                        if (!await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken)) return;
                        await ClickAsync(account.InstanceNumber, logger, new Point(370, 269));
                        await Bot.Core.Utils.Pace.Delay(800, cancellationToken);
                    }
                }
            }
        }

        private async Task<bool> IsSidePanelOpenAsync(AccountSettings account, LogService logger)
        {
            var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            return shot != null && _templateMatcher.MatchTemplate(shot, Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator", "side-panel-handle.png"),
                account.InstanceNumber, threshold: 0.88, searchArea: new Rectangle(400, 460, 140, 170)).found;
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
