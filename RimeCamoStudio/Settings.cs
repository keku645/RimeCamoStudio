using System;
using System.IO;
using Microsoft.Win32;
using RimeShaderEditor;

namespace RimeCamoStudio;

/// <summary>
/// The two folders the studio cannot work without, and where the tools it drives live.
/// Nothing is baked into the build: the game is found through the installer's own registry key.
/// </summary>
public static class Settings
{
    /// <summary>Test seams point this at a scratch folder so a run never touches the real cache.</summary>
    internal static string? CacheOverride;

    /// <summary>
    /// The user's Battlefield 3 folder: the one he chose (the first-run window, Settings ▸ Folders), which the installer's registry key only
    /// pre-fills — a game installed without that key is found all the same. Read-only, always: the studio never writes a byte into it.
    /// </summary>
    public static string GamePath => EditorSettings.Load().GamePath.Trim().TrimEnd('\\', '/');

    /// <summary>
    /// Everything dumped out of the game and everything built. Grows to gigabytes.
    ///
    /// ⛔ THE SAME FOLDER THE SHADER EDITOR USES, and not a second one of our own. The editor's mesh picker
    /// looks for a dump under ITS cache and mounts the game when it is missing — so a separate cache would
    /// mean dumping all 59 weapons a second time, in front of the user, for files we already have. The file
    /// names match too (both turn '/' into '_'), which is what makes sharing it work at all.
    /// </summary>
    public static string CacheFolder => CacheOverride ?? EditorSettings.Load().OutputFolder;

    public static string MeshCache => Path.Combine(CacheFolder, "meshcache");
    public static string TextureCache => Path.Combine(CacheFolder, "texcache");

    public static bool LooksLikeGame(string? p_Folder) =>
        !string.IsNullOrWhiteSpace(p_Folder) && Directory.Exists(p_Folder) &&
        (File.Exists(Path.Combine(p_Folder, "bf3.exe")) || Directory.Exists(Path.Combine(p_Folder, "Data")));

    /// <summary>The RimeREPL the studio drives — the shader editor's lookup (MainWindow.FindRimeRepl): the one the studio ships first.</summary>
    public static string? FindRimeRepl() => MainWindow.FindRimeRepl();

    public static string? DetectGamePath()
    {
        foreach (var s_Key in new[]
                 {
                     @"SOFTWARE\WOW6432Node\EA Games\Battlefield 3",
                     @"SOFTWARE\EA Games\Battlefield 3",
                 })
        {
            try
            {
                using var s_RegistryKey = Registry.LocalMachine.OpenSubKey(s_Key);
                if (s_RegistryKey?.GetValue("Install Dir") is not string s_Path)
                    continue;

                s_Path = s_Path.TrimEnd('\\', '/');
                if (Directory.Exists(s_Path))
                    return s_Path;
            }
            catch (Exception)
            {
                // Unreadable key: try the next candidate.
            }
        }

        return null;
    }
}
