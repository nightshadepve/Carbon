# NightshadeLocker

Original Nightshade plugin for locks, automatic lock placement, and automatic closing. The player command is `/nslock`. `/nsl` is an alias. Both are registered only when they are not one of the other locker plugin's command names.

Data is stored at `carbon/data/Nightshade/NightshadeLocker/State.json`. Backups from an import go in `carbon/data/Nightshade/NightshadeLocker/Backups/`. The plugin does not write settings into its source file.

## How the theme follows the shop

NightshadeShop does not expose a theme API, so NightshadeLocker does not call the shop and does not modify it. On each panel open it reads `carbon/configs/NightshadeShop.json`:

- `Theme mode (Shop, Auto, or a theme name)` in NightshadeLocker defaults to `Shop`.
- In Shop mode the locker uses the shop file's `Theme Mode (Auto or a theme name)` and its `Themes` list.
- Auto uses the same date rule as NightshadeShop: a theme is active when today's `MMDD` is inside `Active From` / `Active To`. If the window wraps past New Year, either side matches. The highest `Priority` wins. On 7 November that selects Autumn, not the year-round Nightshade theme, because Autumn's priority is higher.
- A theme name in the locker config forces that theme.
- If the shop file is missing or invalid, the panel uses the built-in Nightshade palette (`#1A1024` background, `#8B3FBFFF` accent, and the rest of the shop's Nightshade colors).

The logo is read from `Logo URL` in `carbon/configs/NightshadeInfo.json`. If that file cannot be read, the fallback is the Nightshade logo URL already used by NightshadeInfo and NightshadeHud.

Version 1.1.0 draws a centered 700 by 448 window. The header is 46 pixels with a 28 pixel logo, a 16 pixel title, and a small close control. Four text tabs (Locks, Places, Closing, Code) sit under a 2 pixel accent line. Rows are 26 pixels with a 34 by 14 switch. Section labels are 10 pixel muted capitals. Places lists every category, including ones the server has turned off, and those rows are not clickable. The code tab has two 4 digit fields. Colors still come from the shop theme, with the Nightshade purple set as the fallback.

## Permissions

The plugin registers these names and does not grant them:

| Existing grant seen in oxide groups | New permission |
| --- | --- |
| `ultimatelocker.use` on `default`, `default_unlocked`, `vip_base(nouser)` | `nightshadelocker.use` |
| `ultimatelocker.autolock.enabled` on those same groups | `nightshadelocker.autolock` |
| `ultimatelocker.autolock.nolockrequired` on `vip_1` and `vip_base(nouser)` | `nightshadelocker.autolock.free` |
| `ultimatelocker.autoclosing.enabled` on `vipx` | `nightshadelocker.autoclose` |
| `ultimatelocker.autoclosing.nodoorcloserrequired` on `vipx` | `nightshadelocker.autoclose.free` |
| `ultimatelocker.admin` (not present on a group in this data) | `nightshadelocker.admin` |
| `ultimatelocker.bypass.lock_deploy_vehicle_check` | `nightshadelocker.bypass.vehicle` |
| `ultimatelocker.bypass.force` | `nightshadelocker.bypass.force` |
| `ultimatelocker.bypass.dlc_skin_required` | `nightshadelocker.bypass.dlc` |

`vip_1` is titled VIP and `vipx` is titled VIPx in the separate NightshadeLockPickup notes. Confirm parents with `c.group show vip_1` before copying grants. Suggested commands, which this plugin does not run:

```
c.group addperm default nightshadelocker.use
c.group addperm default nightshadelocker.autolock
c.group addperm default_unlocked nightshadelocker.use
c.group addperm default_unlocked nightshadelocker.autolock
c.group addperm vip_base(nouser) nightshadelocker.use
c.group addperm vip_base(nouser) nightshadelocker.autolock
c.group addperm vip_base(nouser) nightshadelocker.autolock.free
c.group addperm vip_1 nightshadelocker.autolock.free
c.group addperm vipx nightshadelocker.autoclose
c.group addperm vipx nightshadelocker.autoclose.free
```

Every chat command, console command, and panel button checks a permission again. `nightshadelocker.admin` is required for admin actions.

## Free locks and pickup

A lock is free only when this plugin creates it without taking an item, and the record written at that moment says `free-grant`. A later permission or toggle change does not rewrite that record.

- A locked lock cannot be picked up. The hook returns false before the game's own locked check.
- An unlocked lock this plugin did not mark free uses the normal pickup, which creates one matching item.
- An unlocked free lock is destroyed and the hook returns a non-null value so the game does not also create an item.
- If a paid item cannot be given and cannot be dropped, the removal stops or the item is marked owed on that player and delivered later. The plugin does not delete a paid item it cannot account for.
- Destroying a lock by raiding or entity removal does not create a refund.

`nslock admin remove norefund` and `removeall <steamId> confirm norefund` are explicit admin destruction. They are not inferred from a permission.

## Switch-over

NightshadeLocker does not unload UltimateLocker, delete its files, or edit groups.

1. Add the new permissions. Leave the old plugin loaded while you do this.
2. As an admin, run `nslock admin import`. It reads `carbon/data/UltimateLocker.json`, copies the current NightshadeLocker state into `Backups` when that state already exists, and imports player toggles and pins. Running it again does not duplicate players. It does not delete the legacy file. It does not import placed-lock rows, because those rows have no free/paid marker.
3. Unload the old plugin yourself: `c.unload UltimateLocker`.
4. Reload NightshadeLocker if it was already loaded: `c.reload NightshadeLocker`.
5. Run `nslock admin status`. Entity control is on only when the status says the conflict pause is no.

While UltimateLocker is loaded, NightshadeLocker does not place, remove, close, or block locks. The check runs at startup and every 10 seconds. The panel still opens and says that it is paused.

Do not load NightshadeLockPickup with NightshadeLocker. That proposed patch (PR 10) prefixes UltimateLocker's pickup hook. This plugin has its own pickup rules and does not reference that patch. Loading both is unnecessary, and the pickup patch is aimed at the other plugin.

## Version 1.1.0 options

New config keys, all honored:

- `Replace another player's vehicle ownership when a lock is allowed` (default true, matching this server's previous locker config). It changes OwnerID only after a lock is allowed on that vehicle, and it does not replace a teammate.
- `Block wires on a locked electrical entity`
- `Block RF frequency changes on a locked broadcaster or receiver`
- `Block harvesting and cloning on a locked planter`
- `Block instruments and boom boxes that are locked`
- `Block turret id, rotate, and mode changes`
- `Allow pushing a locked vehicle` (default true)
- `Allow pushing a locked vehicle inside your building privilege` (default true)

Categories added, off by default to match this server's disabled deployable list: electrical, industrial, crafting stations, farming, instruments and lights, quarries and pump jacks. Short prefab names for the public entity list map into those categories, so turning a category on covers that group. Extra entity prefabs can still name one prefab.

`/nslock show` tells the owner the code in their own chat. `nslock admin show` does the same for an admin looking at a lock. Neither command writes the code to the server log. Streamer mode refuses the player show command.

Raid damage is not blocked.

## Commands

Players, with `nightshadelocker.use`:

- `/nslock` or `/nsl` opens the panel.
- `/nslock toggle` and `/nslock closetoggle` flip the master switches. Those also need `nightshadelocker.autolock` or `nightshadelocker.autoclose`.
- `/nslock place`, `/nslock closer`, `/nslock uncloser` act on the entity you are looking at.
- `/nslock code ####`, `/nslock codeall ####`, `/nslock guest ####`, `/nslock delay <seconds>`.

The reply never repeats the code. The panel shows the saved code to that player unless streamer mode is on. Codes are not written to the server log by this plugin. They are stored in the data file because new locks need them.

Admins, with `nightshadelocker.admin`:

- `nslock admin status`
- `nslock admin grants`
- `nslock admin import`
- `nslock admin lock`, `unlock`, `remove`, `code ####`
- `nslock admin codeall <steamId> ####`
- `nslock admin lockall <steamId>`, `unlockall <steamId>`
- `nslock admin removeall <steamId> confirm`

There is no command that prints a code.

## Defaults taken from this server's settings

Server lock categories match `carbon/configs/UltimateLocker.json`: doors, boxes, storage, lockers, cupboards, vehicles, naval, stashes, and the two "other" categories are on. Windows, medieval, furnaces, vending, farming, turrets, traps, racks, neon, and signs are off. All eight closing types are off. New players start with automatic placement off, doors, lockers, cupboards, vehicles, and boats on, and team share on. The enabled vehicle and boat prefabs are the ones enabled in that config. Deployable rows in that file were all disabled, so furnaces and the other typed categories stay off until an admin turns the category on.

Building privilege is required for structures. Unowned vehicles can be locked and claimed. A vehicle that already has another owner is not locked and its owner is never replaced, even if the ignored ownership option is turned on.

## Sources and originality

Used:

- The public Codefling page for UltimateLocker 2.1.5 (feature list, permissions, commands, and the sample configuration). Maintenance status on that page was not treated as verified.
- This server's `carbon/configs/UltimateLocker.json` and the field names in `carbon/data/UltimateLocker.json`. Pins in that file were not copied into this document or into logs.
- Direct permission strings in `carbon/data/oxide.groups.data`.
- NightshadeShop theme selection and NightshadeInfo / NightshadeHud logo URL.
- Carbon 2.0.262 and the patched Rust `Assembly-CSharp.dll` in this repository, plus lock placement already used by this server's own Convoy and RaidableBases plugins.

Not used: `UltimateLocker.cs`, its history, its language files, and any decompiled third-party locker. NightshadeLockPickup was not copied.

## Checklist

Implemented: code and key locks, auto placement, manual place, per-category toggles for every public category plus electrical, industrial, crafting, farming, instruments, and quarries, guest code, team share, streamer mode, key fallback, dismount lock, Flight Control skin when owned, door closing types and delay, proximity button, wires, RF frequency, harvest and clone, boom box, weapon rack mount, turret id rotate and mode, quarry toggle and gather, elevator, car lift, engine start, horse lead, vehicle push flags, vending admin, phone dial, CCTV aim, conveyor filters, building privilege, unowned vehicle claim, other-owner vehicle lock when that config is on, ownership replace when the lock is allowed, private code show, import, conflict pause, paid and free provenance, locked pickup block.

Not implemented because the game does not give a safe hook for it here: blocking a turret or SAM from shooting (that would stop base defense, and raid damage stays vanilla), and a perfect in-world "use" key for every piano or drum if the boom-box and loot hooks do not see that entity. Those entities can still take a lock when their category is on.

Not live tested: placement, the in-game panel, and each new hook on a running server.

## Checks

`tests/compile-nightshadelocker.sh` compiles the plugin with `CARBON`, `RUST`, `OXIDE_PUBLICIZED`, and `UNIX` against Carbon.dll, Carbon.Common.dll, and the patched Assembly-CSharp.dll. UnityEngine modules and Facepunch.System were not in the repository, so the harness builds small reference assemblies for those missing dependencies only. That compile is not a running server.

`tests/NightshadeLocker.Tests` runs the decision checks with no game assemblies: paid versus free, the free permission ignored, full inventory, locked pickup, ownership, toggles, timer cleanup, theme dates, and a repeatable import. Those checks are not in-game validation.

No Rust server was started for this change. Placement, door closing, the panel, DLC skin ownership, and clan sharing still need a live pass.
