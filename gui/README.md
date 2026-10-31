# Bot GUI

`./run-gui.sh` opens a tkinter window (Python 3 stdlib only) to edit `configs/default_config.json`
per account (tasks, farming targets, Growth/Economy research), start/stop the bot, and tail `logs/bot-cli.out`.

Save validates input, writes `default_config.json.bak` first, and keeps every field it doesn't edit.
Restart the bot after saving. `./run-gui.sh --selftest` checks the load/save round-trip on a temp copy.

Not exposed: AutoShield, Battle research, internal tasks (Startup, AccountDetection, Recovery, ChangeAccount),
and all other settings (edit the JSON for those).
