using System;
using System.Collections.Generic;
using System.Linq;

namespace RimeShaderEditor.Graph;

/// <summary>
/// Expands instance nodes into the full node network of the fragment they reference, as a PURE graph→graph
/// transform run at emit time. The canvas keeps showing the single node; the emitter only ever sees a flat
/// graph — which is what lets preview, translation fingerprinting, baking and per-variant compilation all
/// inherit instances without any of them learning what an instance is.
///
/// Boundary semantics (mirroring the fragments' authoring nodes):
///   - only the dependency closure of the fragment's Instance Output nodes expands — a preview root inside
///     the fragment, and anything only feeding it, is deliberately left behind;
///   - a host wire into a pin BYPASSES the matching Instance Input (its consumers read the host's source);
///     an unwired pin keeps the Instance Input, which emits the pin's value (typed override or default);
///   - expansion is depth-first, so fragments may instance other fragments; a depth cap turns a reference
///     cycle into a loud error instead of a hang.
/// </summary>
public static class InstanceExpander
{
    /// <summary>Resolves a library name to its fragment. Set by the instance library; left null it is
    /// lazily initialised from the on-disk library the first time a graph actually needs it.</summary>
    public static Func<string, ShaderGraph?>? Resolver { get; set; }

    private const int c_MaxDepth = 8;

    public static ShaderGraph Expand(ShaderGraph p_Graph, Action<string>? p_Log = null) =>
        ExpandAt(p_Graph, p_Log, 0);

    private static ShaderGraph ExpandAt(ShaderGraph p_Graph, Action<string>? p_Log, int p_Depth)
    {
        // The fast path returns the SAME instance: a graph without instance nodes must emit (and
        // fingerprint) exactly as it always did.
        if (!p_Graph.Nodes.Any(p_N => p_N.Kind.StartsWith(Palette.InstanceKindPrefix, StringComparison.Ordinal)))
            return p_Graph;

        if (p_Depth > c_MaxDepth)
            throw new InvalidOperationException(
                $"instance graphs nest deeper than {c_MaxDepth} levels — almost certainly a reference cycle");

        if (Resolver == null)
            InstanceLibrary.EnsureLoaded();

        var s_Expanded = ShaderGraph.FromJson(p_Graph.ToJson());
        foreach (var s_Instance in s_Expanded.Nodes
                     .Where(p_N => p_N.Kind.StartsWith(Palette.InstanceKindPrefix, StringComparison.Ordinal))
                     .ToList())
            ExpandOne(s_Expanded, s_Instance, p_Log, p_Depth);

        return s_Expanded;
    }

    private static void ExpandOne(ShaderGraph p_Host, GraphNode p_Instance, Action<string>? p_Log, int p_Depth)
    {
        var s_Name = p_Instance.Kind[Palette.InstanceKindPrefix.Length..];
        var s_Fragment = Resolver?.Invoke(s_Name)
                         ?? throw new InvalidOperationException(
                             $"instance graph '{s_Name}' is not in the library " +
                             "(Documents\\RimeShaderEditor\\instances) — the node cannot expand");

        s_Fragment = ExpandAt(s_Fragment, p_Log, p_Depth + 1);

        var s_Outputs = s_Fragment.Nodes.Where(p_N => p_N.Kind == "InstanceOutput").ToList();
        if (s_Outputs.Count == 0)
            throw new InvalidOperationException(
                $"instance graph '{s_Name}' has no Instance Output node — it exposes nothing");

        // Dependency closure of the outputs: the only part of the fragment that expands.
        var s_Include = new HashSet<string>();
        var s_Stack = new Stack<string>();
        foreach (var s_Output in s_Outputs)
            if (s_Fragment.ConnectionInto(s_Output.Id, "Input") is { } s_Feed)
                s_Stack.Push(s_Feed.FromNode);

        while (s_Stack.Count > 0)
        {
            var s_Id = s_Stack.Pop();
            if (!s_Include.Add(s_Id))
                continue;

            foreach (var s_Connection in s_Fragment.Connections)
                if (s_Connection.ToNode == s_Id)
                    s_Stack.Push(s_Connection.FromNode);
        }

        string Map(string p_Id) => $"{p_Instance.Id}_{p_Id}";

        foreach (var s_Node in s_Fragment.Nodes.Where(p_N => s_Include.Contains(p_N.Id)))
            p_Host.Nodes.Add(new GraphNode
            {
                Id = Map(s_Node.Id),
                Kind = s_Node.Kind,
                X = s_Node.X,
                Y = s_Node.Y,
                Comment = s_Node.Comment,
                Params = new Dictionary<string, string>(s_Node.Params),
            });

        foreach (var s_Connection in s_Fragment.Connections.Where(p_C =>
                     s_Include.Contains(p_C.FromNode) && s_Include.Contains(p_C.ToNode)))
            p_Host.Connections.Add(new GraphConnection
            {
                FromNode = Map(s_Connection.FromNode),
                FromPort = s_Connection.FromPort,
                ToNode = Map(s_Connection.ToNode),
                ToPort = s_Connection.ToPort,
            });

        // OUTPUT boundary FIRST: host consumers of each pin read what feeds the matching Instance Output.
        // The order against the input boundary is load-bearing — a PASSTHROUGH pin (Instance Input wired
        // straight into an Instance Output) leaves host consumers pointing at the mapped Instance Input,
        // and only because the input bypass below runs LATER does it catch those wires too and hand them
        // the host's own source. Reversed, the bypass deleted the node first and the consumers read an
        // output that no longer existed (measured on the map-passthrough preset).
        foreach (var s_Output in s_Outputs)
        {
            var s_Pin = s_Output.GetParam("Name");
            var s_Feed = s_Fragment.ConnectionInto(s_Output.Id, "Input");

            foreach (var s_Consumer in p_Host.Connections
                         .Where(p_C => p_C.FromNode == p_Instance.Id && p_C.FromPort == s_Pin).ToList())
            {
                if (s_Feed == null)
                {
                    p_Host.Connections.Remove(s_Consumer);
                    p_Log?.Invoke($"instance '{s_Name}': output '{s_Pin}' is unwired inside the fragment — " +
                                  "its consumers fall back to their pin defaults");
                }
                else
                {
                    s_Consumer.FromNode = Map(s_Feed.FromNode);
                    s_Consumer.FromPort = s_Feed.FromPort;
                }
            }
        }

        // INPUT boundary: a host wire bypasses the Instance Input; a typed override on the pin becomes the
        // kept Instance Input's value; a plain unwired pin keeps the fragment's own default.
        foreach (var s_Input in s_Fragment.Nodes.Where(p_N =>
                     p_N.Kind == "InstanceInput" && s_Include.Contains(p_N.Id)))
        {
            var s_Pin = s_Input.GetParam("Name");
            var s_HostWire = p_Host.ConnectionInto(p_Instance.Id, s_Pin);

            if (s_HostWire != null)
            {
                foreach (var s_Consumer in p_Host.Connections
                             .Where(p_C => p_C.FromNode == Map(s_Input.Id) && p_C.FromPort == "Out").ToList())
                {
                    s_Consumer.FromNode = s_HostWire.FromNode;
                    s_Consumer.FromPort = s_HostWire.FromPort;
                }

                p_Host.RemoveNode(Map(s_Input.Id));
            }
            else if (p_Instance.GetInputOverride(s_Pin) is { } s_Override)
            {
                p_Host.FindNode(Map(s_Input.Id))!.Params["Value"] = s_Override;
            }
        }

        p_Host.RemoveNode(p_Instance.Id);
    }
}
