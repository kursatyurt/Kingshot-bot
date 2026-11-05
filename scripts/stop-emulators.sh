#!/bin/sh
# Shuts both emulators down. Stops the bot first, or it would keep trying to use them.
ADB="$HOME/Library/Android/sdk/platform-tools/adb"
"$(dirname "$0")/stop-bot.sh"
for port in 5554 5556; do "$ADB" -s "emulator-$port" emu kill >/dev/null 2>&1; done
tries=0
while pgrep -f "qemu-system.*-avd Kingshot_" >/dev/null; do
  tries=$((tries + 1))
  if [ "$tries" -ge 30 ]; then echo "Emulators still running after 60 s"; exit 1; fi
  sleep 2
done
echo "Emulators stopped"
