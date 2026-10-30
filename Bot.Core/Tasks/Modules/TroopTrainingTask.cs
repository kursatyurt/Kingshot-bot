using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Keeps the Barracks, Stable and Range training. The city's side panel (behind the ▷ tab on the left edge)
    /// lists each camp as "Idle" or with its countdown; an idle camp's arrow centres the camera on it with its
    /// action ring open. Ring Train → the training page → Train (resources only; never Finish, which costs gems).
    /// A camp that is not built yet opens its Build page instead, which is tapped too.
    /// More troops mean bigger gathering loads (a march carried 164 troops of a 1,820 limit).
    /// </summary>
    public class TroopTrainingTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.TroopTraining;
        public override string Name => "Troop Training";

        private static readonly ConcurrentDictionary<int, DateTime> _nextCheck = new();
        private static readonly ConcurrentDictionary<int, string[]> _heldCamps = new();

        /// <summary>
        /// A camp can't upgrade while it trains, and the Town Center may require one (e.g. "Range Level 9"): Auto Build
        /// reports the missing requirements and training leaves those camps idle until the requirement is gone.
        /// </summary>
        public static void HoldCampsForUpgrade(int instance, System.Collections.Generic.IEnumerable<string> townCenterNeeds)
        {
            _heldCamps[instance] = townCenterNeeds.SelectMany(need =>
                need.Contains("Barracks", StringComparison.OrdinalIgnoreCase) ? new[] { "Infantry" } :
                need.Contains("Stable", StringComparison.OrdinalIgnoreCase) ? new[] { "Cavalry" } :
                need.Contains("Range", StringComparison.OrdinalIgnoreCase) ? new[] { "Archer" } : Array.Empty<string>()).ToArray();
        }

        private static readonly Point SidePanelCloseHandle = new Point(464, 544);
        private static readonly (string Name, int Y)[] Camps = { ("Infantry", 565), ("Cavalry", 640), ("Archer", 710) };
        private const int CampArrowX = 405;
        private static readonly Point RingTrainButton = new Point(480, 722); // centre of the ring's round Train icon
        private static readonly Point CentredCamp = new Point(368, 560); // where the panel's arrow puts the camp
        private const int TrainButtonX = 545;     // Finish (gems) is on the left half; Train always on the right
        private const int TrainBelowMinus = 162;
        private static readonly Rectangle QuantityMinusArea = new Rectangle(0, 860, 250, 320); // slider "−": only on the Train page
        private static readonly Point BackArrow = new Point(40, 40);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (_nextCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next)
                return new TaskExecutionDetails(true, message: "Skipped: camps still training");

            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

            var statuses = await ReadCampStatusesAsync(account, logger, cancellationToken);
            if (statuses == null)
            {
                _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddMinutes(15);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                return TaskExecutionDetails.Failed("Could not read the troop training panel");
            }

            int started = 0;
            var busy = new System.Collections.Generic.List<TimeSpan>();
            foreach (var (camp, status) in statuses)
            {
                if (status == TimeSpan.Zero)
                {
                    await CollectCampAsync(account, logger, camp, cancellationToken);
                    await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                }
                else if (status is TimeSpan left) { busy.Add(left); continue; }
                if (_heldCamps.TryGetValue(account.InstanceNumber, out var held) && held.Contains(camp))
                {
                    logger.LogInfo($"[{account.AccountName}] {camp}: left idle so its camp can be upgraded for the Town Center");
                    continue;
                }
                if (FarmingTask.TownCenterIsShortOfResources(account.InstanceNumber))
                {
                    // Troops cost the same resources: training ate the Stone a Town Center upgrade was waiting for
                    logger.LogInfo($"[{account.AccountName}] {camp}: not training while the Town Center waits for resources");
                    continue;
                }
                if (await TrainCampAsync(account, logger, camp, cancellationToken))
                {
                    started++;
                    busy.Add(TimeSpan.FromMinutes(5)); // re-read its real timer soon: speedups may already have finished it
                }
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            }

            // Come back when the first camp finishes (at least 5 minutes, at most an hour)
            var wait = busy.Count == 0 ? TimeSpan.FromMinutes(15) : busy.Min();
            wait = wait < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
            _nextCheck[account.InstanceNumber] = DateTime.UtcNow + wait;
            logger.LogInfo($"[{account.AccountName}] 🪖 Troop training: started {started}; next check in {wait:hh\\:mm\\:ss}");
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: $"Started training in {started} camp(s)");
        }

        /// <summary>Opens the side panel and reads each camp: null value = idle, TimeSpan = still training.</summary>
        private async Task<(string Camp, TimeSpan? Left)[]?> ReadCampStatusesAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            using var ocr = new OCRService(logger, new OCRConfiguration { ScaleFactor = 3.0f, PageSegMode = Tesseract.PageSegMode.SingleLine });
            var result = new (string Camp, TimeSpan? Left)[Camps.Length];
            int readable = 0;
            byte[]? screenshot = null;
            for (int open = 0; open < 2 && readable == 0; open++)
            {
                // The ▷ tab toggles: if the panel was already open, the first tap closed it
                await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken);
                screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) return null;
                readable = ReadCamps(screenshot, ocr, result, account, logger);
            }
            if (readable == 0 && screenshot != null) await SaveDebugAsync($"training-panel-unreadable-{account.InstanceNumber}.png", screenshot);
            await ClickAsync(account.InstanceNumber, logger, SidePanelCloseHandle); // the back key would open "Quit game?"
            await Task.Delay(800, cancellationToken);
            return readable == 0 ? null : result;
        }

        private int ReadCamps(byte[] screenshot, OCRService ocr, (string Camp, TimeSpan? Left)[] result, AccountSettings account, LogService logger)
        {
            int readable = 0;
            for (int i = 0; i < Camps.Length; i++)
            {
                // 140, not the default 200: "Completed" is green and "Upgrading" orange, which the default reads as noise
                var text = ocr.ExtractBrightTextFromScreenArea(screenshot, new Rectangle(150, Camps[i].Y - 2, 160, 30), 140);
                var timer = Regex.Match(text, @"\d\d:\d\d:\d\d");
                if (timer.Success && TimeSpan.TryParseExact(timer.Value, @"hh\:mm\:ss", null, out var left))
                {
                    result[i] = (Camps[i].Name, left);
                    readable++;
                }
                else if (text.Contains("Complete", StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = (Camps[i].Name, TimeSpan.Zero); // trained troops waiting to be collected
                    readable++;
                }
                else if (text.Contains("Idle", StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = (Camps[i].Name, null);
                    readable++;
                }
                else
                {
                    result[i] = (Camps[i].Name, TimeSpan.FromMinutes(15)); // unreadable: leave it for now
                }
                logger.LogInfo($"[{account.AccountName}] {Camps[i].Name}: '{text}'");
            }
            return readable;
        }

        /// <summary>A building's info/build page: its cyan "Back" button sits at the top right.</summary>
        private bool IsBuildingPage(byte[] screenshot) =>
            _templateMatcher.MatchTemplate(screenshot, Path.Combine(Path.GetDirectoryName(ImageTemplateFolder)!, "recovery", "page-back-button.png"),
                0, threshold: 0.8, searchArea: new Rectangle(480, 460, 240, 120)).found;

        private static async Task SaveDebugAsync(string name, byte[] screenshot)
        {
            var debugDir = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
            Directory.CreateDirectory(debugDir);
            await File.WriteAllBytesAsync(Path.Combine(debugDir, name), screenshot);
        }

        /// <summary>A "Completed" camp's arrow centres it without a ring; pressing it collects the troops (it turns idle).</summary>
        private async Task CollectCampAsync(AccountSettings account, LogService logger, string camp, CancellationToken cancellationToken)
        {
            var y = Camps.First(c => c.Name == camp).Y;
            await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(CampArrowX, y));
            await Task.Delay(2500, cancellationToken);
            logger.LogInfo($"[{account.AccountName}] 🪖 Collecting trained {camp}");
            await SwipeAsync(account.InstanceNumber, logger, CentredCamp, new Point(CentredCamp.X + 1, CentredCamp.Y + 1), 150);
            await Task.Delay(2000, cancellationToken);
        }

        private async Task<bool> TrainCampAsync(AccountSettings account, LogService logger, string camp, CancellationToken cancellationToken)
        {
            var y = Camps.First(c => c.Name == camp).Y;
            await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(CampArrowX, y));
            await Task.Delay(2500, cancellationToken);

            // A camp that does not exist yet opens its Build page (e.g. the Stable)
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;
            await SaveDebugAsync($"training-{camp}-after-arrow-{account.InstanceNumber}.png", screenshot);
            var buildButton = Path.Combine(Path.GetDirectoryName(ImageTemplateFolder)!, "autobuild", "build-button.png");
            var (buildFound, buildRect, _) = _templateMatcher.MatchTemplate(screenshot, buildButton, account.InstanceNumber,
                threshold: 0.85, searchArea: new Rectangle(150, 1100, 450, 160));
            for (int look = 0; look < 2 && !buildFound && IsBuildingPage(screenshot); look++)
            {
                // A tutorial glove can cover the Build button for a moment
                await Task.Delay(1000, cancellationToken);
                screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger) ?? screenshot;
                (buildFound, buildRect, _) = _templateMatcher.MatchTemplate(screenshot, buildButton, account.InstanceNumber,
                    threshold: 0.85, searchArea: new Rectangle(150, 1100, 450, 160));
            }
            if (buildFound)
            {
                logger.LogInfo($"[{account.AccountName}] 🏗️ The {camp} camp is not built yet; building it");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, buildRect);
                await Task.Delay(2000, cancellationToken);
                return true;
            }

            if (!await TapRingButtonAsync(account.InstanceNumber, logger,
                    Path.Combine(Path.GetDirectoryName(ImageTemplateFolder)!, "autobuild", "ring-train.png"), RingTrainButton))
            {
                await Task.Delay(1000, cancellationToken); // a tutorial glove may cover it for a moment
                await TapRingButtonAsync(account.InstanceNumber, logger,
                    Path.Combine(Path.GetDirectoryName(ImageTemplateFolder)!, "autobuild", "ring-train.png"), RingTrainButton);
            }
            await Task.Delay(2500, cancellationToken);
            screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;
            // Only the Train page has the quantity slider; a camp already training shows Speedups in that spot instead
            var (onTrainPage, minusRect, _) = _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "minus-button.png"),
                account.InstanceNumber, threshold: 0.9, searchArea: QuantityMinusArea);
            if (!onTrainPage)
            {
                // The camp's Build page can still be up once a slow tutorial glove has moved off its button
                var (lateBuild, lateRect, _) = _templateMatcher.MatchTemplate(screenshot, buildButton, account.InstanceNumber,
                    threshold: 0.85, searchArea: new Rectangle(150, 1100, 450, 160));
                if (lateBuild)
                {
                    logger.LogInfo($"[{account.AccountName}] 🏗️ The {camp} camp is not built yet; building it");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, lateRect);
                    await Task.Delay(2000, cancellationToken);
                    return true;
                }
                logger.LogInfo($"[{account.AccountName}] {camp}: no Train page after the ring's Train; leaving it");
                await SaveDebugAsync($"training-{camp}-no-train-page-{account.InstanceNumber}.png", screenshot);
                await ClickAsync(account.InstanceNumber, logger, BackArrow);
                await Task.Delay(1500, cancellationToken);
                return false;
            }

            logger.LogInfo($"[{account.AccountName}] 🪖 Training {camp}");
            // The page's height varies by account (minus at y 948 or 1042); Train sits 162 px below the minus, on the right
            await ClickAsync(account.InstanceNumber, logger, new Point(TrainButtonX, minusRect.Y + TrainBelowMinus));
            await Task.Delay(2000, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, BackArrow);
            await Task.Delay(1500, cancellationToken);
            return true;
        }
    }
}
