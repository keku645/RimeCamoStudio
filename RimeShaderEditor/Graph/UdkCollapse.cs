using System.Collections.Generic;
using System.Linq;

namespace RimeShaderEditor.Graph;

/// <summary>
/// Folds a texture and the mask node behind it into the single node that vocabulary uses.
///
/// Both catalogs are right about their own design: this engine keeps one node per job, so its mask takes
/// ANY input and a texture has one output; that other editor builds the per-channel outputs into the
/// sample itself and keeps a separate mask for the general case. Translating a shader out of the game
/// therefore produces pairs that an author working in the UDK vocabulary would have drawn as one node.
///
/// ⛔ THIS REWRITES THE GRAPH, so it only folds pairs where the two forms are provably the same shader:
/// a tiling factor or an explicit LOD lives on OUR texture node and has nowhere to go on the other one,
/// so those pairs are LEFT ALONE rather than quietly dropped. What it does fold is covered by the twin
/// test, which proves the two emit identical HLSL.
/// </summary>
public static class UdkCollapse
{
    /// <summary>How many pairs were folded, and how many were left alone because they carry more.</summary>
    public sealed class Report
    {
        public int Folded;
        public int Skipped;
        public List<string> Reasons { get; } = new();
    }

    public static Report Collapse(ShaderGraph p_Graph)
    {
        var s_Report = new Report();

        foreach (var s_Texture in p_Graph.Nodes.Where(p_N => p_N.Kind == "Texture").ToList())
        {
            // The mask has to be the texture's ONLY consumer, and nothing else may feed it: folding a
            // texture that also drives something else would silently drop that wire.
            var s_Outgoing = p_Graph.Connections
                .Where(p_C => p_C.FromNode == s_Texture.Id)
                .ToList();

            if (s_Outgoing.Count != 1)
                continue;

            var s_Mask = p_Graph.FindNode(s_Outgoing[0].ToNode);
            if (s_Mask == null || s_Mask.Kind != "MixOut" || s_Outgoing[0].ToPort != "Input")
                continue;

            if (p_Graph.Connections.Count(p_C => p_C.ToNode == s_Mask.Id) != 1)
                continue;

            // Tiling and LOD have no home on the folded node; keeping the pair is the honest outcome.
            var s_Tiling = s_Texture.GetParam("Tiling").Trim();
            if (s_Tiling.Length > 0 && s_Tiling != "1" && s_Tiling != "1,1")
            {
                s_Report.Skipped++;
                s_Report.Reasons.Add($"'{s_Texture.Id[..6]}' keeps its own node: tiling {s_Tiling} has no " +
                                     "property on the folded node");
                continue;
            }

            if (p_Graph.ConnectionInto(s_Texture.Id, "Lod") != null ||
                s_Texture.GetInputOverride("Lod") != null)
            {
                s_Report.Skipped++;
                s_Report.Reasons.Add($"'{s_Texture.Id[..6]}' keeps its own node: an explicit LOD has no " +
                                     "property on the folded node");
                continue;
            }

            var s_Folded = new GraphNode
            {
                Kind = "UdkTextureSample",
                X = s_Texture.X,
                Y = s_Texture.Y,
                Comment = s_Texture.Comment,
            };

            foreach (var s_Param in new[] { "Register", "Unpack", "TransformNormal" })
                s_Folded.Params[s_Param] = s_Texture.GetParam(s_Param);

            p_Graph.Nodes.Add(s_Folded);

            // What fed the texture's coordinate now feeds the folded node's, and every channel the mask
            // published is republished from the same-named output.
            var s_Coord = p_Graph.ConnectionInto(s_Texture.Id, "Coord");
            if (s_Coord != null)
                p_Graph.Connect(s_Coord.FromNode, s_Coord.FromPort, s_Folded.Id, "Coordinates");
            else if (s_Texture.GetInputOverride("Coord") is { } s_Override)
                s_Folded.SetInputOverride("Coordinates", s_Override);

            foreach (var s_Wire in p_Graph.Connections.Where(p_C => p_C.FromNode == s_Mask.Id).ToList())
                p_Graph.Connect(s_Folded.Id, s_Wire.FromPort, s_Wire.ToNode, s_Wire.ToPort);

            p_Graph.RemoveNode(s_Mask.Id);
            p_Graph.RemoveNode(s_Texture.Id);
            s_Report.Folded++;
        }

        return s_Report;
    }
}
