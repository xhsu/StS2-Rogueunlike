using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace Rogueunlike.RogueunlikeCode;

/// <summary>
/// Shared search-bar support for the modded reward screens. The widget is the compendium
/// Card Library's own <see cref="NSearchBar"/> (LineEdit + clear button + localized
/// "Search" placeholder), extracted from its scene. Callers graft it into their scrolling
/// content (via <see cref="CreateBar"/> + <see cref="PlaceCentered"/>) so it scrolls away
/// with the cards/potions/relics like the sort bar, instead of sitting fixed over the
/// relic row.
/// </summary>
internal static class ModSearch
{
    private static readonly string LibraryScene =
        SceneHelper.GetScenePath("screens/card_library/card_library");

    public const float Width = 460f;

    /// <summary>
    /// Grafts a search bar onto <paramref name="parent"/> (must be in-tree so _Ready wires
    /// the text area and clear button) and normalizes its internal layout. Null if
    /// extraction fails. Follow with <see cref="PlaceCentered"/> to position it.
    /// </summary>
    public static NSearchBar? CreateBar(Control parent)
    {
        Control donor = PreloadManager.Cache.GetScene(LibraryScene)
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        NSearchBar? bar = FindDescendant<NSearchBar>(donor);
        bar?.GetParent().RemoveChild(bar);
        donor.QueueFreeSafely();
        if (bar == null)
        {
            MainFile.Logger.Error("[search] NSearchBar not found in card library scene");
            return null;
        }
        float height = bar.Size.Y; // authored height, read before anchors can stretch it
        parent.AddChildSafely(bar); // entering the tree runs _Ready: wires text area, clear button, placeholder

        // In the library a container stretches the bar at runtime; extracted pre-layout it
        // keeps a tiny authored size (the clear button sat on the typed text). Impose a
        // real width and pin the internals: the text field spans the bar but stops short
        // of the right-aligned X, so long input auto-scrolls with the caret instead of
        // disappearing under the button.
        bar.CustomMinimumSize = new Vector2(Width, height);
        Control clear = bar.GetNode<Control>("ClearButton");
        clear.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterRight, Control.LayoutPresetMode.KeepSize);
        LineEdit text = bar.TextArea;
        text.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        text.OffsetRight = -clear.Size.X;
        return bar;
    }

    /// <summary>Anchor the bar horizontally centred in its parent, top edge at <paramref name="top"/> (parent-local).</summary>
    public static void PlaceCentered(NSearchBar bar, float top)
    {
        float h = bar.CustomMinimumSize.Y;
        bar.AnchorLeft = 0.5f;
        bar.AnchorRight = 0.5f;
        bar.AnchorTop = 0f;
        bar.AnchorBottom = 0f;
        bar.OffsetLeft = -Width / 2f;
        bar.OffsetRight = Width / 2f;
        bar.OffsetTop = top;
        bar.OffsetBottom = top + h;
    }

    /// <summary>
    /// The Card Library's canonicalisation, applied to both query and haystack:
    /// strip markup tags, collapse whitespace, lower-case.
    /// </summary>
    public static string Canon(string text) =>
        NSearchBar.Normalize(NSearchBar.RemoveHtmlTags(text));

    /// <summary>
    /// Space = AND: every space-separated token of the (canonicalised) query must appear
    /// somewhere in the (canonicalised) haystack — "sly rare" finds rare cards with Sly.
    /// A single-token query behaves like plain Contains.
    /// </summary>
    public static bool Matches(string haystack, string canonQuery)
    {
        foreach (string token in canonQuery.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            if (!haystack.Contains(token))
                return false;
        return true;
    }

    private static T? FindDescendant<T>(Node node) where T : class
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
}
