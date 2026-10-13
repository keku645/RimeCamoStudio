using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RimeCamoStudio.Cache;

/// <summary>
/// What one material of one mesh binds, as read out of the game's mesh-variation database.
/// </summary>
public sealed class MaterialBinding
{
    public string Shader { get; init; } = "";
    public string Mesh { get; init; } = "";

    /// <summary>0 is the base entry — the weapon as it ships, with no camo picked.</summary>
    public uint Variation { get; init; }

    /// <summary>
    /// The material's index in the MESH's own material list — the game's material ID, the same number the
    /// mesh dump prints as material= and the studio shows in its picker. -1 when the scan predates the
    /// field (a cache written before the command printed it).
    ///
    /// ⛔ IT IS WHAT TELLS TWO MATERIALS OF THE SAME MESH AND THE SAME SHADER APART. The LAV-25's hull and
    /// its ATGM launchers both wear vehiclepreset_mud; keyed without this, the second read replaced the
    /// first and the hull was dressed in the launchers' textures — a flat grey vehicle.
    /// </summary>
    public int MaterialId { get; init; } = -1;

    public string VariationName { get; init; } = "";

    /// <summary>
    /// Texture parameter and the asset bound to it, e.g. ("Diffuse", "Weapons/M416/M416_D").
    ///
    /// ⛔ A LIST, NOT A DICTIONARY, and that is not a detail. ONE MESH CAN CARRY SEVERAL MATERIALS THAT USE
    /// THE SAME SHADER — the L85A2 binds one Diffuse for its body and another for its sight rail, both
    /// through weaponpreset3p — and the scan prints no material index to tell them apart. Keyed by parameter
    /// name, the second silently replaced the first and three textures vanished from the list of what has to
    /// travel, which is the dangling reference that draws a weapon WHITE.
    /// </summary>
    public List<(string Param, string Texture)> Textures { get; } = new();

    /// <summary>
    /// The look values the material feeds the shader — CamoTiling, SmoothnessMasked, WearAmount…
    /// Read per weapon rather than inherited: the numbers hard-coded in the original script were the M416's.
    /// A list for the same reason as above: several materials, same shader, different values.
    /// </summary>
    public List<(string Param, float[] Value)> Vectors { get; } = new();
}

/// <summary>
/// Parses the SHMATMESH/SHMATTEX/SHMATVEC lines the database scan prints.
///
/// It is a text format on purpose: the scan is the only thing that knows what a weapon actually binds, and
/// guessing the names instead (Weapons/&lt;X&gt;/&lt;X&gt;_D) is the same assumption that already failed on
/// mesh names for 24 of the 59 weapons.
/// </summary>
public static class MaterialScan
{
    public static List<MaterialBinding> Parse(IEnumerable<string> p_Lines)
    {
        var s_ByKey = new Dictionary<string, MaterialBinding>(StringComparer.OrdinalIgnoreCase);

        foreach (var s_Line in p_Lines)
        {
            var s_Trimmed = s_Line.Trim();
            var s_Kind = s_Trimmed.Split(':', 2)[0];
            if (s_Kind is not ("SHMATMESH" or "SHMATTEX" or "SHMATVEC"))
                continue;

            var s_Fields = Fields(s_Trimmed);
            if (!s_Fields.TryGetValue("mesh", out var s_Mesh) ||
                !s_Fields.TryGetValue("shader", out var s_Shader))
                continue;

            var s_Variation = s_Fields.TryGetValue("variation", out var s_Raw) &&
                              uint.TryParse(s_Raw, out var s_Parsed)
                ? s_Parsed
                : 0u;

            var s_MaterialId = s_Fields.TryGetValue("material", out var s_RawId) &&
                               int.TryParse(s_RawId, out var s_ParsedId)
                ? s_ParsedId
                : -1;

            // ⛔ THE MATERIAL IS PART OF THE KEY. Without it the hull and the launchers of the LAV-25 — same
            // mesh, same preset — are one row, and whichever came last owns the textures.
            var s_Key = $"{s_Mesh}|{s_Shader}|{s_Variation}|{s_MaterialId}";
            if (!s_ByKey.TryGetValue(s_Key, out var s_Binding))
                s_ByKey[s_Key] = s_Binding = new MaterialBinding
                {
                    Shader = s_Shader,
                    Mesh = s_Mesh,
                    Variation = s_Variation,
                    MaterialId = s_MaterialId,
                    VariationName = s_Fields.GetValueOrDefault("variationName", ""),
                };

            if (!s_Fields.TryGetValue("param", out var s_Param))
                continue;

            if (s_Kind == "SHMATTEX" && s_Fields.TryGetValue("texture", out var s_Texture))
            {
                if (!s_Binding.Textures.Contains((s_Param, s_Texture)))
                    s_Binding.Textures.Add((s_Param, s_Texture));
            }
            else if (s_Kind == "SHMATVEC" && s_Fields.TryGetValue("value", out var s_Value))
            {
                var s_Numbers = s_Value.Split(',')
                    .Select(p_C => float.TryParse(p_C, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var s_Number)
                        ? s_Number
                        : 0f)
                    .ToArray();

                if (!s_Binding.Vectors.Any(p_V => p_V.Param == s_Param && p_V.Value.SequenceEqual(s_Numbers)))
                    s_Binding.Vectors.Add((s_Param, s_Numbers));
            }
        }

        return s_ByKey.Values.ToList();
    }

    public static List<MaterialBinding> ParseFile(string p_Path) => Parse(File.ReadLines(p_Path));

    /// <summary>
    /// Every texture the entries bind, deduplicated — the list that has to travel with a bake.
    ///
    /// ⛔ IT IS NOT "the weapon's own two textures". Copying a mesh's entries brings the variations it
    /// already had, and those bind their own camo patterns; shipping fewer than the full list leaves
    /// dangling references and the OTHER skins of that weapon draw WHITE.
    /// </summary>
    public static List<string> TexturesOf(IEnumerable<MaterialBinding> p_Bindings) =>
        p_Bindings.SelectMany(p_B => p_B.Textures.Select(p_T => p_T.Texture))
            .Where(p_T => !string.IsNullOrWhiteSpace(p_T))
            // ⛔ "(unresolved)" is what the scan prints for a texture reference it could not name (a civilian
            // car's SootTexture): not an asset, and asking Rime to dump it cost ONE MOUNT on every vehicle
            // errand — "TEXTURES: 0/1 written, missing: (unresolved)" (2026-09-22).
            .Where(p_T => !p_T.StartsWith("(", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_T => p_T, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The keys the scan emits. Splitting happens between these, never on whitespace.</summary>
    private static readonly string[] s_Keys =
    {
        "shader", "mesh", "material", "variation", "variationName", "source", "param", "texture", "value",
    };

    /// <summary>
    /// Splits "key=value key=value" by locating the KNOWN keys and taking everything between them.
    ///
    /// ⛔ NOT A SPLIT ON SPACES. Asset names in this game contain them — "weapons/saiga20k/saiga 20k_d",
    /// "weapons/gadgets/combat_pda/combat pda_d" — and a whitespace split truncates those silently, which
    /// showed up only as three textures that failed to dump out of 231. A value ends where the next known
    /// key begins, so the order of the keys does not matter either.
    /// </summary>
    private static Dictionary<string, string> Fields(string p_Line)
    {
        var s_Found = new List<(int Start, int ValueStart, string Key)>();
        foreach (var s_Key in s_Keys)
        {
            var s_Needle = " " + s_Key + "=";
            var s_At = p_Line.IndexOf(s_Needle, StringComparison.Ordinal);
            if (s_At >= 0)
                s_Found.Add((s_At, s_At + s_Needle.Length, s_Key));
        }

        s_Found.Sort((p_A, p_B) => p_A.Start.CompareTo(p_B.Start));

        var s_Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < s_Found.Count; i++)
        {
            var s_End = i + 1 < s_Found.Count ? s_Found[i + 1].Start : p_Line.Length;
            s_Fields[s_Found[i].Key] = p_Line[s_Found[i].ValueStart..s_End].Trim();
        }

        return s_Fields;
    }
}
