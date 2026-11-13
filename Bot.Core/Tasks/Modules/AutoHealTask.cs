using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.LDPlayer;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    public class AutoHealTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoHeal;
        public override string Name => "Auto Heal";
        protected override string GetImageFolderName() => "autoheal";
        
        private new readonly UnifiedTemplateMatchingService _templateMatcher = new UnifiedTemplateMatchingService(new LogService());
        private readonly string _templateFolder;

        public AutoHealTask(LogService logger)
        {
            _templateFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "autoheal");
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            bool isReRun = false,
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[AutoHeal] Starting auto heal process for account {account.AccountName}");

                // Validate template directory exists
                if (!Directory.Exists(_templateFolder))
                {
                    return FailConfiguration("Step 1: Setup", $"Template directory not found: {_templateFolder}");
                }

                // The hospital is in the city
                NotifyProgress(userNotifications, "Going to the city", 10);
                var locator = new LocatorService(logger, account);
                if (!await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                {
                    return FailNavigation("Step 2: Navigating to the city", "Could not reach the city");
                }

                var adbController = await ADBMigrationHelper.GetConnectionAsync(account.InstanceNumber, logger, cancellationToken);
                if (adbController == null)
                {
                    return FailConnection("Step 3: Connecting to emulator", "Failed to establish ADB connection");
                }

                // The green + bubble floats over the hospital; below the top bar, which can clip it so a tap misses.
                // If the camera hides the hospital, drag the city down once (the hospital sits in the north part).
                var heal = Path.Combine(_templateFolder, "heal.png");
                var area = new Rectangle(0, 100, 720, 1000);
                (bool found, Rectangle rect, double conf) bubble = default;
                for (var look = 0; look < 2 && !bubble.found; look++)
                {
                    if (look == 1)
                    {
                        await ADBMigrationHelper.SwipeAsync(adbController, 400, 500, 400, 800, 800, logger, cancellationToken);
                        await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
                    }
                    var shot = await ADBMigrationHelper.TakeScreenshotAsync(adbController, logger, cancellationToken);
                    bubble = _templateMatcher.MatchTemplate(shot, heal, account.InstanceNumber, 0.85, searchArea: area);
                    // After the drag the city keeps sliding (03:54 Nov 12 tapped where the bubble had been): tap only once it stays put
                    var still = look == 0;
                    for (var settle = 0; look == 1 && bubble.found && settle < 3 && !still; settle++)
                    {
                        await Bot.Core.Utils.Pace.Delay(1000, cancellationToken);
                        shot = await ADBMigrationHelper.TakeScreenshotAsync(adbController, logger, cancellationToken);
                        var again = _templateMatcher.MatchTemplate(shot, heal, account.InstanceNumber, 0.85, searchArea: area);
                        var moved = !again.found || Math.Abs(again.matchRect.Y - bubble.rect.Y) + Math.Abs(again.matchRect.X - bubble.rect.X) > 4;
                        bubble = again;
                        still = !moved;
                    }
                    if (!still) bubble = default; // still sliding after 3 s: never tap a moving target
                }
                if (!bubble.found)
                {
                    logger.LogInfo("[AutoHeal] Heal icon not found, skipping healing process");
                    return TaskExecutionDetails.Succeeded("No healing needed");
                }
                var c = bubble.rect.GetCenter();
                logger.LogInfo($"[AutoHeal] Found heal icon at ({c.X}, {c.Y}) with confidence {bubble.conf:F3}");
                await ADBMigrationHelper.TapAsync(adbController, c.X, c.Y, logger, cancellationToken);
                await Bot.Core.Utils.Pace.Delay(2500, cancellationToken);

                // "Heal Injured" screen: tap the cyan Heal button (never Finish, which costs gems)
                // The screen can take a few seconds to open (09:35 Nov 11 read the city bar 2 s after the tap)
                string? title = null;
                using var ocr = new OCRService(logger);
                for (var look = 0; look < 5 && title?.Contains("Heal", StringComparison.OrdinalIgnoreCase) != true; look++)
                {
                    if (look > 0) await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
                    var page = await ADBMigrationHelper.TakeScreenshotAsync(adbController, logger, cancellationToken);
                    title = ocr.ExtractBrightTextFromScreenArea(page, new Rectangle(90, 15, 300, 50));
                }
                if (title?.Contains("Heal", StringComparison.OrdinalIgnoreCase) != true)
                {
                    logger.LogWarning($"[AutoHeal] Heal Injured screen did not open (title '{title}')");
                    await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                    return TaskExecutionDetails.Failed("Heal Injured screen did not open");
                }
                await ADBMigrationHelper.TapAsync(adbController, 571, 1207, logger, cancellationToken);
                await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

                logger.LogInfo("[AutoHeal] Successfully completed healing sequence");
                return TaskExecutionDetails.Succeeded("Healing completed successfully");
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[AutoHeal] Task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[AutoHeal] Error during healing process: {ex.Message}");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Unexpected error",
                    ex.Message,
                    recoveryNeeded: true);
            }
        }
    }
} 