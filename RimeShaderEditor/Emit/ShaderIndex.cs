using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RimeShaderEditor.Emit;

/// <summary>
/// One node of the shader browser tree: either a folder or a shader.
/// </summary>
public sealed class ShaderTreeNode
{
    public string Name { get; init; } = "";

    /// <summary>Full shader name for a leaf, null for a folder.</summary>
    public string? ShaderName { get; init; }

    public List<ShaderTreeNode> Children { get; } = new();

    public bool IsShader => ShaderName != null;

    /// <summary>Shaders at or below this node, so a folder can say how much it holds.</summary>
    public int ShaderCount => IsShader ? 1 : Children.Sum(p_C => p_C.ShaderCount);
}

/// <summary>
/// The browser's catalogue: which shaders a level's database contains, and their hierarchy.
///
/// Built from the SHADER NAMES themselves, not from the EBX tree, for three reasons: the names already ARE the
/// hierarchy ("Props/StreetProps/OilDrumBarrel_01/SS_OilDrumBarrel_01"); every branch therefore leads to a real
/// shader, so the "only branches that hold shaders" pruning is free rather than a filtering pass; and it lists
/// exactly what is in THAT level's database, which is exactly what can be targeted and baked. It also avoids
/// depending on a local EBX dump, which only exists on one machine.
/// </summary>
public static class ShaderIndex
{
    /// <summary>One catalogued shader: its name, how many streamable textures it has, and its detected contract.</summary>
    public sealed class Entry
    {
        public string Name { get; init; } = "";
        public int Textures { get; init; }

        /// <summary>Empty when the level was catalogued before contracts were recorded.</summary>
        public string Family { get; init; } = "";
        public int RenderTargets { get; init; }
        public string Shape { get; init; } = "";

        /// <summary>
        /// The register of each material texture, in streamable order, e.g. "5,7,6,8". Cached because it is the
        /// only reliable slot mapping and re-reading it would cost another game mount.
        /// </summary>
        public string Registers { get; init; } = "";

        public bool HasContract => Family.Length > 0;

        /// <summary>Tab-separated so the cache stays a plain text file anyone can read.</summary>
        public string ToLine() => $"{Name}\t{Textures}\t{Family}\t{RenderTargets}\t{Shape}\t{Registers}";

        public static Entry FromLine(string p_Line)
        {
            var s_Parts = p_Line.Split('\t');
            return new Entry
            {
                Name = s_Parts[0],
                Textures = s_Parts.Length > 1 && int.TryParse(s_Parts[1], out var s_T) ? s_T : 0,
                Family = s_Parts.Length > 2 ? s_Parts[2] : "",
                RenderTargets = s_Parts.Length > 3 && int.TryParse(s_Parts[3], out var s_R) ? s_R : 0,
                Shape = s_Parts.Length > 4 ? s_Parts[4] : "",
                Registers = s_Parts.Length > 5 ? s_Parts[5] : "",
            };
        }
    }

    /// <summary>Where a level's cached catalogue lives, under the editor's output folder.</summary>
    public static string PathFor(string p_OutputFolder, string p_Level) =>
        Path.Combine(p_OutputFolder, "shaderindex", p_Level.ToLowerInvariant() + ".txt");

    /// <summary>
    /// Reads the extracted DXBC tree that `extract_shader_dxbc` writes for a whole level (one numbered folder per
    /// shader plus index.txt) and detects each shader's contract. Done once per level, inside the same game mount
    /// that produced the name list, because detecting on demand would cost a multi-minute mount per double-click.
    /// </summary>
    public static Dictionary<string, ShaderContract> DetectAll(string p_ExtractRoot)
    {
        var s_Result = new Dictionary<string, ShaderContract>(StringComparer.OrdinalIgnoreCase);
        var s_IndexPath = Path.Combine(p_ExtractRoot, "index.txt");
        if (!File.Exists(s_IndexPath))
            return s_Result;

        foreach (var s_Line in File.ReadAllLines(s_IndexPath))
        {
            var s_Parts = s_Line.Split('\t');
            if (s_Parts.Length < 2)
                continue;

            var s_Folder = Path.Combine(p_ExtractRoot, s_Parts[0]);
            if (!Directory.Exists(s_Folder))
                continue;

            // ⛔ THE SAME CHOICE THE TRANSLATOR MAKES, from the same function. This used to take the LARGEST
            // pixel shader while the translator took the smallest g-buffer one, so on any shader whose
            // permutations differ in binding - the Barrack's lightmapped variant holds its material at t5..t8,
            // its plain one at t1..t4 - the cached slot map described a different permutation than the graph on
            // screen, and the textures landed in registers nothing sampled.
            var s_Files = new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").ToList();
            if (s_Files.Count == 0)
                continue;

            var s_File = ChooseGBufferPermutation(s_Files, new List<string>(), s_Parts[1]);

            try
            {
                s_Result[s_Parts[1]] = ShaderContract.Detect(File.ReadAllBytes(s_File.FullName));
            }
            catch
            {
                // A shader we cannot read is simply left without a contract; the editor then says "unknown"
                // instead of guessing a family.
            }
        }

        return s_Result;
    }

    /// <summary>
    /// Which of a shader's permutations to show as "the" graph.
    ///
    /// A prop ships a dozen-odd pixel permutations per render path, and they are not variations on one theme: the
    /// small ones are the depth-only pass and the big ones are the same material plus engine extras (light probe
    /// terms, lightmaps) whose constants this editor cannot yet name. Picking by SIZE therefore picks the one
    /// carrying the most machinery the translator has to fake with zeros — which is what the first run of this
    /// button did, on the oil drum, with four unmodelled cbuffer reads.
    ///
    /// So it picks by what the bytecode DECLARES instead: the number of render targets separates the g-buffer
    /// pass (3-4) from the depth-only one (0-1) with no size threshold to tune, and among those the smallest is
    /// the plainest shading of that material. On the oil drum that lands on the exact permutation whose graph was
    /// proven bit-exact against DICE's.
    /// </summary>
    public static FileInfo ChooseGBufferPermutation(List<FileInfo> p_Candidates, List<string> p_Log) =>
        ChooseGBufferPermutation(p_Candidates, p_Log, null);

    /// <summary>
    /// The vertex declarations a shader's SUBJECTS draw with ("0x…", the one most of their sections draw with first), by shader name — the
    /// host's own knowledge (the studio's cache logs); null or empty when unknown. See <see cref="ChooseGBufferPermutation(List{FileInfo}, List{string}, string?)"/>.
    /// </summary>
    public static Func<string, IReadOnlyList<string>>? SubjectDeclarations { get; set; }

    /// <summary>
    /// <see cref="ChooseGBufferPermutation(List{FileInfo}, List{string})"/>, for the shader named: when the host names the declaration its
    /// SUBJECTS draw with (<see cref="SubjectDeclarations"/>, most-drawn first) and the plainest g-buffer permutation belongs to another one,
    /// the plainest g-buffer permutation of the subjects' declaration instead (the extraction's sidecar, solutions.txt, says each
    /// permutation's declaration). ⭐ MEASURED 2026-09-28 (the US support's legs, where the tiling had to be editable):
    /// `characterroot_xp4`'s plainest is sol0, declaration 0xCE4574FD (skin and faces), which samples CamoTile at a FIXED point and never
    /// reads CamoTileFactor — while its soldier sections draw with 0x56576EBB (22) and 0x5E57E699 (3), the cloth, whose plainest g-buffer
    /// permutation (sol19) tiles CamoTile by the second uv set × CamoTileFactor and lays a cloth detail over it: every Aftermath soldier's
    /// graph showed the camo as one flat colour. ⛔ The host names subjects only where it must (the studio: the Soldiers catalogue's
    /// meshes) — the first version counted every cached mesh, kept sol0 (a weapon-cache mesh draws 0xCE4574FD with it) and moved 14
    /// vehicle and prop shaders that were right. Without a sidecar, or with no subject named, the choice is exactly as before.
    /// </summary>
    public static FileInfo ChooseGBufferPermutation(List<FileInfo> p_Candidates, List<string> p_Log, string? p_Shader)
    {
        var s_Plainest = ChoosePlainestGBufferPermutation(p_Candidates, p_Log);
        // (CAMO_PERMUTATION_OLD=1: the plainest always, as before — the way back)
        if (p_Shader == null || Environment.GetEnvironmentVariable("CAMO_PERMUTATION_OLD") == "1" ||
            SubjectDeclarations?.Invoke(p_Shader) is not { Count: > 0 } s_Theirs ||
            DeclarationsOfPermutations(p_Candidates) is not { Count: > 0 } s_DeclarationOf ||
            !s_DeclarationOf.TryGetValue(s_Plainest.Name, out var s_PlainestDeclaration) ||
            s_Theirs[0].Equals(s_PlainestDeclaration, StringComparison.OrdinalIgnoreCase))
            return s_Plainest;

        foreach (var s_Declaration in s_Theirs)
        {
            var s_Own = p_Candidates.Where(p_F => s_DeclarationOf.TryGetValue(p_F.Name, out var s_Of) &&
                                                  s_Of.Equals(s_Declaration, StringComparison.OrdinalIgnoreCase)).ToList();
            var s_GBuffer = s_Own
                .Select(p_F =>
                {
                    var s_Targets = 0;
                    try { s_Targets = ShaderContract.Detect(File.ReadAllBytes(p_F.FullName)).RenderTargets; }
                    catch { }

                    return (File: p_F, Targets: s_Targets);
                })
                .Where(p_S => p_S.Targets >= 3)
                .OrderByDescending(p_S => p_S.File.Name.Contains("Dx11", StringComparison.OrdinalIgnoreCase))
                .ThenBy(p_S => p_S.File.Length)
                .ThenBy(p_S => p_S.File.Name, StringComparer.Ordinal)
                .ToList();
            if (s_GBuffer.Count == 0)
                continue;

            var s_Chosen = s_GBuffer[0];
            p_Log.Add($"⭐ {s_Plainest.Name} draws declaration {s_PlainestDeclaration}, not the one {p_Shader}'s subjects draw with " +
                      $"({string.Join(", ", s_Theirs)}, most first); translating the plainest g-buffer permutation of {s_Declaration} instead, " +
                      $"{s_Chosen.File.Name} ({s_Chosen.File.Length} B, {s_Chosen.Targets} targets).");
            return s_Chosen.File;
        }

        return s_Plainest;
    }

    /// <summary>Each permutation file's vertex declaration ("0x…"), from the extraction's sidecar beside them (solutions.txt); empty without one.</summary>
    private static Dictionary<string, string> DeclarationsOfPermutations(List<FileInfo> p_Candidates)
    {
        var s_Out = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var s_Sidecar = p_Candidates.Count > 0 ? Path.Combine(p_Candidates[0].DirectoryName ?? "", "solutions.txt") : "";
        if (s_Sidecar.Length == 0 || !File.Exists(s_Sidecar))
            return s_Out;

        foreach (var s_Line in File.ReadLines(s_Sidecar))
        {
            var s_Space = s_Line.IndexOf(' ');
            var s_At = s_Line.IndexOf(" decl=", StringComparison.Ordinal);
            if (s_Space <= 0 || s_At < 0)
                continue;

            var s_End = s_Line.IndexOf(' ', s_At + 6);
            var s_Declaration = s_End < 0 ? s_Line[(s_At + 6)..] : s_Line[(s_At + 6)..s_End];
            s_Out[s_Line[..s_Space] + "_ps.dxbc"] = s_Declaration.ToUpperInvariant().Replace("0X", "0x");
        }

        return s_Out;
    }

    private static FileInfo ChoosePlainestGBufferPermutation(List<FileInfo> p_Candidates, List<string> p_Log)
    {
        var s_Scored = p_Candidates
            .Select(p_F =>
            {
                var s_Targets = 0;
                try { s_Targets = ShaderContract.Detect(File.ReadAllBytes(p_F.FullName)).RenderTargets; }
                catch { }

                return (File: p_F, Targets: s_Targets);
            })
            .ToList();

        var s_GBuffer = s_Scored.Where(p_S => p_S.Targets >= 3).ToList();
        if (s_GBuffer.Count == 0)
        {
            // No GBuffer pass at all — a forward (transparent) or reduced-target shader. The material
            // permutations write the MOST targets the entry has (a depth-only pass writes one and samples
            // nothing but alpha), and among those the smallest is the plainest — the same philosophy as
            // the GBuffer pick. Largest-first chose a lightmapped variant here, which is the wrong
            // reference for the emitter's plain layout.
            var s_MostTargets = s_Scored.Max(p_S => p_S.Targets);
            var s_Fallback = s_Scored
                .Where(p_S => p_S.Targets == s_MostTargets)
                .OrderByDescending(p_S => p_S.File.Name.Contains("Dx11", StringComparison.OrdinalIgnoreCase))
                .ThenBy(p_S => p_S.File.Length)
                .First();

            p_Log.Add($"{p_Candidates.Count} pixel permutation(s), none writing 3+ render targets; " +
                      $"translating the plainest {s_MostTargets}-target one, {s_Fallback.File.Name} " +
                      $"({s_Fallback.File.Length} B).");

            return s_Fallback.File;
        }

        // Dx11 first: it is the path this editor's own preview and diff run on, so a graph taken from it can be
        // compared against them without a second variable in play.
        var s_Chosen = s_GBuffer
            .OrderByDescending(p_S => p_S.File.Name.Contains("Dx11", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p_S => p_S.File.Length)
            .First();

        p_Log.Add($"{p_Candidates.Count} pixel permutation(s), {s_GBuffer.Count} of them g-buffer; translating " +
                  $"the plainest, {s_Chosen.File.Name} ({s_Chosen.File.Length} B, {s_Chosen.Targets} targets). " +
                  $"The others range {s_GBuffer.Min(p_S => p_S.File.Length)}-{s_GBuffer.Max(p_S => p_S.File.Length)} B.");

        return s_Chosen.File;
    }

    public static List<string> Load(string p_Path)
    {
        if (!File.Exists(p_Path))
            return new List<string>();

        return File.ReadAllLines(p_Path)
            .Select(p_L => p_L.Trim())
            .Where(p_L => p_L.Length > 0 && !p_L.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_L => p_L, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void Save(string p_Path, IEnumerable<string> p_Names)
    {
        var s_Directory = Path.GetDirectoryName(p_Path);
        if (s_Directory != null)
            Directory.CreateDirectory(s_Directory);

        File.WriteAllLines(p_Path, p_Names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_N => p_N, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses Rime's shaderdb dump, whose lines read
    /// "  SHADER: &lt;name&gt;  solutions=8 streamableTex=2". The texture count comes along because it is what a
    /// texture-dump size estimate has to be built from - guessing that number would be worse than not showing it.
    /// Kept tolerant on purpose: a stricter parser would silently yield an empty tree if the format shifted.
    /// </summary>
    public static List<(string Name, int Textures)> ParseDump(IEnumerable<string> p_Lines)
    {
        var s_Found = new List<(string Name, int Textures)>();
        foreach (var s_Line in p_Lines)
        {
            var s_Trimmed = s_Line.Trim();
            var s_Marker = s_Trimmed.IndexOf("SHADER:", StringComparison.OrdinalIgnoreCase);
            if (s_Marker < 0)
                continue;

            var s_Rest = s_Trimmed[(s_Marker + "SHADER:".Length)..].Trim();
            var s_Textures = 0;

            var s_TexMarker = s_Rest.IndexOf("streamableTex=", StringComparison.OrdinalIgnoreCase);
            if (s_TexMarker >= 0)
            {
                var s_Digits = new string(s_Rest[(s_TexMarker + "streamableTex=".Length)..]
                    .TakeWhile(char.IsDigit).ToArray());

                int.TryParse(s_Digits, out s_Textures);
            }

            // The name ends where the trailing "solutions=..." metadata begins.
            var s_Cut = s_Rest.IndexOf("  ", StringComparison.Ordinal);
            var s_Name = (s_Cut >= 0 ? s_Rest[..s_Cut] : s_Rest).Trim();
            if (s_Name.Length > 0)
                s_Found.Add((s_Name, s_Textures));
        }

        // ⚠ THE DUMP LISTS EVERY SHADER ONCE PER RENDER-PATH DATABASE (Dx10Plus and Dx11), so each name arrives
        // TWICE. Anything downstream that keys by name has to see distinct shaders or it throws on the duplicate.
        // The larger texture count wins: a path that declares more streamable textures is the one to budget for.
        return s_Found
            .GroupBy(p_F => p_F.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p_G => (p_G.Key, p_G.Max(p_F => p_F.Textures)))
            .OrderBy(p_F => p_F.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Folds the names into a tree. Folders with a single child are NOT collapsed: the paths mirror the game's
    /// own layout and users navigate by it, so shortening them would stop matching what they know.
    /// </summary>
    public static ShaderTreeNode Build(IEnumerable<string> p_Names, string p_RootLabel = "Shaders")
    {
        var s_Root = new ShaderTreeNode { Name = p_RootLabel };

        foreach (var s_Full in p_Names)
        {
            var s_Parts = s_Full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (s_Parts.Length == 0)
                continue;

            var s_Current = s_Root;
            for (var i = 0; i < s_Parts.Length - 1; i++)
            {
                var s_Folder = s_Current.Children.FirstOrDefault(
                    p_C => !p_C.IsShader && string.Equals(p_C.Name, s_Parts[i], StringComparison.OrdinalIgnoreCase));

                if (s_Folder == null)
                {
                    s_Folder = new ShaderTreeNode { Name = s_Parts[i] };
                    s_Current.Children.Add(s_Folder);
                }

                s_Current = s_Folder;
            }

            s_Current.Children.Add(new ShaderTreeNode { Name = s_Parts[^1], ShaderName = s_Full });
        }

        Sort(s_Root);
        return s_Root;
    }

    /// <summary>Folders first, then shaders, each alphabetically - the order a file browser uses.</summary>
    private static void Sort(ShaderTreeNode p_Node)
    {
        var s_Ordered = p_Node.Children
            .OrderBy(p_C => p_C.IsShader ? 1 : 0)
            .ThenBy(p_C => p_C.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        p_Node.Children.Clear();
        p_Node.Children.AddRange(s_Ordered);

        foreach (var s_Child in p_Node.Children)
            Sort(s_Child);
    }

    /// <summary>
    /// Names matching a filter, for the search box. Matches on the whole path so typing "barrel" finds it
    /// wherever it lives, and typing "streetprops" narrows to that folder.
    /// </summary>
    public static List<string> Filter(IEnumerable<string> p_Names, string p_Query)
    {
        var s_Query = p_Query.Trim();
        if (s_Query.Length == 0)
            return p_Names.ToList();

        return p_Names
            .Where(p_N => p_N.Contains(s_Query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
