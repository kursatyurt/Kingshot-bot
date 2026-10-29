// Headless runner for KingshotAuto (used on macOS, works anywhere Bot.Core runs).
// Usage: KingshotAuto.Cli [config.json]   (default: configs/default_config.json, relative to the current directory)
//        KingshotAuto.Cli --selftest      (checks that OpenCV and Tesseract load)
using System.Drawing;
using Bot.Core.Config;
using Bot.Core.LDPlayer;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks;
using Bot.Core.Utils;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;

var logger = new LogService();

if (args.Contains("--selftest"))
    return SelfTest(logger);

ConfigurationManager.Initialize(logger);
ADBConnectionManager.StartHealthMonitoring(logger);

var notifications = new UserNotificationService(logger, new GUILogService(Console.WriteLine));
var taskManager = new TaskManager(logger, new ServiceCollection().BuildServiceProvider(), notifications);
await taskManager.InitializeAsync(); // loads each task's template matcher, as the GUI does before starting
var cycle = new CycleManagementService(logger, taskManager);
BaseTaskWithCommonPatterns.SetCycleManagementService(cycle);
cycle.OnStatusUpdate += s => Console.WriteLine($"[cycle] {s}");
cycle.OnCycleStarted += (n, _) => Console.WriteLine($"[cycle] #{n} started");
cycle.OnCycleCompleted += (n, ok) => Console.WriteLine($"[cycle] #{n} {(ok ? "completed" : "failed")}");
cycle.OnWaitingBetweenCycles += t => Console.WriteLine($"[cycle] next cycle in {t:hh\\:mm\\:ss}");

var configPath = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine("configs", "default_config.json");
var config = BotConfig.LoadFromFile(configPath, logger);
if (config.Accounts.Count == 0)
{
    Console.Error.WriteLine($"No accounts in {Path.GetFullPath(configPath)}. Add at least one account (InstanceNumber N = AVD Kingshot_N).");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (cts.IsCancellationRequested) Environment.Exit(130); // second Ctrl+C: stop right away
    cts.Cancel();
    // Some long steps (e.g. emulator recovery) don't observe cancellation; never let them keep running
    _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Environment.Exit(130));
};
Console.WriteLine($"Running {config.Accounts.Count} account(s) from {Path.GetFullPath(configPath)}. Ctrl+C to stop.");

try
{
    await cycle.StartCycleAsync(config, cts.Token);
}
catch (OperationCanceledException) { }
finally
{
    await cycle.StopCycleAsync();
    ADBConnectionManager.StopHealthMonitoring();
}
return 0;

static int SelfTest(LogService logger)
{
    var template = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "templates"), "*.png", SearchOption.AllDirectories).First();
    using var img = Cv2.ImRead(template, ImreadModes.Color);
    using var result = new Mat();
    Cv2.MatchTemplate(img, img, result, TemplateMatchModes.CCoeffNormed);
    Cv2.MinMaxLoc(result, out _, out double score);
    Console.WriteLine($"OpenCV: matched {Path.GetFileName(template)} against itself, score {score:F3}");

    // Light text on a dark background, like the game UI
    using var text = new Mat(80, 320, MatType.CV_8UC3, Scalar.All(30));
    Cv2.PutText(text, "12345", new OpenCvSharp.Point(20, 60), HersheyFonts.HersheySimplex, 2, Scalar.All(230), 4);
    using var ocr = new OCRService(logger, new OCRConfiguration { CharacterWhitelist = "0123456789" });
    var read = ocr.ExtractTextFromScreenArea(text.ToBytes(".png"), new Rectangle(0, 0, text.Width, text.Height));
    Console.WriteLine($"Tesseract: read '{read}'");

    var ok = score > 0.99 && read.Contains("12345");
    Console.WriteLine(ok ? "SELFTEST PASSED" : "SELFTEST FAILED");
    return ok ? 0 : 1;
}
