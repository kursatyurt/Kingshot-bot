using Bot.Core.LDPlayer;
using Bot.Core.Logging;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    public class AdbRecoveryKeepsKeyTests
    {
        // Runs every recovery strategy (instance 9 does not exist, so none succeeds) and checks the adb key survives:
        // deleting it locked both emulators out at 01:15 Nov 12. Explicit: it restarts the adb server.
        [Test, Explicit]
        public async Task FullRecoveryKeepsAdbKey()
        {
            var key = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "adbkey");
            var before = File.ReadAllBytes(key);
            await new ADBRecoveryService(new LogService()).AttemptFullRecoveryAsync(9, CancellationToken.None);
            Assert.That(File.ReadAllBytes(key), Is.EqualTo(before));
        }
    }
}
