# CoreExile2

CoreExile2 is a **Path of Exile 2** overlay built on the GameHelper2 engine: a Windows x64 .NET
application that reads data from the running game, renders an ImGui / ClickableTransparentOverlay
UI on top of it, and loads feature plugins from the runtime `Plugins` directory.

It ships with a curated set of quality-of-life and automation plugins, a self-contained
**ExileBridge** plugin SDK, and a `Launcher` that auto-updates the overlay and plugins from GitHub
releases.

> ⚠️ Overlays that read game memory can violate the game's Terms of Service. Use at your own risk.

---

## Features

- **Self-updating launcher** — `Launcher.exe` checks GitHub releases and updates the overlay and
  individual plugins in place.
- **ExileBridge SDK** — a pure-interface plugin API (`ExileBridge.dll`). Plugins reference only the
  bridge and never touch native memory directly; the host implements everything behind clean
  interfaces (game/entities/UI/render/events/world/inventory).
- **Patch-resilient core** — offsets isolated in `GameOffsets`, with memory reads hardened against
  the torn reads that are normal when scanning a live process.

## Plugins

| Plugin | What it does |
| --- | --- |
| **Radar** | Terrain/walkability map, entity dots, and configurable item/monster highlighting. |
| **HealthBars** | Life / energy-shield bars drawn over monsters and allies. |
| **Atlas** | Endgame Atlas overlay — node info, content tags, and map pathfinding helpers. |
| **StashValue** | Prices every item in an open stash/inventory and overlays the value on each slot. Resolves each item's **real localized name** from the game's `BaseItemTypes` table and prices it from **poe.ninja + poe2scout** (merged, poe.ninja authoritative). Auto-resolves the league name and exchange rates. |
| **MapKillCounter** | Tracks kills (and kills/hour) for the current map. |
| **AutoAim** | Aims skills at the best nearby target, with optional **filter-based auto-pickup** (pick currencies, uniques, gems, etc. by name/category). |
| **AutoPot** | Automatic life/mana flask use at configurable thresholds. |
| **AutoHotKeyTrigger** | Rule-based automation — fire flasks/skills/keys when conditions (life, ailments, buffs, nearby monsters…) are met. |
| **CustomHotkeys** | User-defined hotkey macros and key remaps. |
| **FollowBot** | Follows a party leader through the map. |
| **MapClearBot** | Automated map clearing: A\* pathfinding, reachability-based exploration, combat, loot, and stuck/flee recovery. |
| **RunecraftHelper** | Prices Expedition / runecraft rewards (reads the game's `BaseItemTypes` table for language-independent matching). |
| **SekhemaHelper** | Helper overlay for the Trial of the Sekhemas. |
| **DebugOverlay** | UI-element / memory inspector for development. |
| **WorldDrawing** | World-space drawing utilities (developer tool). |
| **ExileBridgeSample** / **SamplePluginTemplate** | Starting points for writing your own ExileBridge plugins. |

## ExileBridge SDK (writing plugins)

Plugins target `net10.0-windows` and reference **only** `ExileBridge.dll` (plus `ImGui.NET` and
`Newtonsoft.Json`). Derive from `Plugin<TSettings>` and use the `Ctx` services:

- `Ctx.Game` — game/area state, player, input.
- `Ctx.Entities` — nearby entities and their components (mods, stack, rarity, ground items…).
- `Ctx.Ui` — UI elements, open panels, and `EnumerateOpenItemSlots()` (priced-ready stash/inventory
  items with `DisplayName`, `Path`, `Rarity`, `StackCount`, `ModLines`).
- `Ctx.Render` / `Ctx.Overlay` / `Ctx.Events` / `Ctx.Log`.

See `Plugins/ExileBridgeSample` and `Plugins/SamplePluginTemplate` for working examples.

---

## Building from source (Visual Studio)

### Required tools

- [Visual Studio](https://visualstudio.microsoft.com/downloads/) (Community is enough) with the
  **.NET desktop development** workload.
- [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (the SDK,
  not just the runtime).

### Project settings

- Solution: [`GameOverlay.sln`](GameOverlay.sln)
- Target framework: `net10.0-windows`, runtime identifier `win-x64` (output is x64 even though the
  VS platform shows `Any CPU`).
- Main app: [`GameHelper/GameHelper.csproj`](GameHelper/GameHelper.csproj) ·
  Launcher: [`Launcher/Launcher.csproj`](Launcher/Launcher.csproj)

### Build & run

1. Open [`GameOverlay.sln`](GameOverlay.sln) (open the **solution**, not a single project, so the
   launcher and plugins get copied to the output).
2. Allow NuGet restore if prompted.
3. Select `Release`, then `Build > Rebuild Solution`.
4. The runnable app is produced in:
   ```text
   GameHelper\bin\Release\net10.0-windows\win-x64\
   ```
5. Run **`Launcher.exe`** from that folder (accept the administrator prompt). The launcher prepares
   and starts `GameHelper.exe`.

If the game runs as administrator, run the overlay as administrator too (its manifest already
requests elevation).

> Always `Rebuild Solution` — plugin projects and the launcher rely on MSBuild copy steps to place
> their DLLs/assets into the GameHelper output `Plugins\` folder.

### Runtime configuration

Settings are written next to the executable at runtime and are ignored by Git:

```text
configs\core_settings.json
configs\plugins.json
Plugins\<PluginName>\config\
```

## Troubleshooting

- **"The current .NET SDK does not support targeting .NET 10.0"** — install the .NET 10 SDK, update
  Visual Studio, restart, and reopen the solution.
- **Build succeeded but plugins are missing** — use `Build > Rebuild Solution`, not `Build Project`.
- **"Launcher says GameHelper.exe was not found"** — run `Launcher.exe` from
  `GameHelper\bin\<Configuration>\net10.0-windows\win-x64\`, not from `Launcher\bin\...`.
- **Overlay does not attach** — run the overlay at the same privilege level as the game.

## Writing your own plugin

CoreExile2 has a self-contained plugin SDK (**ExileBridge**). See
[PLUGIN_GUIDE.md](PLUGIN_GUIDE.md) for the full guide: the services exposed
(`Ctx.Game`, `Ctx.Entities`, `Ctx.Ui`, …), entity components, the input/drawing helpers,
and complete copy-paste examples.

## Credits

Built on the open-source GameHelper / GameOffsets engine. Pricing data from
[poe.ninja](https://poe.ninja) and [poe2scout](https://poe2scout.com).

Several bundled plugins are **adaptations of other people's work**, ported to run on this
fork's ExileBridge SDK. Full credit to the original authors — the ideas/implementations are
theirs; only the SDK wiring was changed here:

- **StashValue** — based on [zx0CF1/StashValue](https://github.com/zx0CF1/StashValue)
- **MapKillCounter** — based on [MordWraith/MapKillCounter](https://github.com/MordWraith/MapKillCounter)
- **RunecraftHelper** — based on [yokkenUA/RunecraftHelper](https://github.com/yokkenUA/RunecraftHelper)
- **SekhemaHelper** — based on [yokkenUA/SekhemaHelper](https://github.com/yokkenUA/SekhemaHelper)

If you are one of these authors and want different/extended credit, or want your plugin
removed, please open an issue — happy to adjust.
