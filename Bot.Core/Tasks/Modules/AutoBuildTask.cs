using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Upgrades the Town Center. Kingshot no longer has the left-edge side panel the old flow used,
    /// so the task opens the Town Center directly: it sits in the middle of the city view after the game starts.
    /// When requirements are missing (another building's level, resources), it logs them and backs out
    /// without spending anything.
    /// </summary>
    public class AutoBuildTask : BaseTaskWithCommonPatterns
    {
        private const int MIN_CLICK_DELAY_MS = 500; // Minimum delay between clicks
        private const int MAX_CLICK_DELAY_MS = 1000; // Maximum delay between clicks
        private static readonly Random _random = new Random();

        public override TaskType TaskType => TaskType.AutoBuild;
        public override string Name => "Auto Build";

        protected override string GetImageFolderName() => "autobuild";

        // ponytail: fixed tap point, valid while the camera is on the Town Center (true after a game start,
        // so the task restarts the game when it isn't). A building template match would avoid the restart.
        private static readonly Point TownCenterTapPoint = new Point(340, 610);
        private static readonly Point CentredBuildingPoint = new Point(368, 536); // where Go puts the building
        private static readonly Rectangle CentredBuildingCountdownRect = new Rectangle(330, 512, 110, 30);
        private static readonly Rectangle TownCenterTitleArea = new Rectangle(15, 600, 320, 50); // "Town Center Lv. 6"
        private static readonly Point TownCenterUpgradeButton = new Point(610, 624);
        private static readonly Point RequirementsUpgradeButton = new Point(528, 1215);
        private readonly Rectangle redXSearchArea = new Rectangle(449, 589, 211, 543); // ❌ column of the requirements list
        private readonly Rectangle goButtonSearchArea = new Rectangle(527, 588, 159, 547); // Go buttons next to the requirement rows
        private static readonly Point BuildingRingUpgradeButton = new Point(360, 750); // ring around a building after Go centres it
        private static readonly Rectangle DialogUpgradeButtonArea = new Rectangle(360, 700, 360, 560); // bottom right of a building's upgrade dialog (lower with more rows)
        private static readonly Rectangle ConstructionStatusRect = new Rectangle(40, 722, 200, 30); // "Upgrading" bar on a building under construction
        private static readonly Rectangle ConstructionStatusWordRect = new Rectangle(40, 726, 160, 26);
        private static readonly Rectangle ConstructionTimerRect = new Rectangle(515, 722, 120, 30); // its "00:29:41" countdown
        private static readonly Point GetMoreCloseButton = new Point(668, 144); // X of the "Get More" resource dialog

        // Helper method for random delays
        private async Task RandomDelay(CancellationToken cancellationToken)
        {
            await WaitIfPausedAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            int delay = _random.Next(MIN_CLICK_DELAY_MS, MAX_CLICK_DELAY_MS + 1);
            await Task.Delay(delay, cancellationToken);
        }

        // Per instance: no Town Center check before this time. Cycles can come every few minutes (they follow the
        // march timers), but a started upgrade or a missing requirement does not change that fast.
        private static readonly ConcurrentDictionary<int, DateTime> _nextBuildCheck = new();

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (_nextBuildCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next)
            {
                logger.LogInfo($"[{account.AccountName}] Auto Build checked recently; next check after {next.ToLocalTime():HH:mm}");
                await BuildNewBuildingsAsync(account, logger, cancellationToken);
                return new TaskExecutionDetails(true, message: "Skipped: checked recently");
            }

            var result = await RunBuildAsync(account, logger, cancellationToken, userNotifications);
            // After the Town Center work: the side panel's jump moves the camera off the Town Center
            await BuildNewBuildingsAsync(account, logger, cancellationToken);
            var cooldown = result.Success ? SuccessCooldown(account.InstanceNumber)
                : result.FailureCategory == FailureCategory.GameState ? TimeSpan.FromMinutes(15) // requirements missing
                : TimeSpan.Zero; // navigation/detection problems: just retry next cycle
            if (cooldown > TimeSpan.Zero)
                _nextBuildCheck.AddOrUpdate(account.InstanceNumber, DateTime.UtcNow + cooldown, (_, old) => old > DateTime.UtcNow + cooldown ? old : DateTime.UtcNow + cooldown);
            return result;
        }

        private static readonly ConcurrentDictionary<int, DateTime> _nextNewBuildingCheck = new();
        private static readonly ConcurrentDictionary<int, DateTime> _builderFreeAt = new();

        /// <summary>
        /// The idle builder's suggestion is centred: press it, and if its ring offers Upgrade and every requirement is met
        /// (resources topped up from the backpack), upgrade it. Never the Storehouse (the user asked not to upgrade it).
        /// </summary>
        private async Task UpgradeSuggestedBuildingAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            if (FarmingTask.TownCenterIsShortOfResources(account.InstanceNumber))
            {
                logger.LogInfo($"[{account.AccountName}] Saving resources for the Town Center; no other upgrade now");
                return;
            }
            await SwipeAsync(account.InstanceNumber, logger, CentredBuildingPoint, new Point(CentredBuildingPoint.X + 1, CentredBuildingPoint.Y + 1), 150);
            await Task.Delay(1500, cancellationToken);
            if (!await TapRingButtonAsync(account.InstanceNumber, logger, Path.Combine(ImageTemplateFolder, "ring-upgrade.png"), BuildingRingUpgradeButton))
            {
                logger.LogInfo($"[{account.AccountName}] The suggested building has no Upgrade on its ring; leaving it");
                return;
            }
            var missing = await ReadRequirementsAsync(account, logger, cancellationToken);
            if (missing == null) return;

            var dialog = await TakeScreenshotAsync(account.InstanceNumber, logger);
            using (var words = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f }))
            {
                var title = dialog == null ? "" : words.ExtractTextFromScreenArea(dialog, new Rectangle(120, 200, 480, 70));
                if (title.Contains("Storehouse", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInfo($"[{account.AccountName}] Not upgrading the Storehouse");
                    return;
                }
            }

            if (missing.Count > 0 && await UseBackpackResourcesAsync(account, logger, cancellationToken))
                missing = await ReadRequirementsAsync(account, logger, cancellationToken) ?? missing;
            if (missing.Count > 0)
            {
                logger.LogInfo($"[{account.AccountName}] The suggested upgrade needs: {string.Join("; ", missing)}");
                return;
            }
            dialog = await TakeScreenshotAsync(account.InstanceNumber, logger);
            var (found, rect, _) = dialog == null ? (false, Rectangle.Empty, 0.0)
                : _templateMatcher.MatchTemplate(dialog, Path.Combine(ImageTemplateFolder, "dialog-upgrade-button.png"),
                    account.InstanceNumber, threshold: 0.88, searchArea: DialogUpgradeButtonArea);
            if (!found) return;
            logger.LogInfo($"[{account.AccountName}] 🔨 Builder idle: upgrading the suggested building");
            await ClickRandomInRectAsync(account.InstanceNumber, logger, rect);
            await RandomDelay(cancellationToken);
            await RequestAllianceHelpAsync(account, logger, cancellationToken);
        }

        /// <summary>10 minutes, or sooner when the builder is known to be free sooner (short low-level upgrades).</summary>
        private static TimeSpan SuccessCooldown(int instance)
        {
            if (!_builderFreeAt.TryRemove(instance, out var freeAt)) return TimeSpan.FromMinutes(10);
            var wait = freeAt - DateTime.UtcNow + TimeSpan.FromSeconds(20);
            return wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : wait;
        }

        /// <summary>
        /// Builds buildings that have unlocked (Storehouse, Barricades, Monument, ...). With the builder idle, the
        /// city side panel's "Building Queue 1" arrow opens the game's next suggested build; only its Build page
        /// (a new building, finished in seconds) is tapped, never an upgrade. Repeats while new buildings come up.
        /// </summary>
        private async Task BuildNewBuildingsAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            if (_nextNewBuildingCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next) return;
            _nextNewBuildingCheck[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(30);

            var locator = new LocatorService(logger, account);
            var buildButton = Path.Combine(ImageTemplateFolder, "build-button.png");
            using var ocr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 3.0f, PageSegMode = Tesseract.PageSegMode.SingleLine });
            for (int built = 0; built < 6;)
            {
                if (!await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken)) return;
                await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken); // side panel tab
                var panel = await TakeScreenshotAsync(account.InstanceNumber, logger);
                var queueStatus = panel == null ? "" : ocr.ExtractBrightTextFromScreenArea(panel, new Rectangle(150, 376, 160, 30));
                if (!queueStatus.Contains("Idle", StringComparison.OrdinalIgnoreCase))
                {
                    await ClickAsync(account.InstanceNumber, logger, new Point(464, 544)); // close the panel
                    if (built == 0) logger.LogInfo($"[{account.AccountName}] Builder busy ('{queueStatus}'); no new building now");
                    return;
                }

                await ClickAsync(account.InstanceNumber, logger, new Point(405, 370)); // Building Queue 1 → suggested build
                var (found, rect) = (false, Rectangle.Empty);
                for (int look = 0; look < 3 && !found; look++) // a tutorial glove can cover the Build button for a moment
                {
                    await Task.Delay(look == 0 ? 1800 : 1000, cancellationToken);
                    var page = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (page != null)
                        (found, rect, _) = _templateMatcher.MatchTemplate(page, buildButton, account.InstanceNumber, threshold: 0.85, searchArea: new Rectangle(150, 1100, 450, 160));
                }
                if (!found)
                {
                    // An upgrade (or a camp to collect): keep the builder busy on it as long as nothing else waits
                    await UpgradeSuggestedBuildingAsync(account, logger, cancellationToken);
                    await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                    return;
                }
                logger.LogInfo($"[{account.AccountName}] 🏗️ Building a newly unlocked building");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, rect);
                built++;
                await Task.Delay(2500, cancellationToken);
            }
        }

        private async Task<TaskExecutionDetails> RunBuildAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications)
        {
            try
            {
                // Ensure in base view
                NotifyProgress(userNotifications, "Navigating to base", 10);
                var locator = new LocatorService(logger, account);
                if (!await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                {
                    return FailNavigation("Step 1: Navigating to base view", "Could not reach base view");
                }

                // Open the Town Center page. Other tasks (e.g. Farming's trip to the world map) move the city
                // camera, and only a fresh game start puts it back on the Town Center, so restart once if needed.
                NotifyProgress(userNotifications, "Opening Town Center", 30);
                var (openedFirst, busyFor) = await OpenTownCenterAsync(account, logger, locator, cancellationToken);
                if (busyFor != null) return TownCenterBusy(account, logger, busyFor.Value);
                if (!openedFirst)
                {
                    logger.LogInfo($"[{account.AccountName}] Town Center not in the middle of the city view; restarting the game to re-centre it");
                    NotifyProgress(userNotifications, "Restarting game to re-centre the city", 40);
                    await RestartGameAsync(account, logger, cancellationToken);
                    var opened = false;
                    // The camera sweeps into town for a few seconds after launch, so give it time and a few tries
                    for (int attempt = 1; attempt <= 3 && !opened; attempt++)
                    {
                        await Task.Delay(3000, cancellationToken);
                        if (!await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken, maxRetries: 5)) continue;
                        (opened, busyFor) = await OpenTownCenterAsync(account, logger, locator, cancellationToken);
                        if (busyFor != null) return TownCenterBusy(account, logger, busyFor.Value);
                    }
                    if (!opened)
                    {
                        return TaskExecutionDetails.FailedWith(
                            FailureCategory.Navigation,
                            "Step 2: Opening Town Center",
                            "Could not open the Town Center, even after restarting the game");
                    }
                }

                // Open its upgrade requirements
                NotifyProgress(userNotifications, "Checking requirements", 60);
                await ClickAsync(account.InstanceNumber, logger, TownCenterUpgradeButton);
                var missing = await ReadRequirementsAsync(account, logger, cancellationToken);
                if (missing == null)
                {
                    await CloseTownCenterPagesAsync(account, logger, cancellationToken);
                    return FailDetection("Step 3: Checking requirements", "requirements list (no ❌ or ✓ marks)");
                }
                if (missing.Count > 0)
                {
                    logger.LogInfo($"[{account.AccountName}] Town Center upgrade needs: {string.Join("; ", missing)}");
                    TroopTrainingTask.HoldCampsForUpgrade(account.InstanceNumber, missing);

                    // A missing building has a Go button: upgrade that building instead (one upgrade per cycle)
                    NotifyProgress(userNotifications, "Upgrading a required building", 75);
                    var buildingResult = await UpgradeRequiredBuildingAsync(account, logger, locator, cancellationToken);
                    if (buildingResult != null) return buildingResult;

                    // Only resources are missing: fill them from backpack items. Done only now, when nothing else
                    // blocks the upgrade, so secured (plunder-proof) items aren't opened while the upgrade still waits.
                    NotifyProgress(userNotifications, "Using backpack resources", 80);
                    if (await UseBackpackResourcesAsync(account, logger, cancellationToken))
                    {
                        missing = await ReadRequirementsAsync(account, logger, cancellationToken) ?? missing;
                    }
                }
                if (missing.Count > 0)
                {
                    await CloseTownCenterPagesAsync(account, logger, cancellationToken);
                    return TaskExecutionDetails.FailedWith(
                        FailureCategory.GameState,
                        "Step 3: Checking requirements",
                        $"Town Center upgrade needs: {string.Join("; ", missing)}",
                        customHint: "Not enough resources in the backpack either; gathering continues and the task retries every cycle");
                }

                // All requirements met
                NotifyProgress(userNotifications, "Starting upgrade", 90);
                logger.LogInfo($"[{account.AccountName}] All Town Center requirements met, tapping Upgrade");
                TroopTrainingTask.HoldCampsForUpgrade(account.InstanceNumber, Array.Empty<string>());
                FarmingTask.SetPriorityResource(account.InstanceNumber, null);
                await ClickAsync(account.InstanceNumber, logger, RequirementsUpgradeButton);
                await RandomDelay(cancellationToken);
                await RequestAllianceHelpAsync(account, logger, cancellationToken);
                // The game may already be back in the city after starting the upgrade, where a blind back key
                // would open the exit dialog, so let the locator return to the city view.
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

                account.AutoBuildSettings.LastBuildTime = DateTime.UtcNow;
                return TaskExecutionDetails.Succeeded();
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] AutoBuild task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in AutoBuild: {ex.Message}");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Unexpected error",
                    ex.Message,
                    recoveryNeeded: true);
            }
        }

        /// <summary>
        /// Taps the middle of the city view and checks that the Town Center page opened; closes whatever opened otherwise.
        /// </summary>
        private async Task<(bool opened, TimeSpan? busyFor)> OpenTownCenterAsync(AccountSettings account, LogService logger, LocatorService locator, CancellationToken cancellationToken)
        {
            // The first touch often only moves the camera onto the Town Center; a later press opens it
            byte[]? screenshot = null;
            for (int press = 1; press <= 3; press++)
            {
                await SwipeAsync(account.InstanceNumber, logger, TownCenterTapPoint, new Point(TownCenterTapPoint.X + 1, TownCenterTapPoint.Y + 1), 150);
                await RandomDelay(cancellationToken);
                screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null && IsTownCenterPage(screenshot, logger)) return (true, null);
                if (screenshot != null && ReadConstructionTimeLeft(screenshot, logger) != null) break; // construction page
            }

            // While it upgrades, the Town Center opens a construction page ("Upgrading 00:29:41", Finish for gems,
            // Construction Speedup). Never tap those; back out and wait for the upgrade instead.
            var busyFor = screenshot == null ? null : ReadConstructionTimeLeft(screenshot, logger);
            if (busyFor != null)
            {
                // Its handshake bubble shows above it until help was asked for
                await RequestAllianceHelpAsync(account, logger, cancellationToken);
                await PressBackAsync(account.InstanceNumber, logger);
                await RandomDelay(cancellationToken);
                return (false, busyFor);
            }

            if (screenshot != null)
            {
                // Keep what the tap opened instead, so the bot can be taught it
                var debugDir = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
                Directory.CreateDirectory(debugDir);
                await File.WriteAllBytesAsync(Path.Combine(debugDir, $"autobuild-not-town-center-{account.InstanceNumber}.png"), screenshot, cancellationToken);
            }

            // Not the Town Center: return to the city. A blind back key would open the game's exit
            // dialog when the tap opened nothing; the locator only acts on what is actually on screen.
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return (false, null);
        }

        /// <summary>
        /// Time left on a building's construction page ("Upgrading" bar with a countdown), or null on any other screen.
        /// </summary>
        private static TimeSpan? ReadConstructionTimeLeft(byte[] screenshot, LogService logger)
        {
            using var words = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f });
            // The progress fill behind the label changes how it reads, so try the bar and a tighter crop of the word
            if (!words.ExtractTextFromScreenArea(screenshot, ConstructionStatusRect).Contains("Upgrading", StringComparison.OrdinalIgnoreCase)
                && !words.ExtractTextFromScreenArea(screenshot, ConstructionStatusWordRect).Contains("Upgrading", StringComparison.OrdinalIgnoreCase))
                return null;
            using var digits = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f, CharacterWhitelist = "0123456789:", PageSegMode = Tesseract.PageSegMode.SingleLine });
            var text = digits.ExtractTextFromScreenArea(screenshot, ConstructionTimerRect);
            // The clock icon before the countdown can come out as an extra digit ("100:19:27"): use the last hh:mm:ss
            var match = System.Text.RegularExpressions.Regex.Match(text, @"\d\d:\d\d:\d\d$");
            if (match.Success && TimeSpan.TryParseExact(match.Value, @"hh\:mm\:ss", null, out var left)) return left;
            logger.LogInfo($"Construction timer unreadable ('{text}'); assuming 30 minutes");
            return TimeSpan.FromMinutes(30);
        }

        /// <summary>
        /// Countdown above the building Go centred, while it is being upgraded ("00:08:19" in white), or null.
        /// </summary>
        private static TimeSpan? ReadCentredBuildingCountdown(byte[] screenshot, LogService logger)
        {
            using var ocr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 3.0f, CharacterWhitelist = "0123456789:", PageSegMode = Tesseract.PageSegMode.SingleLine });
            var match = System.Text.RegularExpressions.Regex.Match(ocr.ExtractBrightTextFromScreenArea(screenshot, CentredBuildingCountdownRect), @"\d\d:\d\d:\d\d");
            return match.Success && TimeSpan.TryParseExact(match.Value, @"hh\:mm\:ss", null, out var left) ? left : null;
        }

        private TaskExecutionDetails TownCenterBusy(AccountSettings account, LogService logger, TimeSpan left)
        {
            TroopTrainingTask.HoldCampsForUpgrade(account.InstanceNumber, Array.Empty<string>()); // nothing to keep idle for now
            FarmingTask.SetPriorityResource(account.InstanceNumber, null);
            var wait = left < TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : left;
            _nextBuildCheck[account.InstanceNumber] = DateTime.UtcNow + wait;
            logger.LogInfo($"[{account.AccountName}] Town Center is upgrading ({left} left); next Auto Build check in {wait}");
            return new TaskExecutionDetails(true, message: $"Town Center upgrading, {left} left");
        }

        /// <summary>
        /// Follows the Go button of a missing building requirement and upgrades that building when its own
        /// requirements are met. Returns null when there is no building to go to (only resources missing),
        /// so the caller backs out of the Town Center pages itself.
        /// </summary>
        private async Task<TaskExecutionDetails?> UpgradeRequiredBuildingAsync(AccountSettings account, LogService logger, LocatorService locator, CancellationToken cancellationToken)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return null;
            var (goFound, goRect, _) = _templateMatcher.MatchTemplate(
                screenshot, Path.Combine(ImageTemplateFolder, "go-button.png"), account.InstanceNumber, threshold: 0.85, searchArea: goButtonSearchArea);
            if (!goFound) return null;

            // Go centres the camera on the building and opens its action ring (Details / Upgrade / Train)
            await ClickRandomInRectAsync(account.InstanceNumber, logger, goRect);
            await Task.Delay(2500, cancellationToken);
            var afterGo = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (afterGo != null)
            {
                // Keep the building's action ring (Details / Upgrade / Train) for reference
                var debugDir = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
                Directory.CreateDirectory(debugDir);
                await File.WriteAllBytesAsync(Path.Combine(debugDir, $"autobuild-after-go-{account.InstanceNumber}.png"), afterGo, cancellationToken);
            }
            await TapRingButtonAsync(account.InstanceNumber, logger, Path.Combine(ImageTemplateFolder, "ring-upgrade.png"), BuildingRingUpgradeButton);

            // The building's upgrade dialog lists its own requirements with ✓ / ❌
            var missing = await ReadRequirementsAsync(account, logger, cancellationToken);
            for (int retry = 1; retry <= 3 && missing == null && !await OnFurniturePageAsync(account, logger); retry++)
            {
                // An upgrading building's ring has Speedup where Upgrade would be: never retry on one
                var check = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (check != null && ReadCentredBuildingCountdown(check, logger) != null) break;
                // Go sometimes only centres the camera without opening the ring: press the building, then Upgrade again
                logger.LogInfo($"[{account.AccountName}] No upgrade dialog after Go; pressing the centred building to open its ring ({retry}/3)");
                await SwipeAsync(account.InstanceNumber, logger, CentredBuildingPoint, new Point(CentredBuildingPoint.X + 1, CentredBuildingPoint.Y + 1), 150);
                await Task.Delay(1200, cancellationToken);
                await TapRingButtonAsync(account.InstanceNumber, logger, Path.Combine(ImageTemplateFolder, "ring-upgrade.png"), BuildingRingUpgradeButton);
                missing = await ReadRequirementsAsync(account, logger, cancellationToken);
            }
            if (missing == null)
            {
                // Work buildings (Mill, Quarry, ...) open a furniture page instead; upgrading furniture levels the building
                var furnitureUpgraded = await UpgradeFurnitureAsync(account, logger, cancellationToken);
                if (!furnitureUpgraded)
                {
                    var unknown = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    var busyFor = unknown == null ? null : ReadCentredBuildingCountdown(unknown, logger);
                    if (busyFor != null)
                    {
                        // Still upgrading from the last run: ask the alliance for help if not done yet, come back when it finishes
                        await RequestAllianceHelpAsync(account, logger, cancellationToken);
                        var wait = busyFor.Value < TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : busyFor.Value;
                        _nextBuildCheck[account.InstanceNumber] = DateTime.UtcNow + wait;
                        logger.LogInfo($"[{account.AccountName}] The required building is upgrading ({busyFor} left); next Auto Build check in {wait}");
                        await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                        return new TaskExecutionDetails(true, message: $"Required building upgrading, {busyFor} left");
                    }
                    if (unknown != null)
                    {
                        // Keep it to teach the bot this page (e.g. the building's own construction progress)
                        var debugDir = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
                        Directory.CreateDirectory(debugDir);
                        await File.WriteAllBytesAsync(Path.Combine(debugDir, $"autobuild-required-building-{account.InstanceNumber}.png"), unknown, cancellationToken);
                    }
                }
                await PressBackAsync(account.InstanceNumber, logger); // the furniture page has no back arrow
                await Task.Delay(800, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                if (furnitureUpgraded)
                {
                    account.AutoBuildSettings.LastBuildTime = DateTime.UtcNow;
                    return TaskExecutionDetails.Succeeded();
                }
                return TaskExecutionDetails.FailedWith(FailureCategory.GameState, "Step 4: Upgrading required building",
                    "The required building opened neither an upgrade dialog nor an upgradable furniture page");
            }
            if (missing.Count > 0 && await UseBackpackResourcesAsync(account, logger, cancellationToken))
            {
                missing = await ReadRequirementsAsync(account, logger, cancellationToken) ?? missing;
            }
            if (missing.Count > 0)
            {
                logger.LogInfo($"[{account.AccountName}] The required building needs: {string.Join("; ", missing)}. Not upgrading.");
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                return TaskExecutionDetails.FailedWith(FailureCategory.GameState, "Step 4: Upgrading required building",
                    $"The required building needs: {string.Join("; ", missing)}");
            }

            // Its cyan Upgrade button (with a timer) sits at the bottom right; the Town Center page's greyed one scores ~0.77
            screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            var (upgradeFound, upgradeRect, _) = screenshot == null ? (false, Rectangle.Empty, 0.0)
                : _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "dialog-upgrade-button.png"),
                    account.InstanceNumber, threshold: 0.88, searchArea: DialogUpgradeButtonArea);
            if (!upgradeFound && screenshot != null)
            {
                // A required building that does not exist yet (e.g. the Embassy) shows a centred Build button instead
                (upgradeFound, upgradeRect, _) = _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "build-button.png"),
                    account.InstanceNumber, threshold: 0.85, searchArea: new Rectangle(150, 1100, 450, 160));
            }
            if (!upgradeFound)
            {
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                return FailDetection("Step 4: Upgrading required building", "the building's Upgrade button");
            }

            logger.LogInfo($"[{account.AccountName}] All requirements of the required building met, tapping Upgrade/Build");
            await ClickRandomInRectAsync(account.InstanceNumber, logger, upgradeRect);
            await RandomDelay(cancellationToken);
            await RequestAllianceHelpAsync(account, logger, cancellationToken);
            await ChainQuickUpgradesAsync(account, logger, cancellationToken);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

            account.AutoBuildSettings.LastBuildTime = DateTime.UtcNow;
            return TaskExecutionDetails.Succeeded();
        }

        /// <summary>
        /// Low levels of a required building (e.g. Range 2 → 9) take seconds each. The camera is still on it after the
        /// upgrade, so while the builder frees up within a few seconds, press it and upgrade again (up to 3 more) instead of
        /// waiting for the next Auto Build run, which usually needs a game restart to find the Town Center again.
        /// </summary>
        private async Task ChainQuickUpgradesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            using var ocr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 3.0f, PageSegMode = Tesseract.PageSegMode.SingleLine });
            for (int extra = 1; extra <= 3; extra++)
            {
                await Task.Delay(4000, cancellationToken);
                await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken); // side panel: is the builder free?
                var panel = await TakeScreenshotAsync(account.InstanceNumber, logger);
                var builder = panel == null ? "" : ocr.ExtractBrightTextFromScreenArea(panel, new Rectangle(150, 376, 160, 30));
                await ClickAsync(account.InstanceNumber, logger, new Point(464, 544)); // close the panel
                await Task.Delay(800, cancellationToken);
                if (!builder.Contains("Idle", StringComparison.OrdinalIgnoreCase))
                {
                    // A real upgrade timer: come back when the builder is free rather than after the usual 10 minutes
                    var timer = System.Text.RegularExpressions.Regex.Match(builder, @"\d\d:\d\d:\d\d");
                    if (timer.Success && TimeSpan.TryParseExact(timer.Value, @"hh\:mm\:ss", null, out var left))
                        _builderFreeAt[account.InstanceNumber] = DateTime.UtcNow + left;
                    return;
                }

                await SwipeAsync(account.InstanceNumber, logger, CentredBuildingPoint, new Point(CentredBuildingPoint.X + 1, CentredBuildingPoint.Y + 1), 150);
                await Task.Delay(1200, cancellationToken);
                if (!await TapRingButtonAsync(account.InstanceNumber, logger, Path.Combine(ImageTemplateFolder, "ring-upgrade.png"), BuildingRingUpgradeButton)) return;
                var missing = await ReadRequirementsAsync(account, logger, cancellationToken);
                if (missing == null || missing.Count > 0) { await PressBackAsync(account.InstanceNumber, logger); return; }
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                var (found, rect, _) = screenshot == null ? (false, Rectangle.Empty, 0.0)
                    : _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "dialog-upgrade-button.png"),
                        account.InstanceNumber, threshold: 0.88, searchArea: DialogUpgradeButtonArea);
                if (!found) { await PressBackAsync(account.InstanceNumber, logger); return; }
                logger.LogInfo($"[{account.AccountName}] ⏩ Builder free again; upgrading the same building once more ({extra}/3)");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, rect);
                await RandomDelay(cancellationToken);
                await RequestAllianceHelpAsync(account, logger, cancellationToken);
            }
        }

        private async Task<bool> OnFurniturePageAsync(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            return screenshot != null && _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "furniture-tab.png"),
                account.InstanceNumber, threshold: 0.9, searchArea: new Rectangle(60, 1150, 280, 110)).found;
        }

        /// <summary>
        /// On a work building's furniture page, upgrades the selected piece of furniture (its cyan Upgrade button sits at
        /// the top right). Returns true when it tapped Upgrade.
        /// </summary>
        private async Task<bool> UpgradeFurnitureAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;
            if (!_templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "furniture-tab.png"),
                    account.InstanceNumber, threshold: 0.9, searchArea: new Rectangle(60, 1150, 280, 110)).found)
                return false;

            var (upgradeFound, upgradeRect, _) = _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "furniture-upgrade-button.png"),
                account.InstanceNumber, threshold: 0.9, searchArea: new Rectangle(480, 590, 240, 140));
            if (!upgradeFound)
            {
                logger.LogInfo($"[{account.AccountName}] Furniture page without an Upgrade button (furniture maxed or busy)");
                return false;
            }

            logger.LogInfo($"[{account.AccountName}] 🪑 Upgrading the work building's furniture");
            await ClickRandomInRectAsync(account.InstanceNumber, logger, upgradeRect);
            await Task.Delay(1500, cancellationToken);
            await RequestAllianceHelpAsync(account, logger, cancellationToken);
            return true;
        }

        /// <summary>
        /// For each missing resource: Obtain opens "Get More", whose green Use buttons each use one backpack item;
        /// the game closes the dialog once the requirement is met. Never taps Buy Packs, Replenish All or anything
        /// costing gems. Returns true when at least one resource was filled.
        /// </summary>
        private async Task<bool> UseBackpackResourcesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var obtain = Path.Combine(ImageTemplateFolder, "obtain.png");
            var useItem = Path.Combine(ImageTemplateFolder, "item-use-button.png");
            var filledAny = false;

            for (int resource = 0; resource < 4; resource++) // a requirements list has at most a few resource rows
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) break;
                var (obtainFound, obtainRect, _) = _templateMatcher.MatchTemplate(
                    screenshot, obtain, account.InstanceNumber, threshold: 0.88, searchArea: goButtonSearchArea);
                if (!obtainFound) break;

                await ClickRandomInRectAsync(account.InstanceNumber, logger, obtainRect);
                await Task.Delay(1500, cancellationToken);

                var dialogClosed = false;
                for (int use = 1; use <= 100; use++) // the user wants backpack resources spent to progress
                {
                    screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (screenshot == null) break;
                    var (useFound, useRect, _) = _templateMatcher.MatchTemplate(screenshot, useItem, account.InstanceNumber, threshold: 0.88);
                    if (!useFound)
                    {
                        // No green Use: either the dialog closed (requirement met) or the backpack has no such items
                        dialogClosed = !_templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "replenish-all.png"),
                            account.InstanceNumber, threshold: 0.88).found;
                        break;
                    }
                    logger.LogInfo($"[{account.AccountName}] Using a backpack resource item ({use})");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, useRect);
                    await Task.Delay(1200, cancellationToken);
                }

                if (!dialogClosed)
                {
                    logger.LogInfo($"[{account.AccountName}] Not enough backpack items for this resource; closing Get More");
                    var shortage = await TakeScreenshotAsync(account.InstanceNumber, logger);
                    if (shortage != null)
                    {
                        // Keep the Get More dialog to learn which resource is short (for farming priority)
                        var debugDir = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
                        Directory.CreateDirectory(debugDir);
                        await File.WriteAllBytesAsync(Path.Combine(debugDir, $"autobuild-get-more-{account.InstanceNumber}.png"), shortage, cancellationToken);
                        using var words = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f });
                        var text = words.ExtractTextFromScreenArea(shortage, new Rectangle(40, 100, 640, 400));
                        // Its items are named after the resource ("Stone x1,000"): farm that one first
                        var shortType = new[] { ResourceType.Bread, ResourceType.Wood, ResourceType.Stone, ResourceType.Iron }
                            .Where(t => text.Contains(t.ToString(), StringComparison.OrdinalIgnoreCase)).Select(t => (ResourceType?)t).FirstOrDefault();
                        logger.LogInfo($"[{account.AccountName}] Short of {shortType?.ToString() ?? "an unknown resource"} for the upgrade");
                        FarmingTask.SetPriorityResource(account.InstanceNumber, shortType);
                    }
                    await ClickAsync(account.InstanceNumber, logger, GetMoreCloseButton);
                    await RandomDelay(cancellationToken);
                    break; // the same Obtain row would come up again
                }
                logger.LogInfo($"[{account.AccountName}] Resource requirement filled from the backpack");
                filledAny = true;
            }
            return filledAny;
        }

        /// <summary>
        /// After an upgrade starts, the game shows the alliance help handshake; tapping it asks alliance
        /// members to speed the upgrade up. No handshake (e.g. not in an alliance) is fine.
        /// </summary>
        private async Task RequestAllianceHelpAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            // The white handshake bubble over our own upgrading building asks for help (0.89+); the green one helps allies
            var handshake = Path.Combine(Path.GetDirectoryName(ImageTemplateFolder)!, "alliance", "request-help-bubble.png");
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                await Task.Delay(1000, cancellationToken);
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) continue;

                var (found, rect, confidence) = _templateMatcher.MatchTemplate(screenshot, handshake, account.InstanceNumber, threshold: 0.85);
                if (found)
                {
                    logger.LogInfo($"[{account.AccountName}] Requesting alliance help for the upgrade (confidence {confidence:F2})");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, rect);
                    await RandomDelay(cancellationToken);
                    return;
                }
            }
            logger.LogInfo($"[{account.AccountName}] No alliance help bubble (already requested, or not in an alliance)");
        }

        /// <summary>
        /// Closes and relaunches the game through the emulator console (ldconsole killapp/runapp), then waits
        /// for it to load. A fresh start puts the city camera back on the Town Center.
        /// </summary>
        private static async Task RestartGameAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            await RunGameConsoleCommandAsync("killapp", account.InstanceNumber, cancellationToken);
            await RunGameConsoleCommandAsync("runapp", account.InstanceNumber, cancellationToken);
            // Don't touch the screen while it loads; EnsureViewAsync then closes the launch pop-ups
            await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
        }

        /// <summary>
        /// Reads the page title. The outlined title font comes out of OCR with stray marks
        /// (e.g. "iTown|[CenterJIvY6]*", or "iown|CenterJEvy7j" with the T misread), so only "Center" is required.
        /// </summary>
        private static bool IsTownCenterPage(byte[] screenshot, LogService logger)
        {
            using var ocr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f });
            var title = ocr.ExtractTextFromScreenArea(screenshot, TownCenterTitleArea);
            logger.LogInfo($"Building page title: '{title}'");
            return Regex.Replace(title, "[^A-Za-z]", "").Contains("Center", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Waits for the requirements list and returns its unmet rows: empty when every row has a green ✓,
        /// null when no ❌ or ✓ mark appeared (screen not open yet, or something else on top). "No ❌ seen"
        /// alone must never count as "all met": right after the tap the list is still sliding in.
        /// </summary>
        private async Task<List<string>?> ReadRequirementsAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var greenCheck = Path.Combine(ImageTemplateFolder, "green-check.png");
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                await Task.Delay(1000, cancellationToken);
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) continue;

                var missing = ReadMissingRequirements(screenshot, account, logger);
                if (missing.Count > 0) return missing;

                var (metRowFound, _, _) = _templateMatcher.MatchTemplate(
                    screenshot, greenCheck, account.InstanceNumber, threshold: 0.85, searchArea: redXSearchArea);
                if (metRowFound) return missing;

                logger.LogInfo($"[{account.AccountName}] Requirements list not visible yet (attempt {attempt}/5)");
            }
            return null;
        }

        /// <summary>
        /// Text of every requirement row marked with a red ❌, e.g. "Mill Level 6" or "926/13K".
        /// </summary>
        private List<string> ReadMissingRequirements(byte[] screenshot, AccountSettings account, LogService logger)
        {
            var redX = Path.Combine(ImageTemplateFolder, "red-x.png");
            using var ocr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 2.0f });
            using var image = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            var missing = new List<string>();

            for (int i = 0; i < 8; i++) // a requirements list has only a few rows
            {
                var (found, rect, _) = _templateMatcher.MatchTemplate(
                    image.ToBytes(".png"), redX, account.InstanceNumber, threshold: 0.85, searchArea: redXSearchArea);
                if (!found) break;

                missing.Add(ocr.ExtractTextFromScreenArea(screenshot, new Rectangle(90, rect.Y - 8, rect.X - 100, rect.Height + 16)));
                // Paint over this ❌ so the next match finds the next row
                OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(rect.X, rect.Y, rect.Width, rect.Height), OpenCvSharp.Scalar.All(255), -1);
            }
            return missing;
        }

        private async Task CloseTownCenterPagesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            // Requirements screen -> Town Center page -> city view
            await PressBackAsync(account.InstanceNumber, logger);
            await RandomDelay(cancellationToken);
            await PressBackAsync(account.InstanceNumber, logger);
            await RandomDelay(cancellationToken);
        }
    }
}
