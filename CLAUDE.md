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
dotnet build Rogueunlike.csproj
```

This compiles the mod and auto-copies `Rogueunlike.dll`, `Rogueunlike.pdb`, and `Rogueunlike.json` to the game's `mods/Rogueunlike/` folder via the `CopyToModsFolderOnBuild` MSBuild target. No manual copy step needed.

## Architecture

- **`RogueunlikeCode/MainFile.cs`** — Mod entry point. The `[ModInitializer]` attribute (from `MegaCrit.Sts2.Core.Modding`) marks the static method the game calls on load. Harmony is initialized here for runtime patching.
- **`Rogueunlike.json`** — Mod manifest (id, version, dependencies). The build auto-updates the BaseLib version in this file.
- **`Rogueunlike.csproj`** — Uses `Godot.NET.Sdk/4.5.1`. References `sts2.dll` and `0Harmony.dll` from the game data directory.
- **`Sts2PathDiscovery.props`** — MSBuild props that auto-detect the game install path via registry/Steam paths.
- **`Directory.Build.props`** — Local overrides (git-ignored). Set `Sts2Path` or `GodotPath` here if auto-detection fails.
- **`Rogueunlike/`** — Godot resource folder (assets, scenes, localization). Contents go into the `.pck` file on publish.
- **`project.godot` / `export_presets.cfg`** — Godot project files needed for `.pck` export.

## Feature Architecture (RogueunlikeCode/)

Design invariants every feature follows — do not break these:

- **Selection pool = loot pool at the current context.** Pickable ⟺ that exact source could roll that exact item right now. The rest renders compendium-style: darkened when unlocked-but-excluded here, locked when progression-locked. Never expand a pool.
- **Save-safety.** Every write lands in vanilla-reachable state (model fields vanilla itself rolls, vanilla RNG advancement, vanilla-style bag consumption). Mod-only flags live in memory (weak tables); install/uninstall mid-run is always safe; a reload degrades to the vanilla roll.
- **Discovery = candidate-click only.** Pickers are `ModSeenGate` anchors — rendering/hovering never marks items seen; clicking an item as your pending candidate is the only reveal.
- **SP/MP parity.** Every feature behaves identically in singleplayer and for every player in multiplayer. Channel rule: a mod decision syncs on the SAME channel as the vanilla flow it must precede — reward claims and event option choices are direct messages, so picks preceding them ride mod `INetMessage`s on that stream (per-sender FIFO); map travel is a lockstep action, so picks preceding it ride networked console cmds on the action queue. Cross-channel ordering does not exist. Shared-outcome decisions (the ? node) are votes, tallied identically on every client. Mod wire-ids follow per-machine mod LOAD ORDER, so every sync feature is gated on the `ModWireCheck` handshake — unverified real MP stays pure vanilla: degrade features, never state. Any new networked behavior REQUIRES a manifest version bump; same-version clients must be wire-identical.
- **Blast-radius control.** Patches install per feature group (`ModPatcher`, own Harmony id each): a group whose game seam vanished rolls back whole and logs which feature degrades to vanilla. Every `[HarmonyPatch]` class must be registered in a group (startup flags strays). Cross-group dependencies are runtime poison flags (a net-handler group failing → `ModWireCheck.ForceBroken`), never load-order assumptions. `ModHealthCheck` makes drift loud at startup (game-version stamp, live probes, donor assets). Map pickers fall back to vanilla travel on failure — a broken modal must never block a node. **`docs/GAME-API-SURFACE.md` records every game API touched; update it when adding seams.**

Per-file map — mechanics and edge cases live in each file's header:

| File | Role |
|---|---|
| `MainFile.cs` | Entry point → `ModPatcher.ApplyAll()` + `ModHealthCheck.Run()`. |
| `ModPatcher.cs` | Per-group patch install/rollback, stray-patch guard, `ModHealthCheck` startup drift/asset checks. |
| `ModUi.cs` | Shared picker shell (mount, modal dim, back ribbon, confirm), mod localization, node scavenging. Treasure mounts insert below the hand layer. |
| `ModSearch.cs` | Card Library search-bar graft; space=AND matching. |
| `SeenOnPickPatch.cs` | `ModSeenGate` discovery suppression, unseen-star badge, the `Mark*AsSeen` funnel patch. |
| `ShowAllCardRewardsPatch.cs` | Feature #1, state: card rewards generate the whole valid pool through the vanilla per-card pipeline. |
| `CardRewardScreenOverhaul.cs` | Feature #1, UI seam (≤5 options stays vanilla); per-grid visibility/sparkle/tint tables all card grids share. |
| `ModCardGridPicker.cs` | Abstract card-picker base: grid, deck-view chrome, search, selection. |
| `ModRewardScreenUi.cs`, `ModShopCardPickerUi.cs` | Card-picker hosts: reward screen / merchant modal. |
| `PotionRewardPickerPatch.cs`, `RelicRewardPickerPatch.cs` | Features #2/#3: pick-then-claim on `NRewardButton.GetReward`. Predetermined rewards stay vanilla (potion side: `Populate` roll-witness; relic side: `_predeterminedRelic`). Never consume bags manually — the claim's `RelicCmd.Obtain` does. |
| `ModNetMsg.cs` | Mod `INetMessage`s (`RewardPickMessage`, `ShopAssignMessage`) on the vanilla reward message stream + the channel doctrine; handlers dead while `ModWireCheck.Broken`. |
| `ModWireCheck.cs` | `rl_wirecheck <version> <pickId> <assignId> <designateId>` run-start handshake; `SyncReady` gates every sync feature. |
| `ModPotionPickerUi.cs`, `ModRelicPickerUi.cs` | Compendium-scene pickers (Potion Lab / Relic Collection) for rewards, treasure and shop. |
| `TreasureChestPickerPatch.cs` | Feature #3.1: shaded table, round-based picking over the expanded shared vote list, vanilla RPS fights. The chest phase keeps reporting `SharedRelicPicking` and z-lifts hands over mid-round reward overlays — the MP hand stays the one pointer. |
| `ShopPickerPatch.cs` | Feature #4: shade-and-assign merchant slots; assignments mirror each entry's vanilla stock path; unconditional stock-time seen-suppression (also closes a vanilla MP compendium leak). |
| `AncientPickerPatch.cs` | Feature #5: Ancient pick via the map-click seam; pool mirrors `GenerateRooms`; opens only on a real (probe-measured) choice. Run-start acts auto-enter with no click — that's #5.2's job. Also hosts the map-input gate (stuck-drag guard). |
| `AncientPickSyncCmd.cs` | Feature #5 MP: `rl_ancient <id> [slot:identity…]` console cmd + assertion-loud `BeginEvent` call-site transpiler substitution. |
| `AncientOptionsPatch.cs` | Feature #5.1: empirical per-slot option probe (modifier hooks stubbed while probing; `HasVariation` = any pool ≥2) + post-roll slot substitution. |
| `AncientStartDesignatePatch.cs` | Feature #5.2: in-event designation for auto-entered act-start Ancients; live swap by reference (other mods append page options); MP via `AncientDesignateMessage` on the event message stream. |
| `ModAncientPickerUi.cs` | "Select an Ancient" modal (map + act-start event): roster rows, per-slot designation panel, vanilla roll pre-selected. |
| `UnknownPickerPatch.cs` | Feature #6: ? node category+content. Category forced by feeding a roll VALUE into the vanilla `Roll` body (transpiler); content swapped into the pull cursor slot. First-ever run stays vanilla; Shop/Treasure are category-only. |
| `UnknownPickSyncCmd.cs` | Feature #6 MP: `rl_unknown <col> <row> <fate\|category> [modelEntry]` votes + plurality tally at `EnterMapCoord` (earliest-vote tiebreak; non-voters = fate). |
| `ModUnknownPickerUi.cs` | "Choose Your Fate" modal: category rows with live chances, content list, MP vote strip. |

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
