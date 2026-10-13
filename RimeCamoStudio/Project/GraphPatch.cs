using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace RimeCamoStudio.Project;

/// <summary>
/// ⭐ A GRAPH KEPT AS THE CHANGES MADE TO A TRANSLATION, NOT AS THE GRAPH. A starting document the studio ships (the sound suppressor's,
/// a vehicle's or a soldier part's own) is an edit of the node graph the translator makes out of a game shader — and that graph is made of
/// the game's shader, so it is made on the user's machine, out of his own game, by the cache preparation. What ships is only the edit:
/// the nodes taken out, the nodes changed, the nodes added and the wires moved, applied to the user's own translation when the preset is
/// asked for.
///
/// A node of the translation is named by its kind and the place the translator's layout gives it ("Multiply@826,256"): unique within a
/// translation (Make refuses otherwise) and the same on every machine, the layout being a function of the shader — while its id is a fresh
/// GUID per translation. A node the edit added is named "+1", "+2"… and gets a fresh id each time the patch is applied.
/// </summary>
internal static class GraphPatch
{
    public const string Format = "RimeCamoStudio graph patch";

    /// <summary>The parts of a graph file that are its nodes and wires; every other top-level field is a plain value.</summary>
    private static readonly HashSet<string> s_GraphParts = new(StringComparer.Ordinal) { "Nodes", "Connections", "Root", "Groups" };

    private static string KeyOf(JsonObject p_Node) =>
        $"{(string?) p_Node["Kind"]}@{p_Node["X"]?.ToJsonString()},{p_Node["Y"]?.ToJsonString()}";

    /// <summary>Every node of a graph by id, its root included (the root lives in its own field and wires reach it like any node).</summary>
    private static Dictionary<string, JsonObject> NodesById(JsonObject p_Graph)
    {
        var s_Nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var s_Node in p_Graph["Nodes"]?.AsArray() ?? new JsonArray())
            s_Nodes[(string) s_Node!["Id"]!] = s_Node.AsObject();
        if (p_Graph["Root"] is JsonObject s_Root)
            s_Nodes[(string) s_Root["Id"]!] = s_Root;
        return s_Nodes;
    }

    private static (string From, string FromPort, string To, string ToPort) WireOf(JsonNode p_Wire) =>
        ((string) p_Wire["FromNode"]!, (string) p_Wire["FromPort"]!, (string) p_Wire["ToNode"]!, (string) p_Wire["ToPort"]!);

    /// <summary>A node without its id: what the patch stores for a node it changes or adds.</summary>
    private static JsonObject Body(JsonObject p_Node)
    {
        var s_Body = p_Node.DeepClone().AsObject();
        s_Body.Remove("Id");
        return s_Body;
    }

    /// <summary>
    /// The patch that turns <paramref name="p_Base"/> (a translation) into <paramref name="p_Edited"/> — a graph made by editing that very
    /// translation, so the nodes it kept still carry the translation's ids. Null, with the reason, when it was not made from it.
    /// </summary>
    public static JsonObject? Make(JsonObject p_Base, JsonObject p_Edited, out string p_Report)
    {
        var s_Base = NodesById(p_Base);
        var s_Edited = NodesById(p_Edited);
        var s_Keys = s_Base.ToDictionary(p_N => p_N.Key, p_N => KeyOf(p_N.Value), StringComparer.Ordinal);
        if (s_Keys.Values.Distinct(StringComparer.Ordinal).Count() != s_Keys.Count)
        {
            p_Report = "two nodes of the translation share a kind and a place: its nodes cannot be named";
            return null;
        }

        var s_Kept = s_Edited.Keys.Count(s_Base.ContainsKey);
        if (s_Kept == 0)
        {
            p_Report = "the graph shares no node with the translation: it was not made from it";
            return null;
        }

        if (p_Edited["Groups"] is JsonArray { Count: > 0 })
        {
            p_Report = "the graph has groups, which a patch does not carry";
            return null;
        }

        var s_Added = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s_Id in s_Edited.Keys.Where(p_Id => !s_Base.ContainsKey(p_Id)))
            s_Added[s_Id] = $"+{s_Added.Count + 1}";
        string RefOf(string p_Id) => s_Added.TryGetValue(p_Id, out var s_Ref) ? s_Ref : s_Keys[p_Id];

        var s_Fields = new JsonObject();
        foreach (var (s_Name, s_Value) in p_Edited)
            if (!s_GraphParts.Contains(s_Name) && !JsonNode.DeepEquals(s_Value, p_Base[s_Name]))
                s_Fields[s_Name] = s_Value?.DeepClone();
        foreach (var (s_Name, _) in p_Base)
            if (!s_GraphParts.Contains(s_Name) && !p_Edited.ContainsKey(s_Name))
                s_Fields[s_Name] = null;

        var s_Removed = new JsonArray();
        foreach (var s_Id in s_Base.Keys.Where(p_Id => !s_Edited.ContainsKey(p_Id)))
            s_Removed.Add(s_Keys[s_Id]);

        var s_Changed = new JsonObject();
        foreach (var s_Id in s_Base.Keys.Where(s_Edited.ContainsKey))
            if (Canonical(s_Base[s_Id]) != Canonical(s_Edited[s_Id]))
                s_Changed[s_Keys[s_Id]] = Body(s_Edited[s_Id]);

        var s_New = new JsonObject();
        foreach (var (s_Id, s_Ref) in s_Added)
            s_New[s_Ref] = Body(s_Edited[s_Id]);

        var s_BaseWires = (p_Base["Connections"]?.AsArray() ?? new JsonArray()).Select(p_W => WireOf(p_W!)).ToHashSet();
        var s_EditedWires = (p_Edited["Connections"]?.AsArray() ?? new JsonArray()).Select(p_W => WireOf(p_W!)).ToList();
        var s_Unwired = new JsonArray();
        foreach (var s_Wire in s_BaseWires.Where(p_W => !s_EditedWires.Contains(p_W) && s_Edited.ContainsKey(p_W.From) && s_Edited.ContainsKey(p_W.To)))
            s_Unwired.Add(new JsonArray(s_Keys[s_Wire.From], s_Wire.FromPort, s_Keys[s_Wire.To], s_Wire.ToPort));
        var s_Wired = new JsonArray();
        foreach (var s_Wire in s_EditedWires.Where(p_W => !s_BaseWires.Contains(p_W)))
            s_Wired.Add(new JsonArray(RefOf(s_Wire.From), s_Wire.FromPort, RefOf(s_Wire.To), s_Wire.ToPort));

        p_Report = $"{s_Kept} node(s) of the translation kept ({s_Changed.Count} changed), {s_Removed.Count} removed, {s_New.Count} added; " +
                   $"{s_Unwired.Count} wire(s) taken off, {s_Wired.Count} laid; {s_Fields.Count} field(s) set";
        return new JsonObject
        {
            ["Format"] = Format,
            ["Base"] = p_Base["TargetShader"]?.DeepClone(),
            ["BaseHash"] = p_Base["TranslatedHlslHash"]?.DeepClone(),
            ["Fields"] = s_Fields,
            ["Removed"] = s_Removed,
            ["Changed"] = s_Changed,
            ["Added"] = s_New,
            ["Unwired"] = s_Unwired,
            ["Wired"] = s_Wired,
        };
    }

    /// <summary>
    /// The graph <paramref name="p_Patch"/> makes of <paramref name="p_Translation"/> (left untouched). Null, with the reason, when the
    /// translation is not the one the patch was made on — a node or a wire the patch names is not in it.
    /// </summary>
    public static JsonObject? Apply(JsonObject p_Translation, JsonObject p_Patch, out string? p_Error)
    {
        var s_Graph = p_Translation.DeepClone().AsObject();
        var s_Nodes = NodesById(s_Graph);
        var s_ByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (s_Id, s_Node) in s_Nodes)
            if (!s_ByKey.TryAdd(KeyOf(s_Node), s_Id))
            {
                p_Error = $"two nodes of the translation sit at {KeyOf(s_Node)}";
                return null;
            }

        var s_RootId = (string?) s_Graph["Root"]?["Id"];
        var s_Added = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (s_Ref, _) in p_Patch["Added"]?.AsObject() ?? new JsonObject())
            s_Added[s_Ref] = Guid.NewGuid().ToString("N");

        // every name the patch uses, found BEFORE anything moves (a changed node may change its place)
        var s_Missing = new List<string>();
        string? Find(string p_Ref)
        {
            if (s_Added.TryGetValue(p_Ref, out var s_New) || s_ByKey.TryGetValue(p_Ref, out s_New))
                return s_New;
            s_Missing.Add(p_Ref);
            return null;
        }

        var s_Removed = (p_Patch["Removed"]?.AsArray() ?? new JsonArray()).Select(p_R => Find((string) p_R!)).OfType<string>().ToHashSet();
        var s_Changed = (p_Patch["Changed"]?.AsObject() ?? new JsonObject()).Select(p_C => (Id: Find(p_C.Key), Body: p_C.Value!.AsObject())).ToList();
        (string? From, string Port, string? To, string ToPort) WireIn(JsonNode? p_Wire) =>
            (Find((string) p_Wire![0]!), (string) p_Wire[1]!, Find((string) p_Wire[2]!), (string) p_Wire[3]!);
        var s_Unwired = (p_Patch["Unwired"]?.AsArray() ?? new JsonArray()).Select(WireIn).ToList();
        var s_Wired = (p_Patch["Wired"]?.AsArray() ?? new JsonArray()).Select(WireIn).ToList();
        if (s_Missing.Count > 0)
        {
            p_Error = $"the translation has no node at {string.Join(", ", s_Missing.Distinct().Take(6))}" +
                      $"{(s_Missing.Distinct().Count() > 6 ? ", …" : "")} — it is not the one the preset was made on";
            return null;
        }

        if (s_RootId != null && s_Removed.Contains(s_RootId))
        {
            p_Error = "the patch takes the root away";
            return null;
        }

        var s_NodeArray = s_Graph["Nodes"]!.AsArray();
        var s_Wires = s_Graph["Connections"]!.AsArray();
        foreach (var s_Node in s_NodeArray.Where(p_N => s_Removed.Contains((string) p_N!["Id"]!)).ToList())
            s_NodeArray.Remove(s_Node);
        foreach (var s_Wire in s_Wires.Where(p_W => s_Removed.Contains(WireOf(p_W!).From) || s_Removed.Contains(WireOf(p_W!).To)).ToList())
            s_Wires.Remove(s_Wire);

        foreach (var (s_Id, s_Body) in s_Changed)
        {
            var s_Node = s_Nodes[s_Id!];
            foreach (var s_Name in s_Node.Select(p_P => p_P.Key).Where(p_K => p_K != "Id").ToList())
                s_Node.Remove(s_Name);
            foreach (var (s_Name, s_Value) in s_Body)
                s_Node[s_Name] = s_Value?.DeepClone();
        }

        foreach (var (s_Ref, s_Body) in p_Patch["Added"]?.AsObject() ?? new JsonObject())
        {
            var s_Node = new JsonObject { ["Id"] = s_Added[s_Ref] };
            foreach (var (s_Name, s_Value) in s_Body!.AsObject())
                s_Node[s_Name] = s_Value?.DeepClone();
            s_NodeArray.Add(s_Node);
        }

        foreach (var s_Off in s_Unwired)
        {
            var s_Wire = s_Wires.FirstOrDefault(p_W => WireOf(p_W!) == (s_Off.From!, s_Off.Port, s_Off.To!, s_Off.ToPort));
            if (s_Wire == null)
            {
                p_Error = $"the translation has no wire {s_Off.Port} -> {s_Off.ToPort} where the preset takes one off";
                return null;
            }

            s_Wires.Remove(s_Wire);
        }

        foreach (var s_On in s_Wired)
            s_Wires.Add(new JsonObject { ["FromNode"] = s_On.From, ["FromPort"] = s_On.Port, ["ToNode"] = s_On.To, ["ToPort"] = s_On.ToPort });

        foreach (var (s_Name, s_Value) in p_Patch["Fields"]?.AsObject() ?? new JsonObject())
        {
            if (s_Value == null)
                s_Graph.Remove(s_Name);
            else
                s_Graph[s_Name] = s_Value.DeepClone();
        }

        p_Error = null;
        return s_Graph;
    }

    /// <summary>The translation of a shader in the user's cache: the very file the studio opens for that shader, and what a patch applies over.</summary>
    public static string TranslationPathOf(string p_Shader) =>
        System.IO.Path.Combine(Settings.CacheFolder, "camographs", Cache.GameCache.SanitizeName(p_Shader.ToLowerInvariant()) + ".json");

    /// <summary>A graph file as the graph model writes it, so a translation and an edit of it are compared in the same form.</summary>
    public static JsonObject Normalised(string p_GraphText) =>
        JsonNode.Parse(RimeShaderEditor.Graph.ShaderGraph.FromJson(p_GraphText).ToJson())!.AsObject();

    /// <summary>
    /// The graph a patch file makes of the user's own translation of its shader; null with the reason when that translation is not in the
    /// cache yet or is not one the patch fits.
    /// </summary>
    public static RimeShaderEditor.Graph.ShaderGraph? GraphOf(string p_PatchPath, out string? p_Why)
    {
        var s_Patch = JsonNode.Parse(System.IO.File.ReadAllText(p_PatchPath))!.AsObject();
        var s_Base = (string?) s_Patch["Base"] ?? "";
        var s_Translation = TranslationPathOf(s_Base);
        if (!System.IO.File.Exists(s_Translation))
        {
            p_Why = $"'{s_Base}' has no translation in the cache yet";
            return null;
        }

        var s_Graph = Apply(Normalised(System.IO.File.ReadAllText(s_Translation)), s_Patch, out p_Why);
        return s_Graph == null ? null : RimeShaderEditor.Graph.ShaderGraph.FromJson(ToText(s_Graph));
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>A patch or a graph as the indented text it is saved as.</summary>
    public static string ToText(JsonObject p_Json) => p_Json.ToJsonString(s_Indented);

    /// <summary>A node as text without its id, its parameters in a fixed order: two nodes that say the same thing read the same.</summary>
    private static string Canonical(JsonObject p_Node)
    {
        var s_Params = p_Node["Params"] is JsonObject s_P
            ? string.Join(";", s_P.OrderBy(p_K => p_K.Key, StringComparer.Ordinal).Select(p_K => $"{p_K.Key}={p_K.Value?.ToJsonString()}"))
            : "";
        var s_Rest = string.Join(";", p_Node.Where(p_K => p_K.Key is not ("Id" or "Params")).OrderBy(p_K => p_K.Key, StringComparer.Ordinal)
            .Select(p_K => $"{p_K.Key}={p_K.Value?.ToJsonString()}"));
        return $"{s_Rest}|{s_Params}";
    }

    /// <summary>
    /// Whether two graphs are the same document but for their node ids: the same nodes (kind, place, parameters), the same wires between
    /// them, the same fields (an absent field and a null one alike). Null when they are; the first difference otherwise.
    /// </summary>
    public static string? Difference(JsonObject p_A, JsonObject p_B)
    {
        var s_A = NodesById(p_A);
        var s_B = NodesById(p_B);
        var s_NodesA = s_A.Values.Select(Canonical).OrderBy(p_C => p_C, StringComparer.Ordinal).ToList();
        var s_NodesB = s_B.Values.Select(Canonical).OrderBy(p_C => p_C, StringComparer.Ordinal).ToList();
        if (!s_NodesA.SequenceEqual(s_NodesB))
            return $"nodes: {s_NodesA.Except(s_NodesB).FirstOrDefault() ?? "(a duplicate)"} / {s_NodesB.Except(s_NodesA).FirstOrDefault() ?? "(a duplicate)"}";

        static List<string> Wires(JsonObject p_Graph, Dictionary<string, JsonObject> p_Nodes) =>
            (p_Graph["Connections"]?.AsArray() ?? new JsonArray()).Select(p_W => WireOf(p_W!))
            .Select(p_W => $"{(p_Nodes.TryGetValue(p_W.From, out var s_F) ? Canonical(s_F) : "?" + p_W.From)}.{p_W.FromPort} -> " +
                           $"{(p_Nodes.TryGetValue(p_W.To, out var s_T) ? Canonical(s_T) : "?" + p_W.To)}.{p_W.ToPort}")
            .OrderBy(p_W => p_W, StringComparer.Ordinal).ToList();
        var s_WiresA = Wires(p_A, s_A);
        var s_WiresB = Wires(p_B, s_B);
        if (!s_WiresA.SequenceEqual(s_WiresB))
            return $"wires: {s_WiresA.Except(s_WiresB).FirstOrDefault() ?? "(a duplicate)"} / {s_WiresB.Except(s_WiresA).FirstOrDefault() ?? "(a duplicate)"}";

        foreach (var s_Name in p_A.Select(p_K => p_K.Key).Union(p_B.Select(p_K => p_K.Key)).Where(p_K => !s_GraphParts.Contains(p_K)))
            if (!JsonNode.DeepEquals(p_A[s_Name], p_B[s_Name]))
                return $"field {s_Name}: {p_A[s_Name]?.ToJsonString() ?? "null"} / {p_B[s_Name]?.ToJsonString() ?? "null"}";

        return null;
    }
}
