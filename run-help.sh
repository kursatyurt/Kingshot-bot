#!/bin/sh
# Help-only bot: taps the Alliance Help button within ~1.5 s (configs/help_config.json). Starts the headless bot (it launches the Kingshot_N emulators itself), detached and keeping the Mac awake.
# Stop: pkill -f KingshotAuto.Cli    Log: tail -f logs/bot-cli.out
cd "$(dirname "$0")" || exit 1
if pgrep -f KingshotAuto.Cli >/dev/null; then echo "Bot already running"; exit 0; fi
nohup caffeinate -is dotnet run -c Release --project Bot.Cli -- configs/help_config.json --help-loop > logs/bot-cli.out 2>&1 < /dev/null &
echo "Bot started (pid $!), log: logs/bot-cli.out"
