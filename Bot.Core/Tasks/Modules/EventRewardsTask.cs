using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Claims the free mission rewards of the Path of Growth event (e.g. "Reach Town Center Lv. 8", daily logins).
    /// Only taps green Claim buttons; blue Go buttons (which navigate elsewhere) are never touched.
    /// </summary>
    public class EventRewardsTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.EventRewards;
        public override string Name => "Event Rewards";
        protected override string GetImageFolderName() => "events";

        private static readonly TimeSpan Interval = TimeSpan.FromHours(3);
        private static readonly ConcurrentDictionary<int, DateTime> _lastRun = new();

        private static readonly Rectangle EventIconArea = new Rectangle(440, 90, 280, 560);   // event icons on the city's right side
        private static readonly Rectangle ClaimColumn = new Rectangle(480, 600, 240, 680);    // the missions' button column
        private static readonly Point DailyLoginTab = new Point(160, 650);
        private static readonly Point UpgradeTownCenterTab = new Point(360, 650);
        private static readonly Point BackArrow = new Point(40, 40);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            var locator = new LocatorService(logger, account, TaskType);
            await ClaimStorehouseRewardsAsync(account, logger, locator, cancellationToken);

            if (_lastRun.TryGetValue(account.InstanceNumber, out var last) && DateTime.UtcNow - last < Interval)
                return new TaskExecutionDetails(true, message: "Skipped: checked recently");

            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

            // The previous task may still be closing its page: look a few times
            bool iconFound = false;
            Rectangle iconRect = Rectangle.Empty;
            for (int look = 0; look < 3 && !iconFound; look++)
            {
                await Task.Delay(1500, cancellationToken);
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) continue;
                (iconFound, iconRect, _) = _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "path-of-growth.png"),
                    account.InstanceNumber, threshold: 0.9, searchArea: EventIconArea);
            }
            _lastRun[account.InstanceNumber] = DateTime.UtcNow;
            if (!iconFound)
            {
                logger.LogInfo($"[{account.AccountName}] Path of Growth event not on screen (ended?)");
                return new TaskExecutionDetails(true, message: "Path of Growth not available");
            }

            await ClickRandomInRectAsync(account.InstanceNumber, logger, iconRect);
            await Task.Delay(2000, cancellationToken);

            int claimed = await ClaimAllAsync(account, logger, cancellationToken);
            foreach (var tab in new[] { DailyLoginTab, UpgradeTownCenterTab })
            {
                await ClickAsync(account.InstanceNumber, logger, tab);
                await Task.Delay(1500, cancellationToken);
                claimed += await ClaimAllAsync(account, logger, cancellationToken);
            }

            logger.LogInfo($"[{account.AccountName}] 🎁 Claimed {claimed} Path of Growth reward(s)");
            await ClickAsync(account.InstanceNumber, logger, BackArrow);
            await Task.Delay(1500, cancellationToken);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: $"Claimed {claimed} event reward(s)");
        }

        private static readonly ConcurrentDictionary<int, DateTime> _nextStorehouseCheck = new();

        /// <summary>
        /// The Storehouse gives a Gourmet Feast (twice a day) and an online chest every few minutes. The side panel's
        /// Daily tab has the feast row: its button opens the feast (green Claim when ready) and centres the Storehouse,
        /// whose chest bubble is then tapped.
        /// </summary>
        private async Task ClaimStorehouseRewardsAsync(AccountSettings account, LogService logger, LocatorService locator, CancellationToken cancellationToken)
        {
            if (_nextStorehouseCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next) return;
            _nextStorehouseCheck[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(10);

            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            if (!await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken)) return;
            await ClickAsync(account.InstanceNumber, logger, new Point(370, 268)); // Daily tab
            await Task.Delay(1200, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(408, 378)); // Gourmet Feast row button
            await Task.Delay(2500, cancellationToken);

            var feast = await TakeScreenshotAsync(account.InstanceNumber, logger);
            var claim = new Rectangle(300, 935, 160, 30);
            // Without a feast ready the row button goes straight to the Storehouse: that spot is then green grass
            if (feast != null && HasDimmedBackdrop(feast) && IsLitGreen(feast, claim))
            {
                logger.LogInfo($"[{account.AccountName}] 🍗 Claiming the Gourmet Feast");
                var debugDir = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
                Directory.CreateDirectory(debugDir);
                await File.WriteAllBytesAsync(Path.Combine(debugDir, $"feast-claim-{account.InstanceNumber}-{DateTime.Now:HHmm}.png"), feast, cancellationToken);
                await ClickAsync(account.InstanceNumber, logger, new Point(380, 952));
                await Task.Delay(2500, cancellationToken);
            }
            else if (feast != null && HasDimmedBackdrop(feast))
            {
                await ClickAsync(account.InstanceNumber, logger, new Point(608, 182)); // close the feast without claiming
                await Task.Delay(1500, cancellationToken);
            }

            // The Storehouse is centred now; its chest bubble shows when the online chest is ready
            for (int chest = 0; chest < 3; chest++)
            {
                var city = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (city == null) break;
                if (chest == 0)
                {
                    var debugDir = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
                    Directory.CreateDirectory(debugDir);
                    await File.WriteAllBytesAsync(Path.Combine(debugDir, $"storehouse-view-{account.InstanceNumber}.png"), city, cancellationToken);
                }
                var (found, rect, _) = _templateMatcher.MatchTemplate(city, Path.Combine(ImageTemplateFolder, "storehouse-chest-bubble.png"),
                    account.InstanceNumber, threshold: 0.8); // the bubble bobs: 0.87-0.99; nothing else above 0.74
                if (!found)
                {
                    // At night or under the tutorial glove the template scores ~0.5. The centred Storehouse puts its
                    // bubble (online chest or feast drumstick) at one spot: look for the white bubble frame there
                    if (!HasBubbleFrame(city)) break;
                    rect = new Rectangle(343, 443, 30, 30);
                }
                logger.LogInfo($"[{account.AccountName}] 🎁 Collecting the Storehouse online chest");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, rect);
                await Task.Delay(2500, cancellationToken);
                await ClickAsync(account.InstanceNumber, logger, new Point(360, 1150)); // dismiss "Next Chest Ready In"
                await Task.Delay(1500, cancellationToken);
            }
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
        }

        private static bool HasBubbleFrame(byte[] screenshot)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return false;
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(325, 425, 70, 65)), hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var white = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(0, 0, 191), new OpenCvSharp.Scalar(180, 59, 255), white);
            return OpenCvSharp.Cv2.CountNonZero(white) > 70 * 65 / 5; // bubble ~40%, bare city under 4%
        }

        private static bool HasDimmedBackdrop(byte[] screenshot)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return false;
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(0, 0, 720, 60));
            return OpenCvSharp.Cv2.Mean(roi).Val2 < 85;
        }

        /// <summary>True for a green, claimable button; its grey version (mission not done) has G ≈ R.</summary>
        private static bool IsLitGreen(byte[] screenshot, Rectangle rect)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return false;
            var r = new OpenCvSharp.Rect(rect.X, rect.Y, rect.Width, rect.Height) & new OpenCvSharp.Rect(0, 0, img.Width, img.Height);
            using var roi = new OpenCvSharp.Mat(img, r);
            var mean = OpenCvSharp.Cv2.Mean(roi); // BGR
            return mean.Val1 - mean.Val2 > 40;
        }

        /// <summary>Taps the green Claim buttons one at a time; claimed missions move out of the way.</summary>
        private async Task<int> ClaimAllAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            int claimed = 0;
            for (int i = 0; i < 15; i++)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) break;
                var (found, rect, _) = _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "claim-button.png"),
                    account.InstanceNumber, threshold: 0.85, searchArea: ClaimColumn);
                if (!found || !IsLitGreen(screenshot, rect)) break; // grey Claim: not reached yet
                await ClickRandomInRectAsync(account.InstanceNumber, logger, rect);
                claimed++;
                await Task.Delay(2000, cancellationToken);
            }
            return claimed;
        }
    }
}
