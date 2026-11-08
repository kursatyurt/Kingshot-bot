using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Side panel Daily tab: the Fountain of Life row's button (green check when full, else an arrow)
    /// opens My Island, where each green water bubble is tapped, then its back arrow returns to the city. Once per UTC day (after the 00:00 reset) Pet Adventure's
    /// row arrow → back arrow lands on the pet enclosure → Skills: only the Lynx's Comforting Embrace (+35 stamina) is
    /// used, and only when its Use button is lit; the Gray Wolf's Construction Aide is never touched.
    /// </summary>
    public class PetsFountainTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.PetsFountain;
        public override string Name => "Pets & Fountain";
        protected override string GetImageFolderName() => "pets";

        private static readonly Dictionary<int, DateTime> _nextCheck = new();
        private static readonly string PetsDayFile = Path.Combine(AppContext.BaseDirectory, "data", "pets_day.json");
        private static readonly Rectangle RowIcons = new(0, 290, 120, 570);

        /// <summary>When each instance's next pet comes home (Daily row timer); the task and the cycle wake for it.</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> NextPetReturn = new();

        protected override bool OnCooldown(AccountSettings account) => _nextCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next
            && !(NextPetReturn.TryGetValue(account.InstanceNumber, out var pet) && DateTime.UtcNow >= pet);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (OnCooldown(account)) return new TaskExecutionDetails(true, message: "Pets & Fountain checked recently");
            _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(2);

            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            var done = new List<string>();

            int? fy = await OpenDailyTabAsync(account, logger, cancellationToken) ? await RowYAsync(account, logger, "fountain-row.png") : null;
            if (fy == null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots", $"fountain_norow_{account.InstanceNumber}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                if (await TakeScreenshotAsync(account.InstanceNumber, logger) is { } miss) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, miss, cancellationToken); }
                logger.LogWarning($"[{account.AccountName}] Fountain of Life row not found in the Daily tab; saved {path}");
            }
            else
            {
                // The row's tick or arrow both open My Island, which can hold water either way: always look
                await ClickAsync(account.InstanceNumber, logger, new Point(404, fy.Value));
                if (await WaitForImageAsync("my-island-title.png", account.InstanceNumber, logger, cancellationToken, 8000, threshold: 0.85))
                {
                    var collected = await CollectWaterBubblesAsync(account, logger, cancellationToken);
                    if (collected > 0) done.Add($"collected {collected} water bubble(s)");
                    // My Island's back arrow → city; a building left in move mode eats the first tap
                    for (int i = 0; i < 3; i++)
                    {
                        await ClickAsync(account.InstanceNumber, logger, new Point(46, 34));
                        await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                        if (!await WaitForImageAsync("my-island-title.png", account.InstanceNumber, logger, cancellationToken, 500, threshold: 0.85)) break;
                    }
                }
            }

            var adventure = await PetAdventureAsync(account, logger, locator, cancellationToken);
            if (adventure != null) done.Add(adventure);

            var accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
            if (string.IsNullOrEmpty(accountId)) accountId = $"Account_{account.AccountName}_{account.InstanceNumber}";
            var days = LoadPetsDays();
            if (!days.TryGetValue(accountId, out var day) || day < DateTime.UtcNow.Date)
            {
                // Locked or cooling down: look again next run (the 23 h cooldown would drift if the day were closed)
                if (await UseComfortingEmbraceAsync(account, logger, locator, cancellationToken))
                {
                    done.Add("Comforting Embrace used");
                    days[accountId] = DateTime.UtcNow.Date;
                    SavePetsDays(days);
                }
            }

            await ReadPetTimerAsync(account, logger, locator, cancellationToken);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: done.Count > 0 ? string.Join("; ", done) : "Nothing to do");
        }

        private static readonly Rectangle AdventureMap = new(0, 150, 720, 1000);
        private static readonly Point[] PetSlots = { new(142, 657), new(286, 657), new(430, 657), new(574, 657) };

        /// <summary>
        /// Pet Adventure: opens each finished (green tick) treasure spot's chest, then sends a free pet to a free spot
        /// (only those show Select Pet) while "Remaining attempts today" is above 0. Start costs pet food only; no gems
        /// and no attempt purchases are ever tapped.
        /// </summary>
        private async Task<string?> PetAdventureAsync(AccountSettings account, LogService logger, LocatorService locator, CancellationToken cancellationToken)
        {
            int n = account.InstanceNumber;
            await locator.EnsureViewAsync(ViewType.BaseView, n, cancellationToken);
            if (!await OpenDailyTabAsync(account, logger, cancellationToken) || await RowYAsync(account, logger, "pet-adventure-row.png") is not int py)
                return null;
            await ClickAsync(n, logger, new Point(404, py));
            if (!await WaitForImageAsync("pet-adventure-title.png", n, logger, cancellationToken, 6000, threshold: 0.85))
                return null;

            int claimed = 0, sent = 0;
            for (int i = 0; i < 4; i++)
            {
                var (found, tick, _) = await WaitForImageWithPositionAsync("adventure-done-tick.png", n, logger, cancellationToken, 1500, threshold: 0.85, searchArea: AdventureMap);
                if (!found) break;
                await ClickAsync(n, logger, new Point(tick.X + tick.Width / 2 - 54, tick.Y + tick.Height / 2 + 46)); // the spot under the tick
                await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                await ClickAsync(n, logger, new Point(360, 820)); // open the chest
                await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);
                await ClickAsync(n, logger, new Point(360, 1180)); // "Tap anywhere to exit"
                await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
                await ClosePopupsAsync(account, logger, cancellationToken);
                claimed++;
            }

            for (int i = 0; i < 4; i++)
            {
                var map = await TakeScreenshotAsync(n, logger);
                if (map == null || ParseAttempts(new OCRService(logger).ExtractBrightTextFromScreenArea(map, new Rectangle(150, 110, 420, 50))) is not > 0) break;
                bool dispatched = false;
                foreach (var spot in FindAll(map, Path.Combine(ImageTemplateFolder, "adventure-chest.png"), 0.8, AdventureMap))
                {
                    await ClickAsync(n, logger, new Point(spot.X + spot.Width / 2, spot.Y + spot.Height / 2));
                    await Bot.Core.Utils.Pace.Delay(1800, cancellationToken);
                    if (await WaitForImageAsync("select-pet-button.png", n, logger, cancellationToken, 1500, threshold: 0.85))
                    {
                        await ClickAsync(n, logger, new Point(360, 1046)); // Select Pet
                        await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                        var list = await TakeScreenshotAsync(n, logger);
                        if (list != null && Array.FindIndex(PetSlots, p => IsPetFree(list, p)) is var free and >= 0)
                        {
                            await ClickAsync(n, logger, PetSlots[free]);
                            await Bot.Core.Utils.Pace.Delay(800, cancellationToken);
                            if (await WaitForImageAsync("start-button.png", n, logger, cancellationToken, 1500, threshold: 0.85))
                            {
                                logger.LogInfo($"[{account.AccountName}] 🐾 Sending a pet on adventure");
                                await ClickAsync(n, logger, new Point(360, 1004)); // Start (pet food)
                                await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);
                                sent++;
                                dispatched = true;
                            }
                        }
                    }
                    await ClosePopupsAsync(account, logger, cancellationToken);
                    if (dispatched) break;
                }
                if (!dispatched) break; // no free spot or no free pet
            }

            await ClickAsync(n, logger, new Point(48, 40)); // Pet Adventure → enclosure
            await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
            await ClickAsync(n, logger, new Point(48, 40)); // leave the enclosure
            await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
            logger.LogInfo($"[{account.AccountName}] Pet Adventure: {claimed} chest(s) opened, {sent} pet(s) sent");
            return claimed + sent > 0 ? $"Pet Adventure: {claimed} chest(s), {sent} pet(s) sent" : null;
        }

        /// <summary>The Daily tab's Pet Adventure row shows the next return countdown ("Completed" when a chest waits).</summary>
        private async Task ReadPetTimerAsync(AccountSettings account, LogService logger, LocatorService locator, CancellationToken cancellationToken)
        {
            NextPetReturn.TryRemove(account.InstanceNumber, out _);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            if (!await OpenDailyTabAsync(account, logger, cancellationToken) || await RowYAsync(account, logger, "pet-adventure-row.png") is not int py) return;
            var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (shot != null && EventRewardsTask.ParseTimer(new OCRService(logger).ExtractBrightTextFromScreenArea(shot, new Rectangle(80, py, 290, 32))) is { } wait)
            {
                NextPetReturn[account.InstanceNumber] = DateTime.UtcNow + wait + TimeSpan.FromSeconds(20);
                logger.LogInfo($"[{account.AccountName}] Next pet home in {wait}");
            }
        }

        private async Task ClosePopupsAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 3; i++)
            {
                var (found, x, _) = await WaitForImageWithPositionAsync("popup-close.png", account.InstanceNumber, logger, cancellationToken, 800, threshold: 0.85);
                if (!found) return;
                await ClickAsync(account.InstanceNumber, logger, new Point(x.X + x.Width / 2, x.Y + x.Height / 2));
                await Bot.Core.Utils.Pace.Delay(1200, cancellationToken);
            }
        }

        /// <summary>"Remaining attempts today: 2" → 2.</summary>
        public static int? ParseAttempts(string? text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"today\W*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }

        /// <summary>A pet slot holding a portrait without the red "In Adventure" label.</summary>
        public static bool IsPetFree(byte[] screenshot, Point slot)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(slot.X - 55, slot.Y - 30, 110, 50));
            using var red = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(roi, new OpenCvSharp.Scalar(0, 0, 170), new OpenCvSharp.Scalar(90, 80, 255), red);
            OpenCvSharp.Cv2.MeanStdDev(roi, out _, out var sd);
            bool empty = sd.Val0 + sd.Val1 + sd.Val2 < 45; // plain beige list background, portraits are busy
            return !empty && OpenCvSharp.Cv2.CountNonZero(red) < 150;
        }

        /// <summary>Every place the template matches above the threshold, best first.</summary>
        public static List<Rectangle> FindAll(byte[] screenshot, string templatePath, double threshold, Rectangle area)
        {
            var hits = new List<Rectangle>();
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var tpl = OpenCvSharp.Cv2.ImRead(templatePath, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(area.X, area.Y, area.Width, area.Height));
            using var res = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.MatchTemplate(roi, tpl, res, OpenCvSharp.TemplateMatchModes.CCoeffNormed);
            for (int i = 0; i < 6; i++)
            {
                OpenCvSharp.Cv2.MinMaxLoc(res, out _, out double max, out _, out var loc);
                if (max < threshold) break;
                hits.Add(new Rectangle(area.X + loc.X, area.Y + loc.Y, tpl.Width, tpl.Height));
                OpenCvSharp.Cv2.Rectangle(res, new OpenCvSharp.Rect(loc.X - tpl.Width / 2, loc.Y - tpl.Height / 2, tpl.Width, tpl.Height), OpenCvSharp.Scalar.All(-1), -1);
            }
            return hits;
        }

        /// <summary>True when Comforting Embrace was used.</summary>
        private async Task<bool> UseComfortingEmbraceAsync(AccountSettings account, LogService logger, LocatorService locator, CancellationToken cancellationToken)
        {
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            if (!await OpenDailyTabAsync(account, logger, cancellationToken) || await RowYAsync(account, logger, "pet-adventure-row.png") is not int py)
                return false;
            await ClickAsync(account.InstanceNumber, logger, new Point(404, py)); // Pet Adventure
            if (!await WaitForImageAsync("pet-adventure-title.png", account.InstanceNumber, logger, cancellationToken, 6000, threshold: 0.85))
                return false;
            await ClickAsync(account.InstanceNumber, logger, new Point(48, 40)); // back to the enclosure
            await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(82, 1200)); // Skills
            if (!await WaitForImageAsync("pet-skill-title.png", account.InstanceNumber, logger, cancellationToken, 5000, threshold: 0.85))
                return false;

            bool used = false;
            string result;
            var (found, lynx, _) = await WaitForImageWithPositionAsync("lynx-skill.png", account.InstanceNumber, logger, cancellationToken, 3000, threshold: 0.8, searchArea: new Rectangle(50, 225, 620, 530));
            if (!found) result = "no Lynx skill";
            else
            {
                await ClickAsync(account.InstanceNumber, logger, new Point(lynx.X + lynx.Width / 2, lynx.Y + lynx.Height / 2));
                await Bot.Core.Utils.Pace.Delay(1200, cancellationToken);
                var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                // Locked shows red "Unlocked after…" text and on cooldown the button is grey: only a cyan Use is pressed
                if (shot != null && IsCyan(shot, new Rectangle(240, 1060, 240, 50)))
                {
                    logger.LogInfo($"[{account.AccountName}] 🐱 Using Comforting Embrace (+35 stamina)");
                    await ClickAsync(account.InstanceNumber, logger, new Point(360, 1088));
                    await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
                    result = "Comforting Embrace used";
                    used = true;
                }
                else result = "Comforting Embrace locked or on cooldown";
            }
            await ClickAsync(account.InstanceNumber, logger, new Point(668, 152)); // close Pet Skill
            await Bot.Core.Utils.Pace.Delay(1000, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(48, 40));  // leave the enclosure
            await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
            logger.LogInfo($"[{account.AccountName}] Pets: {result}");
            return used;
        }

        /// <summary>
        /// The row only opens My Island: the water is in drop bubbles (green up to their fill level) over the fountain (+1,440 when full) and the
        /// well (+140), wherever the island camera puts them. Each tap collects one bubble.
        /// </summary>
        private async Task<int> CollectWaterBubblesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            int collected = 0;
            for (; collected < 5; collected++)
            {
                // Match the blue drop, not the bubble: a partly filled bubble is only green at the bottom
                await Bot.Core.Utils.Pace.Delay(collected == 0 ? 2000 : 1000, cancellationToken);
                var island = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (island == null || FindAll(island, Path.Combine(ImageTemplateFolder, "water-drop.png"), 0.72, new Rectangle(0, 90, 720, 1000)) is not [var bubble, ..]) break;
                logger.LogInfo($"[{account.AccountName}] 💧 Collecting water essence");
                await ClickAsync(account.InstanceNumber, logger, new Point(bubble.X + bubble.Width / 2, bubble.Y + bubble.Height / 2));
                await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
            }
            return collected;
        }

        private async Task<bool> OpenDailyTabAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            if (!await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken)) return false;
            await ClickAsync(account.InstanceNumber, logger, new Point(370, 269)); // Daily tab
            await Bot.Core.Utils.Pace.Delay(800, cancellationToken);
            return true;
        }

        private async Task<int?> RowYAsync(AccountSettings account, LogService logger, string icon)
        {
            var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (shot == null) return null;
            var (found, rect, _) = _templateMatcher.MatchTemplate(shot, Path.Combine(ImageTemplateFolder, icon), account.InstanceNumber, threshold: 0.9, searchArea: RowIcons);
            return found ? rect.Y + rect.Height / 2 : null;
        }

        public static bool IsCyan(byte[] screenshot, Rectangle rect)
        {
            var m = Mean(screenshot, rect);
            return m.Val0 > 170 && m.Val1 > 160 && m.Val2 < 120;
        }

        private static OpenCvSharp.Scalar Mean(byte[] screenshot, Rectangle rect)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(rect.X, rect.Y, rect.Width, rect.Height));
            return OpenCvSharp.Cv2.Mean(roi); // BGR
        }

        private static Dictionary<string, DateTime> LoadPetsDays()
        {
            try { return File.Exists(PetsDayFile) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(PetsDayFile)) ?? new() : new(); }
            catch { return new(); }
        }

        private static void SavePetsDays(Dictionary<string, DateTime> days)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PetsDayFile)!);
            File.WriteAllText(PetsDayFile, JsonSerializer.Serialize(days));
        }
    }
}
