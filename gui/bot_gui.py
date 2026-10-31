#!/usr/bin/env python3
"""Tiny setup GUI for the Kingshot bot. Stdlib only. Run: ./run-gui.sh  (or --selftest)."""
import json, os, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONFIG = os.path.join(ROOT, "configs", "default_config.json")
LOG = os.path.join(ROOT, "logs", "bot-cli.out")

# TaskType enum (Bot.Core). Hidden on purpose: Startup(0), AccountDetection(1), Recovery(5),
# ChangeAccount(10) are internal; AutoShield(13) is declined by the user.
TASKS = {2: "Farming", 3: "AutoHunt", 4: "AutoHeal", 6: "AutoAllianceHelp", 7: "TroopTraining",
         8: "ConquestCollect", 9: "ClaimMail", 11: "AutoBuild", 12: "AutoClaimHero", 14: "CollectVip",
         15: "ClaimMissions", 16: "ResidentWelcome", 17: "AllianceTechnology", 18: "AutoTechnology",
         19: "AutoRally", 20: "EventRewards"}
RESOURCES = ["Bread", "Wood", "Stone", "Iron"]  # ResourceType enum
RESEARCH = {0: "Growth", 1: "Economy"}          # ResearchType; Battle (2) never offered


def load(path=CONFIG):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save(cfg, path=CONFIG):
    if os.path.exists(path):
        shutil.copy2(path, path + ".bak")
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2, ensure_ascii=False)
        f.write("\n")
    os.replace(tmp, path)


def get_research(acc):
    try:
        types = json.loads(acc.get("taskSettings", {}).get("AutoTechnology", "{}")).get("ResearchTypes") or [0]
    except (ValueError, AttributeError):
        types = [0]
    return [t for t in types if t in RESEARCH]


def set_research(acc, types):
    ts = acc.setdefault("taskSettings", {})
    try:
        d = json.loads(ts.get("AutoTechnology", "{}"))
    except ValueError:
        d = {}
    d["ResearchTypes"] = [t for t in types if t in RESEARCH]
    ts["AutoTechnology"] = json.dumps(d)


def selftest():
    import tempfile
    tmp = os.path.join(tempfile.mkdtemp(), "cfg.json")
    shutil.copy2(CONFIG, tmp)
    orig = load(tmp)
    cfg = load(tmp)
    for a in cfg["accounts"]:  # exercise the edit paths without changing values
        set_research(a, get_research(a))
    # AutoTechnology string is re-encoded; compare decoded so whitespace inside it doesn't matter
    norm = lambda c: json.loads(json.dumps(c), object_hook=lambda d: {k: (json.loads(v) if k == "AutoTechnology" else v) for k, v in d.items()})
    save(cfg, tmp)
    assert norm(load(tmp)) == norm(orig), "round-trip changed config"
    save(load(tmp), tmp)
    assert load(tmp) == load(tmp + ".bak"), "plain load/save not identical"
    assert get_research({"taskSettings": {"AutoTechnology": '{"ResearchTypes":[2,1]}'}}) == [1]
    print(f"selftest OK: {len(orig['accounts'])} accounts round-tripped via {tmp}")


def gui():
    import tkinter as tk
    from tkinter import ttk, messagebox

    cfg = load()
    root = tk.Tk()
    root.title("Kingshot Bot")
    status = tk.StringVar(value="Loaded " + CONFIG)
    running = tk.StringVar()

    top = ttk.Frame(root, padding=6); top.pack(fill="x")
    ttk.Button(top, text="Start", command=lambda: run(["./run-bot.sh"])).pack(side="left")
    ttk.Button(top, text="Stop", command=lambda: run(["pkill", "-f", "KingshotAuto.Cli"])).pack(side="left")
    ttk.Label(top, textvariable=running).pack(side="left", padx=10)

    nb = ttk.Notebook(root); nb.pack(fill="both", padx=6)
    forms = []
    for acc in cfg["accounts"]:
        f = ttk.Frame(nb, padding=6)
        nb.add(f, text=f"{acc.get('accountName')} (#{acc.get('instanceNumber')})")
        enabled = tk.BooleanVar(value=acc.get("isEnabled", True))
        ttk.Checkbutton(f, text="Account enabled", variable=enabled).grid(row=0, column=0, sticky="w")
        tf = ttk.LabelFrame(f, text="Tasks", padding=4); tf.grid(row=1, column=0, sticky="nw")
        tasks = {}
        for i, (num, name) in enumerate(TASKS.items()):
            tasks[num] = tk.BooleanVar(value=num in acc.get("enabledTasks", []))
            ttk.Checkbutton(tf, text=name, variable=tasks[num]).grid(row=i % 8, column=i // 8, sticky="w")
        ff = ttk.LabelFrame(f, text="Farming targets", padding=4); ff.grid(row=1, column=1, sticky="nw", padx=6)
        targets = []
        for i in range(4):
            t = acc.get("farmingTargets", [])[i] if i < len(acc.get("farmingTargets", [])) else None
            res = tk.StringVar(value=RESOURCES[t["resourceType"]] if t else "")
            lvl = tk.StringVar(value=str(t["level"]) if t else "")
            ttk.Combobox(ff, textvariable=res, values=[""] + RESOURCES, width=8, state="readonly").grid(row=i, column=0)
            ttk.Spinbox(ff, textvariable=lvl, from_=1, to=8, width=4).grid(row=i, column=1)
            targets.append((res, lvl))
        rf = ttk.LabelFrame(f, text="Research", padding=4); rf.grid(row=2, column=1, sticky="nw", padx=6)
        cur = get_research(acc)
        research = {k: tk.BooleanVar(value=k in cur) for k in RESEARCH}
        for k, name in RESEARCH.items():
            ttk.Checkbutton(rf, text=name, variable=research[k]).pack(anchor="w")
        forms.append((acc, enabled, tasks, targets, research))

    def do_save():
        staged = []
        for acc, enabled, tasks, targets, research in forms:
            name = acc.get("accountName")
            tg = []
            for res, lvl in targets:
                if not res.get():
                    continue
                if not lvl.get().isdigit() or not 1 <= int(lvl.get()) <= 8:
                    return messagebox.showerror("Invalid", f"{name}: level must be 1-8")
                tg.append({"resourceType": RESOURCES.index(res.get()), "level": int(lvl.get())})
            rs = [k for k, v in research.items() if v.get()]
            if tasks[18].get() and not rs:
                return messagebox.showerror("Invalid", f"{name}: pick Growth and/or Economy research")
            staged.append((acc, enabled.get(), tasks, tg, rs))
        for acc, en, tasks, tg, rs in staged:
            acc["isEnabled"] = en
            # keep order and any hidden task numbers (e.g. internal ones) already present
            old = acc.get("enabledTasks", [])
            acc["enabledTasks"] = [t for t in old if t not in TASKS or tasks[t].get()] + \
                [t for t in TASKS if tasks[t].get() and t not in old]
            acc["farmingTargets"] = tg
            if rs:
                set_research(acc, rs)
        try:
            save(cfg)
            status.set("Saved (backup: default_config.json.bak). Restart the bot to apply.")
        except OSError as e:
            messagebox.showerror("Save failed", str(e))

    ttk.Button(root, text="Save", command=do_save).pack(anchor="e", padx=6, pady=4)
    log = tk.Text(root, height=15, width=110, state="disabled", wrap="none"); log.pack(fill="both", expand=True, padx=6)
    ttk.Label(root, textvariable=status, relief="sunken").pack(fill="x")

    def run(cmd):
        p = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
        status.set((p.stdout or p.stderr).strip() or f"{cmd[0]} exit {p.returncode}")
        tick(reschedule=False)

    def tick(reschedule=True):
        up = subprocess.run(["pgrep", "-f", "KingshotAuto.Cli"], capture_output=True).returncode == 0
        running.set("Bot: RUNNING" if up else "Bot: stopped")
        try:
            with open(LOG, encoding="utf-8", errors="replace") as fh:
                text = "".join(fh.readlines()[-200:])  # ponytail: reads whole log; seek from end if it gets huge
        except OSError:
            text = "(no log yet)"
        at_end = log.yview()[1] >= 0.999
        log.config(state="normal"); log.delete("1.0", "end"); log.insert("end", text); log.config(state="disabled")
        if at_end:
            log.see("end")
        if reschedule:
            root.after(3000, tick)

    tick()
    root.mainloop()


if __name__ == "__main__":
    selftest() if "--selftest" in sys.argv else gui()
