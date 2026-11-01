using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
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
    /// A lost defence kills residents, so heroes go on the barricades (Dispatch All) first.
    /// </summary>
    public class RebelAssaultTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.RebelAssault;
        public override string Name => "Rebel Assault";
        protected override string GetImageFolderName() => "rebelassault";

        private static readonly Rectangle IconArea = new Rectangle(0, 120, 260, 300);
        private static readonly Rectangle BattleArea = new Rectangle(150, 850, 420, 200);
        private static readonly Rectangle ResultButtonsArea = new Rectangle(0, 820, 720, 200);
        private static readonly Point SkipDialog = new Point(630, 95);
        private static readonly TimeSpan MaxBattle = TimeSpan.FromMinutes(15);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

            var city = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (city == null) return TaskExecutionDetails.Failed("No screenshot");
            var (iconShown, iconRect, _) = _templateMatcher.MatchTemplate(city, Path.Combine(ImageTemplateFolder, "rebel-icon.png"),
                account.InstanceNumber, threshold: 0.8, searchArea: IconArea);
            if (!iconShown) return new TaskExecutionDetails(true, message: "No Rebel Assault");

            if (!await DispatchHeroesToBarricadesAsync(account, logger, cancellationToken))
                return new TaskExecutionDetails(true, message: "Rebel Assault waiting: barricade dispatch not available");

            logger.LogInfo($"[{account.AccountName}] ⚔️ Rebel Assault: defending the city");
            await ClickRandomInRectAsync(account.InstanceNumber, logger, iconRect);
            await Task.Delay(2500, cancellationToken);
            if (!await FindAndClickImageAsync("battle-button.png", account.InstanceNumber, logger, threshold: 0.85, searchArea: BattleArea))
            {
                await PressBackAsync(account.InstanceNumber, logger);
                return TaskExecutionDetails.Failed("Battle button not found");
            }

            // The defence plays itself in waves; wait for the result page's Town button
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < MaxBattle)
            {
                await Task.Delay(10000, cancellationToken);
                var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (shot == null) continue;
                var (done, townRect, _) = _templateMatcher.MatchTemplate(shot, Path.Combine(ImageTemplateFolder, "town-button.png"),
                    account.InstanceNumber, threshold: 0.85, searchArea: ResultButtonsArea);
                if (!done)
                {
                    // Story dialogs can cover the Town button; Skip clears them
                    await ClickAsync(account.InstanceNumber, logger, SkipDialog);
                    continue;
                }
                logger.LogInfo($"[{account.AccountName}] Rebel Assault finished after {(int)(DateTime.UtcNow - start).TotalSeconds}s");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, townRect);
                await Task.Delay(4000, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                return new TaskExecutionDetails(true, message: "Rebel Assault fought");
            }
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return TaskExecutionDetails.Failed("Rebel Assault result not seen");
        }

        /// <summary>Puts heroes on the barricades (Dispatch All). Not mapped yet: how to open a Barricade.</summary>
        private Task<bool> DispatchHeroesToBarricadesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            // ponytail: returns false until the Barricade page's location is known, so the task never fights undefended
            logger.LogInfo($"[{account.AccountName}] Rebel Assault spotted; barricade dispatch not mapped yet, not fighting");
            return Task.FromResult(false);
        }
    }
}
