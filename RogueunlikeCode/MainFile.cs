using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace Rogueunlike.RogueunlikeCode;

//You're recommended but not required to keep all your code in this package and all your assets in the Rogueunlike folder.
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Rogueunlike"; //At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        // No version in this banner: ModManager is still mid-load here and the manifest
        // lookup can miss (see ModWireCheck.ModVersion — reading it this early is what
        // once cached "unknown" into the MP handshake).
        Logger.Info("Rogueunlike loading.");
        // Per-feature-group patching (one broken game seam disables ONE feature, loudly,
        // instead of killing the mod) + startup drift/asset checks. Map of every game
        // API touched: docs/GAME-API-SURFACE.md.
        ModPatcher.ApplyAll();
        ModHealthCheck.Run();
    }
}
