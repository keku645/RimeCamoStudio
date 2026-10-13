using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RimeCamoStudio.Cache;

/// <summary>
/// ⭐ THE CACHE IS BUILT FROM THE USER'S OWN GAME, INSIDE THE STUDIO. A release ships nothing of the game: the weapons, attachments,
/// vehicles and soldiers are read out of the user's install once, into his cache folder. These are the steps that fill it, in the order
/// they depend on each other — the very errands a build of the repository runs by hand (--cache, --pretranslate…) — each one run as the
/// studio's own executable with its command line, with the user's game and cache folders handed over through
/// EditorSettings.GamePathVariable / OutputFolderVariable (a run without a window reads no settings file).
///
/// A step that finished leaves a mark in the cache (setup\&lt;key&gt;.done), and once the preparation has begun only those marks count —
/// a step stopped halfway leaves files behind, not a mark. A cache built by hand before this existed (no setup folder, and everything a
/// step's files can show is there) is adopted whole.
/// </summary>
internal static class CacheSetup
{
    /// <summary>
    /// One step: what it is called on screen, the command line it runs, and what its files look like in a cache that holds them (null when
    /// files cannot tell — a step that rewrites what an earlier one made).
    /// </summary>
    internal sealed record Step(string Key, string Title, string Detail, string[] Arguments, Func<string, bool>? Holds);

    private static bool HasFile(string p_Cache, string p_Name) => File.Exists(Path.Combine(p_Cache, p_Name));

    private static bool HasGraphs(string p_Cache, Func<string, bool> p_Which) =>
        Directory.Exists(Path.Combine(p_Cache, "camographs")) &&
        Directory.EnumerateFiles(Path.Combine(p_Cache, "camographs"), "*.json").Select(Path.GetFileName).Any(p_F => p_Which(p_F!));

    /// <summary>The steps, in order: each reads what the ones before it wrote.</summary>
    public static IReadOnlyList<Step> Steps { get; } = new[]
    {
        new Step("weapons", "Weapons",
            "Every weapon's models, the materials they wear, their textures and shaders, and where the first-person eye sits.",
            new[] { "--cache" }, p_C => HasFile(p_C, "material_scan.txt") && HasFile(p_C, "cache_meshes.log")),
        new Step("attachments", "Attachments",
            "The sights, grips, lasers and suppressors, their textures and shaders (reads what the weapons' step found).",
            new[] { "--cacheaccessories" }, p_C => HasFile(p_C, "cache_accessories.log")),
        new Step("translate", "Weapon shaders as graphs",
            "Each shader the weapons and attachments wear, translated into the node graph the studio edits.",
            new[] { "--pretranslate" }, p_C => HasGraphs(p_C, p_F => p_F.StartsWith("weapons_", StringComparison.OrdinalIgnoreCase))),
        new Step("vehicles", "Vehicles",
            "Every vehicle's models, materials, textures and shaders, read across the levels that field them.",
            new[] { "--cachevehicles" }, p_C => HasFile(p_C, "material_scan_vehicles.txt") && HasFile(p_C, "cache_vehicles.log")),
        new Step("vehiclegraphs", "Vehicle shaders as graphs",
            "The vehicles' shaders translated into graphs.",
            new[] { "--vehiclegraphs" }, p_C => HasGraphs(p_C, p_F => p_F.StartsWith("vehicles_", StringComparison.OrdinalIgnoreCase))),
        new Step("vehiclemeshes", "Vehicle models, laid out for their graphs",
            "The vehicle models read again with the texture coordinates their graphs use.",
            new[] { "--cachevehicles", "--redump", "--meshesonly" }, null),
        new Step("soldiers", "Soldiers",
            "The sixteen soldiers' models, materials, textures and shaders, and their graphs.",
            new[] { "--cachesoldiers" }, p_C => HasFile(p_C, "material_scan_soldiers.txt") && HasFile(p_C, "cache_soldiers.log")),
        new Step("samplers", "Texture sampling",
            "How each shader samples its textures (wrap, clamp), so the preview draws them as the game does.",
            new[] { "--cachesamplers" }, p_C => Directory.Exists(Path.Combine(p_C, "texcache")) &&
                                                Directory.EnumerateFiles(Path.Combine(p_C, "texcache"), "*.slots.json")
                                                    .Any(p_F => File.ReadAllText(p_F).Contains("\"Samplers\"", StringComparison.Ordinal))),
    };

    private static string MarkOf(string p_Cache, Step p_Step) => Path.Combine(p_Cache, "setup", p_Step.Key + ".done");

    /// <summary>A cache built by hand before the preparation existed: no setup folder, and every step whose files can tell holds them.</summary>
    private static bool HandBuilt(string p_Cache) =>
        !Directory.Exists(Path.Combine(p_Cache, "setup")) && Steps.All(p_S => p_S.Holds == null || p_S.Holds(p_Cache));

    /// <summary>Whether a step is done in this cache: it left its mark, or the cache was built by hand.</summary>
    public static bool IsDone(string p_Cache, Step p_Step) => File.Exists(MarkOf(p_Cache, p_Step)) || HandBuilt(p_Cache);

    /// <summary>Whether every step is done in this cache.</summary>
    public static bool IsComplete(string p_Cache) => Steps.All(p_S => IsDone(p_Cache, p_S));

    private static void Mark(string p_Cache, Step p_Step)
    {
        Directory.CreateDirectory(Path.Combine(p_Cache, "setup"));
        File.WriteAllText(MarkOf(p_Cache, p_Step), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>
    /// Runs one step: the studio's own executable with the step's command line, the user's two folders handed over, each line of its output
    /// to <paramref name="p_Line"/>. Kills it — and the RimeREPL it started — when <paramref name="p_Cancel"/> fires. True when it finished
    /// and the cache holds what it makes (a step can end with some item missing — a model only a level carries — and still have done its
    /// work: said in the output, and counted done).
    /// </summary>
    public static async Task<bool> RunAsync(Step p_Step, string p_Game, string p_Cache, Action<string> p_Line, CancellationToken p_Cancel)
    {
        var s_Self = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RimeCamoStudio.exe");
        var s_Info = new ProcessStartInfo(s_Self)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = p_Cache,
        };
        foreach (var s_Argument in p_Step.Arguments)
            s_Info.ArgumentList.Add(s_Argument);
        s_Info.Environment[RimeShaderEditor.EditorSettings.GamePathVariable] = p_Game;
        s_Info.Environment[RimeShaderEditor.EditorSettings.OutputFolderVariable] = p_Cache;

        Directory.CreateDirectory(p_Cache);
        using var s_Process = new Process { StartInfo = s_Info, EnableRaisingEvents = true };
        s_Process.OutputDataReceived += (_, p_Args) => { if (p_Args.Data != null) p_Line(p_Args.Data); };
        s_Process.ErrorDataReceived += (_, p_Args) => { if (p_Args.Data != null) p_Line(p_Args.Data); };
        s_Process.Start();
        s_Process.BeginOutputReadLine();
        s_Process.BeginErrorReadLine();

        using (p_Cancel.Register(() =>
               {
                   try { s_Process.Kill(true); }
                   catch (Exception) { /* already gone */ }
               }))
            await s_Process.WaitForExitAsync(CancellationToken.None);

        if (p_Cancel.IsCancellationRequested)
            return false;

        var s_Done = s_Process.ExitCode == 0 || p_Step.Holds?.Invoke(p_Cache) == true;
        if (s_Done)
            Mark(p_Cache, p_Step);
        if (s_Process.ExitCode != 0 && s_Done)
            p_Line($"(finished with some item missing — named above; the rest is in the cache)");
        return s_Done;
    }
}
