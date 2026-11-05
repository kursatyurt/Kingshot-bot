#!/bin/sh
# Step 1: starts both emulators and waits until Android has booted. Safe to run if they are already up.
SDK="$HOME/Library/Android/sdk"; ADB="$SDK/platform-tools/adb"
"$ADB" start-server >/dev/null 2>&1
for pair in Kingshot_0:5554 Kingshot_1:5556; do
  avd=${pair%:*}; port=${pair#*:}
  if pgrep -f "avd $avd " >/dev/null; then
    echo "$avd already running"
  else
    nohup "$SDK/emulator/emulator" -avd "$avd" -port "$port" -no-boot-anim -no-audio -no-snapshot >/dev/null 2>&1 < /dev/null &
    echo "$avd starting"
  fi
done
for port in 5554 5556; do
  tries=0
  until [ "$("$ADB" -s "emulator-$port" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = 1 ]; do
    tries=$((tries + 1))
    if [ "$tries" -ge 60 ]; then echo "emulator-$port did not boot within 5 minutes"; exit 1; fi
    sleep 5
  done
  echo "emulator-$port ready"
done
