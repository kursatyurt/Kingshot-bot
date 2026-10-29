# Growth plan

What the bot does for each account, in order of priority. Each line names the task that carries it out.

## 1. Town Center first

The Town Center level gates almost everything else: building levels, research, troop tiers, gathering tile levels.

- **Auto Build** opens the Town Center and reads its upgrade requirements.
  - Missing buildings: follows **Go** and upgrades them (or builds them if they don't exist yet). Several quick
    low-level upgrades are chained while the builder frees up within seconds.
  - Missing resources: uses **backpack resource items** (up to 100 per resource), then tells farming which
    resource is still short.
  - When everything is met: starts the upgrade and asks the alliance for help.
- **Troop Training** leaves a camp idle when the Town Center needs it upgraded (a training camp can't upgrade).

## 2. Keep every queue busy

| Queue | What keeps it busy |
|---|---|
| March | **Farming**: one march out at a time, level 7 tiles first (down to level 2). The resource the Town Center is short of goes first; otherwise Bread → Wood → Stone → Iron in turn. |
| Builder | **Auto Build**: the Town Center chain first. When that is blocked or done, newly unlocked buildings are built, then the game's suggested upgrade. Idle upgrades wait while the Town Center is short of resources, so they don't spend them. Never the Storehouse. |
| Academy | **Research**: Economy (resource output) and Growth (construction speed, troop capacity) in turn. |
| Camps | **Troop Training**: Infantry, Cavalry and Archers retrained as soon as they finish; finished troops collected. More troops mean bigger gathering loads. |

## 3. Alliance

- Help allies, and ask for help on every upgrade and on the Town Center while it upgrades (**Alliance Help**, **Auto Build**).
- Contribute to the recommended alliance technology and claim alliance loot chests every 4 hours (**Alliance Technology**).

## 4. Free rewards

Conquest idle rewards, Path of Growth missions, free hero recruits, missions and mail.

## Task order in each cycle

Farming → Alliance Help → Auto Build → Research → Troop Training → free claims. Long timers start as early as possible;
claims can wait.

## Never

- Spend gems: never tap Finish, Buy & Use, Buy Packs, Replenish All, gift packs or Speedups. Ring buttons are tapped
  only where their icon is verified, because a busy building's ring puts Finish (gems) where Upgrade normally is.
- Upgrade the Storehouse.
- Search level 1 resource tiles.
- Use Auto Shield.
