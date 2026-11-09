using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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

        private static readonly Dictionary<int, DateTime> _lastPetRun = new();

        /// <summary>
        /// The Daily tab's Pet Adventure row counts down while every pet is out; without a timer a chest or a free pet
        /// waits. Pets run then (at most every 30 min); the fountain is collected every 4 h whatever its fill.
        /// </summary>
        private static bool PetsDue(int n) =>
            !(_lastPetRun.TryGetValue(n, out var last) && DateTime.UtcNow < last)
            && SidePanelStatus.Latest(n) is { } p
            && !p.Daily.Any(r => r.Is("Adventure") && r.Timer != null) // every pet out: its row counts down to the first one home
            && p.HasDot("Adventure"); // the red dot: a chest to open or a pet to send

        /// <summary>The Daily tab's "Available: have/cap": the island is worth a trip from half full (KINGSHOT_FOUNTAIN_MIN overrides).</summary>
        private static readonly double FountainMin = double.TryParse(Environment.GetEnvironmentVariable("KINGSHOT_FOUNTAIN_MIN"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var min) ? min : 0.5;

        private static bool FountainDue(int n) => SidePanelStatus.Latest(n) is { } p
            ? p.Fountain is { } f ? f.Have >= f.Cap * FountainMin : p.HasDot("Fountain") // unreadable fill: the row's red dot says there is water
            : !(_nextCheck.TryGetValue(n, out var next) && DateTime.UtcNow < next);

        /// <summary>The Daily tab's "Daily Activity Triumph" row shows a red dot while the personal chest is claimable.</summary>
        private static bool TriumphDue(int n) => SidePanelStatus.Latest(n)?.HasDot("Triumph") == true
            && !(_triumphTried.TryGetValue(n, out var t) && DateTime.UtcNow < t);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _triumphTried = new();

        protected override bool OnCooldown(AccountSettings account) => !FountainDue(account.InstanceNumber) && !PetsDue(account.InstanceNumber) && !TriumphDue(account.InstanceNumber);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (OnCooldown(account)) return new TaskExecutionDetails(true, message: "Pets & Fountain checked recently");
            bool fountain = FountainDue(account.InstanceNumber), pets = PetsDue(account.InstanceNumber); // only on the panel's word
            if (fountain) _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(6);
            if (pets) _lastPetRun[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(30);

            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            var done = new List<string>();

            int? fy = !fountain ? null : await OpenDailyTabAsync(account, logger, cancellationToken) ? await RowYAsync(account, logger, "fountain-row.png") : null;
            if (!fountain) { }
            else if (fy == null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots", $"fountain_norow_{account.InstanceNumber}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                if (await TakeScreenshotAsync(account.InstanceNumber, logger) is { } miss) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, miss, cancellationToken); }
                logger.LogWarning($"[{account.AccountName}] Fountain of Life row not found in the Daily tab; saved {path}");
            }
            else
            {
                // The row's tick or arrow both open My Island, which can hold water either way: always look
                await ClickAsync(account.InstanceNumber, logger, new Point(404, fy.Value));
                // The trip passes a cloud transition that can take well over 8 s
                bool onIsland = await WaitForImageAsync("my-island-title.png", account.InstanceNumber, logger, cancellationToken, 20000, threshold: 0.85);
                if (!onIsland && await TakeScreenshotAsync(account.InstanceNumber, logger) is { } missed)
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots", $"island_notopen_{account.InstanceNumber}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, missed, cancellationToken);
                    logger.LogWarning($"[{account.AccountName}] Fountain row did not open My Island; saved {path}");
                }
                if (onIsland)
                {
                    var collected = await CollectWaterBubblesAsync(account, logger, cancellationToken);
                    if (collected > 0) done.Add($"collected {collected} water bubble(s)");
                    else if (await TakeScreenshotAsync(account.InstanceNumber, logger) is { } dry)
                    {
                        var path = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots", $"island_nodrops_{account.InstanceNumber}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllBytesAsync(path, dry, cancellationToken);
                        logger.LogWarning($"[{account.AccountName}] On My Island but no water drop found; saved {path}");
                    }
                    if (!(_islandHelpDone.TryGetValue(account.InstanceNumber, out var helpDay) && helpDay >= DateTime.UtcNow.Date))
                    {
                        var helped = await HelpAllyIslandsAsync(account, logger, cancellationToken);
                        if (helped > 0) done.Add($"helped {helped} ally fountain(s)");
                    }
                    // My Island's back arrow → city; a building left in move mode eats the first tap
                    for (int i = 0; i < 3; i++)
                    {
                        await ClickAsync(account.InstanceNumber, logger, new Point(46, 34));
                        await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                        if (!await WaitForImageAsync("my-island-title.png", account.InstanceNumber, logger, cancellationToken, 500, threshold: 0.85)) break;
                    }
                }
            }

            var adventure = pets ? await PetAdventureAsync(account, logger, locator, cancellationToken) : null;
            if (pets && adventure == null) _lastPetRun[account.InstanceNumber] = DateTime.UtcNow.AddHours(3); // a dot with nothing for us: leave it a while
            if (adventure != null) done.Add(adventure);

            if (TriumphDue(account.InstanceNumber))
            {
                _triumphTried[account.InstanceNumber] = DateTime.UtcNow.AddHours(1); // a dot we could not clear waits an hour
                if (await ClaimActivityTriumphAsync(account, logger, locator, cancellationToken)) done.Add("Activity Triumph chest claimed");
            }

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

            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: done.Count > 0 ? string.Join("; ", done) : "Nothing to do");
        }

        /// <summary>
        /// Daily tab → "Daily Activity Triumph" row's ✓ → Activity Triumph page → the free "Personal Activity Triumph Chest"
        /// → "Tap anywhere to exit" → back. Nothing on the page costs gems.
        /// </summary>
        private async Task<bool> ClaimActivityTriumphAsync(AccountSettings account, LogService logger, LocatorService locator, CancellationToken cancellationToken)
        {
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            if (!await OpenDailyTabAsync(account, logger, cancellationToken) || await TakeScreenshotAsync(account.InstanceNumber, logger) is not { } shot) return false;
            var ocr = new OCRService(logger);
            int? row = SidePanelStatus.RowDots(shot).Cast<int?>().FirstOrDefault(y =>
                ocr.ExtractBrightTextFromScreenArea(shot, new Rectangle(70, y!.Value - 8, 300, 32), 190).Contains("Triumph", StringComparison.OrdinalIgnoreCase));
            if (row == null) { logger.LogInfo($"[{account.AccountName}] Activity Triumph row not found"); return false; }
            await ClickAsync(account.InstanceNumber, logger, new Point(404, row.Value + 18)); // the row's ✓, just below its dot
            await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);
            if (await TakeScreenshotAsync(account.InstanceNumber, logger) is not { } page
                || !ocr.ExtractBrightTextFromScreenArea(page, new Rectangle(90, 15, 300, 50), 190).Contains("Triumph", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInfo($"[{account.AccountName}] Activity Triumph page did not open");
                return false;
            }
            await ClickAsync(account.InstanceNumber, logger, new Point(360, 870)); // Personal Activity Triumph Chest
            await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(360, 1180)); // "Tap anywhere to exit"
            await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
            await PressBackAsync(account.InstanceNumber, logger);
            await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
            logger.LogInfo($"[{account.AccountName}] 🏆 Activity Triumph chest claimed");
            return true;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _islandHelpDone = new();

        /// <summary>Red dots on the Island Prosperity list's visit buttons: those allies' fountains can be helped.</summary>
        public static List<int> VisitDots(byte[] screenshot)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(650, 230, 40, 890));
            using var hsv = roi.CvtColor(OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var lo = hsv.InRange(new OpenCvSharp.Scalar(0, 150, 180), new OpenCvSharp.Scalar(8, 255, 255));
            using var hi = hsv.InRange(new OpenCvSharp.Scalar(172, 150, 180), new OpenCvSharp.Scalar(180, 255, 255));
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.BitwiseOr(lo, hi, mask);
            OpenCvSharp.Cv2.FindContours(mask, out var contours, out _, OpenCvSharp.RetrievalModes.External, OpenCvSharp.ContourApproximationModes.ApproxSimple);
            return contours.Select(OpenCvSharp.Cv2.BoundingRect).Where(r => r.Width is >= 8 and <= 24 && r.Height is >= 8 and <= 24)
                .Select(r => r.Y + 230 + r.Height / 2).OrderBy(y => y).ToList();
        }

        /// <summary>The visited island's help counter ("2/3": helps we have left today), or null when unreadable.</summary>
        public static int? HelpsLeft(byte[] screenshot, LogService logger) =>
            System.Text.RegularExpressions.Regex.Match(new OCRService(logger).ExtractBrightTextFromScreenArea(screenshot, new Rectangle(610, 1088, 90, 36), 220) ?? "", @"(\d)\s*/\s*3") is { Success: true } m
                ? int.Parse(m.Groups[1].Value) : null;

        /// <summary>
        /// From My Island: trophy → Island Prosperity (Your Alliance) → a visit button with a red dot → tap the drop-and-handshake
        /// icon over that ally's Fountain of Life. 3 helps a day (reset 00:00 UTC); each ally once per 6 h. Free, no gems.
        /// </summary>
        private async Task<int> HelpAllyIslandsAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            int helped = 0;
            var ocr = new OCRService(logger);
            for (int visit = 0; visit < 5; visit++)
            {
                await ClickAsync(account.InstanceNumber, logger, new Point(670, 103)); // trophy
                await Bot.Core.Utils.Pace.Delay(3000, cancellationToken);
                if (await TakeScreenshotAsync(account.InstanceNumber, logger) is not { } list
                    || !ocr.ExtractBrightTextFromScreenArea(list, new Rectangle(90, 15, 300, 50), 190).Contains("Prosperity", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInfo($"[{account.AccountName}] Island Prosperity list did not open");
                    break;
                }
                int? dot = null;
                for (int page = 0; page < 15 && dot == null; page++) // the list starts at the top every time it opens
                {
                    if (page > 0)
                    {
                        await SwipeAsync(account.InstanceNumber, logger, new Point(360, 1000), new Point(360, 300), 400);
                        await Bot.Core.Utils.Pace.Delay(1000, cancellationToken);
                    }
                    if (await TakeScreenshotAsync(account.InstanceNumber, logger) is { } shot && VisitDots(shot) is [var y, ..]) dot = y;
                }
                if (dot == null)
                {
                    logger.LogInfo($"[{account.AccountName}] No ally island needs help right now");
                    await PressBackAsync(account.InstanceNumber, logger);
                    await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                    break;
                }
                await ClickAsync(account.InstanceNumber, logger, new Point(645, dot.Value + 22)); // that row's visit (eye) button
                await Bot.Core.Utils.Pace.Delay(9000, cancellationToken);
                var island = await TakeScreenshotAsync(account.InstanceNumber, logger);
                var (found, icon, _) = island == null ? (false, Rectangle.Empty, 0.0)
                    : _templateMatcher!.MatchTemplate(island, Path.Combine(ImageTemplateFolder, "island-help.png"), account.InstanceNumber, threshold: 0.8, searchArea: new Rectangle(0, 90, 720, 900));
                if (found)
                {
                    await ClickAsync(account.InstanceNumber, logger, new Point(icon.X + icon.Width / 2, icon.Y + icon.Height / 2));
                    await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);
                    helped++;
                    logger.LogInfo($"[{account.AccountName}] 🤝 Helped an ally's Fountain of Life");
                }
                var left = await TakeScreenshotAsync(account.InstanceNumber, logger) is { } after ? HelpsLeft(after, logger) : null;
                await ClickAsync(account.InstanceNumber, logger, new Point(46, 34)); // back → My Island
                await Bot.Core.Utils.Pace.Delay(5000, cancellationToken);
                if (left == 0) { _islandHelpDone[account.InstanceNumber] = DateTime.UtcNow.Date; break; }
            }
            return helped;
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

            await LeaveToCityAsync(account, logger, cancellationToken); // Pet Adventure → enclosure → city
            logger.LogInfo($"[{account.AccountName}] Pet Adventure: {claimed} chest(s) opened, {sent} pet(s) sent");
            return claimed + sent > 0 ? $"Pet Adventure: {claimed} chest(s), {sent} pet(s) sent" : null;
        }

        private async Task<bool> InCityAsync(AccountSettings account, LogService logger) =>
            await TakeScreenshotAsync(account.InstanceNumber, logger) is { } shot
            && _templateMatcher.MatchTemplate(shot, Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator", "base-view.png"), account.InstanceNumber, 0.7).found;

        /// <summary>Back arrow until the city shows (Pet Adventure and the enclosure each need one; a slow screen eats a tap).</summary>
        private async Task LeaveToCityAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 4; i++)
            {
                if (await InCityAsync(account, logger)) return;
                await ClickAsync(account.InstanceNumber, logger, new Point(48, 40));
                await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
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
        /// <summary>A full fountain's bubble is a bright green square (~85 px) that the guide hand can cover the drop of.</summary>
        public static Rectangle? FindGreenBubble(byte[] screenshot, Rectangle area, string dropTemplate)
        {
            var faintDrops = FindAll(screenshot, dropTemplate, 0.5, area); // the half-hidden drop still scores ~0.5
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(area.X, area.Y, area.Width, area.Height));
            using var hsv = roi.CvtColor(OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new OpenCvSharp.Scalar(40, 120, 200), new OpenCvSharp.Scalar(75, 255, 255));
            OpenCvSharp.Cv2.FindContours(mask, out var contours, out _, OpenCvSharp.RetrievalModes.External, OpenCvSharp.ContourApproximationModes.ApproxSimple);
            foreach (var c in contours.OrderByDescending(c => OpenCvSharp.Cv2.ContourArea(c)))
            {
                var r = OpenCvSharp.Cv2.BoundingRect(c);
                // The drop and the guide hand split the square: its widest solid piece is enough to tap
                var box = new Rectangle(r.X + area.X, r.Y + area.Y, r.Width, r.Height);
                var near = Rectangle.Inflate(box, 10, 50);
                if (r.Width is >= 55 and <= 120 && r.Height is >= 30 and <= 120 && OpenCvSharp.Cv2.ContourArea(c) > 1000
                    && faintDrops.Any(d => near.Contains(d.X + d.Width / 2, d.Y + d.Height / 2)))
                    return box;
            }
            return null;
        }

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
            // Back sometimes lands in the city, where (82,1200) is the Conquest button: only tap Skills in the enclosure
            if (await InCityAsync(account, logger))
            {
                logger.LogInfo($"[{account.AccountName}] Pets: back went to the city, not the enclosure; skill check next time");
                return false;
            }
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
            await LeaveToCityAsync(account, logger, cancellationToken); // leave the enclosure
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
                // Match the blue drop, not the bubble: a partly filled bubble is only green at the bottom.
                // The drops pop up a few seconds after the island loads: give the first one up to ~12 s
                Rectangle? found = null;
                for (int look = 0; look < (collected == 0 ? 8 : 2) && found == null; look++)
                {
                    await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
                    if (await TakeScreenshotAsync(account.InstanceNumber, logger) is { } island
                        && (FindAll(island, Path.Combine(ImageTemplateFolder, "water-drop.png"), 0.72, new Rectangle(0, 90, 720, 1000)) is [var b, ..]
                            ? b : FindGreenBubble(island, new Rectangle(0, 90, 720, 1000), Path.Combine(ImageTemplateFolder, "water-drop.png"))) is { } hit)
                        found = hit;
                }
                if (found is not { } bubble) break;
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
            // The Daily tab can still be sliding in or its rows moving: only trust a row seen at the same height twice
            int? last = null;
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) await Task.Delay(700);
                var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (shot == null) continue;
                var (found, rect, _) = _templateMatcher.MatchTemplate(shot, Path.Combine(ImageTemplateFolder, icon), account.InstanceNumber, threshold: 0.9, searchArea: RowIcons);
                int? y = found ? rect.Y + rect.Height / 2 : null;
                if (y != null && last != null && Math.Abs(y.Value - last.Value) <= 3) return y;
                last = y;
            }
            return null;
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
