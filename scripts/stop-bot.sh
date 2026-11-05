#!/bin/sh
# Stops the bot. The emulators keep running.
pkill -f "KingshotAuto.Cli"
pkill -f "dotnet run -c Release --project Bot.Cli"
sleep 2
if pgrep -f "KingshotAuto.Cli" >/dev/null; then echo "Bot still running"; exit 1; fi
echo "Bot stopped"
