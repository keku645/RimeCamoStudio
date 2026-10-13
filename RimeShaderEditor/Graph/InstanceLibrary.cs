using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RimeShaderEditor.Graph;

/// <summary>
/// The on-disk library of instanceable graphs: every .json under Documents\RimeShaderEditor\instances that
/// contains an Instance Output node becomes a palette entry (category "Instances") named after its file.
/// Saving a fragment there and refreshing is the whole authoring workflow — no registration step, no
/// separate format: a fragment is an ordinary saved graph that happens to carry boundary nodes (and may
/// carry a preview root, which expansion ignores).
/// </summary>
public static class InstanceLibrary
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RimeShaderEditor", "instances");

    private static readonly Dictionary<string, ShaderGraph> s_Fragments = new(StringComparer.OrdinalIgnoreCase);
    private static bool s_Loaded;

    /// <summary>Idempotent first load, for code paths (headless emits) that never called Refresh.</summary>
    public static void EnsureLoaded()
    {
        if (!s_Loaded)
            Refresh(null);
    }

    /// <summary>
    /// Rescans the library directory, re-registers the palette entries and re-points the expander's
    /// resolver. Returns the number of fragments loaded; files that do not parse or carry no Instance
    /// Output are skipped with a log line — never silently.
    /// </summary>
    public static int Refresh(Action<string>? p_Log)
    {
        s_Loaded = true;
        s_Fragments.Clear();
        System.IO.Directory.CreateDirectory(Directory);

        foreach (var s_Path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var s_Name = Path.GetFileNameWithoutExtension(s_Path);
            ShaderGraph s_Graph;
            try
            {
                s_Graph = ShaderGraph.FromJson(File.ReadAllText(s_Path));
            }
            catch (Exception s_Exception)
            {
                p_Log?.Invoke($"instances: '{s_Name}' does not parse as a graph ({s_Exception.Message}) — skipped.");
                continue;
            }

            if (s_Graph.Nodes.All(p_N => p_N.Kind != "InstanceOutput"))
            {
                p_Log?.Invoke($"instances: '{s_Name}' has no Instance Output node — skipped (a fragment " +
                              "must expose at least one output).");
                continue;
            }

            s_Fragments[s_Name] = s_Graph;
        }

        Palette.RegisterInstanceGraphs(s_Fragments);
        InstanceExpander.Resolver = Get;
        return s_Fragments.Count;
    }

    public static ShaderGraph? Get(string p_Name) =>
        s_Fragments.TryGetValue(p_Name, out var s_Graph) ? s_Graph : null;

    /// <summary>True when the path lies inside the library, meaning a save there should refresh it.</summary>
    public static bool Contains(string p_Path)
    {
        try
        {
            return Path.GetFullPath(p_Path).StartsWith(Path.GetFullPath(Directory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
