using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;
using Bot.Core.Utils;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Service to automatically detect and fix common ADB connection issues
    /// </summary>
    public class ADBRecoveryService
    {
        private readonly LogService _logger;
        private readonly string _adbPath;
        private readonly string _ldConsolePath;
        
        public ADBRecoveryService(LogService logger)
        {
            _logger = logger;
            _adbPath = LDPlayerHelper.GetADBPath();
            _ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
        }
        
        /// <summary>
        /// Attempts multiple recovery strategies to fix ADB connection issues
        /// </summary>
        public async Task<bool> AttemptFullRecoveryAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            _logger.LogInfo($"[ADBRecovery] Starting comprehensive recovery for instance {instanceNumber}", category: LogCategories.ADB);
            
            // Strategy 0: reconnect just this device; restarting the adb server below cuts off every account
            if (await RunAdbAsync($"-s {Serial(instanceNumber)} reconnect", cancellationToken))
            {
                await Task.Delay(2000, cancellationToken); // the device takes a moment to come back
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                {
                    _logger.LogInfo($"[ADBRecovery] Reconnected {Serial(instanceNumber)}", category: LogCategories.ADB);
                    return true;
                }
            }

            // Strategy 1: Kill all adb processes and restart
            if (await RestartADBServerAsync(cancellationToken))
            {
                _logger.LogInfo("[ADBRecovery] ADB server restarted successfully", category: LogCategories.ADB);
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                    return true;
            }
            
            // Strategy 2: Restart the LDPlayer instance, with ADB switched on in its settings first (one restart covers both)
            await EnableADBInSettingsAsync(instanceNumber, cancellationToken);
            if (await RestartInstanceAsync(instanceNumber, cancellationToken))
            {
                _logger.LogInfo($"[ADBRecovery] Instance {instanceNumber} restarted", category: LogCategories.ADB);
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                    return true;
            }
            
            // Strategy 3: Fix port conflicts
            if (await FixPortConflictsAsync(instanceNumber, cancellationToken))
            {
                _logger.LogInfo("[ADBRecovery] Port conflicts resolved", category: LogCategories.ADB);
                if (await TestConnectionAsync(instanceNumber, cancellationToken))
                    return true;
            }
            
            // No key reset: deleting ~/.android/adbkey makes every emulator reject the bot as "unauthorized"

            _logger.LogError($"[ADBRecovery] All recovery strategies failed for instance {instanceNumber}", category: LogCategories.ADB);
            return false;
        }
        
        private async Task<bool> RestartADBServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Kill all adb.exe processes
                foreach (var adbProcess in Process.GetProcessesByName("adb"))
                {
                    try
                    {
                        adbProcess.Kill();
                        await adbProcess.WaitForExitAsync(cancellationToken);
                    }
                    catch { }
                }
                
                await Task.Delay(1000, cancellationToken);
                
                // Start ADB server
                var startInfo = new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = "start-server",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to restart ADB server: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> RestartInstanceAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                // Quit the instance
                var quitInfo = new ProcessStartInfo
                {
                    FileName = _ldConsolePath,
                    Arguments = $"quit --index {instanceNumber}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using (var quitProcess = Process.Start(quitInfo))
                {
                    if (quitProcess != null)
                    {
                        await quitProcess.WaitForExitAsync(cancellationToken);
                    }
                }
                
                await Task.Delay(3000, cancellationToken);
                
                // Launch the instance again
                var launchInfo = new ProcessStartInfo
                {
                    FileName = _ldConsolePath,
                    Arguments = $"launch --index {instanceNumber}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using (var launchProcess = Process.Start(launchInfo))
                {
                    if (launchProcess != null)
                    {
                        await launchProcess.WaitForExitAsync(cancellationToken);
                    }
                }
                
                // Wait for boot
                await Task.Delay(15000, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to restart instance: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> FixPortConflictsAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            // On macOS the Android Studio emulator itself owns this port, so there is nothing safe to kill
            if (!OperatingSystem.IsWindows()) return true;

            try
            {
                var port = 5554 + (instanceNumber * 2);

                // Kill processes using the port
                var netstatInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netstat -ano | findstr :{port}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(netstatInfo);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync(cancellationToken);
                    
                    // Parse PIDs and kill conflicting processes
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 4)
                        {
                            if (int.TryParse(parts[^1], out int pid))
                            {
                                try
                                {
                                    var conflictingProcess = Process.GetProcessById(pid);
                                    if (!conflictingProcess.ProcessName.Contains("LDPlayer", StringComparison.OrdinalIgnoreCase))
                                    {
                                        conflictingProcess.Kill();
                                        _logger.LogInfo($"[ADBRecovery] Killed process {conflictingProcess.ProcessName} using port {port}", category: LogCategories.ADB);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to fix port conflicts: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> EnableADBInSettingsAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                // Use ldconsole to enable ADB debugging
                var enableInfo = new ProcessStartInfo
                {
                    FileName = _ldConsolePath,
                    Arguments = $"globalsetting --index {instanceNumber} --enable_adb 1",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(enableInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to enable ADB in settings: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private static string Serial(int instanceNumber) => $"emulator-{5554 + (instanceNumber * 2)}";

        private Task<bool> TestConnectionAsync(int instanceNumber, CancellationToken cancellationToken) =>
            RunAdbAsync($"-s {Serial(instanceNumber)} shell echo test", cancellationToken, expect: "test");

        // adb can hang on a stuck device: give up after 10 s instead of stalling recovery
        private async Task<bool> RunAdbAsync(string args, CancellationToken cancellationToken, string? expect = null)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (process == null) return false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(10000);
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { process.Kill(); } catch { } throw; }
                return process.ExitCode == 0 && (expect == null || (await output).Contains(expect)) && !(await error).Contains("error");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning($"[ADBRecovery] adb {args} timed out", category: LogCategories.ADB);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning($"[ADBRecovery] adb {args} failed: {ex.Message}", category: LogCategories.ADB);
            }
            return false;
        }
    }
}