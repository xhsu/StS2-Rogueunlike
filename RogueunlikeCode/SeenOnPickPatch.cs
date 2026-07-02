using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using System.Collections.Generic;

namespace Rogueunlike.RogueunlikeCode;

// Compendium discovery, re-ruled for the picker screens. Vanilla marks a card
// "seen" the moment an NCard renders a live run card (NCard.Model setter), and any
// hover tip embedding a card/relic/potion marks its canonical model (NHoverTipSet) —
// with full-pool pickers that would discover the whole roster at a glance. So while
// a picker is on screen the Mark*AsSeen funnel is suppressed, and the ONLY discovery
// path is the player clicking an item as their pending candidate. Confirming (or
// backing out afterwards) doesn't matter: candidacy is the reveal.
internal static class ModSeenGate
{
    private static readonly List<CanvasItem> _anchors = new();
    private static bool _bypass;

    /// <summary>Suppress discovery while <paramref name="anchor"/> is alive and visible on screen.</summary>
    public static void SuppressWhile(CanvasItem anchor)
    {
        _anchors.RemoveAll(n => !GodotObject.IsInstanceValid(n)); // prune freed screens
        _anchors.Add(anchor);
    }

    // Visibility, not just liveness: if a selection screen is pooled/hidden instead of
    // freed, a stale anchor must not keep suppressing combat/deck-view discovery.
    public static bool Suppressing =>
        !_bypass && _anchors.Exists(n => GodotObject.IsInstanceValid(n) && n.IsVisibleInTree());

    public static void MarkPicked(CardModel card) => Bypass(() => SaveManager.Instance.MarkCardAsSeen(card));
    public static void MarkPicked(RelicModel relic) => Bypass(() => SaveManager.Instance.MarkRelicAsSeen(relic));
    public static void MarkPicked(PotionModel potion) => Bypass(() => SaveManager.Instance.MarkPotionAsSeen(potion));

    private static void Bypass(System.Action mark)
    {
        _bypass = true;
        try { mark(); }
        finally { _bypass = false; }
    }
}

// The counterpart QoL to click-to-discover: compendium-new items are worth spotting, so
// pickable-but-undiscovered icons breathe white→gold on their outline (cards use the
// reward screen's own sparkle particles instead; see CardGridSparklePatch). The pulse
// rides the outline channel the picker does NOT use for selection/darkening — potions
// pulse SelfModulate (selection sets Modulate), relics pulse Modulate (selection sets
// SelfModulate) — so the effects multiply instead of fighting. The tween is bound to the
// outline node and dies with it; callers keep the handle only to stop the pulse early
// when a candidate-click discovers the item.
internal static class ModUnseenFx
{
    public static Tween StartPulse(CanvasItem outline, NodePath property)
    {
        Tween tween = outline.CreateTween().SetLoops();
        tween.TweenProperty(outline, property, StsColors.gold, 0.7)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        tween.TweenProperty(outline, property, Colors.White, 0.7)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        return tween;
    }
}

// The single funnel every vanilla trigger routes through (NCard render, hover tips,
// choose-a-card screens): SaveManager.Mark*AsSeen -> ProgressSaveManager.Mark*AsSeen.
// First-launch bootstrap (LoadProgress -> ProgressState directly) and the dev console
// bypass this funnel and stay untouched.
[HarmonyPatch(typeof(ProgressSaveManager))]
public static class SeenSuppressionPatch
{
    [HarmonyPrefix, HarmonyPatch(nameof(ProgressSaveManager.MarkCardAsSeen))]
    static bool Card() => !ModSeenGate.Suppressing;

    [HarmonyPrefix, HarmonyPatch(nameof(ProgressSaveManager.MarkRelicAsSeen))]
    static bool Relic() => !ModSeenGate.Suppressing;

    [HarmonyPrefix, HarmonyPatch(nameof(ProgressSaveManager.MarkPotionAsSeen))]
    static bool Potion() => !ModSeenGate.Suppressing;
}
