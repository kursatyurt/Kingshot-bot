using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Services;
using Bot.Core.Utils;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing;
using Tesseract;

namespace Bot.Core.Tasks.Modules
{
    public class AutoTechnologyTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoTechnology;
        protected override string GetImageFolderName() => "autotech";
        public override string Name => "Auto Technology";

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _nextCheck = new();

        private static readonly Point SidePanelCloseHandle = new Point(464, 544);
        private static readonly Rectangle ResearchStatusArea = new Rectangle(150, 822, 160, 30);
        private static readonly Point ResearchArrow = new Point(405, 820);
        private static readonly Point RingResearchButton = new Point(480, 722);   // same slot as a camp's Train
        private static readonly Point RingUpgradeButton = new Point(360, 750);

        /// <summary>
        /// Keeps the Academy researching. The city side panel shows Tech Research as Idle or busy; its arrow centres
        /// the Academy with its ring open; ring Research opens the tree. Items are tried top to bottom: a research
        /// dialog has an orange Finish (gems, never tapped) beside the cyan Research; a locked item shows only its
        /// requirement with a cyan Go, which is skipped. Categories alternate per ResearchTypes (e.g. Economy, Growth).
        /// </summary>
        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (_nextCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next)
                return new TaskExecutionDetails(true, message: "Skipped: checked recently");
            _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(20);

            var settings = GetTaskSettings<AutoTechnologySettings>(account);
            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

            // 1. Is the Academy free?
            await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken);
            var panel = await TakeScreenshotAsync(account.InstanceNumber, logger);
            using (var ocr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 3.0f, PageSegMode = PageSegMode.SingleLine }))
            {
                var status = panel == null ? "" : ocr.ExtractBrightTextFromScreenArea(panel, ResearchStatusArea);
                if (!status.Contains("Idle", StringComparison.OrdinalIgnoreCase))
                {
                    await ClickAsync(account.InstanceNumber, logger, SidePanelCloseHandle);
                    logger.LogInfo($"[{account.AccountName}] Research busy or no Academy ('{status}')");
                    return new TaskExecutionDetails(true, message: "Research busy");
                }
            }

            // 2. Academy ring → Research (only on its normal idle ring: Details / Upgrade / Research)
            await ClickAsync(account.InstanceNumber, logger, ResearchArrow);
            await Task.Delay(2500, cancellationToken);
            var ring = await TakeScreenshotAsync(account.InstanceNumber, logger);
            var ringUpgrade = Path.Combine(Path.GetDirectoryName(ImageTemplateFolder)!, "autobuild", "ring-upgrade.png");
            if (ring == null || !_templateMatcher.MatchTemplate(ring, ringUpgrade, account.InstanceNumber, threshold: 0.9,
                    searchArea: new Rectangle(RingUpgradeButton.X - 90, RingUpgradeButton.Y - 90, 180, 180)).found)
            {
                logger.LogInfo($"[{account.AccountName}] The Academy's ring is not the idle one; leaving it");
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                return new TaskExecutionDetails(true, message: "Academy not ready");
            }
            await ClickAsync(account.InstanceNumber, logger, RingResearchButton);
            await Task.Delay(2500, cancellationToken);

            // 3. Category tab, then items top to bottom
            var researchType = settings.NextResearchType(account.InstanceNumber);
            var tab = researchType switch { ResearchType.Economy => new Point(361, 123), ResearchType.Battle => new Point(585, 120), _ => new Point(130, 116) };
            await ClickAsync(account.InstanceNumber, logger, tab);
            await Task.Delay(1500, cancellationToken);

            // The tree runs far below one screen (Command Tactics II, the next march queue, is deep in Growth):
            // find the items on each page by their level badges, then scroll on until the page stops changing.
            // Strategy: a priority research (march queue, construction speed) or a new item (level 0, which unlocks
            // the items below it) starts at once; levelling an item already started is only the fallback, so the
            // tree is opened toward Command Tactics instead of maxing every item on the way.
            // ponytail: rescans the overlap between pages; fine while a scan only runs every 20 minutes
            using var badgeOcr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 3.0f, PageSegMode = PageSegMode.SingleLine, CharacterWhitelist = "0123456789/" });
            using var titleOcr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f });
            (int page, Point item, double progress, string title)? fallback = null;
            byte[]? previousPage = null;
            for (int page = 0; page < 15; page++)
            {
                var pageShot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (pageShot == null) break;
                if (previousPage != null && SamePage(previousPage, pageShot)) break; // bottom of the tree
                previousPage = pageShot;
                // Locked (grey) items are never tapped. The scan still pages to the bottom: how finished items look
                // is unverified, so a page without teal items is not taken as the end of what is open
                var unlocked = FindTreeItems(pageShot).Where(p => IsUnlocked(pageShot, p)).ToList();
                foreach (var item in unlocked)
                {
                    var badge = badgeOcr.ExtractBrightTextFromScreenArea(pageShot, new Rectangle(item.X - 35, item.Y + 46, 70, 28));
                    await ClickAsync(account.InstanceNumber, logger, item);
                    await Task.Delay(1500, cancellationToken);
                    var dialog = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    var research = dialog == null ? null : FindResearchButton(dialog);
                    if (research is Point button)
                    {
                        // The outlined title font gets stray marks from OCR: compare its letters only ("CommandTacticsII")
                        var title = System.Text.RegularExpressions.Regex.Replace(titleOcr.ExtractTextFromScreenArea(dialog!, ResearchTitleArea), "[^A-Za-z]", "");
                        var level = System.Text.RegularExpressions.Regex.Match(badge, @"(\d+)\s*/\s*(\d+)");
                        bool isNew = level.Success && level.Groups[1].Value == "0";
                        bool isPriority = PriorityResearch.Any(p => title.Contains(p, StringComparison.OrdinalIgnoreCase));
                        if (isPriority || isNew)
                            return await StartResearchAsync(account, logger, locator, researchType, button,
                                $"{(isPriority ? "priority" : "new")} '{title}' ({badge.Trim()}), page {page}", cancellationToken);
                        double progress = level.Success && int.TryParse(level.Groups[2].Value, out var max) && max > 0
                            ? int.Parse(level.Groups[1].Value) / (double)max : 1;
                        if (fallback == null || progress < fallback.Value.progress) fallback = (page, item, progress, title);
                    }
                    if (dialog != null && HasDialogOpen(dialog))
                    {
                        await PressBackAsync(account.InstanceNumber, logger); // close a locked, maxed or deferred item
                        await Task.Delay(1000, cancellationToken);
                    }
                }
                await SwipeAsync(account.InstanceNumber, logger, new Point(360, 1000), new Point(360, 420), 1500); // slow: no fling
                await Task.Delay(1200, cancellationToken);
            }

            if (fallback is { } f)
            {
                // Nothing new or prioritised: level up the least-researched item that can start. Back to the top,
                // then the same slow swipes land on the same page.
                for (int up = 0; up < 15; up++)
                {
                    var before = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    await SwipeAsync(account.InstanceNumber, logger, new Point(360, 300), new Point(360, 1100), 300);
                    await Task.Delay(800, cancellationToken);
                    var after = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (before != null && after != null && SamePage(before, after)) break;
                }
                for (int page = 0; page < f.page; page++)
                {
                    await SwipeAsync(account.InstanceNumber, logger, new Point(360, 1000), new Point(360, 420), 1500);
                    await Task.Delay(1200, cancellationToken);
                }
                var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                var target = shot == null ? (Point?)null : FindTreeItems(shot).OrderBy(p => Math.Abs(p.X - f.item.X) + Math.Abs(p.Y - f.item.Y)).Cast<Point?>().FirstOrDefault();
                if (target is Point t && Math.Abs(t.X - f.item.X) + Math.Abs(t.Y - f.item.Y) < 60)
                {
                    await ClickAsync(account.InstanceNumber, logger, t);
                    await Task.Delay(1500, cancellationToken);
                    var dialog = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (dialog != null && FindResearchButton(dialog) is Point button)
                        return await StartResearchAsync(account, logger, locator, researchType, button, $"levelling '{f.title}', page {f.page}", cancellationToken);
                }
                logger.LogInfo($"[{account.AccountName}] Could not get back to '{f.title}' to level it up");
            }

            logger.LogInfo($"[{account.AccountName}] No {researchType} research available right now");
            AutoTechnologySettings.AdvanceResearchType(account.InstanceNumber); // try the other category next time
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: $"No {researchType} research available");
        }

        /// <summary>
        /// Researches started as soon as they can be: Command Tactics (march queue) and Tool Enhancement (construction
        /// speed). One distinctive word each, since OCR adds stray letters inside titles ("CommandjTiactics]ll").
        /// </summary>
        private static readonly string[] PriorityResearch = { "Command", "Enhanc" };
        private static readonly Rectangle ResearchTitleArea = new Rectangle(110, 178, 500, 48); // "Stone Output I" in the dialog's header

        private async Task<TaskExecutionDetails> StartResearchAsync(AccountSettings account, LogService logger, LocatorService locator,
            ResearchType researchType, Point button, string what, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] 🔬 Starting {researchType} research: {what}");
            await ClickAsync(account.InstanceNumber, logger, button);
            await Task.Delay(2000, cancellationToken);
            AutoTechnologySettings.AdvanceResearchType(account.InstanceNumber);
            _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(5); // short researches finish fast
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            await RequestAllianceHelpAsync(account, logger, cancellationToken); // the Academy's handshake bubble
            return new TaskExecutionDetails(true, message: $"Started {researchType} research");
        }

        /// <summary>
        /// Research items on the visible part of the tree: each has a brown level badge ("0/3", 62x25) under its
        /// icon, whose centre is ~60 px above the badge. Items cut off at the top or bottom are left for another page.
        /// </summary>
        private static List<Point> FindTreeItems(byte[] screenshot)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return new List<Point>();
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(img, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(10, 60, 80), new OpenCvSharp.Scalar(22, 170, 135), mask);
            using var kernel = OpenCvSharp.Cv2.GetStructuringElement(OpenCvSharp.MorphShapes.Rect, new OpenCvSharp.Size(9, 5));
            OpenCvSharp.Cv2.MorphologyEx(mask, mask, OpenCvSharp.MorphTypes.Close, kernel);
            OpenCvSharp.Cv2.FindContours(mask, out var contours, out _, OpenCvSharp.RetrievalModes.External, OpenCvSharp.ContourApproximationModes.ApproxSimple);
            return contours.Select(OpenCvSharp.Cv2.BoundingRect)
                .Where(r => r.Width is >= 50 and <= 85 && r.Height is >= 18 and <= 34 && r.Y > 230 && r.Y + r.Height < 1250)
                .Select(r => new Point(r.X + r.Width / 2, r.Y + r.Height / 2 - 60))
                .OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        }

        /// <summary>
        /// An unlocked item sits on a teal hexagon (a third of its box is teal); a locked one is grey (0%).
        /// </summary>
        private static bool IsUnlocked(byte[] screenshot, Point item)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return true;
            var box = new OpenCvSharp.Rect(item.X - 50, item.Y - 50, 100, 90) & new OpenCvSharp.Rect(0, 0, img.Width, img.Height);
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(new OpenCvSharp.Mat(img, box), hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var teal = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(80, 70, 120), new OpenCvSharp.Scalar(100, 255, 255), teal);
            return OpenCvSharp.Cv2.CountNonZero(teal) > box.Width * box.Height / 10;
        }

        /// <summary>True when a swipe did not move the tree: its bottom was already on screen.</summary>
        private static bool SamePage(byte[] before, byte[] after)
        {
            using var a = OpenCvSharp.Cv2.ImDecode(before, OpenCvSharp.ImreadModes.Grayscale);
            using var b = OpenCvSharp.Cv2.ImDecode(after, OpenCvSharp.ImreadModes.Grayscale);
            if (a.Empty() || b.Empty()) return false;
            var tree = new OpenCvSharp.Rect(20, 170, 680, 1080);
            using var diff = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.Absdiff(new OpenCvSharp.Mat(a, tree), new OpenCvSharp.Mat(b, tree), diff);
            return OpenCvSharp.Cv2.Mean(diff).Val0 < 3;
        }

        /// <summary>
        /// The cyan Research button of a research dialog: a cyan button in the right half with the orange Finish (gems)
        /// on its left at the same height. A locked item's cyan Go has no Finish beside it.
        /// </summary>
        private static Point? FindResearchButton(byte[] screenshot)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return null;
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(img, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            var cyan = FindButton(hsv, new OpenCvSharp.Scalar(85, 120, 150), new OpenCvSharp.Scalar(100, 255, 255), xMin: 360);
            var orange = FindButton(hsv, new OpenCvSharp.Scalar(10, 150, 180), new OpenCvSharp.Scalar(25, 255, 255), xMin: 0, xMax: 360);
            if (orange is not OpenCvSharp.Rect o) return null;
            // A tutorial glove can cover Research; it always sits 299 px right of Finish, at the same height
            if (cyan is not OpenCvSharp.Rect c) return new Point(o.X + 299 + o.Width / 2, o.Y + o.Height / 2);
            if (Math.Abs((c.Y + c.Height / 2) - (o.Y + o.Height / 2)) > 25) return null;
            return new Point(c.X + c.Width / 2, c.Y + c.Height / 2);
        }

        private static OpenCvSharp.Rect? FindButton(OpenCvSharp.Mat hsv, OpenCvSharp.Scalar lo, OpenCvSharp.Scalar hi, int xMin, int xMax = 720)
        {
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(hsv, lo, hi, mask);
            OpenCvSharp.Cv2.FindContours(mask, out var contours, out _, OpenCvSharp.RetrievalModes.External, OpenCvSharp.ContourApproximationModes.ApproxSimple);
            return contours.Select(OpenCvSharp.Cv2.BoundingRect)
                .Where(r => r.Width > 150 && r.Height > 45 && r.Height < 120 && r.X >= xMin && r.X + r.Width <= xMax && r.Y > 700)
                .Select(r => (OpenCvSharp.Rect?)r).FirstOrDefault();
        }

        private static AutoTechnologySettings GetTaskSettings<T>(AccountSettings account) where T : new()
        {
            if (!account.TaskSettings.TryGetValue("AutoTechnology", out var settingsJson)) return new AutoTechnologySettings();
            try { return JsonSerializer.Deserialize<AutoTechnologySettings>(settingsJson) ?? new AutoTechnologySettings(); }
            catch { return new AutoTechnologySettings(); }
        }

        /// <summary>A research dialog covers the tree with its dimmed backdrop: its title bar shows at the top.</summary>
        private static bool HasDialogOpen(byte[] screenshot)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return false;
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(0, 0, 720, 60));
            return OpenCvSharp.Cv2.Mean(roi).Val2 < 85; // the header darkens behind a dialog (red ~66; tree alone ~105)
        }
    }

    public enum ResearchType
    {
        Growth,
        Economy,
        Battle
    }

    public class AutoTechnologySettings
    {
        public ResearchType ResearchType { get; set; } = ResearchType.Growth;

        /// <summary>
        /// Research categories to alternate between, one research at a time. Overrides ResearchType when set.
        /// </summary>
        public List<ResearchType>? ResearchTypes { get; set; }

        public bool IsEnabled { get; set; } = true;

        // Per-instance position in ResearchTypes; advances only after a research actually starts
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _nextIndex = new();

        /// <summary>Never Battle: the march queue (the goal) is in Growth, and Battle only spends resources on troop stats.</summary>
        public ResearchType NextResearchType(int instanceNumber)
        {
            var types = (ResearchTypes is { Count: > 0 } list ? list : new List<ResearchType> { ResearchType })
                .Where(t => t != ResearchType.Battle).ToList();
            return types.Count == 0 ? ResearchType.Growth : types[_nextIndex.GetOrAdd(instanceNumber, 0) % types.Count];
        }

        public static void AdvanceResearchType(int instanceNumber) =>
            _nextIndex.AddOrUpdate(instanceNumber, 1, (_, i) => i + 1);
    }
}