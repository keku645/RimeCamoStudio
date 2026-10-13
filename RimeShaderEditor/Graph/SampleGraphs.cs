using System;
using System.Collections.Generic;
using System.Linq;

namespace RimeShaderEditor.Graph;

public static class SampleGraphs
{
    /// <summary>
    /// Builds a starting graph for a target from what is actually known about it: one Texture node per streamable
    /// texture it declares, the normal map recognised by its _N suffix and wired to Normal, the first colour map
    /// wired to Diffuse, all feeding a StandardRoot.
    ///
    /// This is NOT the shader's own graph and never can be - the node graph is stripped at bake time. It is a
    /// scaffold derived from the target's real texture list, which exists so that retargeting stops leaving a
    /// graph authored for a DIFFERENT shader on the canvas: that looked like the editor had loaded something,
    /// which is worse than an empty canvas.
    /// </summary>
    public static ShaderGraph ScaffoldFor(string p_Target, IReadOnlyList<(int Register, string Name)> p_TextureSlots)
    {
        var s_Graph = new ShaderGraph
        {
            Name = Shorten(p_Target),
            TargetShader = p_Target,
            // Stamped, so no caching path can mistake it for a translation (see ShaderGraph.IsScaffold).
            IsScaffold = true,
        };

        var s_Root = new GraphNode { Kind = "StandardRoot", X = 320, Y = -40 };
        s_Graph.Nodes.Add(s_Root);

        var s_Y = -220;
        var s_ColourDone = false;

        for (var i = 0; i < p_TextureSlots.Count; i++)
        {
            var s_Name = p_TextureSlots[i].Name;

            // The REAL register from the shader's binding table, never i+1: a lightmapped shader has the engine
            // in t1..t4 and its material from t5, and the parachute's registers are shuffled outright.
            var s_Register = p_TextureSlots[i].Register.ToString();
            var s_IsNormal = s_Name.EndsWith("_N", StringComparison.OrdinalIgnoreCase) ||
                             s_Name.Contains("_N.", StringComparison.OrdinalIgnoreCase) ||
                             s_Name.Contains("Normal", StringComparison.OrdinalIgnoreCase);

            var s_Node = new GraphNode
            {
                Kind = s_IsNormal ? "NormalMap" : "Texture",
                X = -260,
                Y = s_Y,
                Params = { ["Register"] = s_Register },
            };

            s_Graph.Nodes.Add(s_Node);
            s_Y += 200;

            if (s_IsNormal)
            {
                s_Graph.Connect(s_Node.Id, "Out", s_Root.Id, "Normal");
                continue;
            }

            // Only the first colour map is wired up: which of the others is dirt, detail or camo is a decision
            // for the author, and guessing it would be inventing the shader's structure.
            if (s_ColourDone)
                continue;

            var s_Mix = new GraphNode { Kind = "MixOut", X = -20, Y = s_Node.Y };
            s_Graph.Nodes.Add(s_Mix);
            s_Graph.Connect(s_Node.Id, "Out", s_Mix.Id, "Input");
            s_Graph.Connect(s_Mix.Id, "RGB", s_Root.Id, "Diffuse");
            s_ColourDone = true;
        }

        return s_Graph;
    }

    private static string Shorten(string p_Target)
    {
        var s_Last = p_Target.Split('/').LastOrDefault() ?? p_Target;
        return s_Last.Length > 0 ? s_Last : "Untitled";
    }

    private static GraphNode Node(string p_Kind, double p_X, double p_Y, params (string Name, string Value)[] p_Params)
    {
        var s_Node = new GraphNode { Kind = p_Kind, X = p_X, Y = p_Y };
        foreach (var (s_Name, s_Value) in p_Params)
            s_Node.Params[s_Name] = s_Value;

        return s_Node;
    }

    /// <summary>
    /// Rebuilds the vanilla Props/StreetProps/OilDrumBarrel_01 GBuffer shader as a graph. It exists as a
    /// correctness harness: emitting this should produce a shader that behaves like the stock one, so any
    /// visible difference in game is a bug in the emitter rather than in the author's graph.
    /// </summary>
    public static ShaderGraph VanillaEquivalent()
    {
        var s_Graph = new ShaderGraph
        {
            Name = "OilDrum vanilla equivalent",
            TargetShader = "Props/StreetProps/OilDrumBarrel_01/SS_OilDrumBarrel_01",
        };

        var s_Colormap = Node("Texture", -520, -160, ("Register", "1"));
        var s_Split = Node("MixOut", -300, -160);
        var s_NormalMap = Node("NormalMap", -300, 120, ("Register", "2"));

        var s_SmoothGain = Node("Scalar", -300, 40, ("Value", "4"));
        var s_SpecGain = Node("Scalar", -300, -20, ("Value", "2"));
        var s_Smoothness = Node("Multiply", -80, 40);
        var s_Specular = Node("Multiply", -80, -20);

        var s_Root = Node("StandardRoot", 200, -60);

        s_Graph.Nodes.AddRange(new[]
        {
            s_Colormap, s_Split, s_NormalMap, s_SmoothGain, s_SpecGain, s_Smoothness, s_Specular, s_Root,
        });

        s_Graph.Connect(s_Colormap.Id, "Out", s_Split.Id, "Input");
        s_Graph.Connect(s_Split.Id, "RGB", s_Root.Id, "Diffuse");

        s_Graph.Connect(s_Split.Id, "B", s_Smoothness.Id, "Input1");
        s_Graph.Connect(s_SmoothGain.Id, "Out", s_Smoothness.Id, "Input2");
        s_Graph.Connect(s_Smoothness.Id, "Out", s_Root.Id, "Smoothness");

        s_Graph.Connect(s_Split.Id, "B", s_Specular.Id, "Input1");
        s_Graph.Connect(s_SpecGain.Id, "Out", s_Specular.Id, "Input2");
        s_Graph.Connect(s_Specular.Id, "Out", s_Root.Id, "Specular");

        s_Graph.Connect(s_NormalMap.Id, "Out", s_Root.Id, "Normal");

        return s_Graph;
    }
}
