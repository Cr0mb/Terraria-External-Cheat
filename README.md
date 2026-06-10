Converted to GitHub Markdown with a cleaner README style and without BBCode-specific formatting.

<div align="center">

# <span style="color:#E02828">GVoid</span> Terraria External Trainer

### Fully external ImGui (DX11) overlay menu for Terraria 1.4.5.6

</div>

---

## About

**GVoid** is a **fully external** trainer for **Terraria 1.4.5.6**.

All functionality operates through `ReadProcessMemory` / `WriteProcessMemory` from a separate process, with a clean ImGui overlay rendered over the game.

All field offsets are resolved **by name at runtime** using Microsoft's ClrMD, allowing the trainer to continue functioning across game restarts, world reloads, and most patches without requiring manual offset updates.

---

# Features

## Player

* God Mode — immunity-based, works in all world types (Classic / Expert / Master), blocks all damage
* Infinite Health
* Infinite Mana
* Infinite Breath (no drowning)
* No Fall Damage
* Lava / Fire Immunity
* No Knockback
* Instant Respawn
* Quick actions:

  * Full Heal
  * Set Max HP
  * Set Max Mana
* Buff Applier — apply any buff by ID for any duration

---

## Movement

* Fly / Noclip (hold WASD)
* Infinite Flight (wings never run out)
* Super Speed — adjustable multiplier (velocity-driven)
* High Jump
* Water Walking
* Gravity Flip (walk on ceilings)
* Teleport to Spawn
* Waypoints:

  * Create
  * Rename
  * Teleport
  * Delete
  * Saved to config

---

## Combat

* Rapid Attack — hold Mouse1 for continuous autofire on any weapon
* Damage Multiplier — adjustable (1x–50x)
* Free Casting — spells cost 0 mana
* Kill Aura — range-gated, loot-friendly behavior
* Ghost Hit — attacks connect with nearest enemy
* Freeze Enemies
* Weaken Enemies (set to 1 HP)
* Gather Enemies
* Kill All Enemies

---

## Items

* Item Spawner — inject fully initialized item templates directly into inventory slots
* Instant Break — one-hit mining / chopping / hammering
* Extended Reach
* Infinite Items
* Item Vacuum
* Item Teleport

---

## Visuals (ESP)

* 2D Box ESP
* Snaplines
* Entity Information:

  * Name
  * Current / Max HP
  * Defense
  * Distance
* Animated rainbow boss outlines
* Independent toggles:

  * Local Player
  * Other Players
  * Town / Friendly
  * Hostile
  * Bosses
  * Dropped Items
  * Waypoints

### Colors

| Type    | Color  |
| ------- | ------ |
| Hostile | Red    |
| Town    | Cyan   |
| Player  | Orange |
| Local   | Green  |
| Item    | Yellow |

---

## World

* Freeze Time
* Time-of-day slider
* Noon / Midnight presets
* Weather control:

  * Rain
  * Clear Sky
* Toggle:

  * Blood Moon
  * Solar Eclipse
* Config save / load
* Auto-load on attach
* Live Debug HUD

---

## Usage

1. Launch Terraria and load a world
2. Run `TerrariaOverlay.exe`
3. Press `Insert` to toggle the menu
4. Configure features (settings auto-save/load)



This version stays within GitHub README conventions (`headers`, `HTML alignment`, `tables`, `shields.io button`, and centered images).
