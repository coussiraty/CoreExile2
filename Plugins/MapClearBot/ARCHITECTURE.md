# MapClearBot — architecture & roadmap

A foundation for an autonomous map-clearing bot built **only** on the ExileBridge
SDK (entities, terrain, render-to-screen) + the shared `Input` helper.

## Current (skeleton)
Per-frame priority state machine in `DrawUI`:
1. **Combat** — aim at and attack the nearest valid monster within `CombatRange`
   (optional line-of-sight via the walkable grid).
2. **Loot** — move the cursor to the nearest ground `Item` and left-click.
3. **Explore** — move toward the nearest monster anywhere, else toward the
   farthest area tile (`ITerrain.TgtTiles`) as a rough outward push.

All input is throttled by `ActionDelayMs` and dispatched on the shared background
`Input` worker (never blocks the overlay render thread).

## Not done yet (next steps)
- **Pathfinding** over the walkable grid (A*) instead of click-in-direction; the
  current explore has no obstacle avoidance and can get stuck on walls.
- **Stuck detection / unstuck** (no progress over N seconds -> re-path).
- **Item filtering** (rarity/name/value) before looting; needs SDK item/label data.
- **Portals & waypoints** — open/enter portal, take waypoint, area transitions.
- **Flask/buff upkeep** (delegate to AutoPot, or integrate ILife checks).
- **Safety** — disengage on low life, avoid dangerous mods/ground effects.
- **Movement skills** (dash/blink) and smarter target selection (packs, rarity).

## SDK gaps to fill for the above
- Item rarity/name + ground label info (new component view).
- A pathfinding/terrain query helper, or expose enough of `ITerrain` for A*.
- Possibly an input "move command" abstraction if PoE2 changes movement.
