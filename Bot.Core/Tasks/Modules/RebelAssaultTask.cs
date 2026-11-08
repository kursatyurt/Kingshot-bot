using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using Bot.Core.ImageDetection;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Fights the Rebel Assault: the red rebel icon on the city's left opens "Rebel Assault - Imminent" with a Battle
    /// button; the defence then plays itself and ends on a result page with Town / Retry.
    /// A lost defence kills residents; heroes are put on the barricades (Dispatch All) by hand, once.
    /// </summary>
    public class RebelAssaultTask : BaseTaskWithCommonPatterns
    {
        protected override TimeSpan MinInterval => TimeSpan.FromMinutes(10);
        public override TaskType TaskType => TaskType.RebelAssault;
        public override string Name => "Rebel Assault";
        protected override string GetImageFolderName() => "rebelassault";

        private static readonly Rectangle IconArea = new Rectangle(0, 120, 260, 300);
        private static readonly Rectangle BattleArea = new Rectangle(150, 850, 420, 200);
        private static readonly Rectangle ResultButtonsArea = new Rectangle(0, 820, 720, 200);
        private static readonly Rectangle SkipArea = new Rectangle(500, 40, 220, 120);
        private static readonly TimeSpan MaxBattle = TimeSpan.FromMinutes(15);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _assaultAt = new();

        // A countdown under the icon means the wave is still on its way: its dialog has no Battle button until then
        protected override bool OnCooldown(AccountSettings account) => _assaultAt.TryGetValue(account.InstanceNumber, out var at) && DateTime.UtcNow < at;

        /// <summary>The red "00:44:59" under the rebel icon (red digits only; the art around them drops out).</summary>
        public static TimeSpan? ReadCountdown(byte[] screenshot, OCRService ocr)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(10, 276, 130, 32));
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(roi, new OpenCvSharp.Scalar(0, 0, 200), new OpenCvSharp.Scalar(90, 90, 255), mask);
            using var inverted = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.BitwiseNot(mask, inverted);
            using var big = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.Resize(inverted, big, new OpenCvSharp.Size(0, 0), 4, 4, OpenCvSharp.InterpolationFlags.Cubic);
            using var bgr = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(big, bgr, OpenCvSharp.ColorConversionCodes.GRAY2BGR);
            return EventRewardsTask.ParseTimer(ocr.ExtractTextFromMat(bgr));
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

            var city = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (city == null) return TaskExecutionDetails.Failed("No screenshot");
            var (iconShown, iconRect, _) = _templateMatcher.MatchTemplate(city, Path.Combine(ImageTemplateFolder, "rebel-icon.png"),
                account.InstanceNumber, threshold: 0.8, searchArea: IconArea);
            if (!iconShown) return new TaskExecutionDetails(true, message: "No Rebel Assault");
            if (ReadCountdown(city, new OCRService(logger)) is { } wait && wait > TimeSpan.FromSeconds(30))
            {
                _assaultAt[account.InstanceNumber] = DateTime.UtcNow + wait;
                logger.LogInfo($"[{account.AccountName}] ⚔️ Rebel Assault arrives in {wait}; defending then");
                return new TaskExecutionDetails(true, message: $"Rebel Assault in {wait}");
            }

            logger.LogInfo($"[{account.AccountName}] ⚔️ Rebel Assault: defending the city");
            await ClickRandomInRectAsync(account.InstanceNumber, logger, iconRect);
            await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);
            if (!await FindAndClickImageAsync("battle-button.png", account.InstanceNumber, logger, threshold: 0.85, searchArea: BattleArea))
            {
                await PressBackAsync(account.InstanceNumber, logger);
                return TaskExecutionDetails.Failed("Battle button not found");
            }

            // The defence plays itself in waves; wait for the result page's Town button
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < MaxBattle)
            {
                await Bot.Core.Utils.Pace.Delay(10000, cancellationToken);
                var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (shot == null) continue;
                var (done, townRect, _) = _templateMatcher.MatchTemplate(shot, Path.Combine(ImageTemplateFolder, "town-button.png"),
                    account.InstanceNumber, threshold: 0.85, searchArea: ResultButtonsArea);
                if (!done)
                {
                    // Story dialogs can cover the Town button; Skip clears them (only when shown: the battle has wave icons there)
                    await FindAndClickImageAsync("skip-button.png", account.InstanceNumber, logger, threshold: 0.85, searchArea: SkipArea);
                    continue;
                }
                logger.LogInfo($"[{account.AccountName}] Rebel Assault finished after {(int)(DateTime.UtcNow - start).TotalSeconds}s");
                if (new OCRService(logger).ExtractTextFromScreenArea(shot, new Rectangle(0, 100, 720, 500))?.ToLower().Contains("fail") == true)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Rebel Assault LOST - residents may have died; strengthen the barricade heroes");
                    userNotifications?.ShowStatus($"Rebel Assault lost on {account.AccountName}", NotificationType.Error);
                }
                await ClickRandomInRectAsync(account.InstanceNumber, logger, townRect);
                await Bot.Core.Utils.Pace.Delay(4000, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                return new TaskExecutionDetails(true, message: "Rebel Assault fought");
            }
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return TaskExecutionDetails.Failed("Rebel Assault result not seen");
        }
    }
}
