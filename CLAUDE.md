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

- **Selection pool = loot pool at the current context.** Pickable ⟺ that exact source could roll that exact item right now. The rest of the pool renders compendium-style: darkened (NotSeen) when unlocked-but-excluded here, locked when progression-locked. Never expand a loot pool.
- **Save-safety.** Every write lands in vanilla-reachable state (reward/entry model fields, vanilla `CalcCost`/upgrade-roll RNG advancement, grab-bag consumption mirroring the vanilla pull). Mod-only flags live in memory (weak tables); installing or uninstalling mid-run is always safe.
- **Discovery = candidate-click only.** Pickers register as `ModSeenGate` anchors so rendering/hovering a roster never marks items seen; clicking an item as your pending candidate is the only reveal.
- **SP/MP parity.** Every feature behaves identically in singleplayer and for every player in multiplayer (cosmetics excluded). Each mod decision syncs on the channel matching the vanilla flow it precedes: the treasure chest rides the vanilla index-vote action; the Ancient picker rides the networked dev-console action (`ConsoleCmdGameAction` + mod `AbstractConsoleCmd` — DevConsole loads mod subclasses officially); reward picks and shop assignments ride mod `INetMessage`s (`ModNetMsg.cs` — `MessageTypes.Initialize` officially merges `ReflectionHelper.GetSubtypesInMods<INetMessage>()`). Rule of thumb: a pick that precedes a vanilla *direct message* (reward claims) must ride the same message channel — per-sender FIFO puts it ahead of the claim, where a queue-routed command would race across channels; a pick that precedes a vanilla *action* rides the action queue. The merchant is vanilla-non-deterministic (purchases replicate by resulting model; remote inventory replicas go stale by design) — only the assignment replays, because it consumes grab-bag/Shops-rng/card-creation state that feeds later deterministic rolls. Mod wire-ids are index-based over MOD LOAD ORDER, and load order is a per-machine setting — so every sync feature is gated on `ModWireCheck` (a run-start `rl_wirecheck` handshake over the built-in console-cmd channel verifying mod version + message ids across all players); until verified, features stay pure vanilla in real MP. Mismatch or a client without the mod degrades features, never state. Shared-outcome decisions (the ? node, unlike per-player picks) are VOTES: each player's `rl_unknown` rides the console-cmd action channel before their map travel vote (travel needs all votes ⇒ all picks arrived), and every client tallies plurality with earliest-vote tiebreak at `EnterMapCoord`. Any new networked behavior (new cmd, new message semantics) REQUIRES a manifest version bump — same-version clients must be wire-identical or the handshake passes and the tally desyncs.

- **Blast-radius control.** Patches install per feature group (`ModPatcher`), each on its own Harmony id: a group whose game-side target vanished rolls back completely and logs which FEATURE degrades to vanilla — never a half-applied group, never a dead mod. Every `[HarmonyPatch]` class MUST be registered in a `ModPatcher.ApplyAll` group (startup logs an error for strays — `PatchAll` is gone). Cross-group dependencies are runtime poison flags (`reward-net` failing → `ModWireCheck.ForceBroken` → MP substitution never runs), not load-order assumptions. `ModHealthCheck` (same file) turns silent drift loud at startup: game-version stamp vs `VerifiedGameVersion` (bump after re-verifying mirrors on a game update), live probes (? odds bucket order, chest `VotesChanged` field), donor-asset existence. Map pickers fall back to VANILLA TRAVEL on picker failure (a broken modal must never make a node unclickable). **`docs/GAME-API-SURFACE.md` is the maintainer record of every game API touched** — patch targets, publicized internals, mirrored bodies with re-verify instructions, wire formats; update it when adding seams.

| File | Role |
|---|---|
| `MainFile.cs` | Entry point; delegates to `ModPatcher.ApplyAll()` + `ModHealthCheck.Run()`. |
| `ModPatcher.cs` | Feature-group patch isolation (transactional install/rollback per group, stray-patch guard) + `ModHealthCheck` startup drift/asset checks (`VerifiedGameVersion` stamp). |
| `ModUi.cs` | Shared shell every picker is assembled from (mount walk, modal dim, back-ribbon rewire, confirm button), mod localization (`Loc` + Select-a-X labels), node-scavenging helpers. Treasure-chest mounts insert BELOW `NHandImageCollection` — that screen hides the OS cursor and the networked hand sprites are everyone's pointer, so they must render above the picker. |
| `ModSearch.cs` | Card Library search-bar widget graft, canonicalisation, space=AND matching. |
| `SeenOnPickPatch.cs` | `ModSeenGate` discovery suppression (anchors + manual scopes), unseen-star badge (`ModUnseenFx`), the `Mark*AsSeen` funnel patch. |
| `ShowAllCardRewardsPatch.cs` | Feature #1, state side: card rewards generate the whole valid pool through the vanilla per-card pipeline. |
| `CardRewardScreenOverhaul.cs` | Feature #1, UI seam: replaces the reward screen body (≤5 options falls back to the vanilla fan layout); per-grid visibility/sparkle/gray-tint tables used by all card grids. |
| `ModCardGridPicker.cs` | Abstract base for card pickers: grid + deck-view chrome (sort bar, upgrades tickbox, back) + debounced search + selection. |
| `ModRewardScreenUi.cs` | Grand Card Selection — reward-screen host glue over the base. |
| `ModShopCardPickerUi.cs` | Merchant card picker — modal host glue over the base. |
| `PotionRewardPickerPatch.cs`, `RelicRewardPickerPatch.cs` | Features #2/#3: reward-row pick-then-claim seams (`NRewardButton.GetReward` prefix, `_passThrough` re-entry). MP: substitution applies only when the pick is wire-addressable and broadcast (`RewardPickMessage`) before the vanilla claim. Relic bag consumption belongs to the claim's own `RelicCmd.Obtain` (by ModelId, stackable-aware, every client) — never consume manually. |
| `ModNetMsg.cs` | Mod net layer: `RewardPickMessage` + `ShopAssignMessage` (mod `INetMessage`s riding the vanilla reward message channel — same FIFO stream as `RewardSelectedMessage`), registered on `RewardsSetSynchronizer`'s buffer (ctor/Dispose postfixes), vanilla-style not-yet-begun-set buffering drained in a `BeginRewardsSet` prefix, shop-assign replay onto the sender's inventory replica under seen-suppression. Handlers drop everything while `ModWireCheck.Broken` (mis-decoded bytes). |
| `ModWireCheck.cs` | MP handshake: `rl_wirecheck <version> <pickId> <assignId>` networked console cmd announced at run start (GenerateRooms/room-entry/first-rewards triggers); `Verified` ⟺ every player announced a matching tuple; `SyncReady` gates reward pickers, shop shading/assign, chest expansion and the Ancient picker — unverified real MP stays pure vanilla. Exists because mod `INetMessage` wire ids follow per-machine mod LOAD ORDER (`ModManager.SortModList` ties break on local settings). |
| `ModPotionPickerUi.cs`, `ModRelicPickerUi.cs` | Compendium-scene pickers (Potion Lab / Relic Collection) serving rewards, treasure and shop via `Attach(host, player, valid)`. |
| `TreasureChestPickerPatch.cs` | Feature #3.1: shaded treasure table; round-based picking over the deterministically expanded shared vote list; vanilla RPS fights; losers re-pick. |
| `ShopPickerPatch.cs` | Feature #4: merchant shade-and-assign slots — assignment mirrors each entry's vanilla stock path (`Apply*Assignment` internals shared with the MP replay); restock re-shades. Stock-time seen-suppression is unconditional — it also closes a vanilla MP leak where teammates' stock rolls mark into this client's compendium. |
| `AncientPickerPatch.cs` | Feature #5: pick your Ancient — `NMapScreen.OnMapPointSelectedLocally` seam on the act-start Ancient node; pool mirrors `ActModel.GenerateRooms` (unlocked natives + dealt shared subset). Opens whenever there is a real choice: >1 candidate, or 1 candidate with probe-measured dialogue variation (act 1 = {Neow}, pre-epoch acts) — no Ancient names hardcoded. Modifier-driven option chains (Sealed Deck's Neow) probe as no-variation (hook guards) and stay vanilla. SP: writes `RoomSet.Ancient` before vanilla travel. MP: broadcasts the pick via `AncientPickConsoleCmd` before the travel vote. |
| `AncientPickSyncCmd.cs` | Feature #5 MP: `rl_ancient <id> [slot:identity…]` networked console command (vanilla `ConsoleCmdGameAction` wire, lockstep, sender-attributed) + `AncientPickSync` pick/designation maps + `EventSynchronizer.BeginEvent` substitution via a call-site TRANSPILER (the loop's one `canonicalEvent.ToMutable()` → `PickFor(canonical, player, isPrefinished)`; vanilla body runs whole; patch-time asserts throw on body-shape change ⇒ ancient group off loudly). Zero save writes — reload degrades to the saved vanilla roll. All clients must run the mod. |
| `AncientOptionsPatch.cs` | Feature #5.1: slot-faithful option designation. `AncientOptionProbe` discovers each Ancient's per-slot pools empirically (throwaway clones + reseeded per-event RNG — documented independent of run RNG; fixed seeds → deterministic across clients); while probing, every `ModifierModel.GenerateNeowOption` impl is prefix-stubbed (lazy Harmony guards) so modifier code never executes — modifier-driven events probe to no-variation. `HasVariation` = any slot pool ≥2. `GenerateInitialOptionsWrapper` postfix swaps designated slots AFTER the vanilla roll (RNG identical), resolving by TextKey (authored) or via the event's own `RelicOption` (relic options). Designations consumed per player, in-memory only. |
| `ModAncientPickerUi.cs` | "Select an Ancient" modal over the map screen: row list (map icon, name, epithet, home act) + side info panel listing every option the clicked Ancient can offer (hover tips clipped — the tip column caps at viewport height); any unlocked row is inspectable, only pickable rows select; vanilla roll pre-selected. |
| `UnknownPickerPatch.cs` | Feature #6: choose the ? node outcome — category AND content. `OnMapPointSelectedLocally` seam (Unknown nodes; coexists with the Ancient prefix, disjoint point types). `UnknownPools` mirrors both vanilla roll stages: category pickable ⟺ final effective chance > 0 (current escalated odds, sign rule, `BuildRoomTypeBlacklist`, `Hook.ModifyUnknownMapPointRoomTypes` — Juzu/Golden Compass/Deadly Events handled generically); content pickable ⟺ member of the act's dealt `RoomSet` lists (events additionally valid-now). Peek = clone the dedicated odds Rng (public Seed/Counter) + pure reads. Substitution: `UnknownMapPointOdds.Roll` runs FULLY VANILLA — a prefix computes a forcing roll VALUE (midpoint of the voted bucket, simulation-verified) and a call-site transpiler feeds it in place of Roll's one `NextFloat` draw (real draw still advances the stream; any float in [0,1) is a legitimate roll ⇒ vanilla-reachable by construction; patch-time assert throws on body-shape change); postfix drops the content arm if the outcome ≠ vote. `PullNextEvent`/`PullNextEncounter` prefixes swap the pick into the cursor slot (vanilla's own tutorial primitive `SwapToOrCreateAtIndex` does the same). First-ever run (NumberOfRuns==0) stays vanilla. Shop/Treasure are category-only (features #4/#3.1 own their contents in-room). |
| `UnknownPickSyncCmd.cs` | Feature #6 MP: `rl_unknown <col> <row> <fate\|category> [modelEntry]` networked console cmd + `UnknownPickSync` vote registry (arrival order preserved) + plurality tally at `RunManager.EnterMapCoord` prefix (ties → earliest vote; non-voters count as fate; fate = vanilla roll), armed one-shot for that travel's roll/pull, all votes cleared on any travel + GenerateRooms. Zero save writes — quitting pre-travel reloads to an unclicked node. |
| `ModUnknownPickerUi.cs` | "Choose Your Fate" modal over the map: left category rows (Let Fate Decide with peeked outcome pre-selected; Enemy/Elite/Treasure/Merchant/Event with live chance %, darkened with reason when chance ≤ 0), right content list (dealt encounters/events pickable, act extras darkened, epoch-locked events locked, unseen-star badges — browsing/voting never marks seen; vanilla marks on entry via history) or explainer, live per-player vote strip (MP). |

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
