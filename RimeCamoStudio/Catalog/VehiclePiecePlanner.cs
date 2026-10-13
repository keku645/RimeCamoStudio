using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// A vehicle's camo PIECES and its driver part, read out of the game's own data (the M1A2's were measured by hand, the other twenty
/// are not going to be). Two readings of the game go in: the vehicle's blueprint partition (its component
/// tree, as the game's own JSON dump of it) and a census of its composite body (each part's rest transform and its triangles per
/// material). The rules are the ones the M1A2 proved in game (runs 131-138):
///  - a part is painted by a piece when EVERY material it wears takes the camo (hiding a part hides all of it: a part with glass or
///    a lamp would lose them);
///  - a part is left out when the component that owns it hangs under an unlock switch (a kit: those are the vehicle's gadgets) or
///    is a wheel (the one-byte component budget: 18 wheels would leave room for two camos on an M1A2);
///  - parts go together by the component that MOVES them: the nearest ChildComponentData above their owner, a part component
///    whose Transform the game's logic drives (2026-09-26), or the chassis. One piece per mover, hung at identity from the mover
///    itself — or, for the chassis, from a plain part container at its origin (a piece hung from the chassis component is not
///    drawn, run 119);
///  - among one mover's parts, those the game shows and hides by itself go apart, one piece per way it does (a MIRROR piece: its
///    own switch follows the game's Show / Hide, VehicleCamos.lua);
///  - the piece's space is its first part's, so the first part is one that rests exactly where the mover is (a part's rest
///    transform in the body is its component's transform); a mover with no such part is left out;
///  - the stock parts it replaces are the owners of its parts (hidden for the camo's driver);
///  - the driver's part is the own part of the seat whose entry order is 0.
/// A vehicle's component budget is one byte (runtimeComponentCount, exactly its tree's components): each camo costs its decision,
/// a switch per holder and a component per piece, and the plan says how many camos fit.
/// </summary>
public static class VehiclePiecePlanner
{
    /// <summary>One part of a composite body, as the census reads it.</summary>
    public sealed class CensusPart
    {
        public int Index;

        /// <summary>right, up, forward, translation (the rest transform the body stores for the part).</summary>
        public float[] Transform = new float[12];

        /// <summary>Material index -> triangles of the part in it.</summary>
        public Dictionary<int, int> Materials = new();
    }

    /// <summary>A body's census: its parts, and the declaration a piece cut from each material is drawn with.</summary>
    public sealed class Census
    {
        public List<CensusPart> Parts = new();

        /// <summary>Material index -> the rigid declaration ("0xC83353E0") of its section, "-" when it cannot be cut.</summary>
        public Dictionary<int, string> RigidByMaterial = new();
    }

    public sealed class Plan
    {
        public string DriverPart = "";
        public List<VehiclePiece> Pieces = new();

        /// <summary>Part -> the component that owns it (what hiding the part hides).</summary>
        public Dictionary<int, string> OwnerOfPart = new();

        /// <summary>The presets its pieces wear from the package's rigid-capable copy (see VehicleEntry.RigidTwins).</summary>
        public List<string> RigidTwins = new();

        /// <summary>The components the vehicle holds, as its data says (the byte, unsigned) and as its tree counts them.</summary>
        public int ComponentCount;
        public int TreeComponents;
        public int CostPerCamo;
        public int CamosThatFit;

        /// <summary>What was left out and why, one line each.</summary>
        public List<string> Notes = new();
    }

    private const int MaxComponents = 255;
    private const float TransTolerance = 0.003f;
    private const float RotTolerance = 0.002f;

    // the game's own ids (its hash of each name), as the blueprint's connections carry them
    private const int TransformField = -2024647575;   // a component's Transform
    private const int ShowEvent = 2089430886;
    private const int HideEvent = 2089152613;

    /// <summary>The census lines of a Rime run ('PARTCENSUS:', then 'PARTSUBSET:' and 'PART:' lines), by mesh name.</summary>
    public static Dictionary<string, Census> ParseCensus(string p_Output)
    {
        var s_Out = new Dictionary<string, Census>(StringComparer.OrdinalIgnoreCase);
        Census? s_Current = null;

        foreach (var s_Raw in p_Output.Split('\n'))
        {
            var s_Line = s_Raw.Trim();
            if (s_Line.StartsWith("PARTCENSUS: ", StringComparison.Ordinal))
            {
                var s_Mesh = Field(s_Line, "mesh");
                s_Current = new Census();
                s_Out[s_Mesh] = s_Current;
                continue;
            }

            if (s_Current != null && s_Line.StartsWith("PARTSUBSET: ", StringComparison.Ordinal))
            {
                var s_Material = int.Parse(Field(s_Line, "material"), CultureInfo.InvariantCulture);
                s_Current.RigidByMaterial.TryAdd(s_Material, Field(s_Line, "rigid"));
                continue;
            }

            if (s_Current == null || !s_Line.StartsWith("PART: ", StringComparison.Ordinal))
                continue;

            var s_Words = s_Line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var s_Part = new CensusPart { Index = int.Parse(s_Words[1], CultureInfo.InvariantCulture) };
            var s_Rot = Floats(Field(s_Line, "rot"));
            var s_Trans = Floats(Field(s_Line, "trans"));
            Array.Copy(s_Rot, 0, s_Part.Transform, 0, 9);
            Array.Copy(s_Trans, 0, s_Part.Transform, 9, 3);

            var s_Mats = Field(s_Line, "mats");
            if (s_Mats != "-")
                foreach (var s_Pair in s_Mats.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var s_Kv = s_Pair.Split(':');
                    s_Part.Materials[int.Parse(s_Kv[0], CultureInfo.InvariantCulture)] = int.Parse(s_Kv[1], CultureInfo.InvariantCulture);
                }

            s_Current.Parts.Add(s_Part);
        }

        return s_Out;

        static string Field(string p_Line, string p_Name)
        {
            var s_At = p_Line.IndexOf(" " + p_Name + "=", StringComparison.Ordinal);
            if (s_At < 0)
                return "";
            s_At += p_Name.Length + 2;
            var s_End = p_Line.IndexOf(' ', s_At);
            return s_End < 0 ? p_Line[s_At..] : p_Line[s_At..s_End];
        }

        static float[] Floats(string p_Text) => p_Text.Split(',')
            .Select(p_F => float.Parse(p_F, CultureInfo.InvariantCulture)).ToArray();
    }

    // ---- the blueprint's tree ----------------------------------------------------------------------------------------------------
    private sealed class Node
    {
        public string Guid = "";
        public string Type = "";
        public JsonElement Fields;
        public Node? Parent;
        public float[] World = Identity();
        public float[] Local = Identity();
        public bool UnderUnlock;
        public int Part = -1;
        public readonly List<Node> Children = new();
    }

    private static float[] Identity() => new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };

    /// <summary>A local transform under its parent's world one (a row times the parent's rotation; the translation a point).</summary>
    private static float[] Compose(float[] p_Local, float[] p_Parent)
    {
        var s_Out = new float[12];
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 3; c++)
                s_Out[r * 3 + c] = p_Local[r * 3] * p_Parent[c] + p_Local[r * 3 + 1] * p_Parent[3 + c] + p_Local[r * 3 + 2] * p_Parent[6 + c] +
                                   (r == 3 ? p_Parent[9 + c] : 0);
        return s_Out;
    }

    private static bool Same(float[] p_A, float[] p_B)
    {
        for (var i = 0; i < 9; i++)
            if (MathF.Abs(p_A[i] - p_B[i]) > RotTolerance)
                return false;
        for (var i = 9; i < 12; i++)
            if (MathF.Abs(p_A[i] - p_B[i]) > TransTolerance)
                return false;
        return true;
    }

    private static float[] ReadTransform(JsonElement p_Fields)
    {
        if (!p_Fields.TryGetProperty("Transform", out var s_T) || s_T.ValueKind != JsonValueKind.Object)
            return Identity();

        var s_Out = new float[12];
        var s_Row = 0;
        foreach (var s_Name in new[] { "right", "up", "forward", "trans" })
        {
            var s_V = s_T.GetProperty(s_Name);
            s_Out[s_Row * 3] = s_V.GetProperty("x").GetSingle();
            s_Out[s_Row * 3 + 1] = s_V.GetProperty("y").GetSingle();
            s_Out[s_Row * 3 + 2] = s_V.GetProperty("z").GetSingle();
            s_Row++;
        }

        return s_Out;
    }

    private static IEnumerable<string> Refs(JsonElement p_Fields, string p_Name)
    {
        if (!p_Fields.TryGetProperty(p_Name, out var s_List) || s_List.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var s_Ref in s_List.EnumerateArray())
            if (s_Ref.ValueKind == JsonValueKind.Object && s_Ref.TryGetProperty("InstanceGuid", out var s_Guid))
                yield return s_Guid.GetString()!.ToLowerInvariant();
    }

    /// <summary>A connection's end (its "Source" or "Target"), lower case, or "".</summary>
    private static string EndOf(JsonElement p_Connection, string p_Name) =>
        p_Connection.TryGetProperty(p_Name, out var s_Ref) && s_Ref.ValueKind == JsonValueKind.Object &&
        s_Ref.TryGetProperty("InstanceGuid", out var s_Guid) ? s_Guid.GetString()?.ToLowerInvariant() ?? "" : "";

    /// <summary>An event connection's event id (its "SourceEvent" or "TargetEvent"), or 0.</summary>
    private static int EventOf(JsonElement p_Connection, string p_Name) =>
        p_Connection.TryGetProperty(p_Name, out var s_Event) && s_Event.ValueKind == JsonValueKind.Object &&
        s_Event.TryGetProperty("Id", out var s_Id) && s_Id.TryGetInt32(out var s_Value) ? s_Value : 0;

    /// <summary>
    /// The plan for one vehicle: its blueprint partition dumped as JSON (dump_partition_json), its body's census and the material
    /// indices of that body that take the camo.
    /// </summary>
    public static Plan Compute(string p_BlueprintJson, IReadOnlyList<CensusPart> p_Parts, IReadOnlyCollection<int> p_CamoMaterials)
    {
        var s_Plan = new Plan();
        using var s_Doc = JsonDocument.Parse(File.ReadAllText(p_BlueprintJson));
        var s_Instances = s_Doc.RootElement.GetProperty("Instances");
        var s_ByGuid = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_I in s_Instances.EnumerateObject())
            s_ByGuid[s_I.Name.ToLowerInvariant()] = s_I.Value;

        string TypeOf(JsonElement p_I) => p_I.TryGetProperty("$type", out var s_T) ? s_T.GetString() ?? "" : "";

        var s_Entity = s_ByGuid.Values.FirstOrDefault(p_I => TypeOf(p_I) == "VehicleEntityData");
        if (s_Entity.ValueKind != JsonValueKind.Object)
        {
            s_Plan.Notes.Add("no VehicleEntityData in the blueprint -- nothing planned");
            return s_Plan;
        }

        s_Plan.ComponentCount = s_Entity.GetProperty("RuntimeComponentCount").GetInt32() & 0xFF;

        // the tree, with each node's world transform, its part and whether a kit switch is above it
        var s_Nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var s_Roots = new List<Node>();

        void Walk(IEnumerable<string> p_Guids, Node? p_Parent, int p_Depth)
        {
            if (p_Depth > 32)
                return;

            foreach (var s_Guid in p_Guids)
            {
                if (s_Nodes.ContainsKey(s_Guid) || !s_ByGuid.TryGetValue(s_Guid, out var s_Fields))
                    continue;

                var s_Node = new Node
                {
                    Guid = s_Guid,
                    Type = TypeOf(s_Fields),
                    Fields = s_Fields,
                    Parent = p_Parent,
                    Local = ReadTransform(s_Fields),
                };
                s_Node.World = p_Parent != null ? Compose(s_Node.Local, p_Parent.World) : s_Node.Local;
                s_Node.UnderUnlock = s_Node.Type == "UnlockComponentData" || (p_Parent?.UnderUnlock ?? false);

                var s_Health = Refs(s_Fields, "HealthStates").FirstOrDefault();
                if (s_Health != null && s_ByGuid.TryGetValue(s_Health, out var s_State) &&
                    s_State.TryGetProperty("PartIndex", out var s_Index) && s_Index.TryGetInt64(out var s_Part) && s_Part is >= 0 and < 4096)
                    s_Node.Part = (int) s_Part;

                s_Nodes[s_Guid] = s_Node;
                (p_Parent?.Children ?? s_Roots).Add(s_Node);

                if (s_Fields.TryGetProperty("Components", out _))
                    Walk(Refs(s_Fields, "Components"), s_Node, p_Depth + 1);
            }
        }

        Walk(Refs(s_Entity, "Components"), null, 0);
        s_Plan.TreeComponents = s_Nodes.Values.Count(p_N => p_N.Fields.TryGetProperty("Components", out _) &&
                                                            p_N.Fields.TryGetProperty("Transform", out _));

        var s_Chassis = s_Roots.FirstOrDefault(p_N => p_N.Type is "VehicleComponentData" or "ChassisComponentData");
        if (s_Chassis == null)
        {
            s_Plan.Notes.Add("no chassis component -- nothing planned");
            return s_Plan;
        }

        // the driver: the own part of the seat with the LOWEST entry order (0 on every ground vehicle; the AH-1Z, Mi-28, Su-35BM and
        // Z-11W number their pilot 1). ⭐ An aircraft's pilot seat has no part of its own (only the Mi-28's does): then the seat ITSELF
        // is where the camo's decision hangs — the game hangs an UnlockComponentData straight from a seat too (the AH-1Z's and Mi-28's
        // gunner seats, 2026-09-25), and the framework asks that seat's player (VehicleCamos.lua)
        static int OrderOf(Node p_N) => p_N.Fields.TryGetProperty("EntryOrderNumber", out var s_O) ? s_O.GetInt32() : int.MaxValue;
        var s_Seat = s_Nodes.Values.Where(p_N => p_N.Type == "PlayerEntryComponentData" && !p_N.UnderUnlock)
            .OrderBy(OrderOf).FirstOrDefault();
        var s_DriverPart = s_Seat?.Children.FirstOrDefault(p_N => p_N.Type == "PartComponentData") ?? s_Seat;
        if (s_DriverPart == null)
        {
            s_Plan.Notes.Add("no seat -- nothing planned");
            return s_Plan;
        }

        s_Plan.DriverPart = s_DriverPart.Guid;

        // ⭐ what the GAME's logic moves and shows by itself (seen on Riverside: the ASRAD's and the Vodnik AA's launchers stayed
        // frozen in the air and their animated version spawned without camo; the dirt bike's handlebar and gauges static): a
        // part component whose Transform a logic entity drives (a PropertyConnection into it — TransformHub, TransformBlend,
        // TransformMultiplier, AnimatedDriver…: 139 over the vehicles) moves its parts and whatever hangs from it, as a
        // ChildComponentData does; and the game sends Show / Hide to part components by itself (a sequence when a gunner takes his seat,
        // an event gate between first and third person, OnSpawned…) — to the component that OWNS the part (863 of the 865 targets)
        var s_Driven = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var s_Toggles = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var s_Wiring = s_ByGuid.Values.FirstOrDefault(p_I => TypeOf(p_I) == "VehicleBlueprint");
        if (s_Wiring.ValueKind == JsonValueKind.Object)
        {
            if (s_Wiring.TryGetProperty("PropertyConnections", out var s_Properties) && s_Properties.ValueKind == JsonValueKind.Array)
                foreach (var s_C in s_Properties.EnumerateArray())
                    if (s_C.TryGetProperty("TargetFieldId", out var s_Field) && s_Field.TryGetInt32(out var s_Id) && s_Id == TransformField)
                        s_Driven.Add(EndOf(s_C, "Target"));

            if (s_Wiring.TryGetProperty("EventConnections", out var s_Events) && s_Events.ValueKind == JsonValueKind.Array)
            {
                // every event into each entity, and what fires an event splitter's outputs (all of them, at once, on any input)
                var s_Into = new Dictionary<string, List<(string Source, int Event)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var s_C in s_Events.EnumerateArray())
                {
                    var s_Target = EndOf(s_C, "Target");
                    if (!s_Into.TryGetValue(s_Target, out var s_List))
                        s_Into[s_Target] = s_List = new List<(string, int)>();
                    s_List.Add((EndOf(s_C, "Source"), EventOf(s_C, "SourceEvent")));
                }

                // ⭐ a Show / Hide is named by what fires it seen THROUGH event splitters (a splitter only copies its input to its
                // outputs): the M1A2's 11 skirts come through 22 splitters from the same two outputs of one logic reference (hidden
                // when the tank is disabled, shown when it is healthy again) — one way of showing them, one piece (2026-09-27)
                IEnumerable<string> Roots(string p_Source, int p_Event, ImmutableHashSet<string> p_Seen)
                {
                    if (!s_ByGuid.TryGetValue(p_Source, out var s_Splitter) || TypeOf(s_Splitter) != "EventSplitterEntityData" ||
                        p_Seen.Contains(p_Source) || !s_Into.TryGetValue(p_Source, out var s_Inputs) || s_Inputs.Count == 0)
                        return new[] { $"{p_Source}.{p_Event}" };
                    return s_Inputs.SelectMany(p_I => Roots(p_I.Source, p_I.Event, p_Seen.Add(p_Source)));
                }

                foreach (var s_C in s_Events.EnumerateArray())
                    if (EventOf(s_C, "TargetEvent") is ShowEvent or HideEvent)
                    {
                        var s_Target = EndOf(s_C, "Target");
                        if (!s_Toggles.TryGetValue(s_Target, out var s_Set))
                            s_Toggles[s_Target] = s_Set = new SortedSet<string>(StringComparer.Ordinal);
                        var s_Kind = EventOf(s_C, "TargetEvent") == ShowEvent ? "show" : "hide";
                        foreach (var s_Root in Roots(EndOf(s_C, "Source"), EventOf(s_C, "SourceEvent"), ImmutableHashSet<string>.Empty))
                            s_Set.Add($"{s_Root}>{s_Kind}");
                    }
            }
        }

        // how the game shows and hides a component's part ("" = never: it is there while the vehicle is)
        string TogglesOf(Node p_N) => s_Toggles.TryGetValue(p_N.Guid, out var s_Set) ? string.Join(" ", s_Set) : "";

        // the parts and their movers
        var s_OwnerOf = s_Nodes.Values.Where(p_N => p_N.Part >= 0).GroupBy(p_N => p_N.Part).ToDictionary(p_G => p_G.Key, p_G => p_G.First());
        var s_Groups = new Dictionary<(Node Mover, string Toggles), List<CensusPart>>();

        foreach (var s_Part in p_Parts.Where(p_P => p_P.Materials.Count > 0))
        {
            if (!s_Part.Materials.Keys.Any(p_CamoMaterials.Contains))
                continue;

            string? s_Why = null;
            Node? s_Mover = null;

            if (!s_OwnerOf.TryGetValue(s_Part.Index, out var s_Owner))
                s_Why = "no component owns it";
            else if (s_Owner.UnderUnlock)
                s_Why = "a kit's (under an unlock switch)";
            else if (s_Owner.Type.Contains("Wheel", StringComparison.Ordinal))
                s_Why = "a wheel";
            // a part that also wears a material with no camo (a hull's lamp lenses and vision blocks) comes WHOLE: the piece draws
            // that material too, as the game does, because hiding the part hides it — whether it can be drawn rigid is
            // KeepDrawable's question, like any other material's
            else
            {
                // up to the component that moves it: a ChildComponentData or the chassis; plain part containers and seats in between
                // stand still with their parent
                for (var s_N = s_Owner; s_N != null && s_Mover == null && s_Why == null; s_N = s_N.Parent)
                {
                    // (an aircraft's control surfaces move their parts as a turret does: FlapComponentData — the piece hangs from the
                    // flap like the stock part. A WingComponentData does NOT move: a static container of the wing's parts, which rest
                    // anywhere along it (none where the F/A-18F's wing component is) — crossed like a plain part container, 2026-09-25)
                    if (s_N == s_Chassis || s_N.Type is "ChildComponentData" or "FlapComponentData" ||
                        (s_N.Type == "PartComponentData" && s_Driven.Contains(s_N.Guid)))
                        s_Mover = s_N;
                    else if (s_N.Type is not ("PartComponentData" or "PlayerEntryComponentData" or "WingComponentData"))
                        s_Why = $"it moves with a {s_N.Type} ({s_N.Guid[..8]}), which the pieces do not follow";
                }

                s_Why ??= s_Mover == null ? "no component above it moves it" : null;

                // (a component between the part and its mover that the game shows and hides: its piece does not follow that — said,
                // not guessed: 2 of the 865 Show / Hide targets have no part of their own)
                for (var s_N = s_Owner.Parent; s_Why == null && s_N != null && s_N != s_Mover; s_N = s_N.Parent)
                    if (s_Toggles.ContainsKey(s_N.Guid))
                        s_Plan.Notes.Add($"part {s_Part.Index}: the {s_N.Type} ({s_N.Guid[..8]}) above it is shown and hidden by the game — its piece does not follow that");
            }

            if (s_Why != null)
            {
                s_Plan.Notes.Add($"part {s_Part.Index} left out: {s_Why}");
                continue;
            }

            var s_Key = (s_Mover!, TogglesOf(s_Owner!));
            if (!s_Groups.TryGetValue(s_Key, out var s_List))
                s_Groups[s_Key] = s_List = new List<CensusPart>();
            s_List.Add(s_Part);
        }

        // a piece per mover — and, among one mover's parts, per way the game shows and hides them: a piece whose stock parts the game
        // shows and hides by itself is a MIRROR piece (its own switch follows the game's Show / Hide of them, VehicleCamos.lua)
        foreach (var ((s_Mover, s_Shown), s_List) in s_Groups.OrderBy(p_G =>
                     p_G.Key.Mover == s_Chassis && p_G.Key.Toggles.Length == 0 ? -1 : p_G.Value.Min(p_P => p_P.Index)))
        {
            var s_Anchor = s_List.FirstOrDefault(p_P => p_P.Index == s_Mover.Part) ??
                           s_List.Where(p_P => Same(p_P.Transform, s_Mover.World)).OrderBy(p_P => p_P.Index).FirstOrDefault();
            var s_Parts = string.Join(", ", s_List.Select(p_P => p_P.Index));
            var s_Mirror = s_Shown.Length > 0;

            Node? s_Holder = s_Mover;
            if (s_Anchor == null || !Same(s_Anchor.Transform, s_Mover.World))
            {
                // ⭐ what the game moves or shows by itself and rests nowhere on its mover (the dirt bike's levers: their components sit
                // 1.7 m off the part component that swings them; the HIMARS' launcher parts; the M1A2's skirts) hangs WHOLE from the own
                // component of one of its parts, where that part rests: every owner of the group is rigid with the mover (the first
                // component above it that moves is the mover), so rigid with that one, and a hidden stock part does not hide what hangs
                // from it (100 pieces hang from the part they hide)
                var s_OnOwn = s_Mirror || s_Driven.Contains(s_Mover.Guid)
                    ? s_List.OrderBy(p_P => p_P.Index)
                        .FirstOrDefault(p_P => s_OwnerOf[p_P.Index] != s_Chassis && Same(p_P.Transform, s_OwnerOf[p_P.Index].World))
                    : null;

                if (s_OnOwn == null)
                {
                    s_Plan.Notes.Add($"parts {s_Parts} left out: none of them rests where their mover {s_Mover.Type} ({s_Mover.Guid[..8]}) is" +
                                     (s_Mirror || s_Driven.Contains(s_Mover.Guid) ? " nor where its own component is" : ""));
                    continue;
                }

                s_Anchor = s_OnOwn;
                s_Holder = s_OwnerOf[s_OnOwn.Index];
            }
            else if (s_Mover == s_Chassis)
            {
                // a plain part container at the chassis' origin, the emptiest first
                s_Holder = s_Chassis.Children
                    .Where(p_N => p_N.Type == "PartComponentData" && !p_N.UnderUnlock && Same(p_N.Local, Identity()))
                    .OrderBy(p_N => p_N.Part >= 0 ? 1 : 0).ThenBy(p_N => p_N.Children.Count)
                    .FirstOrDefault();

                if (s_Holder == null)
                {
                    s_Plan.Notes.Add($"parts {s_Parts} left out: the chassis has no plain part container at its origin to hang them from");
                    continue;
                }
            }

            var s_Ordered = new[] { s_Anchor }.Concat(s_List.Where(p_P => p_P != s_Anchor).OrderBy(p_P => p_P.Index)).ToList();
            var s_Hide = s_Ordered.Select(p_P => s_OwnerOf[p_P.Index].Guid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var s_P in s_Ordered)
                s_Plan.OwnerOfPart[s_P.Index] = s_OwnerOf[s_P.Index].Guid;

            s_Plan.Pieces.Add(new VehiclePiece
            {
                Name = s_Mover == s_Chassis && !s_Mirror ? "hull" : $"p{s_Anchor.Index:D3}",
                Parts = s_Ordered.Select(p_P => p_P.Index).ToList(),
                Materials = s_Ordered.SelectMany(p_P => p_P.Materials.Keys).Distinct().OrderBy(p_M => p_M).ToList(),
                Holder = s_Holder.Guid,
                Hide = string.Join(",", s_Hide),
                Mirror = s_Mirror,
            });
        }

        Recount(s_Plan);
        return s_Plan;
    }

    /// <summary>What a camo costs the vehicle (its decision, a switch per holder — and one per mirror piece, which has its own —, a
    /// component per piece) and how many fit.</summary>
    private static void Recount(Plan p_Plan)
    {
        var s_Holders = p_Plan.Pieces.Select(p_P => p_P.Mirror ? $"{p_P.Holder}|{p_P.Name}" : p_P.Holder)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        p_Plan.CostPerCamo = p_Plan.Pieces.Count == 0 ? 0 : 1 + s_Holders + p_Plan.Pieces.Count;
        p_Plan.CamosThatFit = p_Plan.Pieces.Count == 0 ? 0 : (MaxComponents - Math.Max(p_Plan.ComponentCount, p_Plan.TreeComponents)) / p_Plan.CostPerCamo;
    }

    /// <summary>
    /// Only what can be DRAWN stays: a part whose material has no solution for the piece's rigid declaration (and no copy a bake
    /// can ship) is taken out of its piece and stays stock (a piece without it would leave a hole where the hidden part was, and
    /// with it the material would not draw). A piece that loses its first part (the one that places it) goes whole.
    /// p_WhyNot(material) = why that material cannot be drawn on a piece, or null.
    /// </summary>
    public static void KeepDrawable(Plan p_Plan, Census p_Census, Func<int, string?> p_WhyNot, Func<int, bool>? p_Undrawn = null)
    {
        var s_PartsById = p_Census.Parts.ToDictionary(p_P => p_P.Index);
        var s_Kept = new List<VehiclePiece>();
        // ⭐ a material the game does not draw at all (no shader on its section, none bound by any variation: the Su-35BM's
        // PlaneSkeleton_M on its wings, 2026-09-25) keeps no part stock and is not cut into the piece
        var s_Undrawn = p_Undrawn ?? (_ => false);

        foreach (var s_Piece in p_Plan.Pieces)
        {
            var s_Parts = new List<int>();
            foreach (var s_Index in s_Piece.Parts)
            {
                var s_Why = s_PartsById[s_Index].Materials.Keys.Where(p_M => !s_Undrawn(p_M))
                    .Select(p_M => (Material: p_M, Why: p_WhyNot(p_M))).FirstOrDefault(p_W => p_W.Why != null);
                if (s_Why.Why != null)
                    p_Plan.Notes.Add($"part {s_Index} taken out of piece {s_Piece.Name}: material #{s_Why.Material} {s_Why.Why}");
                else
                    s_Parts.Add(s_Index);
            }

            if (s_Parts.Count == 0 || s_Parts[0] != s_Piece.Parts[0])
            {
                p_Plan.Notes.Add($"piece {s_Piece.Name} dropped: {(s_Parts.Count == 0 ? "none of its parts can be drawn" : "the part that places it cannot be drawn")}");
                continue;
            }

            s_Piece.Parts = s_Parts;
            s_Piece.Materials = s_Parts.SelectMany(p_P => s_PartsById[p_P].Materials.Keys).Where(p_M => !s_Undrawn(p_M))
                .Distinct().OrderBy(p_M => p_M).ToList();
            s_Piece.Hide = string.Join(",", s_Parts.Select(p_P => p_Plan.OwnerOfPart[p_P]).Distinct(StringComparer.OrdinalIgnoreCase));
            s_Kept.Add(s_Piece);
        }

        p_Plan.Pieces = s_Kept;
        Recount(p_Plan);
    }
}
