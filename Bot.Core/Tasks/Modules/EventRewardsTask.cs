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
            if (_lastRun.TryGetValue(account.InstanceNumber, out var last) && DateTime.UtcNow - last < Interval)
                return new TaskExecutionDetails(true, message: "Skipped: checked recently");

            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return TaskExecutionDetails.Failed("No screenshot");
            var (iconFound, iconRect, _) = _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "path-of-growth.png"),
                account.InstanceNumber, threshold: 0.9, searchArea: EventIconArea);
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
                if (!found) break;
                await ClickRandomInRectAsync(account.InstanceNumber, logger, rect);
                claimed++;
                await Task.Delay(2000, cancellationToken);
            }
            return claimed;
        }
    }
}
