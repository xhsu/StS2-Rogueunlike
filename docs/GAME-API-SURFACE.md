# Game API Surface

Every interface between Rogueunlike and Slay the Spire 2 (and why each is the seam of
choice), so a game update can be triaged in minutes instead of re-derived from scratch.

**Last verified against game `0.107.1`** (= `ModHealthCheck.VerifiedGameVersion` — bump it
there after re-verifying). Decompiled source for diffing lives at
`C:\Users\Hydrogen\Documents\GitHub\sts2-modding-mcp\decompiled\` (one `.cs` per class).

## How breakage presents, and where to look

| Symptom | Cause class | First stop |
|---|---|---|
| `[patcher] group 'X' failed to install` at startup | A hooked method was renamed/removed/re-signatured | The group's target list below → re-find the method in the new decompile |
| `[health] game version … differs` | Any game update (informational) | Re-verify the **mirrored bodies** section below |
| `[health] UnknownMapPointOdds bucket order changed` | Roll's dict order changed | `UnknownPools.ChancesCore`/`Peek` walk order |
| `[health] donor asset missing` | A scavenged scene/texture moved | The **donor assets** section |
| Feature runs but behaves subtly wrong after an update | A **mirrored body** or **semantic assumption** drifted (the silent class) | The per-feature sections below |
| `MissingFieldException`/`MissingMethodException` mid-feature | A publicized internal member was renamed (shipped dll only; a rebuild catches it at compile time) | Rebuild against the new `sts2.dll`; compiler errors point at every rename |

General triage: **rebuild first.** Almost every internal-member rename becomes a compile
error because internals are accessed directly through publicized references, not via
reflection strings. What survives a clean rebuild is exactly the string-named patch
targets, the mirrored bodies and the semantic assumptions listed here.

## Official extension points (stable by contract — never swap these out)

| API | Used for |
|---|---|
| `[ModInitializer]` (`MegaCrit.Sts2.Core.Modding`) | Entry point |
| HarmonyLib (game-bundled `0Harmony.dll`) | All runtime patching |
| `ModelDb` (`GetById/GetByIdOrNull/GetCategory/All*`) | Every model lookup; canonical instance space |
| `Hook.*` (`ModifyCardRewardCreationOptions`, `TryModifyCardRewardOptions`, `ModifyMerchantCardPool/Rarity`, `ModifyNextEvent`, `ModifyUnknownMapPointRoomTypes`, `ModifyOddsIncreaseForUnrolledRoomType`, `AfterRoomEntered`) | Staying inside vanilla pipelines so other mods' hooks keep working |
| `INetMessage` + `MessageTypes.Initialize` (mod-type splicing) | `RewardPickMessage`, `ShopAssignMessage`, `AncientDesignateMessage` — **wire ids are load-order dependent**, hence the `rl_wirecheck` handshake |
| `AbstractConsoleCmd` + `ConsoleCmdGameAction` (DevConsole reflection registration) | `rl_wirecheck`, `rl_ancient`, `rl_unknown` — lockstep, sender-attributed, order-immune |
| `LocString` / `gameplay_ui` table | All UI strings (mod keys with English fallbacks in code) |
| `PreloadManager.Cache` / `SceneHelper` / `ResourceLoader` | Scene/asset loading |
| `TaskHelper.RunSafely`, `AddChildSafely`, `QueueFreeSafely` | Fire-and-forget + Godot lifetime hygiene |

There is no official API for reward/shop/chest/map **flow** — those seams are Harmony-only
by necessity. BaseLib standardizes content *addition* (cards/relics/potions registration,
config, tooltip utilities) and offers nothing for these flows either; re-checked 2026-07-04.

## Patch-group map (mirror of `ModPatcher.ApplyAll`)

A group failing rolls back completely and disables ONLY its feature. String-named targets
are protected/virtual members the publicizer skips (`IncludeVirtualMembers=false`) — they
cannot use `nameof`, so renames there surface at patch time, not compile time.

### seen-gate — discovery rule
| Target | Kind | Note |
|---|---|---|
| `ProgressSaveManager.MarkCardAsSeen/MarkRelicAsSeen/MarkPotionAsSeen` | prefix (nameof) | The single funnel all vanilla seen-marking routes through. If marking moves elsewhere, pickers start discovering on render. |

Events/encounters need no gate: their profile marking derives from map-point **history**
at travel (`ProgressSaveManager` ← `MapPointHistoryEntry.ModelId`), which browsing can't touch.

### wire-check — MP handshake triggers
| Target | Kind | Note |
|---|---|---|
| `RunManager.GenerateRooms` | postfix (nameof) | Announce at run start (also: reset hooks for ancient/unknown vote maps live here in their groups) |
| `Hook.AfterRoomEntered` | postfix (nameof) | Announce backstop on loads/rejoins |

Semantics relied on: `ModManager.GetLoadedMods` exposes `assembly`+`manifest.version`;
`MessageTypes.TypeToId<T>` exists and is stable per session; `RunManager.IsSingleplayerOrFakeMultiplayer`.

### reward-net — MP pick/assign sync (failure poisons the wire check ⇒ MP goes vanilla)
| Target | Kind | Note |
|---|---|---|
| `RewardsSetSynchronizer` ctor (typed 4-arg) | postfix | Handler registration on `_messageBuffer`; **ctor signature is part of the target** |
| `RewardsSetSynchronizer.Dispose` | postfix (nameof) | Unregistration |
| `RewardsSetSynchronizer.BeginRewardsSet` | prefix (nameof) | Buffered-pick drain BEFORE vanilla drains its own buffered claims |
| `MerchantRoom."EnterInternal"` | postfix (string) | Weak room handle for late shop-assign messages |

Publicized internals: `_messageBuffer` (+`RegisterMessageHandler/CurrentLocation`),
`_netService.SendMessage`, `_playerCollection.GetPlayer`, `GetRewardStateForPlayer`
(→ `.nextId`, `.rewardsStack[…].set`), `RewardsSet.Id/Rewards`, `RelicReward._relic/_predeterminedRelic`,
`PotionReward.Potion`, `MerchantRoom.Inventories`, `MerchantInventory.AllEntries`.
Assumptions: reward claims ride a **per-sender FIFO** direct-message channel (the pick must
ride the same one); wire address = top-of-stack set id + index (mirror of `SelectLocalReward`);
`AllEntries` enumerates in identical order on every client.

### card-rewards — feature #1
| Target | Kind | Note |
|---|---|---|
| `CardFactory.CreateForReward(Player,int,CardCreationOptions)` | prefix (nameof, typed overload) | THE pool replacement. Gated on `CardCreationFlags.IsCardReward` — **assumption: only `CardReward.Populate` sets that flag** |
| same method | postfix | Origin witness for NON-IsCardReward rolls (weak table card→options; result is a materialized List — iterating cannot re-roll) |
| `CardReward` fixed-list ctor (typed 5-arg) | postfix | Kaleidoscope-class rewards: if every offered card was witnessed, expand `_cards` to all C/U/R of the rolls' pools via feature #1's per-card pipeline; hand-built lists (tutorial) stay vanilla. `OptionCount`/reroll untouched |
| `NCardRewardSelectionScreen.RefreshOptions` | prefix (nameof) | UI seam; ≤5 options → vanilla fan layout |
| `NCardRewardSelectionScreen.GetCardHolder` | prefix (nameof) | Fly-to-deck VFX redirection |
| `NCardGrid."GetCardVisibility"` | postfix (string) | Per-grid visibility override table |
| `NCardGrid."InitGrid"` (explicit empty args — 2nd async overload exists!) + `"AssignCardsToRow"` | postfix (string) | Sparkle/tint re-apply on scroll recycle |

Pipeline mirror (see mirrored bodies): pool→`GetPossibleCards`→per-card create→upgrade
roll→`TryModifyCardRewardOptions`. Internals: `_ui`, `_banner`, `_options`, `_extraOptions`,
`_cardRows`, `NCard._sparkles`, `NCardHighlight._shaderMaterial/_shaderParameterWidth` (+ the
pooled-NCard healing in `ModCardGridPicker.ForceHighlight`). `"Skip"` alternative matched by
`OptionId == "Skip"` (string).

### reward-pickers — features #2/#3
| Target | Kind | Note |
|---|---|---|
| `NRewardButton.GetReward` | prefix ×2 (nameof) | Pick-then-claim, `_passThrough` re-entry; potion and relic classes coexist on the same method (disjoint `IsActiveFor`) |
| `NRewardButton.Reload` | postfix ×2 (nameof) | Row label/icon cosmetics |
| `PotionReward."ExtraHoverTips"` / `RelicReward."ExtraHoverTips"` getters | prefix (string) | Roll-hiding tips |
| `PotionReward.Populate` | prefix+postfix (nameof) | Roll witness: picker only for rewards `Populate` actually rolled; predetermined potions (Potion Courier etc.) stay vanilla — the relic side gets this via `_predeterminedRelic` |
| `ToyBox.AfterObtained` / `NeowsBones.AfterObtained` | prefix+finalizer ×2 (nameof) | Roll brackets: these two build PREDETERMINED rewards from genuine rolls (bag front-pulls marked wax / a shuffle of `NeowsBones.GetValidRelics`), constructed in the async stub's first synchronous segment — rewards tagged inside get the picker (Toy Box: bag pools + wax carried onto the pick; Neow's Bones: the snapshot pool minus owned/sibling relics). Bought/scripted predetermined (FakeMerchant, tutorial) stay vanilla |
| `RelicReward` predetermined ctor (typed 2-arg) | postfix | Tags rewards constructed inside a roll bracket |

Pool mirrors: relic = `RelicGrabBag._deques[rarity]` ∩ `IsAllowed` (+rolled) — mirrors
`RelicFactory` pulls, **deliberately ignoring dry-deque rarity escalation**; potion =
`PotionFactory.GetPotionOptions` (stateless = whole pool, no mirror needed — but valid ONLY
for Populate-rolled rewards, hence the roll witness). Bag consumption
belongs to the claim's `RelicCmd.Obtain` (by ModelId, every client) — never consume manually.

### shop — feature #4
| Target | Kind | Note |
|---|---|---|
| `MerchantInventory.CreateForNormalMerchant` | prefix+finalizer+postfix (nameof) | Stock-time seen suppression (unconditional: closes a vanilla MP compendium leak) + eligibility registration |
| `NMerchantInventory.Initialize` | postfix (nameof) | Shop screen as suppression anchor |
| `MerchantCard/Relic/PotionEntry."RestockAfterPurchase"` | postfix (string) | Re-shade restocked slots |
| `NMerchantSlot."OnSelected"` | prefix (string) | Click → picker instead of buy |
| `NMerchantCard/Relic/Potion."UpdateVisual"` (+`NMerchantCard.OnInventoryOpened`) | postfix (string) | Shade rendering |
| `NMerchantCard/Relic/Potion."CreateHoverTip"` | prefix (string) | Roll-hiding tips; assigned-slot tip re-shown via `AccessTools.Method(slot.GetType(), "CreateHoverTip")` (protected virtual → reflection) |
| `NMerchantCard."OnPreview"` | prefix (string) | Right-click inspect would leak the roll |
| `NLabPotionHolder."OnFocus"` / `NRelicCollectionEntry."OnFocus"` / `NCardHolder."CreateHoverTips"` | postfix ×3 (string) | Cost preview: while an assign picker is open, pickable items' tips get the exact assignment price prepended (session-gated; other uses of these scenes never see it) |

Assignment mirrors each entry's vanilla stock path (`CreateCard`+`RollForUpgrade(-∞)`+
`ModifyMerchantCardCreationResults`+`CalcCost` / `SetModel`+bag removal / `Model`+`CalcCost`) —
`Apply*Assignment` is shared with the MP replay. Cost preview mirrors each `CalcCost` body
(jitter ranges card/potion 0.95–1.05, relic 0.85–1.15, card sale ÷2, rounding) with the
jitter drawn from a CLONE of the Shops rng (`Seed`+`Counter`) — the very draw the real
assignment consumes — then the `Cost` getter's `Hook.ModifyMerchantPrice`; base costs come
from the entries' own publicized `GetCost` tables + `RelicModel.MerchantCost`. Internals:
`_player`, `_cardPool`, `_cardType`, `_cardRarity`, `CreationResult`, `Model`, `_merchantRug`,
`_costLabel`, `_isHovered`, `Entry`, `IsOnSale`, `NLabPotionHolder._potionNode`,
`NRelicCollectionEntry.relic`, `NCardHolder.CardModel`. Unaffordable preview prices tint
the tip's `"Title"` MegaLabel `StsColors.red` (vanilla's own unaffordable treatment;
cosmetic — a renamed node keeps the default color).

### chest — feature #3.1
| Target | Kind | Note |
|---|---|---|
| `TreasureRoomRelicSynchronizer.BeginRelicPicking` | postfix (nameof) | Pool expansion into `_currentRelics` (the shared vote-index list) |
| `TreasureRoomRelicSynchronizer.OnPicked` | prefix (nameof) | Round-based vote resolution (replaces `AwardRelics` semantics entirely — **the one intentional flow replacement**) |
| `NTreasureRoomRelicCollection.InitializeRelics` | prefix (nameof) | Table layout for the expanded list |
| `NTreasureRoomRelicHolder."OnFocus"` | postfix (string) | Shaded-holder tip swap |
| `NTreasureRoomRelicCollection."_ExitTree"` | postfix (string) | Teardown |
| `ScreenStateTracker."GetCurrentScreen"` | postfix (string) | While chest rounds are live, `Rewards` reports rewrite to `SharedRelicPicking` (else `NHandImageCollection` re-asserts the OS cursor per peer tick — blinking) and `_hands` z-lifts above the overlay stack; pause etc. untouched. Details: TreasureChestPickerPatch.cs |

Vanilla pieces REUSED (not copied): `RelicPickingResult.GenerateRelicFight`, `RelicCmd.Obtain`,
`MoveToFallback`, `EndRelicVoting`, `PickRelicLocally`, `_hands.DoFight/GrabRelic`. The
synchronizer's `VotesChanged` event is raised through its backing field (`AccessTools.Field`,
null-guarded, health-checked) — no public raise API exists. Internals: `_currentRelics`,
`_votes`, `_rng`, `_sharedGrabBag._deques`, `_singleplayerSkipped`, `_predictedVote`,
`_holdersInUse`, `_multiplayerHolders`, `_hands`, `_fightBackstop`, `_cts`, both
`_relicPicking*TaskCompletionSource`s, holder `Relic/Index/VoteContainer(_allPlayers)/_*Glow`.
Chest rarities assumed `{Common, Uncommon, Rare}` (= `RelicFactory.RollRarity` outcomes).

### ancient — features #5/#5.1
| Target | Kind | Note |
|---|---|---|
| `NMapScreen.OnMapPointSelectedLocally` | prefix (nameof) | Click seam (Ancient nodes; disjoint from feature #6's prefix). Act-START ancients auto-enter with no click (`EnterAct`/`StartedWithNeow`) — feature #5.2's seam instead |
| `NMapScreen."_GuiInput"` | prefix (string) | Map input gate while a picker modal is up; also clears `_isDragging` (the opening click's release is swallowed by the gate — stuck map-drag otherwise) |
| `EventSynchronizer.BeginEvent` | **transpiler** (nameof) | Redirects the loop's one `canonicalEvent.ToMutable()` to `PickFor(...)` — vanilla body runs whole. Patch-time asserts (exactly 1 `ToMutable` call, 1 `Player` local) throw ⇒ ancient group off, loudly |
| `AncientEventModel."GenerateInitialOptionsWrapper"` | postfix (string) | Slot designation AFTER the vanilla roll |
| `RunManager.GenerateRooms` | postfix (nameof) | Pick-map reset |
| runtime: every `ModifierModel."GenerateNeowOption"` impl (reflection sweep) | prefix, lazy | Probe guards — modifier code must never execute while probing |
| `NEventRoom."_Ready"` | postfix (string) | Feature #5.2 seam: in-event designation modal for act-0 auto-entered Ancients (raw SceneTree timer — `Cmd.Wait` no-ops under FastMode=Instant); live swap + row rebuild via the room's own `SetOptions` |

### ancient-designate-net — feature #5.2 (own group; failure ⇒ `ForceBroken`, like reward-net)
| Target | Kind | Note |
|---|---|---|
| `EventSynchronizer` ctor (typed 5-arg) / `Dispose` | postfix ×2 | Register/unregister `AncientDesignateMessage` on the event message buffer — same stream as `OptionIndexChosenMessage`, whose per-sender FIFO is the ONLY ordering vs the sender's choice-by-index (the lockstep cmd queue can't order against it) |
| `AncientEventModel."SetInitialEventState"` | postfix (string) | Drain for designates that outran a loading client (event `Begin` is async — `BeginEvent` itself is too early); consumes ALL of that player's buffered entries |

Probe assumptions: `EventModel.Rng` is per-event and disposable (documented); `Owner`+`Rng`
settable on a mutable clone; `GenerateInitialOptions` invocable via reflection without side
effects on run state; authored options identified by unique `TextKey`, relic options by
`Relic.Id` (`RelicOption(pageName, customDonePage)` drops `customDonePage` — verified).
Internals: `_playerVotes`, `_events`, `_pendingOptionTasks`, `_pageIndex`, `_canonicalEvent`,
`_playerCollection`, `ActModel._rooms/_sharedAncientSubset`, `RoomSet.Ancient` (public setter,
saved as `AncientId`), `NAncientMapPoint._icon/_outline`, `GeneratedOptions` (private List —
live-swap keeps it reference-aligned with `_currentOptions`), `AllPossibleOptions`,
`NEventRoom._event/_isPreFinished/_cts/SetOptions`, `EventSynchronizer._netService/_messageBuffer`,
`ExtraRunFields.StartedWithNeow`. Live-apply assumptions: `SetInitialEventState` copies the
SAME `EventOption` objects of `GeneratedOptions` into `_currentOptions` (verified), and the
page may hold MORE than the roll (BaseLib appends Neow interactions — 4 on a 3-slot roll,
observed 2026-07-05) — hence the by-reference slot lookup; a missing roll skips that slot.

### unknown — feature #6
| Target | Kind | Note |
|---|---|---|
| `NMapScreen.OnMapPointSelectedLocally` | prefix (nameof) | Click seam (Unknown nodes) |
| `RunManager.EnterMapCoord` | prefix+postfix (nameof) | Vote tally → one-shot arm; postfix clears all votes |
| `UnknownMapPointOdds.Roll` | prefix + **transpiler** + postfix (nameof) | Prefix computes a forcing roll VALUE (simulation-verified); transpiler redirects Roll's one single-arg `NextFloat` call to `ForcedFloat(rng, max)` (real draw still advances the stream) — the vanilla body runs whole, so reset/escalation/hooks are vanilla-executed. Patch-time assert: exactly 1 such call site. Postfix drops the content arm if the outcome ≠ vote |
| `ActModel.PullNextEvent` / `PullNextEncounter` | prefix (nameof) | Swap the pick into the cursor slot (vanilla's own `RoomSet.SwapToOrCreateAtIndex` semantics) |
| `RunManager.GenerateRooms` | postfix (nameof) | Vote-map reset |

Assumptions: `RunManager.BuildRoomTypeBlacklist` is public+static with these inputs;
`_nonEventOdds`/`_baseOdds` iterate **Monster, Elite, Treasure, Shop** (health-checked live);
Roll consumes exactly ONE `NextFloat`; Event = remainder bucket, fallback = lowest enum;
tutorial forcing ⟺ `UnlockState.NumberOfRuns == 0`; `PullNextEvent` only serves ?-rolled
events (Ancients use `PullAncient`); `EnsureNextEventIsValid` ⟺ `IsAllowed && !VisitedEventIds`;
peek clones RNG via public `Seed`+`Counter`; event profile-discovery = `ProgressState.DiscoveredEvents`,
encounters = `UnlockState.HasSeenEncounter`.

## Mirrored logic — re-verify on game updates

No vanilla body is fully copied anymore (2026-07-04 rework): the two former full-body
mirrors (`EventSynchronizer.BeginEvent`, `UnknownMapPointOdds.Roll`) are now **call-site
transpilers** — the ORIGINAL bodies execute, one expression redirected, with patch-time
assertions that throw (⇒ group rollback, loud) if the body shape changes. What remains
mirrored is read-only or pool-derivation logic:

1. **`UnknownPools.ChancesCore/SimulateRoll/ForcingFloat/PeekNextEvent`** mirror Roll's
   bucket-walk order + sign rule and `RoomSet.EnsureNextEventIsValid`'s validity walk
   (decompiled: `MegaCrit.Sts2.Core.Odds\UnknownMapPointOdds.cs`, `…Rooms\RoomSet.cs`).
   Drift ⇒ wrong percentages/peek, or the forcing float landing off-target — which the
   pre-force simulation catches and degrades to a vanilla roll. Never wrong state.
2. **Pool-derivation mirrors** (selection = loot law): `ModRelicPickerUi.ValidDrops` ↔
   `RelicFactory` pulls; `ShopPicker.AssignCard` filter chain ↔ `CardFactory.CreateForMerchant`;
   `ShopPicker.ApplyCardAssignment` ↔ `MerchantCardEntry.Populate`;
   `ShowAllCardRewardsPatch` ↔ `CardFactory.CreateForReward` stages;
   `TreasureChestPicker` extras ↔ shared-bag `PullFromFront` reachability;
   `ModPotionPickerUi.Fill` ordering ↔ `NPotionLabCategory.LoadPotions`.
3. **Flow replacement** (intentional, not a mirror): `TreasureChestPicker.ResolveRound`
   replaces `AwardRelics` semantics wholesale; it REUSES the vanilla pieces
   (`GenerateRelicFight`, `RelicCmd.Obtain`, `MoveToFallback`, `EndRelicVoting`).

## Donor assets (scavenged vanilla UI; health-checked at startup)

| Asset | Provides | On failure |
|---|---|---|
| `screens/card_selection/simple_card_select_screen` | `NCardGrid`, `NConfirmButton` | Card pickers fall back to vanilla; other pickers lose confirm → whole picker aborts → callers fall back to vanilla |
| `screens/deck_view_screen` | Sort bar, upgrades tickbox, bottom label, back button | Chrome-less picker (each piece degrades independently) |
| `screens/card_library/card_library` | `NSearchBar` (+`"ClearButton"` child by name) | No search; picker works |
| `relic_collection` / `potion_lab` scenes (via `NRelicCollection.Create()` / `NPotionLab.Create()`) | Whole picker body | Picker throws → callers fall back to vanilla reward/slot |
| `res://images/packed/sprite_fonts/star_icon.png` | Unseen star badge | No badge |
| `FindChild` names: `"SortingBg"`, `"ScrollContainer"`, `"ViewUpgradesLabel"`, `"BottomLabel"`, `"BackButton"`, `"ClearButton"` | Widget lookup inside donors | Null-checked; piece skipped |

## Wire formats (bump the MOD version whenever any of this changes)

`rl_wirecheck <modVersion> <pickMsgId> <assignMsgId> <designateMsgId>` (4th id since v0.5.0;
older announces read as -1 ⇒ mismatch ⇒ broken, loudly) ·
`rl_ancient <ancientId> [slot:K|R:identity …]` ·
`rl_unknown <col> <row> <fate|monster|elite|treasure|shop|event> [modelEntry]` ·
`RewardPickMessage {location,setId,rewardIndex:8,isRelic,itemEntry}` ·
`ShopAssignMessage {location,entryIndex:8,itemEntry}` ·
`AncientDesignateMessage {location,ancientEntry,designations}` (designations = space-joined
`slot:K|R:identity` tokens, `rl_ancient`'s format).

Two clients with the same mod version MUST be wire-identical — the handshake compares
version strings and cannot see behavior differences (learned v0.3.0→v0.4.0: an old client
silently no-ops an unknown console cmd and the vote tally desyncs).
