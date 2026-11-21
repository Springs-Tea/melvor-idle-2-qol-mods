using System;
using System.Runtime.InteropServices;

namespace GodotPlugins.Game;

// Godot looks up "GodotPlugins.Game.Main.InitializeFromGameProject" in the assembly named by
// dotnet/project/assembly_name. override.cfg points that name at us, so we are called instead of the game.
// IMPORTANT: no Godot or game types may appear in this class. They cannot be resolved until
// Bootstrap has installed its assembly resolver. (The real return type, godot_bool, is a byte.)
internal static class Main
{
    [UnmanagedCallersOnly(EntryPoint = "godotsharp_game_main_init")]
    private static byte InitializeFromGameProject(nint godotDllHandle, nint outManagedCallbacks, nint unmanagedCallbacks, int unmanagedCallbacksSize)
    {
        try
        {
            MelvorModLoader.Bootstrap.Run(godotDllHandle, outManagedCallbacks, unmanagedCallbacks, unmanagedCallbacksSize);
            return 1;
        }
        catch (Exception e)
        {
            MelvorModLoader.Log.Write("STARTUP ERROR: " + e);
            return 0;
        }
    }
}
