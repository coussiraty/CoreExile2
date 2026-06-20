# ExileBridge — Plugin SDK Design Plan

> Status: **Implementado e validado** (Fases 0–4, faltando só o AHK). Build verde;
> `ExileBridgeSample`, `HealthBars`, `Atlas` e `Radar` rodaram contra o jogo ao vivo
> sem exceções, dependendo **apenas** do ExileBridge. Ver "Status de implementação"
> no fim do documento.
>
> **Decisões fixadas:**
> - Nome / namespace: **`ExileBridge`**.
> - Modelo de API: **interfaces puras** (sem vazar tipos concretos de `RemoteObjects`).
> - Inspiração: o [POEFixer Plugin Development Guide](https://github.com/POEFixer/FixerWiki/wiki/Plugin-Development-Guide)
>   — um `Context` que agrega **serviços** + um `Snapshot` imutável do estado.

---

## 1. Objetivo

Criar uma DLL `ExileBridge` que seja a **única** referência de um plugin. Ela define:
1. O **contrato de plugin** (`IPlugin` / base class).
2. Um **`IContext`** que agrega serviços focados (`Game`, `Entities`, `Components`,
   `Ui`, `Render`, `Terrain`, `Events`, `Overlay`, `Log`, `Input`).
3. **Interfaces puras** para todo dado do jogo (`IEntity`, `ILife`, `IAreaInstance`…),
   desacopladas das classes `RemoteObjects` do GameHelper.

Escrever um plugin = referenciar `ExileBridge.dll` → herdar `Plugin` → usar `Ctx.*`.
O GameHelper **implementa** essas interfaces; o ExileBridge não conhece o GameHelper.

---

## 2. Por que isso (estado atual, fundamentado)

- Plugins referenciam o **projeto inteiro** `GameHelper.csproj` → a "API" é o app todo;
  qualquer refactor interno pode quebrar plugins.
- Acesso a dados via god-object estático `GameHelper.Core` (`Core.States.InGameStateObject…`).
- Membros úteis estão `internal` (`GameScale`, `GameCull`, caches GGPK) → inacessíveis.
- `GameOffsets` é memória crua → péssimo como contrato público.
- Contrato de plugin mora dentro do GameHelper → sem fronteira.

**Boa notícia:** o GameHelper **já tem** o equivalente de quase todo serviço do POEFixer.
O trabalho é definir interfaces puras e adaptadores, não reescrever leitura de memória.

### Mapa: serviço POEFixer → capacidade existente no GameHelper

| Serviço POEFixer | Equivalente no GameHelper | Vira no ExileBridge |
|---|---|---|
| GameService (snapshot/estado/janela) | `Core.States`, `Core.Process` | `IGameService` |
| EntitiesService | `AreaInstance.AwakeEntities` / `SleepingEntities` | `IEntitiesService` |
| ComponentsService (readers) | `Entity.TryGetComponent<T>()` + `RemoteObjects.Components.*` | via `IEntity.TryGetComponent<TComp>()` |
| UiService | `InGameState.GameUi.*` (`MiniMap`, `LargeMap`, painéis) | `IUiService` |
| RenderService (WorldToScreen / Grid→Map) | `WorldData.WorldToScreen`, `GameWindowScale`, `GameWindowCull` | `IRenderService` |
| TerrainService (grid/altura/TGT) | `AreaInstance.GridWalkableData`, `GridHeightData`, `TgtTilesLocations`, `TerrainHeightHelper` | `ITerrainService` |
| EventsService | `CoroutineEvents` (`OnAreaChange`, `OnRender`, preloads…) | `IEventsService` |
| OverlayService | `Core.Overlay` (`Size`, `AddOrGetImagePointer`, `RemoveImage`) | `IOverlayService` |
| LogService | `Console.WriteLine` / logging atual | `ILogService` |
| MemoryService (RPM cru) | `Core.Process` handle | **escopo controlado** — talvez `IMemoryService` só leitura |
| FlasksService / PricesService / RuneshapeService | — (POE2 / poefixer-específico) | **fora de escopo** |

---

## 3. Arquitetura e direção de dependência

```
                ┌────────────────┐
                │   ExileBridge  │  contrato + IContext + interfaces puras
                └────────────────┘
                   ▲           ▲
                   │           │
        ┌──────────┴──┐     ┌──┴───────────────┐
        │  GameHelper │     │   Plugins/*      │
        │  implementa │     │  (referenciam SÓ │
        │  IContext   │     │   ExileBridge)   │
        └─────────────┘     └──────────────────┘
              ▲
        ┌─────┴──────┐
        │ GameOffsets│
        └────────────┘
```

- **Hoje:** Plugin → GameHelper (tudo).
- **Proposto:** Plugin → ExileBridge ← GameHelper. Dependência invertida.

### Projeto `ExileBridge`
- `net10.0-windows`, `x64`.
- Referência mínima: `ImGui.NET` (plugins desenham com ImGui; tipos ImGui já são
  uma fronteira estável de pacote). Tudo o mais é interface própria.
- **Sem** referência a `GameHelper` nem a `GameOffsets` (mantém o contrato puro).

> **Ponto crítico de ALC (testar na Fase 0):** `ExileBridge.dll` precisa ser
> resolvida pelo **Default ALC** (carregada junto do GameHelper.exe), não pelo ALC
> de cada plugin. Senão `typeof(IPlugin).IsAssignableFrom(type)` no `PManager` vê
> dois `IPlugin` distintos e nenhum plugin carrega. O `PluginAssemblyLoadContext`
> deve devolver `null` em `Load(ExileBridge)` para herdar do contexto pai.

---

## 4. Contrato de plugin

```csharp
namespace ExileBridge;

public interface IPlugin
{
    void OnEnable(bool isGameAttached);
    void OnDisable();
    void DrawSettings();
    void DrawUI();
    void SaveSettings();
}

// Base class com o açúcar (settings tipadas + injeção de contexto).
public abstract class Plugin<TSettings> : IPlugin
    where TSettings : IPluginSettings, new()
{
    protected IContext Ctx { get; private set; } = null!;   // injetado pelo host
    protected TSettings Settings { get; set; } = new();
    public string DirectoryPath { get; private set; } = null!;

    public abstract void OnEnable(bool isGameAttached);
    public abstract void OnDisable();
    public abstract void DrawSettings();
    public abstract void DrawUI();
    public abstract void SaveSettings();
}
```

O `PManager` injeta `Ctx` e `DirectoryPath` logo após instanciar o plugin
(hoje já faz `SetPluginDllLocation`; ganha um `SetContext`).

---

## 5. O `IContext` (agregador de serviços)

```csharp
namespace ExileBridge;

public interface IContext
{
    IGameService     Game     { get; }
    IEntitiesService Entities { get; }
    IUiService       Ui       { get; }
    IRenderService   Render   { get; }
    ITerrainService  Terrain  { get; }
    IEventsService   Events   { get; }
    IOverlayService  Overlay  { get; }
    ILogService      Log      { get; }
    IInputService    Input    { get; }
}
```

### 5.1 `IGameService` + Snapshot imutável
Inspirado no `Snapshot` do POEFixer — uma visão imutável por frame, evitando que o
plugin segure referências a objetos voláteis.

```csharp
public interface IGameService
{
    GameState State { get; }          // InGame, Loading, Login, etc.
    bool IsAttached { get; }
    bool IsForeground { get; }
    RectInfo WindowArea { get; }
    int Pid { get; }

    IGameSnapshot Snapshot { get; }   // visão imutável do frame atual
}

public interface IGameSnapshot
{
    GameState State { get; }
    string CurrentAreaName { get; }
    string AreaHash { get; }
    int AreaLevel { get; }
    bool IsTown { get; }
    bool IsHideout { get; }
    IPlayer Player { get; }
}
```

### 5.2 `IEntitiesService` + `IEntity` + componentes
Componentes são **interfaces puras** (`ILife`, `IBuffs`, `IActor`, `IMods`,
`IPositioned`, `IRender`, …), lidas via genérico tipado:

```csharp
public interface IEntitiesService
{
    IReadOnlyCollection<IEntity> Awake { get; }
    IReadOnlyCollection<IEntity> Sleeping { get; }
    bool TryGetById(uint id, out IEntity entity);
}

public interface IEntity
{
    uint Id { get; }
    string Path { get; }
    bool IsValid { get; }
    EntityType Type { get; }
    EntitySubtype Subtype { get; }
    GridPosition GridPosition { get; }

    bool TryGetComponent<TComponent>(out TComponent component)
        where TComponent : class, IComponent;   // ex.: TryGetComponent<ILife>(...)
}

public interface ILife : IComponent
{
    int CurrentHp { get; } int MaxHp { get; }
    int CurrentEs { get; } int MaxEs { get; }
    int CurrentMana { get; } int MaxMana { get; }
}
```

> O mapeamento `ILife → RemoteObjects.Components.Life` vive **dentro do GameHelper**
> (adaptadores), nunca no SDK. O plugin só vê `ILife`.

### 5.3 `IRenderService`
```csharp
public interface IRenderService
{
    Vector2 WorldToScreen(Vector3 worldPos);
    Vector2 GridToLargeMap(Vector2 gridPos);
    Vector2 GridToMiniMap(Vector2 gridPos);
}
```

### 5.4 `IEventsService` (tokens, como o POEFixer)
```csharp
public interface IEventsService
{
    IDisposable OnAreaChange(Action handler);
    IDisposable OnFrame(Action handler);
    IDisposable OnGameAttached(Action handler);
    IDisposable OnGameDetached(Action handler);
}
```
Internamente embrulha o `CoroutineHandler` + `CoroutineEvents`. O `IDisposable`
cancela a inscrição (equivalente ao `Unsubscribe(token)`).

### 5.5 Demais serviços (assinaturas resumidas)
- `IUiService`: `IMapUi MiniMap`, `ILargeMapUi LargeMap`, `bool IsAnyLargePanelOpen`,
  `TryFindPanel(stringId, out IUiElement)`.
- `ITerrainService`: `ReadOnlySpan<byte> WalkableGrid`, `float HeightAt(x,y)`,
  `IReadOnlyDictionary<...> TgtTiles`.
- `IOverlayService`: `Vector2 Size`, `nint AddOrGetImage(path)`, `RemoveImage(path)`.
- `ILogService`: `Debug/Info/Warn/Error(string)`.
- `IInputService`: leitura de teclas/mouse (hoje via `ClickableTransparentOverlay`).

### 5.6 Desenho
Plugins continuam usando **ImGui.NET** diretamente em `DrawUI()` (igual hoje e igual
ao POEFixer). O SDK só fornece o que o ImGui não dá: posições de tela, imagens,
tamanho do overlay (via serviços acima). Sem reinventar uma camada de render.

---

## 6. Fases de migração (build sempre verde)

**Fase 0 — Fundação**
1. Criar projeto `ExileBridge` + adicionar à solution.
2. Garantir que `ExileBridge.dll` carrega no Default ALC (ajuste no
   `PluginAssemblyLoadContext` + teste com os plugins atuais ainda no esquema antigo).

**Fase 1 — Contrato + IContext vazio**
3. Definir `IPlugin`/`Plugin<TSettings>`/`IPluginSettings` no SDK.
4. Definir `IContext` e as interfaces de serviço (sem implementação ainda).

**Fase 2 — Implementação (adaptadores no GameHelper)**
5. `GameHelperContext : IContext` + adaptadores (`LifeAdapter : ILife`, etc.)
   que delegam para `Core`/`RemoteObjects`. `Core` continua intacto.
6. `PManager` injeta o contexto.

**Fase 3 — Plugin de exemplo (substitui o papel didático do PreloadAlert)**
7. Plugin "hello world" usando **só** `ExileBridge`, espelhando o estilo do guia
   POEFixer. Vira o `SamplePluginTemplate` novo.

**Fase 4 — Migrar plugins reais (um a um)**
8. Radar / HealthBars / Atlas / AutoHotKeyTrigger trocam `Core.*` por `Ctx.*`.
9. Removem o `ProjectReference` para `GameHelper` → passam a depender só do SDK.

**Fase 5 — Endurecer**
10. `Core` vira `internal`. Versionar o SDK (`SdkVersion` checado no load, como o
    `PLUGIN_SDK_VERSION` do POEFixer). Documentar.

Cada fase compila e mantém os plugins existentes funcionando.

---

## 7. Riscos / pontos de atenção

| Tema | Nota |
|---|---|
| **Identidade de tipo entre ALCs** | Bloqueador #1. SDK deve estar no Default ALC. Validar Fase 0. |
| **Snapshot imutável vs. objetos vivos** | POEFixer copia para value-types por frame. Em C# podemos expor interfaces somente-leitura sobre os objetos vivos (mais barato) — mas então o dado muda entre frames. Decidir: snapshot copiado (seguro) vs. view ao vivo (barato). |
| **Genérico `TryGetComponent<ILife>`** | Precisa de um mapa `interface→tipo concreto` nos adaptadores. Simples, mas é boilerplate por componente (≈21 deles). |
| **Custo dos adaptadores** | Interfaces puras = um adaptador por tipo exposto. Mais trabalho que reexpor, porém é exatamente o desacoplamento que você pediu. |
| **Versionamento** | Adotar `SdkVersion` desde a Fase 1 evita dor depois. |

### Pergunta em aberto pra você
- **Snapshot copiado (imutável, à la POEFixer) ou view ao vivo (interface sobre o
  objeto volátil)?** Isso muda a implementação dos adaptadores. Recomendo começar
  com **view ao vivo** (menos cópia, casa com o modelo de corrotina por frame do
  GameHelper) e, se precisar de imutabilidade, adicionar `Snapshot` copiado só no
  `IGameService`.

---

## Status de implementação (o que já existe no repo)

**Feito (Fases 0–4 parcial):**
- Projeto `ExileBridge/` — contrato (`IPlugin`, `Plugin<TSettings>`, `IPluginSettings`,
  `IHostBoundPlugin`, `SdkInfo`) + `IContext` e serviços como **interfaces puras**:
  `IGameService`/`IInGame`, `IEntitiesService`/`IEntity` (com `Type`/`Subtype`/`State`/
  `CustomGroup`), componentes `ILife`(+`IVital`)/`IPositioned`/`IRender`/
  `IObjectMagicProperties`, `IUiService`, `IRenderService`, `IEventsService`,
  `IOverlayService` (métricas + texturas), `ILogService`. Enums de fidelidade total
  `EntityType`/`EntitySubtype`/`EntityState`/`Rarity`/`GameState`, `RectInfo`.
- Helpers de UI/Math portados pro SDK: `ExileBridge.Draw` (Color/ToolTip/
  Vector2SliderInt) e `ExileBridge.MathUtil` (Lerp).
- Adaptadores no host: `GameHelper/Sdk/GameHelperContext.cs` e `EntityAdapters.cs`
  (delegam ao `Core`/`RemoteObjects`; `Core` segue intacto).
- `GameHelper/Plugin/SdkPluginAdapter.cs` embrulha `IPlugin` no pipeline `IPCore`.
- `PluginAssemblyLoadContext` registra `ExileBridge` como assembly compartilhado
  (Default ALC) — **bloqueador #1 resolvido e validado ao vivo**.
- `PManager` descobre plugins `IPCore` (legado) **ou** `IPlugin` (SDK).
- Plugin de exemplo `Plugins/ExileBridgeSample/` usando **só** o ExileBridge.
- **`HealthBars` migrado** para o ExileBridge: removida a dependência de
  `GameHelper`, agora referencia só o SDK; validado ao vivo sem exceções.
- **`Atlas` migrado**: novas interfaces `IUiElement`, `IAtlasMapNode`,
  `AtlasMapNodeState`, `GridPoint` + `IUiService.Atlas/RightPanel/WorldMapPanel/
  AtlasMaps`. Referencia só o SDK; validado ao vivo sem exceções.

- **Radar — superfície do SDK pronta (Fase A)**: adicionadas `ITerrain`
  (WalkableData/HeightData/BytesPerRow/WorldToGridConvertor/TgtTiles), `IMapElement`
  (+`IUiService.MiniMap/LargeMap`), componentes `IPlayer`/`IShrine`/`ITargetable`/
  `IMinimapIcon`/`IStateMachine`/`ITriggerableBlockage`, `IObjectMagicProperties.ModNames`,
  `IEntitiesService.ScanSleeping`, `IInGame.AreaId/Terrain`, `GameState.Escape`, e o
  overload de textura `IOverlayService.AddOrGetImage(Image<Rgba32>)` (SDK passou a
  referenciar SixLabors.ImageSharp). Build verde. **Falta a Fase B**: reconectar
  `Radar.cs` (114 touchpoints) + IconPicker/LineWalker/RadarSettings ao `Ctx.*`.

- **Radar migrado (Fases A+B)**: `Radar.cs` + IconPicker/LineWalker/RadarSettings
  reconectados ao `Ctx.*`; depende só do ExileBridge. Exigiu ampliar o SDK com
  `ITerrain` (incl. TotalTiles/TileHeightMultiplier), `IMapElement`, os 6 componentes
  novos, eventos `OnWindowMove`/`OnForegroundChange`, `IGameService.IsControllerMode`,
  `IRenderService.WorldToScreen(pos,height)`, `IUiService.BaseResolution`,
  `Draw.TransparentWindowFlags`/`IEnumerableComboBox`. **ImageSharp agora é shared no
  ALC** (o overload `AddOrGetImage(Image<Rgba32>)` cruza a fronteira do plugin).
  Validado ao vivo (geração da textura walkable + TgtTiles + eventos, sem exceções).

**Pendente (Fase 4 final + 5):**
- **AutoHotKeyTrigger**: último plugin. Componentes `IBuffs`/`IActor`/`IStats`/
  `ICharges`/`IAilments` + `ServerData.FlaskInventory`; reconectar `DynamicConditionState`
  ao `Ctx.*` (Dynamic LINQ opera nas interfaces do próprio plugin, não é bloqueador).
- **AutoHotKeyTrigger** (~4100 linhas): já tem camada própria de interfaces para o
  Dynamic LINQ (`IDynamicConditionState` etc.); migrar = reconectar
  `DynamicConditionState` ao `Ctx.*` + expor componentes `IBuffs`/`IActor`/
  `IStats`/`ICharges`/`IAilments` e `ServerData.FlaskInventory`. Dynamic LINQ
  **não** é bloqueador (opera nas interfaces do próprio plugin).
- Tornar `Core` `internal` quando ninguém de fora depender mais dele.
- Checagem de `SdkInfo.Version` no load; docs/template do SDK.
- Otimização: `IEntitiesService.Awake/Sleeping` aloca uma lista de adaptadores por
  acesso (ok para o proof; revisar se virar gargalo num mapa cheio).
- Decidir snapshot copiado vs. view ao vivo (hoje é **view ao vivo**).

## 8. Resumo de uma linha
`ExileBridge` = DLL com contrato de plugin + `IContext` agregando serviços
(`Game/Entities/Ui/Render/Terrain/Events/Overlay/Log/Input`) expostos como
**interfaces puras**, no estilo do POEFixer; GameHelper implementa via adaptadores,
dependência invertida, migração em fases sem quebrar build. PreloadAlert já removido.
