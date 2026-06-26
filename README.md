# CoreExile2

A **Path of Exile 2** overlay built on the GameHelper2 engine. It reads the running game,
renders an ImGui overlay on top of it, and loads feature plugins at runtime. CoreExile2 ships
with a curated set of quality-of-life and automation plugins, a self-contained **ExileBridge**
plugin SDK, and a launcher that keeps everything up to date.

> ⚠️ **Disclaimer.** Overlays that read game memory can violate the game's Terms of Service.
> Use at your own risk.

## Highlights

- **Self-updating launcher** — `Launcher.exe` updates the overlay and individual plugins in
  place from GitHub releases.
- **ExileBridge SDK** — a pure-interface plugin API. Plugins reference only `ExileBridge.dll`
  and never touch game memory directly; the host implements everything behind clean interfaces.
- **Patch-resilient core** — memory offsets isolated in `GameOffsets`, with reads hardened
  against the torn reads that are normal when scanning a live process.

## Plugins

| Plugin | What it does |
| --- | --- |
| **Radar** | Terrain/walkability map, entity dots, configurable item/monster highlighting. |
| **HealthBars** | Life / energy-shield bars over monsters and allies. |
| **Atlas** | Endgame Atlas overlay — node info, content tags, map pathfinding helpers. |
| **StashValue** | Prices every item in an open stash/inventory, on each slot. Resolves the item's real localized name from the game's `BaseItemTypes` table and prices it from **poe.ninja + poe2scout** (merged). Auto-resolves league and exchange rates. |
| **MapKillCounter** | Kills (and kills/hour) for the current map. |
| **AutoAim** | Aims skills at the best nearby target, with optional filter-based **auto-pickup** (currencies, uniques, gems… by name/category). |
| **AutoPot** | Automatic life/mana flask use at configurable thresholds. |
| **AutoHotKeyTrigger** | Rule-based automation — fire flasks/skills/keys on conditions (life, ailments, buffs, nearby monsters…). |
| **CustomHotkeys** | User-defined hotkey macros and key remaps. |
| **FollowBot** | Follows a party leader through the map. |
| **MapClearBot** | Automated map clearing: A\* pathfinding, reachability-based exploration, combat, loot, stuck/flee recovery. |
| **RunecraftHelper** | Prices Expedition / runecraft rewards (language-independent matching via `BaseItemTypes`). |
| **SekhemaHelper** | Helper overlay for the Trial of the Sekhemas. |
| **DebugOverlay** | UI-element / memory inspector (developer tool). |
| **WorldDrawing** | World-space drawing utilities (developer tool). |
| **ExileBridgeSample** · **SamplePluginTemplate** | Starting points for your own plugins. |

## Getting started

1. Grab the latest [release](https://github.com/coussiraty/CoreExile2/releases) — or build from
   source (see **[BUILD.md](BUILD.md)**).
2. Run **`Launcher.exe`** and accept the administrator prompt. If the game runs as administrator,
   run the overlay as administrator too.

The launcher prepares and starts the overlay, and keeps it and the plugins updated.

## Documentation

- **[BUILD.md](BUILD.md)** — build from source in Visual Studio, runtime layout, troubleshooting.
- **[PLUGIN_GUIDE.md](PLUGIN_GUIDE.md)** — write your own plugin: the ExileBridge SDK, services,
  entity components, helpers, and worked examples.

## Credits

Built on the open-source GameHelper / GameOffsets engine. Pricing data from
[poe.ninja](https://poe.ninja) and [poe2scout](https://poe2scout.com).

Several bundled plugins are **adaptations of other people's work**, ported to this fork's
ExileBridge SDK — full credit to the original authors; only the SDK wiring was changed here:

| Plugin | Original author |
| --- | --- |
| StashValue | [zx0CF1/StashValue](https://github.com/zx0CF1/StashValue) |
| MapKillCounter | [MordWraith/MapKillCounter](https://github.com/MordWraith/MapKillCounter) |
| RunecraftHelper | [yokkenUA/RunecraftHelper](https://github.com/yokkenUA/RunecraftHelper) |
| SekhemaHelper | [yokkenUA/SekhemaHelper](https://github.com/yokkenUA/SekhemaHelper) |

If you are one of these authors and want different/extended credit, or want your plugin removed,
please open an issue — happy to adjust.
