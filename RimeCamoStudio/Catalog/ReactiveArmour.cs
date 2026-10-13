using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimeCamoStudio.Cache;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// Where a vehicle's REACTIVE ARMOUR lives inside its hull mesh, so the studio can show it — or leave it
/// off — the way the game does: as a kit the player equips, not as part of the hull.
///
/// ⛔ IT IS NOT ONE SHAPE, IT IS THREE, and a rule written for one misses the others (measured 2026-09-22
/// over every cached vehicle dump):
///   (a) a material of its own whose textures are named *ActiveArmor / *ReactiveArmor — the LAV-25 (#7),
///       LAV_AD (#5), the Stryker (#5), the LAV paradrop (#7), the AAV-7A1 (#4, and #8 for its LOD);
///   (b) a section with a shader of its own, `<vehicle>/armor` — the BMP-2 (M_Rarmor) and the Tunguska
///       (M_ReactiveSide). ⚠ The material scan does NOT see these: that shader carries its art in the
///       shader's own fixed table, so nothing binds a texture per material. Its slot map does
///       (`vehicles/bmp2/armor` → BMP2/ReactiveArmor_D);
///   (c) PARTS of the shared kit-atlas section — the Sprut-SD (12, 13, 15) and the Tunguska (46), where the
///       blocks sit in the same draw as the stowage and only the composite's part index separates them.
///
/// ⛔ AND "IT REFERENCES THE UNLOCK" IS NOT "IT HAS THE PIECE": eleven vehicles reference the four shared
/// ReactiveArmor dummy unlocks, but the T-90, the M1A2 and the BTR-90 carry no armour geometry at all —
/// what the T-90 has is the SLAT CAGE (`armorcage`), which is another piece entirely. Only the geometry
/// answers, which is why this reads the dumps and not a name list.
/// </summary>
public static class ReactiveArmour
{
    /// <summary>The picker tag the window hands back when this piece is the one being looked at.</summary>
    public const string Tag = "ReactiveArmour";

    public const string Display = "reactive armour";

    /// <summary>
    /// The piece, as the preview needs it: whole sections, plus the parts of sections that are only partly
    /// armour. Triangle counts are kept so the picker can say how big it is and a seam can assert on it.
    /// </summary>
    public sealed class Piece
    {
        public List<int> WholeSections { get; } = new();
        public Dictionary<int, int[]> PartsBySection { get; } = new();
        public int Triangles { get; set; }

        public bool Any => WholeSections.Count > 0 || PartsBySection.Count > 0;

        public override string ToString() =>
            $"{Triangles} tri" +
            (WholeSections.Count > 0 ? $", section(s) [{string.Join(",", WholeSections)}]" : "") +
            (PartsBySection.Count > 0
                ? ", part(s) " + string.Join(" ", PartsBySection.Select(p_S =>
                    $"#{p_S.Key}=[{string.Join(",", p_S.Value)}]"))
                : "");
    }

    /// <summary>
    /// The tile atlas' block band. The kit shader picks its atlas by the SIGN of V and adds the tile atlas
    /// sampled at (u, v+1); the reactive blocks are the pattern in the top quarter of that tile atlas, and
    /// the stowage reads the main atlas below it. Measured: on the Sprut the split is 100%/0% — every
    /// triangle of parts 12/13/15 is in the band and no other part has a single one.
    /// </summary>
    private const float BandV = 0.25f;

    private const float BandShare = 0.6f;

    private sealed record Section(string Shader, string Material, float[] V, int[] Parts, int[] Indices);

    /// <summary>Reads what this needs out of a dump: per section the V of each vertex and its part.</summary>
    private static List<Section> Read(string p_Path)
    {
        var s_Sections = new List<Section>();

        using var s_Reader = new BinaryReader(File.OpenRead(p_Path));
        var s_Magic = new string(s_Reader.ReadChars(4));
        if (s_Magic != "RSM4" && s_Magic != "RSM5" && s_Magic != "RSM6")
            return s_Sections;

        var s_Floats = s_Magic is "RSM5" or "RSM6" ? 7 : 5;
        var s_HasParts = s_Magic == "RSM6";
        var s_Count = s_Reader.ReadInt32();

        for (var s_Index = 0; s_Index < s_Count; s_Index++)
        {
            var s_Shader = Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32())).Replace('\\', '/');
            var s_Material = Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32()));
            s_Reader.ReadInt32();
            s_Reader.ReadInt32();

            var s_VertexCount = s_Reader.ReadInt32();
            var s_V = new float[s_VertexCount];
            for (var i = 0; i < s_VertexCount; i++)
            {
                s_Reader.ReadSingle();
                s_Reader.ReadSingle();
                s_Reader.ReadSingle();
                s_Reader.ReadSingle();
                s_V[i] = s_Reader.ReadSingle();
                if (s_Floats == 7)
                {
                    s_Reader.ReadSingle();
                    s_Reader.ReadSingle();
                }
            }

            var s_IndexCount = s_Reader.ReadInt32();
            var s_Indices = new int[s_IndexCount];
            for (var i = 0; i < s_IndexCount; i++)
                s_Indices[i] = s_Reader.ReadInt32();

            var s_Parts = new int[s_VertexCount];
            if (s_HasParts)
                for (var i = 0; i < s_VertexCount; i++)
                    s_Parts[i] = s_Reader.ReadInt32();
            else
                Array.Fill(s_Parts, -1);

            s_Sections.Add(new Section(s_Shader, s_Material, s_V, s_Parts, s_Indices));
        }

        return s_Sections;
    }

    /// <summary>
    /// Whether a shader draws armour the vehicle only has WITH THE KIT — its own plates (`…/armor`) or the
    /// SLAT CAGE (`…/armorcage`).
    ///
    /// ⛔ THE CAGE BELONGS TO THE KIT, and reading it as a separate thing left it on a vehicle that should
    /// have none (seen 2026-09-22, with the game beside the studio: on the Sprut the rear cage still had to be
    /// moved into the reactive kit — without reactive armour the vehicle does not have it). The data agrees: the
    /// kit is equipped in ZONES — the shared unlocks are Front/Left/Right/REAR — and the rear zone of a Sprut
    /// or an M1A2 is exactly that cage (measured by isolating the part and looking at it).
    /// </summary>
    private static bool IsArmourShader(string p_Shader)
    {
        var s_Leaf = p_Shader.Split('/').LastOrDefault() ?? "";
        return s_Leaf.Equals("armor", StringComparison.OrdinalIgnoreCase) ||
               s_Leaf.Equals("armor_lod", StringComparison.OrdinalIgnoreCase) ||
               s_Leaf.Equals("armorcage", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hull PARTS that are armour on vehicles whose plates carry no art of their own — they are drawn by
    /// the hull's own material, with the hull's own texture, so nothing in the data tells them apart.
    ///
    /// ⛔ MEASURED BY LOOKING, one vehicle at a time (`--viewshot &lt;v&gt; &lt;dir&gt; parts=17,18`), which is why they
    /// are written here and not guessed by a rule about thin boxes — a faulty rule would take the skirts too.
    /// The M1A2's are its side plates (17, 18: the row of blocks along each flank, 432 tri) and its rear slat
    /// cage (28, 290 tri); the count matches its three unlock zones, Left/Right/Rear.
    /// </summary>
    private static readonly Dictionary<string, int[]> s_HullParts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vehicles/m1a2/m1abrams_mesh"] = new[] { 17, 18, 28 },

        // The BTR-90's kit is a SLAT CAGE down each flank, across the back AND across the nose — looked at the
        // same way. Part 9 is the FRONT zone (three panels wrapping the nose, 3.5 m wide at z=+3.03): with
        // the game open, the front part had been left out (2026-09-22) — the four unlock zones are
        // Front/Left/Right/Rear, and a list of three was one short.
        ["vehicles/xpack01/btr-90/btr90_mesh"] = new[] { 9, 18, 28, 31 },

        // ⛔ AND THE T-90'S SIDE PLATES ARE THE KIT TOO — seen with the game open: the T-90 has the same as the
        // M1A2, plus metal plates sticking out along its flanks, and those are part of the armour too. I had
        // left them out because isolated they look like painted skirts (they carry the tank's number); what
        // settles it is the game, not the render. Its rear cage comes in through `armorcage`, as before.
        ["vehicles/t90/t90_mesh"] = new[] { 17, 18 },

        // The Stryker's armour is a material of its own (a), BUT two square slat panels of the kit hang
        // beside the hull as parts of the KIT-ATLAS section (15 at x=-1.78 by the front, 16 at x=+1.78 by
        // the rear; 0.66 m squares, 5 cm thick) — seen with the studio open: two square cage panels left
        // floating in the air that belong to the armour (2026-09-22). Isolated and looked at.
        ["vehicles/xp3/m1128-stryker/m1128-stryker_mesh"] = new[] { 15, 16 },
    };

    private static bool IsKitAtlas(string p_Shader) =>
        p_Shader.Contains("/kits/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The reactive armour of one mesh, or null when it carries none. Reads the cached dump, so it costs
    /// nothing and answers offline; a mesh with no dump yet answers null rather than guessing.
    /// </summary>
    public static Piece? Of(string p_Mesh, IReadOnlyList<MaterialBinding>? p_Scan = null,
        IReadOnlyList<(int MaterialId, string Shader)>? p_SectionMaterials = null)
    {
        var s_Path = GameCache.MeshFile(p_Mesh);
        if (!File.Exists(s_Path))
            return null;

        List<Section> s_Sections;
        try
        {
            s_Sections = Read(s_Path);
        }
        catch (Exception)
        {
            return null;
        }

        // (a) the material ids whose textures the scan names as armour, for THIS mesh.
        var s_ArmourMaterials = (p_Scan ?? Array.Empty<MaterialBinding>())
            .Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.MaterialId >= 0 &&
                          p_B.Textures.Any(p_T => IsArmourTexture(p_T.Texture)))
            .Select(p_B => p_B.MaterialId)
            .ToHashSet();

        var s_Piece = new Piece();
        var s_Materials = p_SectionMaterials ?? Array.Empty<(int MaterialId, string Shader)>();

        for (var s_Index = 0; s_Index < s_Sections.Count; s_Index++)
        {
            var s_Section = s_Sections[s_Index];
            var s_Triangles = s_Section.Indices.Length / 3;

            // (a) and (b): the whole section is the piece.
            if (IsArmourShader(s_Section.Shader) ||
                (s_Index < s_Materials.Count && s_ArmourMaterials.Contains(s_Materials[s_Index].MaterialId)))
            {
                s_Piece.WholeSections.Add(s_Index);
                s_Piece.Triangles += s_Triangles;
                continue;
            }

            // (d) the hull parts measured for this mesh, wherever they sit.
            if (s_HullParts.TryGetValue(p_Mesh, out var s_Hull))
            {
                var s_Here = new Dictionary<int, int>();
                for (var i = 0; i + 2 < s_Section.Indices.Length; i += 3)
                {
                    var s_A = s_Section.Indices[i];
                    if (s_A >= 0 && s_A < s_Section.Parts.Length && s_Hull.Contains(s_Section.Parts[s_A]))
                        s_Here[s_Section.Parts[s_A]] = s_Here.GetValueOrDefault(s_Section.Parts[s_A]) + 1;
                }

                if (s_Here.Count > 0)
                {
                    s_Piece.PartsBySection[s_Index] = s_Here.Keys.OrderBy(p_P => p_P).ToArray();
                    s_Piece.Triangles += s_Here.Values.Sum();
                    continue;
                }
            }

            // (c) the parts of a kit-atlas section whose triangles read the tile atlas' block band.
            if (!IsKitAtlas(s_Section.Shader))
                continue;

            var s_Total = new Dictionary<int, int>();
            var s_InBand = new Dictionary<int, int>();

            for (var i = 0; i + 2 < s_Section.Indices.Length; i += 3)
            {
                var s_A = s_Section.Indices[i];
                if (s_A < 0 || s_A >= s_Section.Parts.Length)
                    continue;

                var s_Part = s_Section.Parts[s_A];
                if (s_Part < 0)
                    continue;

                var s_V = (s_Section.V[s_A] + s_Section.V[s_Section.Indices[i + 1]] +
                           s_Section.V[s_Section.Indices[i + 2]]) / 3f;

                s_Total[s_Part] = s_Total.GetValueOrDefault(s_Part) + 1;
                if (s_V < BandV)
                    s_InBand[s_Part] = s_InBand.GetValueOrDefault(s_Part) + 1;
            }

            var s_Parts = s_Total
                .Where(p_P => s_InBand.GetValueOrDefault(p_P.Key) > p_P.Value * BandShare)
                .Select(p_P => p_P.Key)
                .OrderBy(p_P => p_P)
                .ToArray();

            if (s_Parts.Length == 0)
                continue;

            s_Piece.PartsBySection[s_Index] = s_Parts;
            s_Piece.Triangles += s_Parts.Sum(p_P => s_Total[p_P]);
        }

        return s_Piece.Any ? s_Piece : null;
    }

    private static bool IsArmourTexture(string p_Texture) =>
        p_Texture.Contains("activearmor", StringComparison.OrdinalIgnoreCase) ||
        p_Texture.Contains("reactivearmor", StringComparison.OrdinalIgnoreCase);

}
