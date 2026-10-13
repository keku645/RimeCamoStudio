using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimeCamoStudio.Catalog;
using RimeShaderEditor.Graph;

namespace RimeCamoStudio;

// ⭐ THE EMBLEM ON THE VEHICLES: literally a decal on the vehicle, no shader. The emblem positions on the vehicles BF4 and BF3 share
// are taken from BF4; on the vehicles that are not shared they are placed by hand in the studio, as was done with the weapons — the
// studio is only used to give the emblems' positions.
// A vehicle's emblem slot is a list of squares laid out in SPACE on its body (StickerPlacement.IsSpatial: centre, outward normal, a turn
// about it, sides in metres) — what a decal stamped on the vehicle in the game needs, and what BF4 measures: its logo section's patches
// (BF4's emblems21_bf3.json, 2026-09-29: 128 squares on the 21 vehicles the two games share, 0–0.4 cm
// off BF3's surface). They ship with the studio (Data/emblem/slots, one file per body mesh, as the weapons' do) and the rest are placed
// by hand in sticker mode, which on a vehicle only places these squares and leaves its graph alone.
public static partial class Program
{
    /// <summary>
    /// --vehicleslotsfrombf4 &lt;emblems21_bf3.json&gt; &lt;out-dir&gt;: BF4's emblem squares on the vehicles both games share, written as the
    /// studio's slot files for their BF3 body meshes (into the out dir: copying them to Data/emblem/slots is a separate, checked step). Each
    /// square goes in as measured — centre, normal, sides — turned so the emblem stands with its top where BF4's does; then read back
    /// through the studio's own frame (StickerSurface.FrameOf) and compared with BF4's numbers, which are the oracle. Also said per square:
    /// how far its centre lies from the body (every section, and the camo's body sections alone) and whether BF4 lays it out mirrored.
    /// </summary>
    private static int VehicleSlotsFromBf4(string p_Json, string p_OutDir)
    {
        if (!File.Exists(p_Json))
        {
            Console.Error.WriteLine($"VEHICLESLOTS: no file '{p_Json}'.");
            return 2;
        }

        var s_Out = Path.GetFullPath(p_OutDir);
        if (Directory.Exists(s_Out))
            Directory.Delete(s_Out, true);
        Directory.CreateDirectory(s_Out);
        Environment.SetEnvironmentVariable("CAMO_EMBLEM_SLOTS", s_Out);
        Console.WriteLine($"output (cleared): {s_Out}");

        var s_Vehicles = VehicleCatalog.Load();
        using var s_Doc = JsonDocument.Parse(File.ReadAllText(p_Json));
        var s_Failures = new List<string>();
        var s_Squares = 0;
        var s_Mirrored = 0;
        var s_Culture = System.Globalization.CultureInfo.InvariantCulture;

        static System.Numerics.Vector3 Vec(JsonElement p_E) =>
            new(p_E[0].GetSingle(), p_E[1].GetSingle(), p_E[2].GetSingle());

        foreach (var s_Entry in s_Doc.RootElement.EnumerateArray())
        {
            var s_Name = s_Entry.GetProperty("vehicle").GetString() ?? "?";
            var s_Bf3 = s_Entry.GetProperty("bf3Mesh").GetString() ?? "";
            var s_Vehicle = s_Vehicles.Vehicles.FirstOrDefault(p_V => p_V.PreviewMesh.Length > 0 &&
                Path.GetFileNameWithoutExtension(Cache.GameCache.MeshFile(p_V.PreviewMesh)).Equals(s_Bf3, StringComparison.OrdinalIgnoreCase));
            if (s_Vehicle == null)
            {
                s_Failures.Add($"{s_Name}: no vehicle of the catalogue has the body '{s_Bf3}'");
                Console.WriteLine($"!! {s_Name}: no vehicle of the catalogue has the body '{s_Bf3}'");
                continue;
            }

            var s_File = Cache.GameCache.MeshFile(s_Vehicle.PreviewMesh);
            var s_All = StickerSurface.LoadRsm3(s_File, _ => true);
            var s_Body = StickerSurface.LoadRsm3(s_File, CamoSession.IsCamoPreset);
            if (s_All == null)
            {
                s_Failures.Add($"{s_Name}: its body '{s_File}' is not in the mesh cache");
                Console.WriteLine($"!! {s_Name}: its body is not in the mesh cache ({s_File})");
                continue;
            }

            Console.WriteLine($"== {s_Vehicle.Display} ({s_Vehicle.PreviewMesh})");
            var s_Placements = new List<StickerPlacement>();
            var s_Index = 0;
            foreach (var s_Emblem in s_Entry.GetProperty("emblems").EnumerateArray())
            {
                var s_Centre = Vec(s_Emblem.GetProperty("centre"));
                var s_Normal = System.Numerics.Vector3.Normalize(Vec(s_Emblem.GetProperty("normal")));
                var s_Right = Vec(s_Emblem.GetProperty("right"));
                var s_Top = Vec(s_Emblem.GetProperty("top"));
                var s_Width = s_Emblem.GetProperty("width").GetDouble();
                var s_Height = s_Emblem.GetProperty("height").GetDouble();

                // the emblem's down, flattened onto the square's plane: the turn is the one that stands it with its top where BF4's is
                var s_Down = -(s_Top - s_Normal * System.Numerics.Vector3.Dot(s_Top, s_Normal));
                s_Down = System.Numerics.Vector3.Normalize(s_Down);
                var (s_X0, s_Y0) = StickerSurface.UprightAxes(s_Normal);
                var s_Turn = Math.Atan2(System.Numerics.Vector3.Dot(s_Down, s_X0), System.Numerics.Vector3.Dot(s_Down, s_Y0)) * 180.0 / Math.PI;

                var s_Placement = new StickerPlacement
                {
                    Mesh = s_Vehicle.PreviewMesh, Image = EmblemSlot.Image, Width = s_Width, Height = s_Height,
                    Rotation = RimeShaderEditor.MainWindow.WrapDegrees(s_Turn),
                    Anchor = new double[] { s_Centre.X, s_Centre.Y, s_Centre.Z },
                    Normal = new double[] { s_Normal.X, s_Normal.Y, s_Normal.Z },
                };

                // read back as the studio reads it: the oracle is BF4's own numbers
                var s_Frame = s_All.FrameOf(s_Placement, 1.0);
                var s_Off = s_Frame == null ? float.MaxValue : System.Numerics.Vector3.Distance(s_Frame.Value.Origin, s_Centre);
                var s_FacesBf4 = s_Frame == null ? 0f : System.Numerics.Vector3.Dot(s_Frame.Value.Normal, s_Normal);
                var s_Upright = s_Frame == null ? 0f : System.Numerics.Vector3.Dot(s_Frame.Value.AxisY, s_Down);
                var s_Sides = s_Frame == null ? double.MaxValue : Math.Max(Math.Abs(s_Frame.Value.HalfWidth * 2 - s_Width), Math.Abs(s_Frame.Value.HalfHeight * 2 - s_Height));
                // BF4 lays some squares out in mirror (its logo UV turned over): ours reads the right way round there, upright as BF4's
                var s_SameRight = s_Frame != null && System.Numerics.Vector3.Dot(s_Frame.Value.AxisX, System.Numerics.Vector3.Normalize(s_Right)) > 0;
                if (!s_SameRight)
                    s_Mirrored++;

                var s_AnyDistance = s_All.ClosestTriangle(s_Centre).Distance;
                var s_BodyDistance = s_Body?.ClosestTriangle(s_Centre).Distance ?? float.MaxValue;
                var s_Ok = s_Frame != null && s_Off < 1e-4f && s_FacesBf4 > 0.9999f && s_Upright > 0.9999f && s_Sides < 1e-5;
                if (!s_Ok)
                    s_Failures.Add($"{s_Vehicle.Display} #{s_Index}: the studio's frame is not BF4's (centre {s_Off:0.0000} m off, normal {s_FacesBf4:0.0000}, upright {s_Upright:0.0000}, sides {s_Sides:0.00000})");

                Console.WriteLine(string.Create(s_Culture,
                    $"  {(s_Ok ? "ok  " : "FAIL")} #{s_Index} centre ({s_Centre.X:+0.000;-0.000},{s_Centre.Y:+0.000;-0.000},{s_Centre.Z:+0.000;-0.000}) " +
                    $"normal ({s_Normal.X:+0.00;-0.00},{s_Normal.Y:+0.00;-0.00},{s_Normal.Z:+0.00;-0.00}) {s_Width:0.00} x {s_Height:0.00} m, turn {s_Placement.Rotation:+0.0;-0.0}°  " +
                    $"off the body {s_AnyDistance * 100:0.0} cm (camo sections {(s_BodyDistance == float.MaxValue ? "none" : string.Create(s_Culture, $"{s_BodyDistance * 100:0.0} cm"))})" +
                    $"{(s_SameRight ? "" : "  (BF4 lays it out MIRRORED: ours reads the right way round)")}" +
                    $"{(s_Frame is { Triangle: < 0 } ? "  (no body under its centre along the normal)" : "")}"));
                s_Placements.Add(s_Placement);
                s_Index++;
                s_Squares++;
            }

            var s_Written = EmblemSlot.SavePlacements(s_Vehicle.PreviewMesh, s_Vehicle.Display, s_Placements);
            var s_Read = EmblemSlot.SavedPlacements(s_Vehicle.PreviewMesh, out var s_From);
            var s_Same = s_Read != null && s_From == s_Written && s_Read.Count == s_Placements.Count &&
                         s_Read.Zip(s_Placements).All(p_P => p_P.First.IsSpatial && p_P.First.Normal!.SequenceEqual(p_P.Second.Normal!) &&
                                                            p_P.First.Anchor!.SequenceEqual(p_P.Second.Anchor!) &&
                                                            p_P.First.Rotation == p_P.Second.Rotation && p_P.First.Width == p_P.Second.Width);
            if (!s_Same)
                s_Failures.Add($"{s_Vehicle.Display}: its slot file does not read back as written ({s_Written})");
            Console.WriteLine($"  -> {s_Written} ({s_Placements.Count} square(s){(s_Same ? ", reads back the same" : ", ⛔ DOES NOT READ BACK THE SAME")})");
        }

        Console.WriteLine($"{s_Squares} square(s) on {s_Doc.RootElement.GetArrayLength()} vehicle(s); BF4 lays {s_Mirrored} of them out mirrored.");
        foreach (var s_Failure in s_Failures)
            Console.WriteLine("  FAIL: " + s_Failure);
        Console.WriteLine(s_Failures.Count == 0 ? "VEHICLESLOTS: PASS" : $"VEHICLESLOTS: FAIL ({s_Failures.Count})");
        return s_Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// --vehicleslotreview &lt;out-dir&gt; [vehicle…]: every vehicle's emblem squares (2026-10-09, the BMP-2: the sticker either floated in the
    /// air or came out cut across several layers — so every vehicle is reviewed for it), through the real window in
    /// sticker mode — the user's own slots read from a COPY of his folder (out-dir/slots), so nothing of his is ever written. Per square, over
    /// a 9 × 9 grid dropped along its normal: how much of it lands on the body the view draws, and on how many heights (a step or an edge
    /// splits the picture) — and the same on every section of the mesh, the rule before today, as the control. The BMP-2 also gets a probe
    /// square on its hull side, where a hand-placed one floated over the reactive armour the game does not fit. Photographs (front and almost edge-on)
    /// of every square flagged and of the probe.
    /// </summary>
    private static int VehicleSlotReview(string p_OutputDir, string[] p_Only)
    {
        _ = new Application();
        var s_OutDir = Path.GetFullPath(p_OutputDir);
        if (Directory.Exists(s_OutDir))
            Directory.Delete(s_OutDir, true);
        Directory.CreateDirectory(s_OutDir);
        var s_Slots = Path.Combine(s_OutDir, "slots");
        Directory.CreateDirectory(s_Slots);
        var s_UserFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimeCamoStudio", "emblem_slots");
        if (Directory.Exists(s_UserFolder))
            foreach (var s_File in Directory.GetFiles(s_UserFolder, "*.json"))
                File.Copy(s_File, Path.Combine(s_Slots, Path.GetFileName(s_File)));
        Environment.SetEnvironmentVariable("CAMO_EMBLEM_SLOTS", s_Slots);
        CamoSession.StickerLibraryOverride = Path.Combine(s_OutDir, "library");
        Directory.CreateDirectory(CamoSession.StickerLibraryOverride);
        Console.WriteLine($"output (cleared): {s_OutDir}; the user's slots copied from {s_UserFolder}");

        var s_Weapons = WeaponCatalog.Load();
        var s_Session = new CamoSession(s_Weapons, HookPool.Load(s_Weapons));
        var s_Editor = CreateWindow(s_Session);
        WireSimpleMode(s_Editor, s_Session, false);
        s_Editor.Width = 1400;
        s_Editor.Height = 800;
        s_Editor.SetHeadlessPreviewSize(960, 640);
        void Settle() => Pump(Task.Delay(700), TimeSpan.FromSeconds(10));
        var s_Culture = System.Globalization.CultureInfo.InvariantCulture;

        void Snap(string p_Shot)
        {
            var s_Shown = s_Editor.CaptureShown(out var s_W, out var s_H);
            if (s_Shown == null || s_W <= 0)
                return;
            var s_Opaque = (byte[]) s_Shown.Clone();
            for (var i = 3; i < s_Opaque.Length; i += 4)
                s_Opaque[i] = 255;
            Write(Path.Combine(s_OutDir, $"{p_Shot}.png"), BitmapSource.Create(s_W, s_H, 96, 96, PixelFormats.Bgra32, null, s_Opaque, s_W * 4));
        }

        void LookAt(StickerFrame p_Frame, float p_Turn)
        {
            var s_Mirror = s_Editor.PreviewMeshMirrored ? -1f : 1f;
            var s_N = new System.Numerics.Vector3(p_Frame.Normal.X * s_Mirror, p_Frame.Normal.Y, p_Frame.Normal.Z);
            s_Editor.SetPreviewView((float) Math.Atan2(s_N.X, s_N.Z) + p_Turn, (float) Math.Asin(Math.Clamp(s_N.Y, -0.95f, 0.95f)),
                Math.Max(0.25f, Math.Max(p_Frame.HalfWidth, p_Frame.HalfHeight) * 9f), 0f, 0f,
                new SharpDX.Vector3(p_Frame.Origin.X * s_Mirror, p_Frame.Origin.Y, p_Frame.Origin.Z));
            Settle();
        }

        // the square's grid dropped along its normal: the share that lands, and the heights it lands on (clusters more than 2 cm apart)
        (double Cover, int Heights, double Mean) Reading(StickerSurface p_Surface, StickerFrame p_Frame, float p_UnitsPerMetre)
        {
            var s_Heights = new List<float>();
            for (var j = 0; j < 9; j++)
            for (var i = 0; i < 9; i++)
                if (p_Surface.Contact(p_Frame, p_Frame.HalfWidth * ((i + 0.5f) / 4.5f - 1f), p_Frame.HalfHeight * ((j + 0.5f) / 4.5f - 1f)) is { } s_On)
                    s_Heights.Add(p_Frame.LocalOf(s_On.Position).Z / p_UnitsPerMetre);
            s_Heights.Sort();
            var s_Clusters = s_Heights.Count == 0 ? 0 : 1;
            for (var i = 1; i < s_Heights.Count; i++)
                if (s_Heights[i] - s_Heights[i - 1] > 0.02f)
                    s_Clusters++;
            return (s_Heights.Count / 81.0, s_Clusters, s_Heights.Count == 0 ? 0 : s_Heights.Average());
        }

        var s_Lines = new List<string>();
        var s_Flagged = 0;
        var s_Squares = 0;
        var s_Vehicles = s_Session.Vehicles.Vehicles.Where(p_V => p_V.PreviewMesh.Length > 0 && File.Exists(Cache.GameCache.MeshFile(p_V.PreviewMesh)) &&
                                                               (p_Only.Length == 0 || p_Only.Any(p_O => VehicleCatalog.Asks(p_O, p_V)))).ToList();
        Check(s_Editor.PressCamoFamily("vehicles"), "the Vehicles tab presses");
        foreach (var s_Vehicle in s_Vehicles)
        {
            var s_Mesh = s_Vehicle.PreviewMesh;
            var s_Saved = EmblemSlot.SavedPlacements(s_Mesh, out var s_From);
            var s_Probe = s_Vehicle.Folder.Equals("bmp2", StringComparison.OrdinalIgnoreCase);

            var s_Item = s_Editor.MeshList.Items.OfType<RimeShaderEditor.MainWindow.CamoWeapon>()
                .FirstOrDefault(p_W => p_W.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase));
            if (s_Item == null)
                continue;
            s_Editor.MeshList.SelectedItem = s_Item;
            if (s_Editor.LastMeshPick != null)
                Pump(s_Editor.LastMeshPick, TimeSpan.FromMinutes(3));
            Settle();
            s_Session.StartStickers(s_Editor);
            Settle();
            Settle();

            var s_Shown = s_Editor.BodySurface;
            var s_Every = StickerSurface.LoadRsm3(Cache.GameCache.MeshFile(s_Mesh), _ => true);
            if (s_Shown == null || s_Every == null)
            {
                s_Lines.Add($"!! {s_Vehicle.Display}: no surface");
                continue;
            }

            var s_Placements = (s_Saved ?? new List<StickerPlacement>()).ToList();
            var s_Where = s_From == null ? "" : s_From.StartsWith(s_Slots, StringComparison.OrdinalIgnoreCase) ? "hand-placed" : "BF4's";
            var s_Probes = new HashSet<int>();

            // ⭐ the HIDDEN-GEOMETRY probe, on every vehicle (2026-10-09, the Sprut-SD: the floating stickers came back, so every vehicle is
            // reviewed again): a triangle of the mesh the view does not draw — a hidden section, a part left out — facing sideways with
            // drawn body behind it within 40 cm. A square laid there is where the hand-placed one floated: on the surface drawn it has to
            // land on that body behind, never on the hidden part (the reading says how far behind; the rule before lands on the part).
            var s_DrawnCentres = new HashSet<(long, long, long)>();
            for (var t = 0; t < s_Shown.TriangleCount; t++)
            {
                var (s_A, s_B, s_C) = s_Shown.CornersOf(t);
                var s_Raw = s_Shown.ToRaw((s_A + s_B + s_C) / 3f);
                s_DrawnCentres.Add(((long) Math.Round(s_Raw.X * 1000), (long) Math.Round(s_Raw.Y * 1000), (long) Math.Round(s_Raw.Z * 1000)));
            }

            var s_Hidden = new List<(int Triangle, float Area)>();
            for (var t = 0; t < s_Every.TriangleCount; t++)
            {
                var (s_A, s_B, s_C) = s_Every.CornersOf(t);
                var s_Centre = (s_A + s_B + s_C) / 3f;
                if (!s_DrawnCentres.Contains(((long) Math.Round(s_Centre.X * 1000), (long) Math.Round(s_Centre.Y * 1000), (long) Math.Round(s_Centre.Z * 1000))))
                    s_Hidden.Add((t, System.Numerics.Vector3.Cross(s_B - s_A, s_C - s_A).Length() / 2));
            }

            foreach (var (s_T, _) in s_Hidden.OrderByDescending(p_H => p_H.Area))
            {
                var (s_A, s_B, s_C) = s_Every.CornersOf(s_T);
                var s_Centre = (s_A + s_B + s_C) / 3f;
                var s_Normal = s_Every.FaceNormalOf(s_T);
                if (Math.Abs(s_Normal.Y) > 0.5f)
                    continue;

                // the side whose back has drawn body within 40 cm is the outside
                System.Numerics.Vector3? s_Out = null;
                foreach (var s_N in new[] { s_Normal, -s_Normal })
                    if (s_Shown.RayHit(s_Shown.FromRaw(s_Centre) - s_N * 0.001f * s_Shown.RawScale, -s_N, 0.4f * s_Shown.RawScale) != null)
                    {
                        s_Out = s_N;
                        break;
                    }

                if (s_Out is not { } s_Facing)
                    continue;

                s_Placements.Add(new StickerPlacement
                {
                    Mesh = s_Mesh, Image = EmblemSlot.Image, Width = 0.4,
                    Anchor = new double[] { s_Centre.X, s_Centre.Y, s_Centre.Z },
                    Normal = new double[] { s_Facing.X, s_Facing.Y, s_Facing.Z },
                });
                s_Probes.Add(s_Placements.Count - 1);
                s_Where += (s_Where.Length > 0 ? " + " : "") + $"a probe on hidden geometry ({s_Hidden.Count} triangle(s) not drawn)";
                break;
            }

            if (s_Placements.Count == 0 && !s_Probe)
            {
                s_Editor.SetStickerMode(false);
                Settle();
                s_Lines.Add($"== {s_Vehicle.Display}: no square, {s_Hidden.Count} triangle(s) of the mesh not drawn, none with body behind it");
                continue;
            }

            // the probe: a square on the BMP-2's hull side where the click lands from its left, as the hand-placed one
            // (CAMO_REVIEW_PROBE="x,y,z;nx,ny,nz" in the mesh's units names the spot exactly: the BMP-2's left hull plate by its painted
            // numbers, -1.27,1.30,0.75;-0.97,0.25,0.01, is where the hand-placed one floated)
            if (s_Probe && Environment.GetEnvironmentVariable("CAMO_REVIEW_PROBE") is { Length: > 0 } s_Spot)
            {
                var s_Numbers = s_Spot.Split(';', ',').Select(p_N => double.Parse(p_N, s_Culture)).ToArray();
                s_Placements.Add(new StickerPlacement
                {
                    Mesh = s_Mesh, Image = EmblemSlot.Image, Width = 0.45,
                    Anchor = s_Numbers[..3], Normal = s_Numbers[3..6],
                });
                s_Probes.Add(s_Placements.Count - 1);
                s_Where += (s_Where.Length > 0 ? " + " : "") + "a probe at " + s_Spot;
            }
            else if (s_Probe)
            {
                s_Editor.SetPreviewView((float) (-Math.PI / 2), 0.25f, 2.4f);
                Settle();
                s_Editor.CaptureShown(out var s_W, out var s_H);
                if (s_Editor.StickerHitInfo(s_W / 2f, s_H * 0.45f) is { } s_Hit)
                {
                    var s_Raw = s_Shown.ToRaw(s_Hit.Position);
                    s_Placements.Add(new StickerPlacement
                    {
                        Mesh = s_Mesh, Image = EmblemSlot.Image, Width = 0.45,
                        Anchor = new double[] { s_Raw.X, s_Raw.Y, s_Raw.Z },
                        Normal = new double[] { s_Hit.Normal.X, s_Hit.Normal.Y, s_Hit.Normal.Z },
                    });
                    s_Probes.Add(s_Placements.Count - 1);
                    s_Where += (s_Where.Length > 0 ? " + " : "") + "a probe on the hull side";
                }
            }

            s_Lines.Add($"== {s_Vehicle.Display} ({s_Placements.Count} square(s), {s_Where}; the view draws {s_Editor.SolidSectionCount} section(s) solid)");
            for (var i = 0; i < s_Placements.Count; i++)
            {
                var s_P = s_Placements[i];
                if (!s_P.IsSpatial || s_Shown.FrameOf(s_P, 1.0) is not { } s_Frame || s_Every.FrameOf(s_P, 1.0) is not { } s_EveryFrame)
                {
                    s_Lines.Add($"   #{i}: not laid out in space");
                    continue;
                }

                s_Squares++;
                var s_Now = Reading(s_Shown, s_Frame, s_Shown.RawScale);
                var s_Before = Reading(s_Every, s_EveryFrame, 1f);
                var s_Flags = new List<string>();
                if (s_Now.Heights > 1)
                    s_Flags.Add($"lands on {s_Now.Heights} heights (a step or an edge under it)");
                if (s_Now.Cover < 0.75)
                    s_Flags.Add($"only {s_Now.Cover:P0} of it on the body");
                var s_IsProbe = s_Probes.Contains(i);
                s_Lines.Add(string.Create(s_Culture,
                    $"   #{i}{(s_IsProbe ? " (probe)" : "")}: {s_P.Width:0.00} m, on the body drawn {s_Now.Cover:P0} at {s_Now.Heights} height(s), " +
                    $"{s_Now.Mean * 100:+0.0;-0.0} cm off its plane; every section (before) {s_Before.Cover:P0} at {s_Before.Heights}, {s_Before.Mean * 100:+0.0;-0.0} cm" +
                    $"{(s_Flags.Count > 0 && !s_IsProbe ? "   ⚠ " + string.Join("; ", s_Flags) : "")}"));

                if (s_Flags.Count > 0 || s_IsProbe || s_Before.Heights != s_Now.Heights)
                {
                    s_Flagged += s_Flags.Count > 0 && !s_IsProbe ? 1 : 0;
                    if (s_IsProbe)
                        s_Editor.AddStickerForDriver(s_P);
                    LookAt(s_Frame, 0f);
                    Snap($"{s_Vehicle.Folder.Replace('/', '_')}_{i}_front");
                    LookAt(s_Frame, 1.3f);
                    Snap($"{s_Vehicle.Folder.Replace('/', '_')}_{i}_edge");
                }
            }

            s_Editor.RemoveDriverStickers();
            s_Editor.SetStickerMode(false);
            Settle();
        }

        foreach (var s_Line in s_Lines)
            Console.WriteLine(s_Line);
        Console.WriteLine($"{s_Squares} square(s) read; {s_Flagged} flagged.");
        Console.WriteLine("VEHICLESLOTREVIEW: DONE");
        return 0;

        void Check(bool p_Ok, string p_What) => Console.WriteLine($"  {(p_Ok ? "ok  " : "FAIL")} {p_What}");
    }

    /// <summary>
    /// --vehicleslottest &lt;vehicle&gt; &lt;out-dir&gt;: the user's gesture on a vehicle, through the real window. The Vehicles tab, the
    /// vehicle picked, sticker mode — which on a vehicle places only the emblem's squares and must leave its graph alone (the graph's JSON
    /// before and after is compared) —, Edit slot (the squares of its slot: BF4's where the studio ships them), photographs of each square from its own normal,
    /// a click on bare body that puts a square of 0.4 m where it lands, one turned and widened, Save slot (a file in the out dir's slots
    /// folder, CAMO_EMBLEM_SLOTS: the user's own folder is never written), the file read back, Forget mine, Done. The photographs are for
    /// LOOKING at: the outline of each square and the arrow on its top edge.
    /// </summary>
    private static int VehicleSlotTest(string p_Vehicle, string p_OutputDir)
    {
        _ = new Application();
        var s_OutDir = Path.GetFullPath(p_OutputDir);
        if (Directory.Exists(s_OutDir))
            Directory.Delete(s_OutDir, true);
        Directory.CreateDirectory(s_OutDir);
        var s_Slots = Path.Combine(s_OutDir, "slots");
        Environment.SetEnvironmentVariable("CAMO_EMBLEM_SLOTS", s_Slots);
        CamoSession.StickerLibraryOverride = Path.Combine(s_OutDir, "library");
        Directory.CreateDirectory(CamoSession.StickerLibraryOverride);
        var s_UserFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimeCamoStudio", "emblem_slots");
        var s_UserBefore = Directory.Exists(s_UserFolder) ? Directory.GetFiles(s_UserFolder).Length : 0;
        Console.WriteLine($"output (cleared): {s_OutDir}");

        var s_Weapons = WeaponCatalog.Load();
        var s_Session = new CamoSession(s_Weapons, HookPool.Load(s_Weapons));
        var s_Asked = s_Session.Vehicles.Vehicles.Where(p_V => VehicleCatalog.Asks(p_Vehicle.Trim(), p_V)).ToList();
        var s_Entry = s_Asked.Count == 1 ? s_Asked[0] : null;
        if (s_Entry == null || s_Entry.PreviewMesh.Length == 0 || !File.Exists(Cache.GameCache.MeshFile(s_Entry.PreviewMesh)))
        {
            Console.Error.WriteLine($"VEHICLESLOTTEST: '{p_Vehicle}' names {(s_Asked.Count > 1 ? "more than one vehicle" : "no cached vehicle")} of the " +
                                    "catalogue. Keys: " + string.Join(", ", s_Session.Vehicles.Vehicles.Select(VehicleCatalog.KeyOf)));
            return 2;
        }

        var s_Mesh = s_Entry.PreviewMesh;
        var s_Editor = CreateWindow(s_Session);
        WireSimpleMode(s_Editor, s_Session, false);
        s_Editor.Width = 1400;
        s_Editor.Height = 800;
        s_Editor.SetHeadlessPreviewSize(960, 640);

        var s_Failures = new List<string>();
        void Check(bool p_Ok, string p_What)
        {
            Console.WriteLine($"  {(p_Ok ? "ok  " : "FAIL")} {p_What}");
            if (!p_Ok)
                s_Failures.Add(p_What);
        }

        void Settle() => Pump(Task.Delay(700), TimeSpan.FromSeconds(10));

        var s_Shots = new Dictionary<string, string>();
        void Snap(string p_Shot)
        {
            var s_Shown = s_Editor.CaptureShown(out var s_W, out var s_H);
            if (s_Shown == null || s_W <= 0)
            {
                Console.WriteLine($"      (no picture for {p_Shot})");
                return;
            }

            var s_Opaque = (byte[]) s_Shown.Clone();
            for (var i = 3; i < s_Opaque.Length; i += 4)
                s_Opaque[i] = 255;
            Write(Path.Combine(s_OutDir, $"{p_Shot}.png"), BitmapSource.Create(s_W, s_H, 96, 96, PixelFormats.Bgra32, null, s_Opaque, s_W * 4));
            s_Shots[p_Shot] = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(s_Shown))[..12];
        }

        // (each print also kept as the graph's JSON beside the photographs, so a difference can be read)
        var s_Prints = 0;
        string GraphPrint()
        {
            var s_Json = s_Editor.DocumentGraph.ToJson();
            File.WriteAllText(Path.Combine(s_OutDir, $"graph_{s_Prints++}.json"), s_Json);
            return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s_Json)))[..12];
        }

        // the camera on a square, from outside along its normal (the view draws the mesh through a lateral mirror: the normal and the
        // target are taken into its world the same way)
        void Look(int p_Index)
        {
            if (s_Editor.StickerFrameOf(p_Index) is { } s_Frame)
                LookAt(s_Frame);
        }

        void LookAt(StickerFrame s_Frame, float p_Turn = 0f)
        {
            var s_Mirror = s_Editor.PreviewMeshMirrored ? -1f : 1f;
            var s_N = new System.Numerics.Vector3(s_Frame.Normal.X * s_Mirror, s_Frame.Normal.Y, s_Frame.Normal.Z);
            var s_Yaw = (float) Math.Atan2(s_N.X, s_N.Z) + p_Turn;
            var s_Pitch = (float) Math.Asin(Math.Clamp(s_N.Y, -0.95f, 0.95f));
            var s_Target = new SharpDX.Vector3(s_Frame.Origin.X * s_Mirror, s_Frame.Origin.Y, s_Frame.Origin.Z);
            s_Editor.SetPreviewView(s_Yaw, s_Pitch, Math.Max(0.25f, Math.Max(s_Frame.HalfWidth, s_Frame.HalfHeight) * 9f), 0f, 0f, s_Target);
            Settle();
        }

        IReadOnlyList<(int Index, StickerPlacement Sticker)> Squares() =>
            s_Editor.StickersOnCurrentWeapon().Where(p_S => EmblemSlot.IsEmblem(p_S.Sticker)).ToList();

        // --- 1. the vehicle, picked under the Vehicles tab ------------------------------------------------------------------------------
        Console.WriteLine($"STEP 1 - {s_Entry.Display} under the Vehicles tab");
        Check(s_Editor.PressCamoFamily("vehicles"), "the Vehicles tab presses");
        var s_Item = s_Editor.MeshList.Items.OfType<RimeShaderEditor.MainWindow.CamoWeapon>()
            .FirstOrDefault(p_W => p_W.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase));
        if (s_Item == null)
        {
            Console.WriteLine($"VEHICLESLOTTEST: FAIL - {s_Entry.Display} is not in the Vehicles list.");
            return 1;
        }

        s_Editor.MeshList.SelectedItem = s_Item;
        if (s_Editor.LastMeshPick == null || !Pump(s_Editor.LastMeshPick, TimeSpan.FromMinutes(3)))
        {
            Console.WriteLine("VEHICLESLOTTEST: FAIL - the pick never finished.");
            return 1;
        }

        Settle();
        var s_GraphBefore = GraphPrint();
        var s_Shipped = EmblemSlot.SavedPlacements(s_Mesh, out var s_ShippedFrom);
        Console.WriteLine($"      graph {s_GraphBefore}; the slot shipped for it: {(s_Shipped == null ? "none" : $"{s_Shipped.Count} square(s), {s_ShippedFrom}")}");

        // --- 2. sticker mode: the emblem's squares only, the graph left alone ---------------------------------------------------------
        Console.WriteLine("STEP 2 - sticker mode on the vehicle");
        s_Session.StartStickers(s_Editor);
        Settle();
        var s_Panel = s_Session.StickerPanelFor(s_Editor);
        Check(s_Editor.StickerSlotOnly, "the vehicle takes only an emblem slot (StickerSlotOnly)");
        Check(s_Panel.ShowsSlotOnly, "the panel shows the vehicle's form (no pictures, sizes in metres)");
        Check(s_Editor.DocumentGraph.StickerRegister == null && GraphPrint() == s_GraphBefore,
            $"the vehicle's graph is not touched by sticker mode ({GraphPrint()} vs {s_GraphBefore}, sticker register {s_Editor.DocumentGraph.StickerRegister?.ToString() ?? "none"})");
        Console.WriteLine($"      panel: \"{s_Panel.SlotStatusText}\"");
        Snap("1_mode");

        // --- 3. Edit slot: the squares of its slot --------------------------------------------------------------------------------------
        Console.WriteLine("STEP 3 - Edit slot");
        s_Panel.EditSlot();
        Settle();
        var s_Editing = Squares();
        var s_Expected = s_Shipped?.Count ?? 0;
        Check(s_Editing.Count == s_Expected, $"Edit slot puts the slot's {s_Expected} square(s) on the vehicle ({s_Editing.Count}): \"{s_Panel.SlotStatusText}\"");
        Check(s_Editing.All(p_S => p_S.Sticker.IsSpatial), "every square is laid out in space (anchor + normal)");
        Check(s_Editor.StickerToPlace == EmblemSlot.Image, "a click on bare body puts another square (the square is armed)");
        var s_Rows = s_Panel.RowTexts;
        Check(s_Rows.Count == s_Editing.Count && s_Rows.Select((p_T, p_I) => p_T.StartsWith($"#{p_I} Emblem slot", StringComparison.Ordinal)).All(p_B => p_B),
            $"the panel numbers the squares in their order ({string.Join(" | ", s_Rows.Take(3))}{(s_Rows.Count > 3 ? " | …" : "")})");
        foreach (var (s_Index, s_Square) in s_Editing)
        {
            var s_Frame = s_Editor.StickerFrameOf(s_Index);
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"      #{s_Index}: {s_Square.Width:0.00} x {s_Square.Height:0.00} m, turn {s_Square.Rotation:+0.0;-0.0}°, " +
                $"{(s_Frame == null ? "NO FRAME" : s_Frame.Value.Triangle < 0 ? "no body under its centre" : "on the body")}"));
        }

        Check(s_Editing.All(p_S => s_Editor.StickerFrameOf(p_S.Index) is { Triangle: >= 0 }), "every square's centre lies on the vehicle as the window sees it");

        // the sample emblem drawn in every square (otherwise no sticker at all is drawn as an example), projected onto
        // the body's own triangles under each square — whole triangles, three corners each
        Check(s_Editing.Count == 0 ? s_Editor.OverlayPictureVertices == 0
                                   : s_Editor.OverlayPictureVertices >= s_Editing.Count * 3 && s_Editor.OverlayPictureVertices % 3 == 0,
            $"the sample emblem is laid on the body under the {s_Editing.Count} square(s) ({s_Editor.OverlayPictureVertices} picture corners)");

        // the rays the overlay casts now walk a hierarchy of boxes: the hit must be the plain loop's, triangle and distance, on rays from
        // anywhere around the body and on the outline's own rays (dropped along each square's normal)
        if (s_Editor.BodySurface is { } s_Body)
        {
            var s_Random = new Random(20261009);
            var (s_Min, s_Max) = s_Body.Bounds();
            var s_Size = s_Max - s_Min;
            var s_Rays = 0;
            var s_Different = 0;
            var s_Hits = 0;
            void Compare(System.Numerics.Vector3 p_From, System.Numerics.Vector3 p_Direction, float p_Max, System.Numerics.Vector3? p_Facing)
            {
                var s_Tree = s_Body.RayHit(p_From, p_Direction, p_Max, p_Facing);
                var s_Loop = s_Body.RayHitLinear(p_From, p_Direction, p_Max, p_Facing);
                s_Rays++;
                s_Hits += s_Loop != null ? 1 : 0;
                if (s_Tree?.Triangle != s_Loop?.Triangle || s_Tree?.Distance != s_Loop?.Distance)
                    s_Different++;
            }

            float Next() => (float) s_Random.NextDouble();
            for (var i = 0; i < 1500; i++)
            {
                var s_From = s_Min - s_Size * 0.25f + new System.Numerics.Vector3(Next(), Next(), Next()) * s_Size * 1.5f;
                var s_To = s_Min + new System.Numerics.Vector3(Next(), Next(), Next()) * s_Size;
                var s_Direction = System.Numerics.Vector3.Normalize(s_To - s_From);
                Compare(s_From, s_Direction, s_Size.Length() * 3, i % 3 == 0 ? -s_Direction : null);
            }

            foreach (var (s_Index, _) in s_Editing)
                if (s_Editor.StickerFrameOf(s_Index) is { } s_F)
                    for (var i = 0; i < 50; i++)
                    {
                        var s_OnSquare = s_F.At((Next() * 2 - 1) * s_F.HalfWidth, (Next() * 2 - 1) * s_F.HalfHeight);
                        Compare(s_OnSquare + s_F.Normal * s_F.Reach, -s_F.Normal, s_F.Reach * 2f, s_F.Normal);
                    }

            Check(s_Different == 0, $"the ray hierarchy answers as the plain loop on {s_Rays} rays ({s_Hits} hits, {s_Different} different)");
        }

        // ⏱ what an orbit step costs in sticker mode (rotating the vehicle once ran extremely slow, 1 or 2 fps): every
        // mouse move of an orbit rebuilds the overlay — the camera changes, the squares do not
        {
            var s_Clock = System.Diagnostics.Stopwatch.StartNew();
            const int c_Steps = 10;
            for (var i = 0; i < c_Steps; i++)
                s_Editor.SetPreviewView(0.6f + i * 0.05f, 0.35f, 3.2f);
            var s_PerStep = s_Clock.Elapsed.TotalMilliseconds / c_Steps;
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"      orbit: {s_PerStep:0.0} ms per step of the camera ({s_Editing.Count} square(s) listed, {s_Editor.BodySurface?.TriangleCount ?? 0} body triangle(s))"));
            Check(s_PerStep < 8.0, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"a step of the orbit costs under 8 ms in sticker mode ({s_PerStep:0.0} ms)"));
        }
        s_Editor.SetPreviewView(0.6f, 0.35f, 3.2f);
        Settle();
        Snap("2_slot_orbit");
        for (var i = 0; i < Math.Min(4, s_Editing.Count); i++)
        {
            s_Editor.SelectSticker(s_Editing[i].Index);
            Look(s_Editing[i].Index);
            Snap($"3_square{i}");
            // …and almost edge-on (a view of the LAV-25's bow, 2026-10-09: spikes stood out of the body there)
            if (s_Editor.StickerFrameOf(s_Editing[i].Index) is { } s_Edge)
            {
                LookAt(s_Edge, 1.3f);
                Snap($"3_square{i}_edge");
            }
        }

        // --- 3c. a click on a square selects it ----------------------------------------------------------------------------------------
        // (a left click directly on the mesh where a sticker is projected selects that sticker instead of creating a new one)
        if (s_Editing.Count > 0)
        {
            Console.WriteLine("STEP 3c - a click on a square selects it, it adds none");
            var s_Target = s_Editing[s_Editing.Count - 1].Index;
            s_Editor.SelectSticker(-1);
            Look(s_Target);
            var s_Before3c = Squares().Count;
            var s_At3c = s_Editor.StickerFrameOf(s_Target) is { } s_F3c ? s_Editor.PanelPointOf(s_F3c.Origin) : null;
            if (s_At3c is { } s_Click3c)
            {
                s_Editor.DriveStickerMouse("down", s_Click3c.X, s_Click3c.Y);
                s_Editor.DriveStickerMouse("up", s_Click3c.X, s_Click3c.Y);
                Settle();
            }

            Check(s_At3c != null && Squares().Count == s_Before3c && s_Editor.SelectedSticker == s_Target,
                $"a click on square #{s_Target} selects it ({s_Before3c} -> {Squares().Count} square(s), selected {s_Editor.SelectedSticker})");
        }

        // --- 4. a click on bare body: a square where it lands ---------------------------------------------------------------------------
        Console.WriteLine("STEP 4 - a click on the vehicle puts a square");
        s_Editor.SelectSticker(-1);
        s_Editor.SetPreviewView((float) (Math.PI / 2), 0.15f, 2.6f);
        Settle();
        s_Editor.CaptureShown(out var s_ViewW, out var s_ViewH);
        (float X, float Y)? s_Click = null;
        for (var s_Ring = 0; s_Ring < 14 && s_Click == null; s_Ring++)
        for (var s_Step = -s_Ring; s_Step <= s_Ring && s_Click == null; s_Step++)
        {
            var (s_X, s_Y) = (s_ViewW / 2f + s_Step * s_ViewW / 40f, s_ViewH / 2f - s_Ring * s_ViewH / 40f);
            if (s_Editor.StickerHitInfo(s_X, s_Y) is { } s_Info &&
                !s_Editing.Any(p_S => s_Editor.StickerFrameOf(p_S.Index) is { } s_F && s_F.Contains(s_Info.Position)))
                s_Click = (s_X, s_Y);
        }

        var s_Before = Squares().Count;
        if (s_Click is { } s_At)
        {
            s_Editor.DriveStickerMouse("down", s_At.X, s_At.Y);
            s_Editor.DriveStickerMouse("up", s_At.X, s_At.Y);
            Settle();
        }

        var s_After = Squares();
        var s_New = s_After.FirstOrDefault(p_S => s_Editing.All(p_E => p_E.Index != p_S.Index));
        Check(s_Click != null && s_After.Count == s_Before + 1 && s_New.Sticker is { IsSpatial: true, Width: 0.4 },
            $"the click put one square of 0.4 m laid out in space ({s_Before} -> {s_After.Count}" +
            $"{(s_New.Sticker is { } s_P ? $", width {s_P.Width}, spatial {s_P.IsSpatial}" : "")})");
        Snap("4_clicked");

        // --- 5. turned and widened -----------------------------------------------------------------------------------------------------
        Console.WriteLine("STEP 5 - the new square turned by 30° and 1.4 times wider");
        var s_Turned = 0.0;
        var s_Wider = 0.0;
        if (s_New.Sticker != null)
        {
            var s_Start = s_Editor.StickerFrameOf(s_New.Index);
            s_Turned = RimeShaderEditor.MainWindow.WrapDegrees(s_New.Sticker.Rotation + 30);
            s_Wider = s_New.Sticker.Width * 1.4;
            s_Editor.UpdateSticker(s_New.Index, p_Width: s_Wider, p_Rotation: s_Turned);
            Settle();
            var s_End = s_Editor.StickerFrameOf(s_New.Index);
            Check(s_Start != null && s_End != null && Math.Abs(s_End.Value.HalfWidth / s_Start.Value.HalfWidth - 1.4) < 1e-3 &&
                  Math.Abs(System.Numerics.Vector3.Dot(s_End.Value.AxisX, s_Start.Value.AxisX) - Math.Cos(Math.PI / 6)) < 1e-3,
                "its frame turns by 30° about its normal and widens 1.4 times");

            // ⏱ what a change of a square costs (a drag, a turn): its frame, outline and picture are made again — a new key every step
            var s_Clock = System.Diagnostics.Stopwatch.StartNew();
            const int c_Turns = 10;
            for (var i = 1; i <= c_Turns; i++)
                s_Editor.UpdateSticker(s_New.Index, p_Rotation: RimeShaderEditor.MainWindow.WrapDegrees(s_Turned + i));
            var s_PerTurn = s_Clock.Elapsed.TotalMilliseconds / c_Turns;
            s_Editor.UpdateSticker(s_New.Index, p_Rotation: s_Turned);
            Check(s_PerTurn < 16.0, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"a change of a square (turned by a degree) costs under 16 ms ({s_PerTurn:0.0} ms)"));
            s_Editor.SelectSticker(s_New.Index);
            Look(s_New.Index);
            Snap("5_turned");
        }

        // --- 6. Save slot ----------------------------------------------------------------------------------------------------------------
        Console.WriteLine("STEP 6 - Save slot");
        var s_Count = Squares().Count;
        var s_File = s_Panel.SaveSlot();
        Settle();
        Check(s_File != null && File.Exists(s_File) &&
              Path.GetFullPath(Path.GetDirectoryName(s_File)!).Equals(Path.GetFullPath(s_Slots), StringComparison.OrdinalIgnoreCase),
            $"Save slot writes the vehicle's file into the scratch folder ({s_File}): \"{s_Panel.SlotStatusText}\"");
        var s_Read = EmblemSlot.SavedPlacements(s_Mesh, out var s_ReadFrom);
        Check(s_Read != null && s_ReadFrom == s_File && s_Read.Count == s_Count && s_Read.All(p_P => p_P.IsSpatial) &&
              s_Read.Any(p_P => Math.Abs(p_P.Rotation - s_Turned) < 0.01 && Math.Abs(p_P.Width - s_Wider) < 1e-9),
            $"the file reads back with its {s_Count} square(s) laid out in space, the turned one among them");
        if (s_File != null && File.Exists(s_File))
            Console.WriteLine("      " + File.ReadAllText(s_File).Split('\n').Take(26).Aggregate((p_A, p_B) => p_A + "\n      " + p_B));
        Check(!Squares().Any() && s_Editor.CamoSnapshot().StickerCount == 0, "after saving, the camo carries no sticker (the squares were the slot)");
        s_Editor.SetPreviewView(0.6f, 0.35f, 3.2f);
        Settle();
        Snap("6_saved");

        // --- 6b. a click on a saved square: the slot goes into editing with that square selected -----------------------------------------
        if (s_Read is { Count: > 0 } && s_Editor.BodySurface is { } s_SavedBody && s_SavedBody.FrameOf(s_Read[0], 1.0) is { } s_SavedFrame)
        {
            Console.WriteLine("STEP 6b - a click on a saved square edits the slot with it selected");
            LookAt(s_SavedFrame);
            var s_At6b = s_Editor.PanelPointOf(s_SavedFrame.Origin);
            if (s_At6b is { } s_Click6b)
            {
                s_Editor.DriveStickerMouse("down", s_Click6b.X, s_Click6b.Y);
                s_Editor.DriveStickerMouse("up", s_Click6b.X, s_Click6b.Y);
                Settle();
            }

            var s_Now = Squares();
            Check(s_At6b != null && s_Now.Count == s_Read.Count && s_Now.Count > 0 && s_Editor.SelectedSticker == s_Now[0].Index,
                $"a click on saved square #0 puts the {s_Read.Count} saved square(s) into editing with #0 selected " +
                $"({s_Now.Count} listed, selected {s_Editor.SelectedSticker})");
        }

        // --- 7. Forget mine --------------------------------------------------------------------------------------------------------------
        Console.WriteLine("STEP 7 - Forget mine");
        s_Panel.ResetSlot();
        Settle();
        Check(s_File == null || !File.Exists(s_File), "Forget mine removes the vehicle's file from the scratch folder");
        var s_Back = EmblemSlot.SavedPlacements(s_Mesh, out var s_BackFrom);
        Check((s_Back?.Count ?? 0) == s_Expected && s_BackFrom == s_ShippedFrom,
            $"the shipped slot is back ({s_Back?.Count ?? 0} square(s) from {s_BackFrom ?? "nowhere"})");

        // --- 8. Done ---------------------------------------------------------------------------------------------------------------------
        Console.WriteLine("STEP 8 - Done");
        s_Editor.SetStickerMode(false);
        Settle();
        Check(GraphPrint() == s_GraphBefore && s_Editor.DocumentGraph.StickerRegister == null,
            $"leaving, the vehicle's graph is as it was ({GraphPrint()} vs {s_GraphBefore})");
        var s_UserAfter = Directory.Exists(s_UserFolder) ? Directory.GetFiles(s_UserFolder).Length : 0;
        Check(s_UserAfter == s_UserBefore, $"the user's own slot folder was not written ({s_UserBefore} -> {s_UserAfter} file(s))");

        var s_Hashes = s_Shots.Values.ToList();
        Check(s_Hashes.Distinct().Count() == s_Hashes.Count, $"every photograph differs from the others ({s_Hashes.Count} taken)");
        Console.WriteLine(s_Failures.Count == 0 ? "VEHICLESLOTTEST: PASS" : $"VEHICLESLOTTEST: FAIL ({s_Failures.Count})");
        return s_Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// --vehicleslotslua &lt;out.lua&gt;: the squares the studio ships (Data/emblem/slots — never the user's own folder) as the Lua table the
    /// game's emblem decals are stamped from (the driver's emblem on his vehicle). One entry per body mesh, keyed by its
    /// last path segment in lower case (a camo's body clone keeps it: C/SNOW/s/xpack01/dpv/dpv_mesh); per square, in the mesh's own space
    /// (the game's: BF4's squares read back through this frame to 0.1 mm): c = centre, r = the picture's right and u = its up seen from
    /// outside, n = the outward normal (r × u = n), w and h = its sides in metres — the frame sticker mode draws (StickerSurface.FrameOf).
    /// </summary>
    private static int VehicleSlotsLua(string p_Out)
    {
        var s_Culture = System.Globalization.CultureInfo.InvariantCulture;
        var s_Empty = Path.Combine(Path.GetTempPath(), "camo_slots_shipped_only_" + Environment.ProcessId);
        Directory.CreateDirectory(s_Empty);
        Environment.SetEnvironmentVariable("CAMO_EMBLEM_SLOTS", s_Empty);
        var s_Failures = new List<string>();
        var s_Lua = new System.Text.StringBuilder();
        var s_Squares = 0;
        var s_Vehicles = 0;

        string N(float p_Value) => p_Value.ToString("0.######", s_Culture);
        string V(System.Numerics.Vector3 p_V) => $"{{ {N(p_V.X)}, {N(p_V.Y)}, {N(p_V.Z)} }}";

        s_Lua.Append("-- Generated by RimeCamoStudio --vehicleslotslua from the slots the studio ships (Data/emblem/slots), ")
             .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", s_Culture)).Append(".\n")
             .Append("-- Per body mesh (its last path segment, lower case): the emblem squares in the mesh's own space. c = centre, r = the\n")
             .Append("-- picture's right and u = its up seen from outside, n = the outward normal (r x u = n), w / h = its sides in metres.\n")
             .Append("return {\n");

        foreach (var s_Vehicle in VehicleCatalog.Load().Vehicles.Where(p_V => p_V.PreviewMesh.Length > 0).OrderBy(p_V => p_V.Display, StringComparer.Ordinal))
        {
            var s_Placements = EmblemSlot.SavedPlacements(s_Vehicle.PreviewMesh, out var s_From);
            if (s_Placements == null || s_Placements.Count == 0)
            {
                s_Failures.Add($"{s_Vehicle.Display}: no shipped slot");
                Console.WriteLine($"!! {s_Vehicle.Display} ({s_Vehicle.PreviewMesh}): no shipped slot");
                continue;
            }

            var s_File = Cache.GameCache.MeshFile(s_Vehicle.PreviewMesh);
            var s_Surface = StickerSurface.LoadRsm3(s_File, _ => true);
            if (s_Surface == null)
            {
                s_Failures.Add($"{s_Vehicle.Display}: its body is not in the mesh cache ({s_File})");
                Console.WriteLine($"!! {s_Vehicle.Display}: its body is not in the mesh cache ({s_File})");
                continue;
            }

            var s_Key = Path.GetFileNameWithoutExtension(EmblemSlot.SlotFileName(s_Vehicle.PreviewMesh));
            s_Lua.Append($"\t[\"{s_Key}\"] = {{ -- {s_Vehicle.Display}\n");
            var s_Index = 0;

            // ⭐ (2026-10-10, an M1A2 made again with its turret kept 72° round: the Pikachu of the turret's square came out half
            // off it — an emblem on a part of the vehicle that may already be moved will not be in its original position): a square
            // lies on a part the game may turn (a turret, a gun), so the game has to know WHICH. The part
            // under the square's centre (the cached body keeps each vertex's part) → the camo piece that holds that part (its mover,
            // Data/vehicles.json, VehiclePiecePlanner) → m = that piece's holder, the component the stamp follows (none on the hull)
            var s_TriangleParts = TrianglePartsOf(s_File);
            if (s_TriangleParts == null)
                Console.WriteLine($"   {s_Vehicle.Display}: its cached body keeps no part per vertex (an RSM4/5 dump) -- every square as the hull's");
            foreach (var s_P in s_Placements)
            {
                if (!s_P.IsSpatial || s_Surface.FrameOf(s_P, 1.0) is not { } s_Frame)
                {
                    s_Failures.Add($"{s_Vehicle.Display} #{s_Index}: not a square laid out in space");
                    s_Index++;
                    continue;
                }

                var s_Right = System.Numerics.Vector3.Normalize(s_Frame.AxisX);
                var s_Up = -System.Numerics.Vector3.Normalize(s_Frame.AxisY);
                var s_Normal = System.Numerics.Vector3.Normalize(s_Frame.Normal);
                var s_Handed = System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(s_Right, s_Up), s_Normal);
                var s_Square = Math.Abs(System.Numerics.Vector3.Dot(s_Right, s_Up)) + Math.Abs(System.Numerics.Vector3.Dot(s_Right, s_Normal));
                if (s_Handed < 0.9999f || s_Square > 1e-4f)
                    s_Failures.Add($"{s_Vehicle.Display} #{s_Index}: its frame is not right x up = normal ({s_Handed:0.0000}, {s_Square:0.00000})");

                var (s_Closest, s_Off) = s_Surface.ClosestTriangle(s_Frame.Origin);
                var s_Mover = "";
                var s_On = "the hull";
                if (s_TriangleParts != null && s_Closest >= 0 && s_Closest < s_TriangleParts.Length)
                {
                    var s_Part = s_TriangleParts[s_Closest];
                    var s_Piece = s_Vehicle.Pieces.FirstOrDefault(p_Piece => p_Piece.Parts.Contains(s_Part));
                    if (s_Piece == null)
                        s_On = $"part {s_Part}, in no camo piece: as the hull";
                    else if (string.Equals(s_Piece.Name, "hull", StringComparison.OrdinalIgnoreCase) || s_Piece.Holder.Length == 0)
                        s_On = $"part {s_Part}, the hull";
                    else
                    {
                        s_Mover = s_Piece.Holder.ToUpperInvariant();
                        s_On = $"part {s_Part}, piece {s_Piece.Name}: follows {s_Mover[..8]}";
                    }
                }

                s_Lua.Append($"\t\t{{ c = {V(s_Frame.Origin)}, r = {V(s_Right)}, u = {V(s_Up)}, n = {V(s_Normal)}, " +
                             $"w = {N(s_Frame.HalfWidth * 2)}, h = {N(s_Frame.HalfHeight * 2)}" +
                             (s_Mover.Length > 0 ? $", m = \"{s_Mover}\"" : "") + " },\n");
                Console.WriteLine(string.Create(s_Culture,
                    $"  {s_Vehicle.Display} #{s_Index}: {s_Frame.HalfWidth * 2:0.000} x {s_Frame.HalfHeight * 2:0.000} m, {s_Off * 100:0.0} cm off the body, " +
                    $"turned {s_P.Rotation:0}°, on {s_On}"));
                s_Index++;
                s_Squares++;
            }

            s_Lua.Append("\t},\n");
            s_Vehicles++;
            Console.WriteLine($"== {s_Vehicle.Display}: {s_Index} square(s) from {s_From}");
        }

        s_Lua.Append("}\n");
        File.WriteAllText(p_Out, s_Lua.ToString(), new System.Text.UTF8Encoding(false));
        Directory.Delete(s_Empty, true);
        Console.WriteLine($"{s_Squares} square(s) on {s_Vehicles} vehicle(s) -> {Path.GetFullPath(p_Out)}");
        foreach (var s_Failure in s_Failures)
            Console.WriteLine("  FAIL: " + s_Failure);
        Console.WriteLine(s_Failures.Count == 0 ? "VEHICLESLOTSLUA: PASS" : $"VEHICLESLOTSLUA: FAIL ({s_Failures.Count})");
        return s_Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The part of each triangle of a cached body (RSM6/RSM7 keep the part index per vertex; a triangle's is its first corner's), in
    /// the order StickerSurface.LoadRsm3(p_Path, _ => true) lays the triangles out — every section, one after another. Null when the dump
    /// keeps no parts (RSM4/RSM5) or cannot be read.
    /// </summary>
    private static int[]? TrianglePartsOf(string p_Path)
    {
        try
        {
            using var s_Reader = new BinaryReader(File.OpenRead(p_Path));
            var s_Magic = new string(s_Reader.ReadChars(4));
            if (s_Magic is not ("RSM6" or "RSM7"))
                return null;

            // per vertex: position + uv, and one extra uv set (RSM6) or two (RSM7) -- LoadRsm3's own layout
            var s_Floats = s_Magic == "RSM7" ? 9L : 7L;
            var s_Parts = new List<int>();
            var s_Sections = s_Reader.ReadInt32();
            for (var s_Section = 0; s_Section < s_Sections; s_Section++)
            {
                s_Reader.ReadBytes(s_Reader.ReadInt32());   // shader
                s_Reader.ReadBytes(s_Reader.ReadInt32());   // material
                s_Reader.ReadInt32();
                s_Reader.ReadInt32();
                var s_Vertices = s_Reader.ReadInt32();
                s_Reader.BaseStream.Seek(s_Vertices * s_Floats * sizeof(float), SeekOrigin.Current);
                var s_Indices = new int[s_Reader.ReadInt32()];
                for (var i = 0; i < s_Indices.Length; i++)
                    s_Indices[i] = s_Reader.ReadInt32();
                var s_VertexPart = new int[s_Vertices];
                for (var i = 0; i < s_Vertices; i++)
                    s_VertexPart[i] = s_Reader.ReadInt32();
                for (var t = 0; t + 2 < s_Indices.Length; t += 3)
                    s_Parts.Add(s_VertexPart[s_Indices[t]]);
            }

            return s_Parts.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
