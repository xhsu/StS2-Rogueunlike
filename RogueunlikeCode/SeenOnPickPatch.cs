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

// The counterpart QoL to click-to-discover: compendium-new pickables carry a small
// static star pinned to the icon's top-right corner — the game's own star sprite, the
// one card/relic text embeds for star costs, so the art language matches. (Cards keep
// the reward screen's sparkle particles instead; see CardGridSparklePatch.) Pinned by
// anchors so it follows the icon through layout and hover scaling, appended last so it
// draws above icon and outline. Callers keep the node handle and free it when a
// candidate-click discovers the item. This replaced an earlier modulate pulse: relic
// and potion outline/modulate channels render far too inconsistently to read.
internal static class ModUnseenFx
{
    private const string StarTexturePath = "res://images/packed/sprite_fonts/star_icon.png";
    private const float StarSize = 26f; // ponytail: corner badge size; tune by eye

    public static TextureRect? AddStar(Control host)
    {
        Texture2D? texture;
        try
        {
            texture = ResourceLoader.Load<Texture2D>(StarTexturePath);
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Error($"[unseen star] texture load failed, no badge: {e}");
            return null;
        }
        if (texture == null)
            return null;
        var star = new TextureRect
        {
            Name = "ModUnseenStar",
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        host.AddChildSafely(star);
        // Pin to the host's top-right corner (slight overhang), whatever its size is.
        star.AnchorLeft = 1f;
        star.AnchorRight = 1f;
        star.AnchorTop = 0f;
        star.AnchorBottom = 0f;
        star.OffsetLeft = -StarSize * 0.7f;
        star.OffsetRight = StarSize * 0.3f;
        star.OffsetTop = -StarSize * 0.3f;
        star.OffsetBottom = StarSize * 0.7f;
        return star;
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
