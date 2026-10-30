using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    public class ConquestCollectTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ConquestCollect;
        protected override string GetImageFolderName() => "conquest";
        public override string Name => "Conquest Collection";

        // Static dictionary to track last collection times per account
        private static readonly ConcurrentDictionary<string, DateTime> _lastCollectionTimes = new();
        private static readonly string TimeStorageFile = Path.Combine(
            AppContext.BaseDirectory, "data", "conquest_collection_times.json"
        );

        // UI Element coordinates for navigation sequence

        // Default collection interval settings (used as fallback)
        private static readonly TimeSpan DefaultCollectionInterval = TimeSpan.FromHours(4);
        private static readonly Random _random = new Random();

        public ConquestCollectTask()
        {
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
                // Get unique account ID for accurate timer tracking
                string accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                if (string.IsNullOrEmpty(accountId))
                {
                    logger.LogWarning($"[{account.AccountName}] No cached account ID found, using fallback key");
                    accountId = $"Account_{account.AccountName}_{account.InstanceNumber}";
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Using cached account ID: '{accountId}'");
                }

                // Check if enough time has passed since last collection
                if (!ShouldCollect(accountId, account))
                {
                    var nextCollection = GetNextCollectionTime(accountId, account);
                    logger.LogInfo($"[{account.AccountName}] Too soon to collect conquest rewards. Next collection at: {nextCollection}. Wait time: {account.ConquestCollectSettings.WaitHours} hours");
                    return new TaskExecutionDetails(true, nextCollection, "Skipped - Too soon to collect");
                }

                // Rebel Conquest (the Conquest tab): claim the idle income, then push stages until a defeat
                var locator = new LocatorService(logger, account);
                if (!await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                    return TaskExecutionDetails.Failed("Could not reach the city view");
                await ClickAsync(account.InstanceNumber, logger, ConquestTab);
                await Task.Delay(3000, cancellationToken);
                if (!await OnConquestScreenAsync(account, logger))
                {
                    await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                    return TaskExecutionDetails.Failed("Conquest screen did not open");
                }

                logger.LogInfo($"[{account.AccountName}] 🎁 Claiming Rebel Conquest idle income");
                await ClickAsync(account.InstanceNumber, logger, IdleChestClaim);
                await Task.Delay(2000, cancellationToken);
                await ClickAsync(account.InstanceNumber, logger, IdleIncomeClaimButton); // "Idle Income" dialog's Claim
                await Task.Delay(2000, cancellationToken);
                await ClickAsync(account.InstanceNumber, logger, new Point(360, 200)); // "Tap anywhere to exit" the rewards
                await Task.Delay(1500, cancellationToken);

                int cleared = await ConquerStagesAsync(account, logger, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] ⚔️ Rebel Conquest: {cleared} stage(s) cleared this run");
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

                // Update collection time
                UpdateCollectionTime(accountId);

                logger.LogInfo($"[{account.AccountName}] ✅ Conquest collection completed successfully");
                return TaskExecutionDetails.Succeeded();
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during conquest collection: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during conquest collection: {ex.Message}");
            }
        }

        private static readonly Point ConquestTab = new(70, 1225);           // bottom bar, far left
        private static readonly Point IdleChestClaim = new(617, 922);        // the glowing chest's Claim on the road
        private static readonly Point IdleIncomeClaimButton = new(360, 924);
        private static readonly Point ConquerButton = new(360, 1177);
        private static readonly Point QuickDeployButton = new(190, 1200);
        private static readonly Point FightButton = new(528, 1200);
        private static readonly Rectangle ResultTownButton = new(60, 885, 260, 55);     // Victory/defeat screen: Town | Continue
        private static readonly Rectangle ResultContinueButton = new(398, 885, 260, 55);
        private const int MaxStagesPerRun = 10;

        private async Task<bool> OnConquestScreenAsync(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            return screenshot != null && _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "conquer-button.png"),
                account.InstanceNumber, threshold: 0.9, searchArea: new Rectangle(150, 1100, 420, 160)).found;
        }

        /// <summary>
        /// Conquer → Quick Deploy (best available heroes) → Fight; the battle plays itself. Victory → Continue opens the
        /// next stage's squad; anything else (defeat) → Town, and the run stops. Battles cost nothing, so a defeat only
        /// means waiting for stronger heroes and troops until the next run.
        /// </summary>
        private async Task<int> ConquerStagesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            if (!await OnConquestScreenAsync(account, logger)) return 0;
            await ClickAsync(account.InstanceNumber, logger, ConquerButton);
            await Task.Delay(3000, cancellationToken);
            int cleared = 0;
            while (cleared < MaxStagesPerRun && !cancellationToken.IsCancellationRequested)
            {
                await ClickAsync(account.InstanceNumber, logger, QuickDeployButton);
                await Task.Delay(1500, cancellationToken);
                await ClickAsync(account.InstanceNumber, logger, FightButton);

                // Battles last ~20-60 s; the result screen has Town and Continue side by side
                byte[]? result = null;
                for (int wait = 0; wait < 30 && result == null; wait++)
                {
                    await Task.Delay(3000, cancellationToken);
                    var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (shot != null && IsCyanButton(shot, ResultTownButton)) result = shot;
                }
                if (result == null)
                {
                    logger.LogInfo($"[{account.AccountName}] No Conquest battle result after 90 s; stopping");
                    return cleared;
                }
                bool victory = _templateMatcher.MatchTemplate(result, Path.Combine(ImageTemplateFolder, "victory.png"),
                    account.InstanceNumber, threshold: 0.85, searchArea: new Rectangle(150, 280, 420, 140)).found;
                if (!victory || !IsCyanButton(result, ResultContinueButton))
                {
                    logger.LogInfo($"[{account.AccountName}] Conquest stage lost; trying again next run");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, ResultTownButton);
                    await Task.Delay(3000, cancellationToken);
                    return cleared;
                }
                cleared++;
                logger.LogInfo($"[{account.AccountName}] 🏆 Conquest stage cleared ({cleared} this run)");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, ResultContinueButton); // next stage's squad
                await Task.Delay(3000, cancellationToken);
            }
            return cleared;
        }

        private static bool IsCyanButton(byte[] screenshot, Rectangle rect)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            if (img.Empty()) return false;
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(rect.X, rect.Y, rect.Width, rect.Height));
            var mean = OpenCvSharp.Cv2.Mean(roi); // BGR
            return mean.Val0 > 170 && mean.Val1 > 160 && mean.Val2 < 120;
        }

        private bool ShouldCollect(string accountId, AccountSettings account)
        {
            LoadCollectionTimes();
            if (!_lastCollectionTimes.TryGetValue(accountId, out DateTime lastCollection))
            {
                return true; // First time collection
            }

            var nextCollection = GetNextCollectionTime(accountId, account);
            return DateTime.UtcNow >= nextCollection;
        }

        private DateTime GetNextCollectionTime(string accountId, AccountSettings account)
        {
            if (!_lastCollectionTimes.TryGetValue(accountId, out DateTime lastCollection))
            {
                return DateTime.UtcNow; // First time collection
            }

            // Use the configured wait hours from the account settings
            var waitHours = account.ConquestCollectSettings?.WaitHours ?? (int)DefaultCollectionInterval.TotalHours;

            // Add a small random variance (±10%) to prevent predictable timing
            var variance = _random.NextDouble() * 0.2 - 0.1; // -10% to +10%
            var actualWaitHours = waitHours * (1 + variance);

            return lastCollection.AddHours(actualWaitHours);
        }

        private void UpdateCollectionTime(string accountId)
        {
            _lastCollectionTimes[accountId] = DateTime.UtcNow;
            SaveCollectionTimes();
        }

        private void LoadCollectionTimes()
        {
            try
            {
                if (File.Exists(TimeStorageFile))
                {
                    var json = File.ReadAllText(TimeStorageFile);
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var times = JsonSerializer.Deserialize<ConcurrentDictionary<string, DateTime>>(json, options);
                    if (times != null)
                    {
                        // Validate and clean up old entries
                        var now = DateTime.UtcNow;
                        foreach (var kvp in times)
                        {
                            // Only keep entries from the last 24 hours
                            if (now.Subtract(kvp.Value).TotalHours <= 24)
                            {
                                _lastCollectionTimes[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Can't use logger here as it's not available in this context anymore
                Console.WriteLine($"[ConquestCollectTask] Error loading collection times: {ex.Message}");
                _lastCollectionTimes.Clear();
            }
        }

        private void SaveCollectionTimes()
        {
            try
            {
                var directory = Path.GetDirectoryName(TimeStorageFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Validate times before saving
                var now = DateTime.UtcNow;
                var validTimes = _lastCollectionTimes
                    .Where(kvp => now.Subtract(kvp.Value).TotalHours <= 24)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(validTimes, options);
                File.WriteAllText(TimeStorageFile, json);
            }
            catch (Exception ex)
            {
                // Can't use logger here as it's not available in this context anymore
                Console.WriteLine($"[ConquestCollectTask] Error saving collection times: {ex.Message}");
            }
        }

        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            // Load existing collection times during initialization
            LoadCollectionTimes();

            // Create README if it doesn't exist
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                await File.WriteAllTextAsync(readmePath,
                    "Conquest Collection Task - Image Templates\n\n" +
                    "Required images:\n\n" +
                    "1. conquest-icon.png - The conquest menu icon\n" +
                    "2. collect-button.png - The collect rewards button\n" +
                    "3. close-button.png - The close interface button\n\n" +
                    "Image Requirements:\n" +
                    "- Must be clear, high-contrast screenshots\n" +
                    "- Should include some surrounding context\n" +
                    "- Minimum size: 50x50 pixels\n" +
                    "- Maximum size: 200x200 pixels\n" +
                    "- Format: PNG with transparency where applicable\n\n" +
                    "Collection Timing:\n" +
                    "- Minimum interval: 3 hours\n" +
                    "- Maximum interval: 6 hours\n" +
                    "- Random interval chosen between min and max\n" +
                    "- Times stored per instance in conquest_collection_times.json",
                    cancellationToken);
            }
        }
    }
} 