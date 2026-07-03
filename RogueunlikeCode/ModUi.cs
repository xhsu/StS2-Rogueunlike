using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using System;
using System.Collections.Generic;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// The mod's shared UI plumbing: the modal-picker shell every full-screen picker is
/// assembled from, and the scene-scavenging node helpers. One home instead of a copy
/// per picker — if MegaCrit reshapes a donor scene, this is the only place to mend.
/// </summary>
internal static class ModUi
{
    private static readonly string SelectScene =
        SceneHelper.GetScenePath("screens/card_selection/simple_card_select_screen");

    // ---- modal picker shell ----

    /// <summary>
    /// Attach <paramref name="picker"/> to the enclosing screen that owns the current
    /// interaction — rewards screen, treasure room or merchant room — so the picker
    /// covers it and dies with it. Falls back to the tree root.
    /// </summary>
    public static void Mount(Node host, Control picker)
    {
        Node? attach = host;
        while (attach != null && attach is not NRewardsScreen
               && attach is not NTreasureRoom && attach is not NMerchantRoom)
            attach = attach.GetParent();
        attach ??= host.GetTree().Root;
        attach.AddChildSafely(picker);
    }

    /// <summary>Full-rect root, focus enabled, compendium discovery suppressed while visible.</summary>
    public static void SetupPickerRoot(Control root)
    {
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // Screens outside the ACTIVE screen context get FocusBehaviorRecursive=Disabled
        // (ScreenContextUtils) — at the merchant that killed the search bar's focus.
        // An explicit Enabled on our root overrides the inherited disable.
        root.FocusBehaviorRecursive = Control.FocusBehaviorRecursiveEnum.Enabled;
        ModSeenGate.SuppressWhile(root); // rendering/hovering a roster must not "discover" it
    }

    /// <summary>
    /// In-run there is no compendium backdrop; supply one. It also swallows every click
    /// aimed at the screen underneath, which is what makes the picker modal.
    /// </summary>
    public static void AddModalDim(Control root)
    {
        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.88f) };
        root.AddChildSafely(dim);
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        dim.MouseFilter = Control.MouseFilterEnum.Stop;
    }

    /// <summary>
    /// A borrowed compendium scene's back ribbon is wired to a submenu stack we don't
    /// have; strip those connections and rewire it to <paramref name="onBack"/>.
    /// </summary>
    public static void RewireBackRibbon(NBackButton back, Action onBack)
    {
        foreach (Godot.Collections.Dictionary conn in
                 back.GetSignalConnectionList(NClickableControl.SignalName.Released))
        {
            // IsConnected guard: on a cold-instantiated scene the list can report a
            // scene-authored connection the engine refuses to disconnect (logged as a
            // "nonexistent connection" error in godot.log otherwise).
            Callable callable = conn["callable"].AsCallable();
            if (back.IsConnected(NClickableControl.SignalName.Released, callable))
                back.Disconnect(NClickableControl.SignalName.Released, callable);
        }
        back.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onBack()));
        back.MoveToHidePosition();
        back.Enable();
    }

    /// <summary>
    /// The checkmark confirm button from the simple-card-select scene, mounted on
    /// <paramref name="root"/> (it self-anchors bottom-right), wired to
    /// <paramref name="onConfirm"/> and disabled until a pick is pending.
    /// </summary>
    public static NConfirmButton ExtractConfirmButton(Control root, Action onConfirm)
    {
        Control select = PreloadManager.Cache.GetScene(SelectScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        NConfirmButton? confirm = Extract<NConfirmButton>(select);
        select.QueueFreeSafely();
        if (confirm == null)
            throw new InvalidOperationException("NConfirmButton not found in donor scene");
        root.AddChildSafely(confirm);
        confirm.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onConfirm()));
        confirm.Disable();
        return confirm;
    }

    // ---- localization ----

    // Every mod string has an entry in the "gameplay_ui" loc table: vanilla keys where the
    // game already localizes the text, ROGUEUNLIKE.* keys otherwise, merged from the .pck's
    // Rogueunlike/localization/{lang}/gameplay_ui.json; without the .pck we fall back to
    // English so every feature keeps working. Raw text, not SmartFormat — mod entries may
    // carry string.Format placeholders ({0}), which SmartFormat would reject to Sentry.
    public static string Loc(string key, string fallback) =>
        LocString.GetIfExists("gameplay_ui", key)?.GetRawText() ?? fallback;

    public static string SelectCardLabel => Loc("CHOOSE_CARD_HEADER", "Choose a Card");     // vanilla
    public static string SelectRelicLabel => Loc("CHOOSE_RELIC_HEADER", "Choose a Relic");  // vanilla
    public static string SelectPotionLabel => Loc("ROGUEUNLIKE.SELECT_POTION.label", "Select a Potion");
    public static string SelectAncientLabel => Loc("ROGUEUNLIKE.SELECT_ANCIENT.label", "Select an Ancient");

    // ---- scene scavenging ----

    /// <summary>Depth-first search for the first descendant of type <typeparamref name="T"/>.</summary>
    public static T? FindDescendant<T>(Node node) where T : class
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is T deep)
                return deep;
        }
        return null;
    }

    /// <summary>Find and detach the first descendant of type <typeparamref name="T"/> from a donor scene.</summary>
    public static T? Extract<T>(Node donor) where T : Node
    {
        T? node = FindDescendant<T>(donor);
        node?.GetParent().RemoveChild(node);
        return node;
    }

    /// <summary>Collect every descendant of type <typeparamref name="T"/>, depth-first.</summary>
    public static void Collect<T>(Node node, List<T> into) where T : class
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T match)
                into.Add(match);
            Collect(child, into);
        }
    }

    /// <summary>
    /// The ancestor of <paramref name="n"/> that is a direct child of <paramref name="root"/>
    /// (so its scene-authored position, which was relative to the full-screen root, is
    /// preserved when re-parented into another full-screen view). Null if not under root.
    /// </summary>
    public static Node? AncestorUnder(Node root, Node? n)
    {
        if (n == null)
            return null;
        while (n.GetParent() != null && n.GetParent() != root)
            n = n.GetParent();
        return n.GetParent() == root ? n : null;
    }
}
