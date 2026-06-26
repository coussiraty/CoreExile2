# CoreExile2 Plugin Guide (ExileBridge SDK)

This is the complete reference for writing a CoreExile2 plugin. Plugins are .NET class
libraries that the host loads at runtime from the `Plugins/` folder. They talk to the game
**only** through the **ExileBridge** SDK — a pure-interface contract. You never read game
memory directly; the host does that and hands you clean, typed views.

- **Contract assembly:** `ExileBridge.dll`
- **SDK version:** `ExileBridge.SdkInfo.Version` (currently `1`). The host refuses to load a
  plugin built against a different major contract.
- **Everything public lives in the `ExileBridge` namespace.**

---

## 1. Project setup

Create a folder under `Plugins/<YourPlugin>/` with a `.csproj` like this (copy it from
`Plugins/StashValue/StashValue.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <Version>1.0.0</Version>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <NoWarn>1701;1702;1591</NoWarn>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|AnyCPU'"><PlatformTarget>x64</PlatformTarget></PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Release|AnyCPU'"><PlatformTarget>x64</PlatformTarget></PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ImGui.NET" Version="1.91.6.1" />
    <!-- Newtonsoft is provided by the host at runtime; compile-only, do not copy. -->
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3"><Private>false</Private></PackageReference>
  </ItemGroup>

  <ItemGroup>
    <!-- Reference ONLY ExileBridge. Private=false: the host already loads ExileBridge.dll. -->
    <ProjectReference Include="..\..\ExileBridge\ExileBridge.csproj">
      <Private>false</Private>
      <CopyLocalSatelliteAssemblies>false</CopyLocalSatelliteAssemblies>
    </ProjectReference>
  </ItemGroup>

  <!-- Copies the built DLL into the host's runtime Plugins folder. -->
  <Target Name="CopyFiles" AfterTargets="Build">
    <Copy SourceFiles="$(OutDir)$(TargetName)$(TargetExt); $(OutDir)$(TargetName).pdb;"
          DestinationFolder="..\..\GameHelper\$(OutDir)Plugins\$(ProjectName)" SkipUnchangedFiles="true" />
  </Target>
</Project>
```

Then add it to the solution (`dotnet sln GameOverlay.sln add Plugins\YourPlugin\YourPlugin.csproj`)
and build the **whole solution** so the copy step runs.

**Golden rules**
- Reference **only** `ExileBridge` (+ `ImGui.NET`, `Newtonsoft.Json`), all `Private=false`. The host
  loads those into the default load context; copying your own would break type identity.
- Never `P/Invoke` game memory or reference `GameHelper`/`GameOffsets`. Use the SDK; if you need
  something not exposed, use the **escape hatches** (`Address` + `Ctx.Game.Pid`) — see §9.

---

## 2. The plugin class

Derive from `Plugin<TSettings>`:

```csharp
namespace MyPlugin
{
    using ExileBridge;
    using ImGuiNET;

    public sealed class MyPlugin : Plugin<MySettings>
    {
        // Provided by the base class:
        //   Ctx           -> IContext (all game services)
        //   DirectoryPath -> this plugin's folder on disk
        //   Settings      -> your MySettings instance

        public override void OnEnable(bool isGameAttached) { /* load settings, subscribe */ }
        public override void OnDisable() { /* unsubscribe, release */ }
        public override void DrawSettings() { /* ImGui config UI (host settings window) */ }
        public override void DrawUI() { /* per-frame overlay drawing; not called while disabled */ }
        public override void SaveSettings() { /* persist Settings to disk */ }
    }
}
```

**Lifecycle**
| Method | When | Use for |
| --- | --- | --- |
| `OnEnable(isGameAttached)` | startup (if enabled) or when toggled on | load settings from disk, subscribe to `Ctx.Events` |
| `DrawSettings()` | every frame the host settings window is open | ImGui controls bound to `Settings` |
| `DrawUI()` | every rendered frame **while enabled** | draw overlays, run per-frame logic |
| `SaveSettings()` | when you call it (usually after a settings change) | write `Settings` to `DirectoryPath/config/...` |
| `OnDisable()` | when toggled off / shutdown | dispose event tokens, stop input |

---

## 3. Settings & persistence

Settings implement the marker `IPluginSettings`. **Use public fields** so ImGui can bind them
by `ref`:

```csharp
using ExileBridge;
using System.Numerics;

public sealed class MySettings : IPluginSettings
{
    public bool Enabled = true;
    public float Range = 50f;
    public Vector4 Color = new(1f, 0.9f, 0.4f, 1f);
}
```

Persist with Newtonsoft (the host provides it). The common pattern:

```csharp
using System.IO;
using Newtonsoft.Json;

private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

public override void OnEnable(bool isGameAttached)
{
    if (File.Exists(this.SettingsPath))
        this.Settings = JsonConvert.DeserializeObject<MySettings>(File.ReadAllText(this.SettingsPath)) ?? new();
}

public override void SaveSettings()
{
    Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
    File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
}

public override void DrawSettings()
{
    var changed = false;
    changed |= ImGui.Checkbox("Enabled", ref this.Settings.Enabled);
    changed |= ImGui.SliderFloat("Range", ref this.Settings.Range, 0f, 200f);
    changed |= ImGui.ColorEdit4("Color", ref this.Settings.Color);
    if (changed) this.SaveSettings();
}
```

---

## 4. `Ctx` — the services

`Ctx` is an `IContext` exposing seven services:

| `Ctx.` | Type | What |
| --- | --- | --- |
| `Game` | `IGameService` | process/window/area state + the in-game snapshot |
| `Entities` | `IEntitiesService` | nearby entities + lookup |
| `Ui` | `IUiService` | open panels, item slots, UI elements, maps |
| `Render` | `IRenderService` | world/grid → screen projection |
| `Events` | `IEventsService` | lifecycle event subscriptions |
| `Overlay` | `IOverlayService` | overlay size + texture/image loading |
| `Log` | `ILogService` | `Debug` / `Info` / `Warn` / `Error` |

### 4.1 `Ctx.Game` (`IGameService`)
```
GameState State;            // NotLoaded | Login | Loading | InGame | Escape | Other
bool   IsAttached;          // game process attached
bool   IsInGame;            // player is in a zone
bool   IsForeground;        // game window focused
uint   Pid;                 // process id (0 if detached) — for escape-hatch reads
bool   IsControllerMode;
RectInfo WindowArea;        // (X, Y, Width, Height) of the game window
IInGame InGame;             // only meaningful while IsInGame
```
`IInGame`: `AreaName`, `AreaHash`, `AreaLevel`, `IsTown`, `IsHideout`, `AreaId`, `Terrain`
(`ITerrain`), `Player` (`IEntity`).

### 4.2 `Ctx.Entities` (`IEntitiesService`)
```
IReadOnlyCollection<IEntity> Awake;     // active, near the player
IReadOnlyCollection<IEntity> Sleeping;  // dormant / far
bool TryGetById(uint id, out IEntity entity);
void ScanSleeping(Func<string,bool> pathFilter, Action<IEntity> onMatch); // background scan
```

`IEntity`:
```
nint Address; uint Id; string Path; bool IsValid;
EntityType Type; EntitySubtype Subtype; EntityState State; int CustomGroup;
Vector2 GridPosition;
bool TryGetComponent<TComponent>(out TComponent component) where TComponent : class, IComponent;
```

### 4.3 Entity components (read with `TryGetComponent<T>`)
| Interface | Key members |
| --- | --- |
| `ILife` | `IsAlive`, `Health`/`EnergyShield`/`Mana`/`Ward`/`Divinity` (each an `IVital`) |
| `IVital` | `Current`, `Total`, `Unreserved`, `ReservedFlat`, `ReservedPercent`, `Regeneration`, `CurrentInPercent` |
| `IStats` | `TryGetItemStat(GameStat, out int)`, `GetTotalStat(GameStat)` |
| `IObjectMagicProperties` | `Rarity`, `ModNames` |
| `IPositioned` | `IsFriendly` |
| `IRender` | `GridPosition`, `WorldPosition`, `ModelBounds`, `TerrainHeight` |
| `IPlayer` | `Name` |
| `ITargetable` | `IsTargetable`, `IsHidden` (prefer `!IsHidden` for "can I attack this") |
| `IBuffs` | `Count`, `Has(buffName)` |
| `IChest` | `IsOpened`, `IsStrongbox` |
| `IShrine` | `IsUsed` |
| `IGroundItem` | `ItemPath`, `Rarity`, `StackCount` (dropped loot) |
| `IMinimapIcon` | `IconName` |
| `IStateMachine` | `Address`, `States` (`IStateMachineState{Name,Value}`), `TryGetRuneStationSocketCount` |
| `ITriggerableBlockage` | `IsBlocked` |

### 4.4 `Ctx.Ui` (`IUiService`)
```
bool IsAnyLargePanelOpen;                 // a stash/inv/market/etc panel is open
nint GameUiAddress;                       // escape hatch: root UI element address
IUiElement SekhemaTrialPanel, Atlas, RightPanel, WorldMapPanel;
IReadOnlyList<IAtlasMapNode> AtlasMaps;
IMapElement MiniMap, LargeMap;
Vector2 BaseResolution;
IReadOnlyList<IItemSlot> EnumerateOpenItemSlots();   // priced-ready stash/inv/merchant items
IUiElement? FindElementByStringId(string stringId, IUiElement? root = null);
```
- `IItemSlot`: `Position`, `Size`, `Panel` (`ItemPanel.Left`=stash / `Right`=inventory /
  `Merchant`), `Item` (`IInventoryItem`).
- `IInventoryItem`: `Path`, `DisplayName` (real localized name from the game's BaseItemTypes
  table), `Rarity`, `StackCount`, `ModLines`.
- `IUiElement`: `Address`, `Exists`, `IsVisible`, `Position`, `Size`, `StringId`, `ChildCount`,
  `Child(int index)`.

### 4.5 `Ctx.Render` (`IRenderService`)
```
Vector2 WorldToScreen(Vector3 worldPosition);
Vector2 WorldToScreen(Vector3 worldPosition, float height);
```

### 4.6 `Ctx.Events` (`IEventsService`)
Each subscription returns an `IDisposable` — **dispose it in `OnDisable`** to unsubscribe.
```
IDisposable OnAreaChange(Action), OnFrame(Action), OnGameAttached(Action),
            OnGameDetached(Action), OnWindowMove(Action), OnForegroundChange(Action);
```

### 4.7 `Ctx.Overlay` (`IOverlayService`)
```
Vector2 Size;
void AddOrGetImage(string filePath, out nint handle, out uint w, out uint h);
void AddOrGetImage(string key, Image<Rgba32> image, bool srgb, out nint handle);
bool RemoveImage(string filePath);
```

### 4.8 `Ctx.Log` (`ILogService`)
`Debug`, `Info`, `Warn`, `Error` — go to the host console/log.

---

## 5. Drawing (ImGui)

`DrawUI()` runs inside the host's ImGui frame. Use `ImGui.GetForegroundDrawList()` /
`GetBackgroundDrawList()` for world overlays, or a window for panels.

```csharp
public override void DrawUI()
{
    if (!this.Ctx.Game.IsInGame) return;
    var fg = ImGui.GetForegroundDrawList();
    foreach (var e in this.Ctx.Entities.Awake)
    {
        if (e.Type != EntityType.Monster || !e.TryGetComponent<IRender>(out var r)) continue;
        var screen = this.Ctx.Render.WorldToScreen(r.WorldPosition);
        fg.AddCircle(screen, 8f, Draw.Color(this.Settings.Color));
    }
}
```

The `Draw` helper (static): `Color(r,g,b,a)` / `Color(Vector4)` / `Color(uint)`,
`ToolTip(text)`, `IEnumerableComboBox`, `Vector2SliderInt`, `TransparentWindowFlags`.
`MathUtil.Lerp` for interpolation.

---

## 6. Input & movement (automation)

`Input` (static) sends synthetic key/mouse on a background worker (never blocks render):
```
Input.PressKey(vk, ctrl, shift, alt, holdMs);   // vk = Win32 virtual-key code
Input.KeyDown(vk); Input.KeyUp(vk);             // tracked; auto-released on shutdown
Input.Click(Input.MouseButton.Left);
Input.MoveMouse(x, y);                           // absolute desktop pixels
Input.SendChat("@Seller hi");                    // opens chat, pastes, sends (clipboard)
Input.IsKeyDown(vk); Input.TryCaptureKey(out vk);// for keybind-capture UIs
Input.Flush(); Input.ReleaseAllHeld();
// Constants: Input.VkControl, VkShift, VkAlt, VkEscape, VkReturn, VkLButton, VkRButton
```

`MovementController` (instance) holds WASD-style direction keys toward a screen target:
```csharp
private readonly MovementController move = new();
// move.Configure(W, S, A, D);                  // optional; defaults to WASD
move.MoveToward(playerScreen, targetScreen, deadzone: 16f);
move.Stop();                                     // also: move.IsMoving
```

> Synthetic input goes to the **focused** window — gate automation on
> `Ctx.Game.IsForeground` and keep the game focused.

---

## 7. Diagnostics

`Diagnostics` (static) writes a rolling, timestamped log file (handy when the elevated overlay
console isn't visible):
```csharp
Diagnostics.Log("MyPlugin", $"awake monsters: {count}");
// Diagnostics.SetFile(path); Diagnostics.Clear(); Diagnostics.Enabled = false;
```

---

## 8. Full examples

### 8.1 Minimal plugin
```csharp
namespace HelloPlugin
{
    using ExileBridge;
    using ImGuiNET;

    public sealed class HelloSettings : IPluginSettings { public bool Show = true; }

    public sealed class HelloPlugin : Plugin<HelloSettings>
    {
        public override void OnEnable(bool isGameAttached) { }
        public override void OnDisable() { }
        public override void SaveSettings() { }
        public override void DrawSettings() => ImGui.Checkbox("Show overlay", ref this.Settings.Show);

        public override void DrawUI()
        {
            if (!this.Settings.Show || !this.Ctx.Game.IsInGame) return;
            ImGui.GetForegroundDrawList().AddText(new(20, 200),
                Draw.Color(255, 255, 0, 255), $"Area: {this.Ctx.Game.InGame.AreaName}");
        }
    }
}
```

### 8.2 Low-life flask (input + components)
```csharp
public override void DrawUI()
{
    if (!this.Ctx.Game.IsForeground) return;
    var player = this.Ctx.Game.InGame.Player;
    if (player == null || !player.TryGetComponent<ILife>(out var life)) return;
    if (life.Health.CurrentInPercent < 50)
        Input.PressKey(0x31); // press "1"
}
```

### 8.3 Price the open stash (UI + items)
```csharp
public override void DrawUI()
{
    if (!this.Ctx.Ui.IsAnyLargePanelOpen) return;
    var fg = ImGui.GetForegroundDrawList();
    foreach (var slot in this.Ctx.Ui.EnumerateOpenItemSlots())
    {
        // slot.Item.DisplayName / .Path / .Rarity / .StackCount / .ModLines
        fg.AddText(slot.Position, Draw.Color(255, 255, 0, 255), slot.Item.DisplayName);
    }
}
```

### 8.4 Subscribe to events (and clean up)
```csharp
private System.IDisposable? areaSub;
public override void OnEnable(bool _) => this.areaSub = this.Ctx.Events.OnAreaChange(
    () => this.Ctx.Log.Info($"entered {this.Ctx.Game.InGame.AreaName}"));
public override void OnDisable() => this.areaSub?.Dispose();
```

---

## 9. Escape hatches

The SDK is curated, not exhaustive. When you need data it doesn't expose, every wrapper hands
you the raw address: `IEntity.Address`, `IUiElement.Address`, `IStateMachine.Address`,
`IUiService.GameUiAddress`. Combine with `Ctx.Game.Pid` to read that memory yourself
(`OpenProcess` + `ReadProcessMemory`), exactly how `RunecraftHelper` reads the game's
`BaseItemTypes` table. Treat offsets as volatile — keep them in your own plugin and expect to
re-verify them after game patches.

---

## 10. Conventions checklist

- Reference only ExileBridge / ImGui.NET / Newtonsoft, all `Private=false`.
- One settings class implementing `IPluginSettings`, public fields, JSON-persisted under
  `DirectoryPath/config/`.
- Dispose every `Ctx.Events` subscription in `OnDisable`.
- Gate automation on `Ctx.Game.IsForeground`; never leave keys held (use `MovementController` /
  `Input.KeyUp`).
- Heavy work (HTTP, big scans) off the render thread; cache results and redraw from the cache.
- Match the surrounding code style; keep volatile offsets local to your plugin.
