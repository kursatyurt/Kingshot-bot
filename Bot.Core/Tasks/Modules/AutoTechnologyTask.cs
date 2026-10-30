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
        private static readonly int[] TreeColumns = { 140, 360, 579 };            // research items sit on a grid
        private static readonly int[] TreeRows = { 245, 515, 785, 1055 };          // icon centres (level badges ~58 px below)

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
            if (Modules.FarmingTask.TownCenterIsShortOfResources(account.InstanceNumber))
            {
                // Research costs the resources a Town Center upgrade is waiting for; the Town Center unlocks more
                logger.LogInfo($"[{account.AccountName}] Not researching while the Town Center waits for resources");
                return new TaskExecutionDetails(true, message: "Saving resources for the Town Center");
            }

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

            foreach (var y in TreeRows)
            foreach (var x in TreeColumns)
            {
                await ClickAsync(account.InstanceNumber, logger, new Point(x, y));
                await Task.Delay(1500, cancellationToken);
                var dialog = await TakeScreenshotAsync(account.InstanceNumber, logger);
                var research = dialog == null ? null : FindResearchButton(dialog);
                if (research is Point button)
                {
                    logger.LogInfo($"[{account.AccountName}] 🔬 Starting {researchType} research (item at {x},{y})");
                    await ClickAsync(account.InstanceNumber, logger, button);
                    await Task.Delay(2000, cancellationToken);
                    AutoTechnologySettings.AdvanceResearchType(account.InstanceNumber);
                    _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(5); // short researches finish fast
                    await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                    return new TaskExecutionDetails(true, message: $"Started {researchType} research");
                }
                if (dialog != null && HasDialogOpen(dialog))
                {
                    await PressBackAsync(account.InstanceNumber, logger); // close a locked or maxed item
                    await Task.Delay(1000, cancellationToken);
                }
            }

            logger.LogInfo($"[{account.AccountName}] No {researchType} research available right now");
            AutoTechnologySettings.AdvanceResearchType(account.InstanceNumber); // try the other category next time
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: $"No {researchType} research available");
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

        public ResearchType NextResearchType(int instanceNumber) =>
            ResearchTypes is { Count: > 0 } types ? types[_nextIndex.GetOrAdd(instanceNumber, 0) % types.Count] : ResearchType;

        public static void AdvanceResearchType(int instanceNumber) =>
            _nextIndex.AddOrUpdate(instanceNumber, 1, (_, i) => i + 1);
    }
}