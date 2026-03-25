<div align="center">
  <a href="https://swiftlys2.net/docs/" target="_blank">
    <img src="https://github.com/user-attachments/assets/d0316faa-c2d0-478f-a642-1e3c3651f1d4" alt="SwiftlyS2" width="780" />
  </a>
</div>

<div align="center">
  <a href="./README.en.md"><img src="https://flagcdn.com/48x36/gb.png" alt="English" width="48" height="36" /> <strong>English</strong></a>
  &nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;
  <a href="./README.md"><img src="https://flagcdn.com/48x36/cn.png" alt="中文" width="48" height="36" /> <strong>中文版</strong></a>
</div>

<hr>

# HZP_DarkFog

A **SwiftlyS2** plugin for CS2 per-player exposure control.  
It uses the **HanZombiePlague API** to detect human/zombie state and zombie class, then applies player-specific `post_processing_volume` entities so each player can see different brightness/exposure.

Designed for production zombie servers, with support for:

- separate human vs zombie exposure
- class-based zombie exposure override groups
- admin command for forced target exposure (persistent override)
- hidden silent command for one-shot self exposure effects

---

## Credit :

<div style="display:flex; align-items:center; gap:6px; flex-wrap:wrap;">
  <span>Powered by yumiai :</span>
  <a href="https://yumi.chat:3000/">
    <img src="https://yumi.chat:3000/logo.png" width="50" alt="yumiai logo">
  </a>
  <span>(AI model provider)</span>
</div>

<div style="display:flex; align-items:center; gap:6px; flex-wrap:wrap;">
  <span>SwiftlyS2-Toolkit & agents By laoshi :</span>
  <a href="https://github.com/2oaJ">
    <img src="https://github.com/user-attachments/assets/2da5deb4-2be9-4269-8f8e-df0029bb7c91" width="50" alt="toolkit logo">
  </a>
  <span>(open-source SwiftlyS2 Skills & agents)</span>
</div>

<div style="display:flex; align-items:center; gap:6px; flex-wrap:wrap;">
  <span>SwiftlyS2-mdwiki By LynchMus :</span>
  <a href="https://github.com/himenekocn/sw2-mdwiki">
    <img src="https://github.com/user-attachments/assets/c7f3b4ca-629a-4df9-a405-3f1a7507ecf2" width="50" alt="mdwiki logo">
  </a>
  <span>(open-source SwiftlyS2 mdwiki)</span>
</div>

---

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/Z8Z31PY52N)

---

## Feature Overview

- Per-player exposure control with isolated view effect.
- Human/zombie detection comes from `HanZombiePlague` API, not CT/T team fallback.
- `ZombieGroups` supports class-specific exposure overrides by `ZombieClassName`.
- Global defaults: `HumanExposure` and `ZombieExposure`.
- Admin command (default `fog`) can set/reset exposure for any target.
- Hidden command (default `hauhdahsdasd`) applies exposure silently to sender only.
- Hot config update: rebuilds class cache, re-registers commands, reapplies exposure.
- Proper cleanup on disconnect/map unload/plugin unload.

## Exposure Decision Priority

Applied in this order:

1. `Enable = false`: remove player exposure entity (restore default)
2. Admin manual override exists (set by admin command and not reset yet)
3. API says human: use `HumanExposure`
4. API says zombie:
   - if zombie class matches an enabled `ZombieGroups` item, use group exposure
   - otherwise use `ZombieExposure`

Hidden command exposure is **one-shot immediate apply** and is not stored in admin override dictionary.  
So it can be overwritten by later re-apply events (spawn/infect/class-change/map lifecycle, etc).

## Commands

### Admin command

- Default name: `fog`
- Source field: `AdminCommandName`
- Permission: `AdminCommandPermission`

Usage:

```text
!fog <player-name|playerid|@me> <exposure|reset>
```

Examples:

```text
!fog H-AN 0.45
!fog 12 1.0
!fog @me reset
```

Notes:

- `reset/clear/off` are all treated as reset.
- Supports PlayerID, exact name, and fuzzy name matching.
- Ambiguous fuzzy result is blocked to avoid wrong target write.

### Hidden command (silent)

- Switch: `HiddenExposureCommandEnabled`
- Name: `HiddenExposureCommandName`
- Intended use: called secretly by another plugin (for temporary item effects, etc.)

Usage:

```text
!<HiddenExposureCommandName> <exposure|reset>
```

Behavior:

- sender must be a valid in-game player
- fully silent, no chat output at all
- immediate apply only, no persistent override write

## Lifecycle Behavior (Current Code Behavior)

- `player_spawn`: re-apply this player after 0.15s
- `player_team`: re-apply this player after 0.15s
- ZP infection event `HZP_OnPlayerInfect`: re-apply infected player after 0.1s
- ZP special-role events (mother/nemesis/assassin/hero/survivor/sniper): re-apply target after 0.1s
- ZP game-start state event `HZP_OnGameStart`: re-apply all after 0.2s
- map load `OnMapLoad`: re-apply all after 1.0s
- map unload `OnMapUnload`: clear all exposure volumes + clear admin override dictionary
- client disconnect `OnClientDisconnected`: clear that player's volume + remove admin override
- plugin unload: unregister commands, detach API events, clear all runtime state

## Dependencies

- Required: SwiftlyS2 runtime
- Required: `HanZombiePlague` shared interface (key: `HanZombiePlague`)
- Compile reference: `API/HanZombiePlagueAPI.dll`

Current version treats HanZombiePlague as a hard dependency and throws if missing.

## Configuration

- File: `HZP_DarkFog.jsonc`
- Root section: `HZP_DarkFogCFG`

### Global fields

| Field | Type | Description |
|------|------|------|
| `Enable` | bool | Global plugin switch |
| `HumanExposure` | float | Default exposure for human |
| `ZombieExposure` | float | Default exposure for zombie (when no class group matches) |
| `AdminCommandName` | string | Admin command name, default `fog` |
| `AdminCommandPermission` | string | Permission required for admin command |
| `HiddenExposureCommandEnabled` | bool | Enable/disable hidden command |
| `HiddenExposureCommandName` | string | Hidden command name |
| `ZombieGroups` | array | Class-based zombie exposure override list |

### `ZombieGroups` item

| Field | Type | Description |
|------|------|------|
| `Enable` | bool | Item switch |
| `ZombieClassName` | string | Zombie class name from API (case-insensitive match) |
| `Exposure` | float | Exposure value for this class |

### Example config

```jsonc
{
  "HZP_DarkFogCFG": {
    "Enable": true,
    "HumanExposure": 0.01,
    "ZombieExposure": 0.01,

    "AdminCommandName": "fog",
    "AdminCommandPermission": "admin.dex",

    "HiddenExposureCommandEnabled": true,
    "HiddenExposureCommandName": "PPk0wBpK0m8W0sOi",

    "ZombieGroups": [
      { 
        "Enable": true,
        "ZombieClassName": "轻型丧尸",
        "Exposure": 1.0 
      }
    ]
  }
}

```

## Installation

1. Deploy plugin files to your server plugin directory.
2. Ensure `HanZombiePlague` is loaded first and exports the shared interface.
3. Start server once, then edit/check `HZP_DarkFog.jsonc`.
4. Set `AdminCommandPermission` according to your permission system.
5. Validate admin command and hidden silent command behavior in-game.

## Pre-release Validation Checklist

1. Verify baseline human/zombie exposure switching.
2. Verify `ZombieGroups` class override works when switching between zombie classes.
3. Verify admin `fog` override persists across respawn (until reset/disconnect/map change).
4. Verify hidden command stays silent and gets overridden by normal re-apply lifecycle.
5. Verify fuzzy multi-match is blocked correctly.
6. Verify no exposure entity residue after map change.
7. Verify hot config change immediately updates commands, permissions, and group mapping.

