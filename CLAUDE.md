# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A C# mod for **Slay the Spire 2** (Steam, legal copy). The game runs on **Godot 4.5.1** (MegaCrit's fork "MegaDot") with **.NET 9.0**. The game has a built-in mod loader — no BepInEx or MelonLoader.

## Key Paths

- **Game install:** `E:\SteamLibrary\steamapps\common\Slay the Spire 2\`
- **Game DLL (sts2.dll):** `E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll`
- **Mods folder:** `E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\`
- **BaseLib (dependency):** `E:\SteamLibrary\steamapps\workshop\content\2868840\3737335127\`
- **Decompiled game source:** `C:\Users\Hydrogen\Documents\GitHub\sts2-modding-mcp\decompiled\` — one `.cs` per class in namespaced folders; read/grep directly for method bodies (faster than the MCP search tools)

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
- Game logs: `%APPDATA%\SlayTheSpire2\logs\godot.log`

## Internet Research

**Always search the internet** when working on this mod. The game is in Early Access — APIs change between updates, and community knowledge evolves fast. Use WebSearch/WebFetch for:
- Looking up game classes/methods before writing Harmony patches
- Checking for updated modding patterns or API changes
- Finding example implementations in other mods
- Verifying that approaches still work with the current game version

## Modding Resources

### Templates & Frameworks

- **[Alchyr Mod Template](https://github.com/Alchyr/ModTemplate-StS2)** — NuGet template (`dotnet new install Alchyr.Sts2.Templates`) with empty, content, and character mod types
- **[Template Wiki](https://github.com/Alchyr/ModTemplate-StS2/wiki)** — Setup, decompiling, extracting assets, adding cards/ancients, shaders, reflection, common commands cookbook
- **[BaseLib](https://github.com/Alchyr/BaseLib-StS2)** — Standardizes content additions (cards, relics, potions); required dependency for template mods
- **[BaseLib Wiki](https://alchyr.github.io/BaseLib-Wiki/)** — Features, utilities, dynamic variable tooltips, mod configuration
- **[ModSmith](https://github.com/cpimhoff/Sts2-ModSmith)** — Alternative framework with guided setup for cards/potions/relics/powers/events/ancients
- **[ModSmith Docs](https://cpimhoff.github.io/Sts2-ModSmith/)** — Includes decompilation guide and beginner walkthrough

### Tutorials & Guides

- **[Comprehensive Modding Tutorial](https://fresh-milkshake.github.io/Modding-Tutorial/)** — Full walkthrough: setup, mod loading, manifests, Harmony/hooks, content registration, .pck assets, native UI, debugging, packaging ([source](https://github.com/fresh-milkshake/Modding-Tutorial))
- **[Wiki.gg Modding Tutorials](https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Modding_Tutorials)** — Community hub: recommended software, mod folder structure, mod.json, models, localization, assets, decompiling, Harmony, SpireField

### Example Mods (reference implementations)

- **[jiegec/STS2FirstMod](https://github.com/jiegec/STS2FirstMod)** — Beginner example with build/install scripts
- **[lamali292/sts2_example_mod](https://github.com/lamali292/sts2_example_mod)** — Another starter example with Steam path config
- **[erasels/Minty-Spire-2](https://github.com/erasels/Minty-Spire-2)** — QoL compilation mod, good reference for real-world Harmony patches
- **[JaydenLiang/slay-the-spire-2-mods](https://github.com/JaydenLiang/slay-the-spire-2-mods)** — Collection of mods

### Decompilation & Reverse Engineering

- **[ILSpy](https://github.com/icsharpcode/ILSpy)** — Primary tool for decompiling `sts2.dll` (~3,300 C# source files)
- **[ModSmith Decompilation Guide](https://cpimhoff.github.io/Sts2-ModSmith/docs/setup/decompile.html)** — Step-by-step using ILSpy/ilspycmd
- **[spire-codex](https://github.com/ptrlrd/spire-codex)** — Project for decompiling StS2 into an API reference
- **[Sts2Repairer](https://github.com/Yanxiyimengya/Sts2Repairer)** — CLI tool for fixing common decompilation artifacts

### Harmony (Runtime Patching)

- **[Harmony GitHub](https://github.com/pardeike/Harmony)** — The library bundled with StS2
- **[Harmony Docs — Introduction](https://harmony.pardeike.net/articles/intro.html)**
- **[Harmony Docs — Patching](https://harmony.pardeike.net/articles/patching.html)**
- **[Harmony Docs — Basics](https://harmony.pardeike.net/articles/basics.html)**

### Godot Engine (C#)

- **[Godot 4.5 C#/.NET Docs](https://docs.godotengine.org/en/4.5/tutorials/scripting/c_sharp/)** — C# scripting for the exact engine version StS2 uses
- **[Godot 4.5 C# Basics](https://docs.godotengine.org/en/4.5/tutorials/scripting/c_sharp/c_sharp_basics.html)**

### AI-Assisted Tooling

- **[STS2 Modding MCP](https://github.com/elliotttate/sts2-modding-mcp)** — MCP server with 153 tools for game data querying, mod code generation, building, deployment; works with Claude Code ([NexusMods](https://www.nexusmods.com/slaythespire2/mods/345))

### Community

- **[Slay the Spire Discord](https://discord.com/invite/slaythespire)** — #sts2-modding channel
- **[NexusMods StS2](https://www.nexusmods.com/slaythespire2)** — Mod downloads and community
- **[GitHub Topic](https://github.com/topics/slay-the-spire-2)** — All tagged StS2 repos
- **[sts2.gg Mod Install Guide](https://sts2.gg/guides/how-to-install-mods)**
