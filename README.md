# GVoid — Terraria External Trainer

An **external** (out-of-process) cheat menu for **Terraria 1.4.5.6** with a DirectX 11
ImGui overlay. Nothing is injected into the game — GVoid reads and writes the game's
memory from a separate process via `ReadProcessMemory` / `WriteProcessMemory`, and
resolves all field offsets **by name** at runtime using ClrMD, so it survives game
restarts, world reloads, and most patches.

> Single-player / educational use. Built as a reverse-engineering exercise.

---

## Layout

```
GVoid/
├─ Core/        Shared sources (no project of its own; compiled into both apps)
│   ├─ GameMemory.cs   RPM/WPM wrapper (UIntPtr addressing for >2GB heap objects)
│   ├─ Offsets.cs      Serializable offset table (statics / Player / Item / NPC / Misc)
│   └─ Resolver.cs     ClrMD name-based offset resolution + item-spawn + diagnostics
├─ Helper/      OffsetDump — x86 .NET Framework 4.8 console helper (uses ClrMD)
│   └─ OffsetDump.csproj
└─ Overlay/     TerrariaOverlay — x64 .NET 8 ImGui/DX11 borderless overlay (the menu)
    └─ TerrariaOverlay.csproj
```

### Why two processes (the bitness split)

* `Terraria.exe` is a **32-bit (x86) .NET Framework** assembly (`CorFlags 0x3`,
  ILONLY + 32BITREQUIRED). It is **not** native code — Ghidra/IDA are the wrong tools;
  it was reverse-engineered with **dnSpy / ILSpy**.
* **ClrMD must run as x86** to attach to a 32-bit process's CLR (DAC must match
  bitness). So offset resolution lives in the small **x86 `Helper`** (`OffsetDump.exe`).
* The **ImGui + DX11 overlay** uses `ClickableTransparentOverlay`, which is modern
  .NET, so the **`Overlay`** runs as **x64**. An x64 process can RPM/WPM a 32-bit
  target across WOW64 without issue.
* The overlay launches the bundled x86 helper, reads back the resolved offset table,
  then does all its own memory I/O.

---

## Build & Run

Requirements: .NET 8 SDK (x64) + .NET Framework 4.8 targeting pack. Both restore
NuGet packages on first build (ClrMD, ClickableTransparentOverlay).

```powershell
# 1. Build the x86 helper
cd GVoid\Helper
dotnet build -c Release

# 2. Build the x64 overlay
cd ..\Overlay
dotnet build -c Release

# 3. Bundle the helper next to the overlay (the overlay looks for x86helper\OffsetDump.exe)
$ov = "bin\Release\net8.0-windows\win-x64"
New-Item -ItemType Directory -Force "$ov\x86helper" | Out-Null
Copy-Item ..\Helper\bin\Release\* "$ov\x86helper" -Recurse -Force

# 4. Launch Terraria, load a world, then run the overlay
.\bin\Release\net8.0-windows\win-x64\TerrariaOverlay.exe
```

In-game:
1. Press **Insert** to toggle the menu.
2. Click **Attach to Terraria** (run Terraria in **borderless/windowed**, not exclusive
   fullscreen, or the overlay can't draw over it).
3. Toggle features. Status (HP/Mana/position) shows in the footer.

---

## Features

**Player** — God Mode, Infinite Health / Mana / Breath / Flight, No Knockback,
Instant Respawn, Rapid Attack (hold M1 autofire), Fly/Noclip (WASD), Gravity Flip,
Damage Multiplier, Item Vacuum, Full Heal, Max HP/Mana, Teleport to Spawn, Item Teleport.

**Combat** — Freeze Enemies, Weaken Enemies, Kill Aura (range, loot-friendly),
Ghost Hit, Gather Enemies, Kill All Enemies.

**Visuals** — 2D Box ESP, Snaplines, dropped-item ESP, HP numbers
(red = hostile, cyan = town/friendly, green = you, gold = item).

**World** — Freeze Time, time-of-day slider, weather (rain / clear), Blood Moon, Eclipse.

**Spawner** — full item-template spawner (works instantly, no drop/pickup).

---

## Notable engineering details

* **Addressing:** Terraria is LargeAddressAware; GC objects can live **above 2 GB**.
  Addresses are passed as `UIntPtr` — a `uint`→`IntPtr` cast throws on x86 for >2 GB.
* **GC-stable resolution:** statics never move, so their absolute addresses are
  resolved once. Per-entity reads re-walk `static array → [myPlayer] → object → +offset`
  every tick. x86 SZArray layout: data @ +8, reference element size 4.
* **God Mode** is immunity-based (`immune` + `immuneTime` + `hurtCooldowns[]`), because
  `creativeGodMode` is wiped every frame by `ResetEffects()` outside Journey mode.
* **Item Spawner** copies a fully-initialized template from
  `ContentSamples.ItemsByType` into the slot (done under a brief ClrMD suspend), so the
  item has all stats immediately — no drop/pickup needed.
* **Loot from Kill Aura:** external code can't call `NPCLoot()`/`checkDead()`, and a
  forced despawn drops nothing. Instead the aura **teleports hostiles onto you** so your
  *real* weapon swing runs the game's own `StrikeNPC → NPCLoot` path → full loot+coins.
  Pair with Rapid Attack + Damage Multiplier + Item Vacuum for an auto-farm.
* **ESP projection** uses Terraria's actual `SpriteViewMatrix` transform
  (`screen = zoom·(world − screenPos) − (screenSize/2)·(zoom−1)`), read + projected on
  the render thread each frame so boxes don't lag/snap.

---

## Caveats

* Single-player only by design.
* Requires borderless/windowed Terraria and matching resolution for correct ESP.
* Some world/event toggles (weather, blood moon) can be overridden by the game's own
  logic over time — they nudge state, they don't lock it.
