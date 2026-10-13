using System;
using System.Linq;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor.Emit;

/// <summary>
/// Turns a plain forward (transparent) graph into its PROBE-LIT (cbuffer) twin, applying the delta
/// measured instruction-by-instruction between the two vanilla solutions:
///   ambient' = hemisphere-lerp × ShO + max(dp4((N,1), lightProbeShR/G/B), 0)
///   sky-reflection' = cube sample × ShO   (inserted FIRST, the vanilla multiply order)
///   everything else identical.
/// The four SH rows live in $Globals at c7..c10. The transform anchors on facts of the graph — the
/// hemisphere-direction dot names the normal, the ambient spelling is the one the translator emits — and
/// REFUSES (returns false) when an anchor is missing, so an authored graph it does not understand keeps
/// vanilla bytes instead of guessing.
/// </summary>
internal static class ForwardProbe
{
    public static bool TryProbeize(ShaderGraph p_Graph, out string p_Why)
    {
        // ── anchor 1: the hemisphere ambient dot names the decoded world NORMAL ─────────────────────────
        var s_Hemi = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "ExternalConstant" &&
            p_N.GetParam("Name") == "outdoorLightHemisphereDir");
        if (s_Hemi == null)
        {
            p_Why = "no outdoorLightHemisphereDir constant";
            return false;
        }

        (string Node, string Port)? s_Normal = null;
        foreach (var s_Dot in p_Graph.Nodes.Where(p_N => p_N.Kind == "Dot"))
        {
            var s_A = Source(p_Graph, s_Dot.Id, "A");
            var s_B = Source(p_Graph, s_Dot.Id, "B");
            if (s_A == null || s_B == null)
                continue;

            if (ReachesNode(p_Graph, s_A.Value.Node, s_Hemi.Id, 0))
                s_Normal = s_B;
            else if (ReachesNode(p_Graph, s_B.Value.Node, s_Hemi.Id, 0))
                s_Normal = s_A;
        }

        if (s_Normal == null)
        {
            p_Why = "no dot against the hemisphere direction (normal not identifiable)";
            return false;
        }

        // ── anchor 2: the hemisphere ambient node (lerp of top/bottom, or its raw mad spelling) ─────────
        var s_Top = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "ExternalConstant" &&
            p_N.GetParam("Name") == "outdoorLightTopColor");
        var s_Bottom = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "ExternalConstant" &&
            p_N.GetParam("Name") == "outdoorLightBottomColor");
        if (s_Top == null || s_Bottom == null)
        {
            p_Why = "no outdoor-light top/bottom colours";
            return false;
        }

        var s_Ambient = FindAmbient(p_Graph, s_Top.Id, s_Bottom.Id);
        if (s_Ambient == null)
        {
            p_Why = "hemisphere-ambient expression not recognized";
            return false;
        }

        // ── anchor 3: the sky-reflection cube sample ────────────────────────────────────────────────────
        var s_Cube = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "TextureCube");
        if (s_Cube == null)
        {
            p_Why = "no cube sample (sky reflection)";
            return false;
        }

        // ── the four SH rows, c7..c10 of $Globals, and their dp4 against (N, 1) ─────────────────────────
        var s_ShLanes = new string[4];
        var s_Names = new[] { "lightProbeShR", "lightProbeShG", "lightProbeShB", "lightProbeShO" };
        for (var i = 0; i < 4; i++)
        {
            var s_Row = NewNode(p_Graph, "ExternalConstant");
            s_Row.Params["Name"] = s_Names[i];
            s_Row.Params["Buffer"] = "$Globals";
            s_Row.Params["Register"] = "0";
            s_Row.Params["Element"] = (7 + i).ToString();

            var s_Mix = NewNode(p_Graph, "MixOut");
            p_Graph.Connect(s_Row.Id, "Out", s_Mix.Id, "Input");

            // dp4((N,1), row) spelled with the 3-wide Dot: dot3(N, row.xyz) + row.w.
            var s_Dot = NewNode(p_Graph, "Dot");
            p_Graph.Connect(s_Normal.Value.Node, s_Normal.Value.Port, s_Dot.Id, "A");
            p_Graph.Connect(s_Mix.Id, "RGB", s_Dot.Id, "B");

            var s_Sum = NewNode(p_Graph, "Add");
            p_Graph.Connect(s_Dot.Id, "Out", s_Sum.Id, "Input1");
            p_Graph.Connect(s_Mix.Id, "A", s_Sum.Id, "Input2");

            if (i < 3)
            {
                // The colour rows clamp at zero; the occlusion row does not (measured).
                var s_Zero = NewNode(p_Graph, "Scalar");
                s_Zero.Params["Value"] = "0";
                var s_Max = NewNode(p_Graph, "Max");
                p_Graph.Connect(s_Sum.Id, "Out", s_Max.Id, "Input1");
                p_Graph.Connect(s_Zero.Id, "Out", s_Max.Id, "Input2");
                s_ShLanes[i] = s_Max.Id;
            }
            else
            {
                s_ShLanes[i] = s_Sum.Id;
            }
        }

        var s_ShRgb = NewNode(p_Graph, "Append");
        p_Graph.Connect(s_ShLanes[0], "Out", s_ShRgb.Id, "X");
        p_Graph.Connect(s_ShLanes[1], "Out", s_ShRgb.Id, "Y");
        p_Graph.Connect(s_ShLanes[2], "Out", s_ShRgb.Id, "Z");

        // ── ambient' = ambient × ShO + SHrgb: every consumer of the old ambient reads the new one ──────
        var s_AmbientTimesO = NewNode(p_Graph, "Multiply");
        var s_AmbientNew = NewNode(p_Graph, "Add");

        foreach (var s_Connection in p_Graph.Connections
                     .Where(p_C => p_C.FromNode == s_Ambient && p_C.ToNode != s_AmbientTimesO.Id).ToList())
        {
            s_Connection.FromNode = s_AmbientNew.Id;
            s_Connection.FromPort = "Out";
        }

        p_Graph.Connect(s_Ambient, "Out", s_AmbientTimesO.Id, "Input1");
        p_Graph.Connect(s_ShLanes[3], "Out", s_AmbientTimesO.Id, "Input2");
        p_Graph.Connect(s_AmbientTimesO.Id, "Out", s_AmbientNew.Id, "Input1");
        p_Graph.Connect(s_ShRgb.Id, "Out", s_AmbientNew.Id, "Input2");

        // ── reflection × ShO, inserted directly at the cube sample (the vanilla multiply order) ─────────
        var s_CubeTimesO = NewNode(p_Graph, "Multiply");
        foreach (var s_Connection in p_Graph.Connections
                     .Where(p_C => p_C.FromNode == s_Cube.Id && p_C.ToNode != s_CubeTimesO.Id).ToList())
        {
            s_Connection.FromNode = s_CubeTimesO.Id;
            s_Connection.FromPort = "Out";
        }

        p_Graph.Connect(s_Cube.Id, "Out", s_CubeTimesO.Id, "Input1");
        p_Graph.Connect(s_ShLanes[3], "Out", s_CubeTimesO.Id, "Input2");

        p_Why = "";
        return true;
    }

    /// <summary>The hemisphere ambient node: a Lerp between the two colours, or the raw spelling — an Add
    /// of one colour and a Multiply whose chain holds the Subtract of both.</summary>
    private static string? FindAmbient(ShaderGraph p_Graph, string p_TopId, string p_BottomId)
    {
        foreach (var s_Node in p_Graph.Nodes)
        {
            if (s_Node.Kind == "Lerp")
            {
                var s_Ends = new[] { Source(p_Graph, s_Node.Id, "Input1")?.Node,
                                     Source(p_Graph, s_Node.Id, "Input2")?.Node };
                if (s_Ends.Contains(p_TopId) && s_Ends.Contains(p_BottomId))
                    return s_Node.Id;
            }
            else if (s_Node.Kind == "Add")
            {
                var s_A = Source(p_Graph, s_Node.Id, "Input1");
                var s_B = Source(p_Graph, s_Node.Id, "Input2");
                if (s_A == null || s_B == null)
                    continue;

                foreach (var (s_Anchor, s_Rest) in new[] { (s_A.Value, s_B.Value), (s_B.Value, s_A.Value) })
                {
                    if (s_Anchor.Node != p_TopId && s_Anchor.Node != p_BottomId)
                        continue;

                    if (p_Graph.FindNode(s_Rest.Node)?.Kind != "Multiply")
                        continue;

                    var s_Factors = new[] { Source(p_Graph, s_Rest.Node, "Input1"),
                                            Source(p_Graph, s_Rest.Node, "Input2") };
                    if (s_Factors.Any(p_F => p_F != null &&
                            p_Graph.FindNode(p_F.Value.Node)?.Kind == "Subtract" &&
                            new[] { Source(p_Graph, p_F.Value.Node, "Input1")?.Node,
                                    Source(p_Graph, p_F.Value.Node, "Input2")?.Node }
                                .Intersect(new[] { p_TopId, p_BottomId }).Count() == 2))
                        return s_Node.Id;
                }
            }
        }

        return null;
    }

    private static (string Node, string Port)? Source(ShaderGraph p_Graph, string p_Node, string p_Port)
    {
        var s_Connection = p_Graph.ConnectionInto(p_Node, p_Port);
        return s_Connection == null ? null : (s_Connection.FromNode, s_Connection.FromPort);
    }

    /// <summary>True when p_From's INPUT chain reaches p_Target within a few hops (MixOut/Swizzle wrappers).</summary>
    private static bool ReachesNode(ShaderGraph p_Graph, string p_From, string p_Target, int p_Depth)
    {
        if (p_From == p_Target)
            return true;
        if (p_Depth > 2)
            return false;

        return p_Graph.Connections.Where(p_C => p_C.ToNode == p_From)
            .Any(p_C => ReachesNode(p_Graph, p_C.FromNode, p_Target, p_Depth + 1));
    }

    private static GraphNode NewNode(ShaderGraph p_Graph, string p_Kind)
    {
        var s_Node = new GraphNode { Kind = p_Kind };
        p_Graph.Nodes.Add(s_Node);
        return s_Node;
    }
}
