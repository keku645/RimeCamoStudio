using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// Where the first-person eye sits on each weapon, for sticker mode's "1st person" view (how the soldier sees the weapon in his hands):
/// the cache's meshcache/firstperson/cameras.json, measured out of the user's own game by Rime's dump_weapon_camera — the soldier's 1P
/// skeleton posed by the weapon's idle pose, its camera bone (CameraJoint) in the weapon mesh's own space. The cache preparation measures
/// every weapon; the "1st person" button measures one that is missing. The eye sits on the weapon's +x, its left, the side the
/// first-person view shows.
/// </summary>
public static class FirstPersonCameras
{
    public readonly record struct Eye(Vector3 Position, Vector3 Look, Vector3 Up);

    public static string DefaultPath => Cache.GameCache.CameraFile;

    private static Dictionary<string, Eye>? s_Eyes;
    private static string? s_ReadPath;
    private static DateTime s_ReadStamp;

    /// <summary>The eye of a weapon mesh (its first-person body mesh), or null when none was measured.</summary>
    public static Eye? Of(string? p_Mesh)
    {
        if (p_Mesh == null)
            return null;

        // read again whenever the file is another one or has changed: a measurement adds weapons to it while the studio runs
        var s_Path = DefaultPath;
        var s_Stamp = File.Exists(s_Path) ? File.GetLastWriteTimeUtc(s_Path) : DateTime.MinValue;
        if (s_Eyes == null || s_Path != s_ReadPath || s_Stamp != s_ReadStamp)
        {
            s_Eyes = Load(s_Path);
            s_ReadPath = s_Path;
            s_ReadStamp = s_Stamp;
        }

        return s_Eyes.TryGetValue(p_Mesh, out var s_Eye) ? s_Eye : null;
    }

    private static Dictionary<string, Eye> Load(string p_Path)
    {
        var s_Eyes = new Dictionary<string, Eye>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(p_Path))
            return s_Eyes;

        try
        {
            using var s_Document = JsonDocument.Parse(File.ReadAllText(p_Path));
            if (!s_Document.RootElement.TryGetProperty("Meshes", out var s_Meshes))
                return s_Eyes;

            static Vector3 Triple(JsonElement p_Array) =>
                new(p_Array[0].GetSingle(), p_Array[1].GetSingle(), p_Array[2].GetSingle());

            foreach (var s_Mesh in s_Meshes.EnumerateObject())
                s_Eyes[s_Mesh.Name] = new Eye(Triple(s_Mesh.Value.GetProperty("Eye")), Triple(s_Mesh.Value.GetProperty("Look")),
                    Triple(s_Mesh.Value.GetProperty("Up")));
        }
        catch (Exception)
        {
            // a damaged file is no camera: the button says so
        }

        return s_Eyes;
    }
}
