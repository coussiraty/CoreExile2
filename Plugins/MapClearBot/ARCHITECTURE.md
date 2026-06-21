# MapClearBot — architecture & roadmap

An autonomous map-clearing bot built **only** on the ExileBridge SDK (entities,
terrain, render-to-screen) + the shared `Input` helper.

## Implemented
Per-frame priority state machine in `DrawUI` (throttled by `ActionDelayMs`;
bookkeeping/drawing runs every frame):
1. **Flee** — when life% ≤ `FleeLifePercent`, run directly away from the nearest
   threat instead of fighting.
2. **Combat** — attack the best monster in `CombatRange`, prioritised by rarity
   then proximity, with optional grid line-of-sight.
3. **Loot** — walk-click the nearest ground `Item` in `LootRange`, optional
   metadata-path substring filter (`LootFilter`).
4. **Explore** — frontier search (`Pathfinder.TryNearestFrontier`) finds the
   nearest unexplored coarse cell; **A\*** (`Pathfinder.FindPath`) over the
   walkable grid builds a real path; the bot walks it via a lookahead waypoint
   (no wall-clipping). Visited cells are tracked so the whole map gets covered.
5. **Transition** — when no frontier remains and `GoToTransitionWhenCleared` is
   set, path to the nearest area-transition tile (`ITerrain.TgtTiles`).

Robustness:
- **A\*** and **frontier BFS** are bounded by `MaxPathNodes` so a frame can't hang.
- **Stuck detection** — no progress for `StuckSeconds` ⇒ abandon the current
  frontier (mark a block explored) and re-path.
- Path recompute throttled by `PathRecomputeMs`; exploration resets on area change.
- Optional on-screen **path/target debug draw** (`ShowPath`).
- All synthetic input runs on the shared background `Input` worker.

## Not done yet (next steps)
- **Item filtering by rarity/value** (needs item rarity/name in the SDK; only a
  path substring is available today).
- **Portals & waypoints** — open/enter portal, take waypoint, full area progression
  loop (the transition step only walks to the tile).
- **Flask/buff upkeep** — delegate to AutoPot or read `ILife` here.
- **Movement skills** (dash/blink) and smarter combat (kiting, AoE positioning).
- **Smoother path following** (string-pulling / funnel) and dynamic obstacle
  avoidance for doors/blockages (`ITriggerableBlockage`).

## SDK gaps to fill for the above
- Item rarity/name + ground label info (new component view).
- Portal/waypoint UI hooks.
