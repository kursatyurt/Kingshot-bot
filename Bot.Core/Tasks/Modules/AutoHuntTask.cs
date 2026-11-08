using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Tasks;
using Bot.Core.Tasks.Modules.AutoHunt;
using Bot.Core.Tasks.Modules.AutoHunt.Services;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Refactored AutoHunt task using the new modular architecture
    /// </summary>
    public class AutoHuntTask : BaseTaskWithCommonPatterns
    {
        private readonly AutoHuntCoordinator _coordinator;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _nextRun = new();

        public override TaskType TaskType => TaskType.AutoHunt;
        public override string Name => "Auto Hunt";

        public AutoHuntTask()
        {
            // Wire up simplified service implementations
            // The actual logic is handled by the coordinator
            var targetDetectionService = new SimplifiedTargetDetectionService();
            var marchManagementService = new SimplifiedMarchManagementService();
            var staminaManagementService = new SimplifiedStaminaManagementService();
            var huntModeNavigationService = new SimplifiedHuntModeNavigationService();
            var visualDebugger = new SimplifiedAutoHuntVisualDebugger();

            // Create the coordinator with all dependencies
            _coordinator = new AutoHuntCoordinator(
                targetDetectionService,
                marchManagementService,
                staminaManagementService,
                huntModeNavigationService,
                visualDebugger);
        }

        protected override bool OnCooldown(AccountSettings account) => _nextRun.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            bool isReRun = false,
            IUserNotificationService? userNotifications = null)
        {
            // Intel missions refresh every few hours and beasts wait for a march to come home: a run every cycle
            // (~35 s) mostly found nothing new
            if (_nextRun.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next)
                return new TaskExecutionDetails(true, message: "Skipped: Intel checked recently");
            _nextRun[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(15);
            var result = await _coordinator.ExecuteAutoHuntAsync(
                account, 
                logger, 
                cancellationToken, 
                isReRun, 
                userNotifications);
            // Out of stamina (stamina items are never used): every attempt would only open Get More again
            if (result.Message?.Contains("Stamina depleted", StringComparison.OrdinalIgnoreCase) == true)
            {
                _nextRun[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(60);
                logger.LogInfo($"[{account.AccountName}] No stamina: next hunt in 60 min");
            }
            return result;
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        protected override string GetImageFolderName() => "autohunt";

    }
}