<div align="center">

# GVoid — Terraria External Trainer

### Fully external ImGui (DX11) overlay trainer for Terraria 1.4.5.6

Name-resolved offsets · single self-contained x86 executable · no injection

</div>

---

## Preview

<div align="center">

<img src="https://i.imgur.com/XmTnGuM.png" width="95%">

<br><br>

<img src="https://i.imgur.com/JOIwhCY.png" width="95%">

</div>

---

## About

**GVoid** is a **fully external** trainer for **Terraria 1.4.5.6** (the 32-bit .NET Framework
build of the game).

Nothing is injected into the game. Every feature works by reading and writing the game's
memory from a *separate* process via `ReadProcessMemory` / `WriteProcessMemory`, while a
transparent DirectX 11 ImGui overlay is drawn on top of the window.

Field and static offsets are **resolved by name at runtime** using Microsoft's
[ClrMD](https://github.com/microsoft/clrmd) (`Microsoft.Diagnostics.Runtime`). Because the
trainer walks the live CLR heap and looks fields up by their managed names — `Player.maxRunSpeed`,
`Item.pick`, `NPC.boss`, and so on — it keeps working across game restarts, world reloads, and
most game updates **without manually re-finding a single offset**.

The entire thing ships as **one self-contained `GVoid.exe`** (~12 MB). No .NET runtime install,
no loose DLLs, no config files required on disk — native dependencies self-extract to a temp
cache on launch.

---

### External, name-based memory editing

* **`Resolver`** uses ClrMD to snapshot the running game's CLR, find `Terraria.Main` /
  `Terraria.Player` / `Terraria.Item` / `Terraria.NPC`, and record the absolute address of each
  static slot plus the relative offset of each wanted instance field. These are stored in a small
  text-serializable `Offsets` table (`S`/`P`/`I`/`N` lines).
* **`GameMemory`** is a thin RPM/WPM wrapper. Addresses are passed as **`UIntPtr`**, not `IntPtr`:
  Terraria is `LargeAddressAware`, so heap objects can live above 2 GB, and casting such an address
  to a signed `IntPtr` on x86 throws `OverflowException`.
* **`Cheats`** holds the feature state and the background worker loops that apply it.
* **`MenuOverlay`** renders the tabbed ImGui interface through
  [ClickableTransparentOverlay](https://github.com/zaafar/ClickableTransparentOverlay)
  (Vortice + ImGui.NET, DX11 borderless transparent window).

### Why x86

The overlay is built **x86** specifically so ClrMD can attach to 32-bit Terraria **in-process**.
That removes the need for a separate 64-bit helper executable to dump offsets, which is what makes
the single-file build possible. The matching bitness also means a 32-bit game pointer maps 1:1
onto a `uint` address in the trainer.

### Auto-attach

There is no "attach" screen. A background thread polls for the `Terraria` process every ~1.2 s.
When the game appears, GVoid resolves offsets, loads entity names and saved config, and flips the
banner to `● attached`. When the game closes, it returns to `○ waiting for Terraria` and re-attaches
automatically on the next launch.

### The movement-timing problem

Some movement fields (`maxRunSpeed`, `jumpSpeedBoost`, `waterWalk`, …) are **reset at the top of
the player update and consumed within the same frame**, so an external write has only a sub-millisecond
window to land. GVoid handles these with a dedicated tight `MovementLoop` that re-resolves the player
pointer every iteration, and for Super Speed it writes `velocity.X` **directly** (bypassing the
`maxRunSpeed` clamp race entirely). A **Live Debug HUD** (WORLD tab) shows live field values and the
movement-loop write rate so timing-sensitive features can be diagnosed in real time.

---

## Project structure

```
GVoid/
├─ Core/
│  ├─ GameMemory.cs     RPM/WPM wrapper (UIntPtr addressing, typed read/write helpers)
│  ├─ Offsets.cs        Resolved layout table + tiny line-based serialization
│  └─ Resolver.cs       ClrMD: name→offset resolution, entity-name dump, item spawn
├─ Overlay/
│  ├─ Program.cs        Entry point (+ `--selftest` headless resolution check)
│  ├─ MenuOverlay.cs    ImGui tabbed UI, auto-attach, ESP rendering, debug HUD
│  ├─ Cheats.cs         Feature state + background apply loops
│  ├─ OffsetClient.cs   In-process bridge to Resolver (attach / names / spawn)
│  └─ TerrariaOverlay.csproj   Single-file x86 self-contained publish settings
└─ Helper/
   └─ OffsetDump        Standalone offset-dumper (legacy two-process design)
```

---

## Building

Requires the **.NET 8 SDK** (x86 targeting).

```powershell
cd D:\GHaxLabs\Terrraria\GVoid\Overlay
dotnet publish -c Release -r win-x86 --self-contained true -o ..\dist
```

Output: `dist\GVoid.exe` — a single self-contained ~12 MB executable.

The publish profile (`TerrariaOverlay.csproj`) enables `PublishSingleFile`, `SelfContained`,
single-file compression, and `PublishTrimmed` with **`TrimMode=partial`**. Partial trimming shrinks
the .NET framework (the bulk of the size) while keeping the reflection-heavy third-party assemblies
(ClrMD, ImGui.NET) intact, so offset resolution isn't broken by the trimmer.

**Self-test the build** (verifies ClrMD resolution works inside the bundle, with Terraria running):

```powershell
.\dist\GVoid.exe --selftest
# → OK pid=#### statics=25 player=50 npc=16
```

---

## Usage

1. Launch Terraria and enter a world.
2. Run `GVoid.exe` (it auto-attaches; no setup screen).
3. Press **`Insert`** to open or close the menu.
4. Configure features across the tabs and play.

> Single-player Terraria **pauses while its window is unfocused**, so movement-related features are
> best observed with the game window focused; alt-tabbing to the overlay freezes the simulation.

---

# Features

## Player

* God Mode — immunity-based, works in all world types (Classic / Expert / Master)
* Infinite Health
* Infinite Mana
* Infinite Breath
* No Fall Damage
* Lava / Fire Immunity
* No Knockback
* Instant Respawn
* Quick Actions

  * Full Heal
  * Set Max HP
  * Set Max Mana
* Buff Applier

---

## Movement

* Fly / Noclip
* Infinite Flight
* Super Speed (velocity-direct, bypasses the clamp race)
* High Jump
* Water Walking
* Gravity Flip
* Teleport to Spawn
* Waypoints

  * Create
  * Rename
  * Teleport
  * Delete
  * Config Saved

---

## Combat

* Rapid Attack
* Damage Multiplier (1x–50x)
* Free Casting
* Kill Aura
* Ghost Hit
* Freeze Enemies
* Weaken Enemies
* Gather Enemies
* Kill All Enemies

---

## Items

* Item Spawner
* Instant Break
* Extended Reach
* Infinite Items
* Item Vacuum
* Item Teleport

---

## Visuals (ESP)

* 2D Box ESP
* Snaplines
* Entity Info (name / HP / distance)
* Animated rainbow boss outline
* Entity filters — local player, other players, town/friendly, hostile
* Color classification by entity type

---

## World

* Freeze Time
* Time Slider
* Weather Control
* Blood Moon / Solar Eclipse
* Config Save / Load
* Live Debug HUD

---

## Technical notes & constraints

* **Loot from Kill Aura** — an external process can't call the game's `NPCLoot()` / `checkDead()`,
  so Kill Aura teleports enemies onto the player and lets real weapon swings register the kill, which
  is what makes loot and coins drop normally.
* **Item Spawner** clones a template item from `ContentSamples.ItemsByType` into an empty inventory
  slot (briefly suspending the game) so the spawned item is fully valid.
* **Frequency dilution** — all movement writes share one loop, so enabling many timing-sensitive
  features at once reduces the writes-per-frame each one gets. The Live Debug HUD shows the current
  write rate.
* **God Mode + healing** are intentionally kept separate — God Mode is full damage immunity, not an
  auto-heal.


Not affiliated with Re-Logic or Terraria.
