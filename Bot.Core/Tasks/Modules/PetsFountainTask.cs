using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Side panel Daily tab: the Fountain of Life row shows a green check when Water Essence can be collected; tapping it
    /// collects and opens My Island, left with its back arrow. Once per UTC day (after the 00:00 reset) Pet Adventure's
    /// row arrow → back arrow lands on the pet enclosure → Skills: only the Lynx's Comforting Embrace (+35 stamina) is
    /// used, and only when its Use button is lit; the Gray Wolf's Construction Aide is never touched.
    /// </summary>
    public class PetsFountainTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.PetsFountain;
        public override string Name => "Pets & Fountain";
        protected override string GetImageFolderName() => "pets";

        private static readonly Dictionary<int, DateTime> _nextCheck = new();
        private static readonly string PetsDayFile = Path.Combine(AppContext.BaseDirectory, "data", "pets_day.json");
        private static readonly Rectangle RowIcons = new(0, 290, 120, 570);

        protected override bool OnCooldown(AccountSettings account) => _nextCheck.TryGetValue(account.InstanceNumber, out var next) && DateTime.UtcNow < next;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (OnCooldown(account)) return new TaskExecutionDetails(true, message: "Pets & Fountain checked recently");
            _nextCheck[account.InstanceNumber] = DateTime.UtcNow.AddHours(2);

            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            var done = new List<string>();

            if (await OpenDailyTabAsync(account, logger, cancellationToken) && await RowYAsync(account, logger, "fountain-row.png") is int fy)
            {
                var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (shot != null && IsGreenCheck(shot, fy))
                {
                    logger.LogInfo($"[{account.AccountName}] 💧 Collecting Fountain of Life water essence");
                    await ClickAsync(account.InstanceNumber, logger, new Point(404, fy));
                    await Bot.Core.Utils.Pace.Delay(3000, cancellationToken);
                    await FindAndClickImageAsync("back-arrow.png", account.InstanceNumber, logger, threshold: 0.85, searchArea: new Rectangle(0, 0, 160, 90)); // leave My Island
                    await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
                    done.Add("fountain collected");
                }
            }

            var accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
            if (string.IsNullOrEmpty(accountId)) accountId = $"Account_{account.AccountName}_{account.InstanceNumber}";
            var days = LoadPetsDays();
            if (!days.TryGetValue(accountId, out var day) || day < DateTime.UtcNow.Date)
            {
                // Locked or cooling down: look again next run (the 23 h cooldown would drift if the day were closed)
                if (await UseComfortingEmbraceAsync(account, logger, locator, cancellationToken))
                {
                    done.Add("Comforting Embrace used");
                    days[accountId] = DateTime.UtcNow.Date;
                    SavePetsDays(days);
                }
            }

            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            return new TaskExecutionDetails(true, message: done.Count > 0 ? string.Join("; ", done) : "Nothing to do");
        }

        /// <summary>True when Comforting Embrace was used.</summary>
        private async Task<bool> UseComfortingEmbraceAsync(AccountSettings account, LogService logger, LocatorService locator, CancellationToken cancellationToken)
        {
            await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
            if (!await OpenDailyTabAsync(account, logger, cancellationToken) || await RowYAsync(account, logger, "pet-adventure-row.png") is not int py)
                return false;
            await ClickAsync(account.InstanceNumber, logger, new Point(404, py)); // Pet Adventure
            if (!await WaitForImageAsync("pet-adventure-title.png", account.InstanceNumber, logger, cancellationToken, 6000, threshold: 0.85))
                return false;
            await ClickAsync(account.InstanceNumber, logger, new Point(48, 40)); // back to the enclosure
            await Bot.Core.Utils.Pace.Delay(2000, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(82, 1200)); // Skills
            if (!await WaitForImageAsync("pet-skill-title.png", account.InstanceNumber, logger, cancellationToken, 5000, threshold: 0.85))
                return false;

            bool used = false;
            string result;
            var (found, lynx, _) = await WaitForImageWithPositionAsync("lynx-skill.png", account.InstanceNumber, logger, cancellationToken, 3000, threshold: 0.8, searchArea: new Rectangle(50, 225, 620, 530));
            if (!found) result = "no Lynx skill";
            else
            {
                await ClickAsync(account.InstanceNumber, logger, new Point(lynx.X + lynx.Width / 2, lynx.Y + lynx.Height / 2));
                await Bot.Core.Utils.Pace.Delay(1200, cancellationToken);
                var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                // Locked shows red "Unlocked after…" text and on cooldown the button is grey: only a cyan Use is pressed
                if (shot != null && IsCyan(shot, new Rectangle(240, 1060, 240, 50)))
                {
                    logger.LogInfo($"[{account.AccountName}] 🐱 Using Comforting Embrace (+35 stamina)");
                    await ClickAsync(account.InstanceNumber, logger, new Point(360, 1088));
                    await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
                    result = "Comforting Embrace used";
                    used = true;
                }
                else result = "Comforting Embrace locked or on cooldown";
            }
            await ClickAsync(account.InstanceNumber, logger, new Point(668, 152)); // close Pet Skill
            await Bot.Core.Utils.Pace.Delay(1000, cancellationToken);
            await ClickAsync(account.InstanceNumber, logger, new Point(48, 40));  // leave the enclosure
            await Bot.Core.Utils.Pace.Delay(1500, cancellationToken);
            logger.LogInfo($"[{account.AccountName}] Pets: {result}");
            return used;
        }

        private async Task<bool> OpenDailyTabAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            if (!await OpenSidePanelAsync(account.InstanceNumber, logger, cancellationToken)) return false;
            await ClickAsync(account.InstanceNumber, logger, new Point(370, 269)); // Daily tab
            await Bot.Core.Utils.Pace.Delay(800, cancellationToken);
            return true;
        }

        private async Task<int?> RowYAsync(AccountSettings account, LogService logger, string icon)
        {
            var shot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (shot == null) return null;
            var (found, rect, _) = _templateMatcher.MatchTemplate(shot, Path.Combine(ImageTemplateFolder, icon), account.InstanceNumber, threshold: 0.9, searchArea: RowIcons);
            return found ? rect.Y + rect.Height / 2 : null;
        }

        /// <summary>Daily rows end in a green check when claimable and a cyan arrow otherwise.</summary>
        public static bool IsGreenCheck(byte[] screenshot, int rowY)
        {
            var m = Mean(screenshot, new Rectangle(395, rowY - 8, 18, 16));
            return m.Val1 > 200 && m.Val1 - m.Val0 > 40;
        }

        public static bool IsCyan(byte[] screenshot, Rectangle rect)
        {
            var m = Mean(screenshot, rect);
            return m.Val0 > 170 && m.Val1 > 160 && m.Val2 < 120;
        }

        private static OpenCvSharp.Scalar Mean(byte[] screenshot, Rectangle rect)
        {
            using var img = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color);
            using var roi = new OpenCvSharp.Mat(img, new OpenCvSharp.Rect(rect.X, rect.Y, rect.Width, rect.Height));
            return OpenCvSharp.Cv2.Mean(roi); // BGR
        }

        private static Dictionary<string, DateTime> LoadPetsDays()
        {
            try { return File.Exists(PetsDayFile) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(PetsDayFile)) ?? new() : new(); }
            catch { return new(); }
        }

        private static void SavePetsDays(Dictionary<string, DateTime> days)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PetsDayFile)!);
            File.WriteAllText(PetsDayFile, JsonSerializer.Serialize(days));
        }
    }
}
