# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A C# mod for **Slay the Spire 2** (Steam, legal copy). The game runs on **Godot 4.5.1** (MegaCrit's fork "MegaDot") with **.NET 9.0**. The game has a built-in mod loader — no BepInEx or MelonLoader.

## Key Paths

- **Game install:** `E:\SteamLibrary\steamapps\common\Slay the Spire 2\`
- **Game DLL (sts2.dll):** `E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll`
- **Mods folder:** `E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\`
- **BaseLib (dependency):** `E:\SteamLibrary\steamapps\workshop\content\2868840\3737335127\`

## Build

```
dotnet build StS2Mod.csproj
```

This compiles the mod and auto-copies `StS2Mod.dll`, `StS2Mod.pdb`, and `StS2Mod.json` to the game's `mods/StS2Mod/` folder via the `CopyToModsFolderOnBuild` MSBuild target. No manual copy step needed.

## Architecture

- **`StS2ModCode/MainFile.cs`** — Mod entry point. The `[ModInitializer]` attribute (from `MegaCrit.Sts2.Core.Modding`) marks the static method the game calls on load. Harmony is initialized here for runtime patching.
- **`StS2Mod.json`** — Mod manifest (id, version, dependencies). The build auto-updates the BaseLib version in this file.
- **`StS2Mod.csproj`** — Uses `Godot.NET.Sdk/4.5.1`. References `sts2.dll` and `0Harmony.dll` from the game data directory.
- **`Sts2PathDiscovery.props`** — MSBuild props that auto-detect the game install path via registry/Steam paths.
- **`Directory.Build.props`** — Local overrides (git-ignored). Set `Sts2Path` or `GodotPath` here if auto-detection fails.
- **`StS2Mod/`** — Godot resource folder (assets, scenes, localization). Contents go into the `.pck` file on publish.
- **`project.godot` / `export_presets.cfg`** — Godot project files needed for `.pck` export.

## Modding Conventions

- Game code lives under `MegaCrit.Sts2.*` namespaces in `sts2.dll`.
- Use `MegaCrit.Sts2.Core.Logging.Logger` for log output (not `GD.Print`).
- Use **HarmonyLib** (`[HarmonyPatch]`) to hook into game methods. Harmony is bundled with the game.
- `.pck` export requires the MegaDot/Godot 4.5.1 mono editor. For code-only mods, set `has_pck: false` in the manifest.
- To publish (with .pck): `dotnet publish` — requires `GodotPath` set in `Directory.Build.props`.
