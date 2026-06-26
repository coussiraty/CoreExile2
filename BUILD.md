# Building CoreExile2 from source

## Requirements

- [Visual Studio](https://visualstudio.microsoft.com/downloads/) (Community is enough) with the
  **.NET desktop development** workload.
- [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) — the
  **SDK**, not just the runtime.

## Project layout

| | |
| --- | --- |
| Solution | [`GameOverlay.sln`](GameOverlay.sln) |
| Target framework | `net10.0-windows` (runtime id `win-x64`; output is x64 even though VS shows `Any CPU`) |
| Main app | [`GameHelper/GameHelper.csproj`](GameHelper/GameHelper.csproj) |
| Launcher | [`Launcher/Launcher.csproj`](Launcher/Launcher.csproj) |
| Plugins | `Plugins/<Name>/` (each copies its DLL into the host output on build) |

## Build & run

1. Open **[`GameOverlay.sln`](GameOverlay.sln)** — the *solution*, not a single project, so the
   launcher and plugins get copied to the output.
2. Allow NuGet restore if prompted.
3. Select `Release`, then `Build > Rebuild Solution`.
4. The runnable app is produced in:
   ```text
   GameHelper\bin\Release\net10.0-windows\win-x64\
   ```
5. Run **`Launcher.exe`** from that folder (accept the administrator prompt). The launcher
   prepares and starts `GameHelper.exe`.

If the game runs as administrator, run the overlay as administrator too — its manifest already
requests elevation.

> **Always `Rebuild Solution`.** Plugin projects and the launcher rely on MSBuild copy steps to
> place their DLLs/assets into the GameHelper output `Plugins\` folder. Building a single project
> can leave the launcher or plugins missing.

### Command line

```sh
dotnet build GameOverlay.sln -c Release
```

## Runtime layout

These are generated next to the executable at runtime and are ignored by Git:

```text
configs\core_settings.json
configs\plugins.json
Plugins\<PluginName>\config\
```

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| *"The current .NET SDK does not support targeting .NET 10.0"* | Install the .NET 10 SDK, update Visual Studio, restart, reopen the solution. |
| Build succeeded but plugins are missing | Use `Build > Rebuild Solution`, not `Build Project`. |
| *"Launcher says GameHelper.exe was not found"* | Run `Launcher.exe` from `GameHelper\bin\<Configuration>\net10.0-windows\win-x64\`, not from `Launcher\bin\...`. |
| Overlay does not attach to the game | Run the overlay at the same privilege level as the game (as administrator if the game is elevated). |
