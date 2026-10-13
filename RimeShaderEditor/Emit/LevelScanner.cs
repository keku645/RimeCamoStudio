using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RimeShaderEditor.Emit;

/// <summary>
/// Finds the levels a bake can target by looking at the installed game rather than carrying a list. A hardcoded
/// map list would be wrong for anyone missing a DLC, and would quietly offer targets whose shaderdb cannot be
/// read. Levels appear as &lt;root&gt;/Data/Win32/Levels/&lt;Name&gt;/&lt;Name&gt;.toc, both in the base install and
/// again under Update/Patch and Update/Xpack1..5, so names are de-duplicated.
/// </summary>
public static class LevelScanner
{
    /// <summary>Level names in the install, de-duplicated and sorted. Empty when the path is not a BF3 install.</summary>
    public static List<string> Scan(string p_GamePath)
    {
        var s_Found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(p_GamePath) || !Directory.Exists(p_GamePath))
            return new List<string>();

        foreach (var s_Root in Roots(p_GamePath))
        {
            if (!Directory.Exists(s_Root))
                continue;

            foreach (var s_Directory in Directory.EnumerateDirectories(s_Root))
            {
                var s_Name = Path.GetFileName(s_Directory);

                // The .toc next to the .sb is what makes it a real superbundle rather than a stray folder.
                if (!File.Exists(Path.Combine(s_Directory, s_Name + ".toc")))
                    continue;

                // First spelling wins, so the base install's casing is what the user sees.
                if (!s_Found.ContainsKey(s_Name))
                    s_Found[s_Name] = s_Name;
            }
        }

        return s_Found.Values.OrderBy(p_N => p_N, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Roots(string p_GamePath)
    {
        yield return Path.Combine(p_GamePath, "Data", "Win32", "Levels");

        var s_Update = Path.Combine(p_GamePath, "Update");
        if (!Directory.Exists(s_Update))
            yield break;

        foreach (var s_Pack in Directory.EnumerateDirectories(s_Update))
            yield return Path.Combine(s_Pack, "Data", "Win32", "Levels");
    }

    /// <summary>
    /// Bucket for the picker. Multiplayer, the five expansions, singleplayer and co-op are all valid targets -
    /// a shader lives in whichever level's database uses it, and SP/COOP levels have databases like any other.
    /// Anything that is not a playable level (the front end, the loading movie) lands in "Other" rather than
    /// being hidden, because a shader could legitimately be in one and silently dropping targets is worse.
    /// </summary>
    public static string GroupOf(string p_Level)
    {
        var s_Upper = p_Level.ToUpperInvariant();
        if (s_Upper.StartsWith("MP_", StringComparison.Ordinal))
            return "Multiplayer";

        if (s_Upper.StartsWith("XP", StringComparison.Ordinal))
            return "Expansions";

        if (s_Upper.StartsWith("SP_", StringComparison.Ordinal))
            return "Singleplayer";

        if (s_Upper.StartsWith("COOP_", StringComparison.Ordinal))
            return "Co-op";

        return "Other";
    }

    /// <summary>Group order for the picker; anything unlisted sorts last.</summary>
    public static int GroupRank(string p_Group) => p_Group switch
    {
        "Multiplayer" => 0,
        "Expansions" => 1,
        "Singleplayer" => 2,
        "Co-op" => 3,
        _ => 4,
    };

    /// <summary>
    /// The shaderdb resource name for a level. Lowercase on purpose: resource names are matched lowercase, and
    /// the folder on disk is capitalised (MP_017), which is not what the engine asks for.
    /// </summary>
    public static string ShaderDbFor(string p_Level)
    {
        var s_Lower = p_Level.ToLowerInvariant();
        return $"levels/{s_Lower}/{s_Lower}/shaderdb";
    }
}
