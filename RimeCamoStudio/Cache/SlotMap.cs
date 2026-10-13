using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Cache;

/// <summary>
/// A shader's texture map as the editor cached it: one BASE set plus one per mesh that wears the shader.
///
/// Read here as data rather than through the editor's own loader, so the studio can audit what a weapon
/// WILL get before drawing anything — the difference between "this weapon has its own textures" and "this
/// weapon will borrow whichever weapon happens to be the base" is invisible until it is on screen.
/// </summary>
public sealed class SlotMap
{
    [JsonPropertyName("Slots")] public Dictionary<string, string>? Slots { get; set; }
    [JsonPropertyName("Variations")] public Dictionary<string, SlotVariation>? Variations { get; set; }

    /// <summary>
    /// The shader's OWN defaults for its material parameters.
    ///
    /// ⛔ A MATERIAL ONLY LISTS WHAT IT OVERRIDES. The Jackhammer names no DiffuseDarkening at all, so
    /// feeding the preview only what the material lists left that constant unwritten and the weapon drew
    /// flat green — while weapons that happen to override it looked fine. Anything the weapon does not
    /// name has to come from here, exactly as the game takes it from the shader.
    /// </summary>
    [JsonPropertyName("ExternalDefaults")] public Dictionary<string, string>? ExternalDefaults { get; set; }

    /// <summary>
    /// How many meshes of the map it was built in wear the shader; null in maps cached before it existed.
    /// 0 means the base set was taken from NO subject — it is the shader's own art, not a borrowed look.
    /// </summary>
    [JsonPropertyName("MeshUsers")] public int? MeshUsers { get; set; }

    /// <summary>
    /// This map when its base set is the shader's OWN art (built where no mesh wears it: MeshUsers 0) — the
    /// only look a shader that no mesh-variation database binds can have (the F-35B's). Null otherwise:
    /// a base set taken from a user belongs to that user, and dressing another subject in it is the
    /// "M4A1 art on every weapon" failure.
    /// </summary>
    public static SlotMap? OwnArtOf(string? p_Shader) =>
        p_Shader != null && Load(p_Shader) is { MeshUsers: 0, Slots.Count: > 0 } s_Map ? s_Map : null;

    public static SlotMap? Load(string p_Shader)
    {
        var s_Path = Path.Combine(Settings.TextureCache,
            RimeShaderEditor.MainWindow.SlotMapFileName(p_Shader));

        if (!File.Exists(s_Path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SlotMap>(File.ReadAllText(s_Path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The set a given mesh gets: its own shipped look, or null when it has none of its own.</summary>
    public SlotVariation? ForMesh(string p_Mesh) => Variations?.Values.FirstOrDefault(p_V =>
        string.Equals(p_V.Mesh, p_Mesh, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(p_V.AssetName, "(base)", StringComparison.OrdinalIgnoreCase));
}

public sealed class SlotVariation
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("Mesh")] public string Mesh { get; set; } = "";
    [JsonPropertyName("AssetName")] public string AssetName { get; set; } = "";
    [JsonPropertyName("Slots")] public Dictionary<string, string>? Slots { get; set; }
}
