using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace StS2Mod.StS2ModCode;

//You're recommended but not required to keep all your code in this package and all your assets in the StS2Mod folder.
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "StS2Mod"; //At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        Logger.Info("Hello from StS2Mod! Mod loaded successfully.");

        Harmony harmony = new(ModId);
        harmony.PatchAll();
    }
}
