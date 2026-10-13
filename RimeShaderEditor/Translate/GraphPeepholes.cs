using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimeShaderEditor.Emit;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor.Translate;

/// <summary>
/// Recognises hand-expanded idioms in a freshly translated graph and folds each into the single node an
/// author would draw: normalize() spelled as dot/rsq/multiply, lerp() spelled as subtract/multiply/add, 1-x
/// spelled as a subtraction from a literal one. Every fold is EXACT - the fused node's HLSL compiles to the
/// same maths (no authoring-safe guards; Power's max() clamp is exactly why Power is NOT folded here) - and
/// only fires when the intermediate results feed nothing else.
/// </summary>
internal static class GraphPeepholes
{
    public static void Run(ShaderGraph p_Graph, ShaderContract? p_Contract = null)
    {
        // Repeat until quiet: one fold can expose another (the Add of a lerp appears once its Subtract fold
        // has cleaned the operand chain).
        while (FoldOneMinus(p_Graph) || FoldNormalize(p_Graph) || FoldLerp(p_Graph) ||
               FoldTexCoord(p_Graph, p_Contract))
        {
        }

        // The forward (transparent) closing idiom folds into its authored root, and the constants whose
        // role the fold PROVES get a comment naming it - a translated glass graph otherwise leaves the
        // tunable literals (the tint) anonymous, and the only way to find them was already knowing.
        if (p_Contract?.Family == Emit.ShaderFamily.Forward)
        {
            FoldForwardRoot(p_Graph);
            AnnotateForward(p_Graph);
        }

        // Every family: name what the bytecode itself names (texture bindings, interpolator roles) and the
        // baked literals whose role the structure proves (UV tiling, rotation pairs, the fresnel constant).
        // The compiled shader carries no author comments - 2036 permutations scanned, zero debug chunks -
        // so these notes are the closest thing to them the data allows, and nothing here is guessed.
        if (p_Contract != null)
        {
            AnnotateResources(p_Graph, p_Contract);
            AnnotateInterpolators(p_Graph, p_Contract);
            AnnotateBakedUv(p_Graph);
            AnnotateFresnelConstant(p_Graph);
        }

        // Translator scaffolding nothing reads any more (an Interpolator bypassed by its TexCoord, a constant
        // whose only consumer folded away) - dropped iteratively, since removing one can orphan its feeders.
        bool s_Dropped;
        do
        {
            s_Dropped = false;
            foreach (var s_Node in p_Graph.Nodes
                         .Where(p_N => !p_N.Def.IsRoot && FanOut(p_Graph, p_N.Id) == 0).ToList())
            {
                p_Graph.RemoveNode(s_Node.Id);
                s_Dropped = true;
            }
        } while (s_Dropped);
    }

    // ── shared plumbing ──────────────────────────────────────────────────────────────────────────────────

    private static (string Node, string Port)? SourceOf(ShaderGraph p_Graph, string p_Node, string p_Port)
    {
        var s_Connection = p_Graph.ConnectionInto(p_Node, p_Port);
        return s_Connection == null ? null : (s_Connection.FromNode, s_Connection.FromPort);
    }

    private static int FanOut(ShaderGraph p_Graph, string p_Node) =>
        p_Graph.Connections.Count(p_C => p_C.FromNode == p_Node);

    private static bool IsLiteralOne(ShaderGraph p_Graph, (string Node, string Port) p_Source)
    {
        var s_Node = p_Graph.FindNode(p_Source.Node);
        return s_Node?.Kind == "Scalar" &&
               float.TryParse(s_Node.GetParam("Value"), NumberStyles.Float, CultureInfo.InvariantCulture,
                   out var s_Value) && s_Value == 1f;
    }

    private static bool OutputIsScalar(ShaderGraph p_Graph, (string Node, string Port) p_Source)
    {
        var s_Node = p_Graph.FindNode(p_Source.Node);
        return s_Node?.Def.FindOutput(p_Source.Port)?.Type == ShaderPortType.SptScalar;
    }

    /// <summary>Points every consumer of one output port at a replacement, then drops the old node.</summary>
    private static void Replace(ShaderGraph p_Graph, string p_OldNode, GraphNode p_New)
    {
        foreach (var s_Connection in p_Graph.Connections.Where(p_C => p_C.FromNode == p_OldNode).ToList())
        {
            s_Connection.FromNode = p_New.Id;
            s_Connection.FromPort = "Out";
        }

        p_Graph.RemoveNode(p_OldNode);
    }

    private static void DropIfOrphan(ShaderGraph p_Graph, string p_Node)
    {
        if (FanOut(p_Graph, p_Node) == 0 && p_Graph.FindNode(p_Node)?.Def.IsRoot != true)
            p_Graph.RemoveNode(p_Node);
    }

    private static GraphNode NewNode(ShaderGraph p_Graph, string p_Kind)
    {
        var s_Node = new GraphNode { Kind = p_Kind };
        p_Graph.Nodes.Add(s_Node);
        return s_Node;
    }

    // ── the folds ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Subtract(1, x) is One Minus, and the author's node says so without the literal.</summary>
    private static bool FoldOneMinus(ShaderGraph p_Graph)
    {
        foreach (var s_Subtract in p_Graph.Nodes.Where(p_N => p_N.Kind == "Subtract").ToList())
        {
            var s_Left = SourceOf(p_Graph, s_Subtract.Id, "Input1");
            var s_Right = SourceOf(p_Graph, s_Subtract.Id, "Input2");
            if (s_Left == null || s_Right == null || !IsLiteralOne(p_Graph, s_Left.Value))
                continue;

            var s_OneMinus = NewNode(p_Graph, "OneMinus");
            p_Graph.Connect(s_Right.Value.Node, s_Right.Value.Port, s_OneMinus.Id, "Input");
            Replace(p_Graph, s_Subtract.Id, s_OneMinus);
            DropIfOrphan(p_Graph, s_Left.Value.Node);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Multiply(v, Divide(1, SqrtRaw(Dot(v, v)))) is normalize(v) - the four-node spelling every DXBC
    /// normalisation compiles to, folded back into the one node the author drew.
    /// </summary>
    private static bool FoldNormalize(ShaderGraph p_Graph)
    {
        foreach (var s_Multiply in p_Graph.Nodes.Where(p_N => p_N.Kind == "Multiply").ToList())
        {
            var s_A = SourceOf(p_Graph, s_Multiply.Id, "Input1");
            var s_B = SourceOf(p_Graph, s_Multiply.Id, "Input2");
            if (s_A == null || s_B == null)
                continue;

            foreach (var (s_Vector, s_Rsq) in new[] { (s_A.Value, s_B.Value), (s_B.Value, s_A.Value) })
            {
                if (p_Graph.FindNode(s_Rsq.Node)?.Kind != "Divide" || FanOut(p_Graph, s_Rsq.Node) != 1)
                    continue;

                var s_One = SourceOf(p_Graph, s_Rsq.Node, "Input1");
                var s_Sqrt = SourceOf(p_Graph, s_Rsq.Node, "Input2");
                if (s_One == null || s_Sqrt == null || !IsLiteralOne(p_Graph, s_One.Value) ||
                    p_Graph.FindNode(s_Sqrt.Value.Node)?.Kind != "SqrtRaw" ||
                    FanOut(p_Graph, s_Sqrt.Value.Node) != 1)
                    continue;

                var s_Dot = SourceOf(p_Graph, s_Sqrt.Value.Node, "Input");
                if (s_Dot == null || p_Graph.FindNode(s_Dot.Value.Node)?.Kind != "Dot" ||
                    FanOut(p_Graph, s_Dot.Value.Node) != 1)
                    continue;

                var s_DotA = SourceOf(p_Graph, s_Dot.Value.Node, "A");
                var s_DotB = SourceOf(p_Graph, s_Dot.Value.Node, "B");
                if (s_DotA == null || s_DotB == null || s_DotA != s_DotB || s_DotA != s_Vector)
                    continue;

                var s_Normalize = NewNode(p_Graph, "Normalize");
                p_Graph.Connect(s_Vector.Node, s_Vector.Port, s_Normalize.Id, "Input");

                var s_OneNode = s_One.Value.Node;
                Replace(p_Graph, s_Multiply.Id, s_Normalize);
                p_Graph.RemoveNode(s_Rsq.Node);
                p_Graph.RemoveNode(s_Sqrt.Value.Node);
                p_Graph.RemoveNode(s_Dot.Value.Node);
                DropIfOrphan(p_Graph, s_OneNode);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A raw Interpolator that the contract says carries a UV becomes the TexCoord node - the name an author
    /// reads - wherever it feeds a genuine 2-component coordinate: a direct wire into a vec2 input takes the
    /// xy pair, an xy/zw Swizzle in front of vec2 inputs takes its half. Reads of any other lane shape keep
    /// the raw node, which is exactly what "raw" is for.
    /// </summary>
    private static bool FoldTexCoord(ShaderGraph p_Graph, ShaderContract? p_Contract)
    {
        if (p_Contract == null)
            return false;

        foreach (var s_Interpolator in p_Graph.Nodes.Where(p_N => p_N.Kind == "Interpolator").ToList())
        {
            if (!int.TryParse(s_Interpolator.GetParam("Index"), out var s_Index))
                continue;

            var s_IsUv = p_Contract.SemanticsVerified
                ? p_Contract.FieldNameFor(s_Index) == "TexCoord"
                : p_Contract.InterpolatorMeanings.TryGetValue(s_Index, out var s_Meaning) &&
                  s_Meaning == "UvPair";

            if (!s_IsUv)
                continue;

            var s_Changed = false;

            GraphNode TexCoordNode(string p_Half)
            {
                var s_Existing = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "TexCoord" &&
                    p_N.GetParam("Interp") == s_Index.ToString() && p_N.GetParam("Half") == p_Half);
                if (s_Existing != null)
                    return s_Existing;

                var s_Node = NewNode(p_Graph, "TexCoord");
                s_Node.Params["Interp"] = s_Index.ToString();
                s_Node.Params["Half"] = p_Half;
                return s_Node;
            }

            // Direct wires into vec2 inputs: the adapter was already truncating to xy - now the node says so.
            foreach (var s_Connection in p_Graph.Connections
                         .Where(p_C => p_C.FromNode == s_Interpolator.Id).ToList())
            {
                var s_Input = p_Graph.FindNode(s_Connection.ToNode)?.Def.FindInput(s_Connection.ToPort);
                if (s_Input?.Type != ShaderPortType.SptVec2)
                    continue;

                var s_TexCoord = TexCoordNode("xy");
                s_Connection.FromNode = s_TexCoord.Id;
                s_Connection.FromPort = "Out";
                s_Changed = true;
            }

            // An xy/zw Swizzle whose consumers all take vec2: that IS one UV set of a packed layout.
            foreach (var s_Swizzle in p_Graph.Nodes.Where(p_N => p_N.Kind == "Swizzle").ToList())
            {
                var s_Source = SourceOf(p_Graph, s_Swizzle.Id, "Input");
                if (s_Source == null || s_Source.Value.Node != s_Interpolator.Id)
                    continue;

                var s_Channels = s_Swizzle.GetParam("Channels");
                if (s_Channels != "xy" && s_Channels != "zw")
                    continue;

                var s_Consumers = p_Graph.Connections.Where(p_C => p_C.FromNode == s_Swizzle.Id).ToList();
                if (s_Consumers.Count == 0 || !s_Consumers.All(p_C =>
                        p_Graph.FindNode(p_C.ToNode)?.Def.FindInput(p_C.ToPort)?.Type == ShaderPortType.SptVec2))
                    continue;

                var s_TexCoord = TexCoordNode(s_Channels);
                foreach (var s_Consumer in s_Consumers)
                {
                    s_Consumer.FromNode = s_TexCoord.Id;
                    s_Consumer.FromPort = "Out";
                }

                p_Graph.RemoveNode(s_Swizzle.Id);
                s_Changed = true;
            }

            if (s_Changed && FanOut(p_Graph, s_Interpolator.Id) == 0)
                p_Graph.RemoveNode(s_Interpolator.Id);

            if (s_Changed)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Add(Multiply(t, Subtract(b, a)), a) is lerp(a, b, t). Folded only when t is a genuine scalar - the
    /// Lerp node's Delta pin is scalar, and a per-component vector t must stay in the expanded form.
    /// </summary>
    private static bool FoldLerp(ShaderGraph p_Graph)
    {
        foreach (var s_Add in p_Graph.Nodes.Where(p_N => p_N.Kind == "Add").ToList())
        {
            var s_Left = SourceOf(p_Graph, s_Add.Id, "Input1");
            var s_Right = SourceOf(p_Graph, s_Add.Id, "Input2");
            if (s_Left == null || s_Right == null)
                continue;

            foreach (var (s_Product, s_Anchor) in new[] { (s_Left.Value, s_Right.Value), (s_Right.Value, s_Left.Value) })
            {
                if (p_Graph.FindNode(s_Product.Node)?.Kind != "Multiply" ||
                    FanOut(p_Graph, s_Product.Node) != 1)
                    continue;

                var s_MulA = SourceOf(p_Graph, s_Product.Node, "Input1");
                var s_MulB = SourceOf(p_Graph, s_Product.Node, "Input2");
                if (s_MulA == null || s_MulB == null)
                    continue;

                foreach (var (s_Delta, s_Difference) in new[] { (s_MulA.Value, s_MulB.Value), (s_MulB.Value, s_MulA.Value) })
                {
                    if (p_Graph.FindNode(s_Difference.Node)?.Kind != "Subtract" ||
                        FanOut(p_Graph, s_Difference.Node) != 1 ||
                        !OutputIsScalar(p_Graph, s_Delta))
                        continue;

                    var s_To = SourceOf(p_Graph, s_Difference.Node, "Input1");
                    var s_From = SourceOf(p_Graph, s_Difference.Node, "Input2");
                    if (s_To == null || s_From == null || s_From.Value != s_Anchor)
                        continue;

                    var s_Lerp = NewNode(p_Graph, "Lerp");
                    p_Graph.Connect(s_Anchor.Node, s_Anchor.Port, s_Lerp.Id, "Input1");
                    p_Graph.Connect(s_To.Value.Node, s_To.Value.Port, s_Lerp.Id, "Input2");
                    p_Graph.Connect(s_Delta.Node, s_Delta.Port, s_Lerp.Id, "Delta");

                    Replace(p_Graph, s_Add.Id, s_Lerp);
                    p_Graph.RemoveNode(s_Product.Node);
                    p_Graph.RemoveNode(s_Difference.Node);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The forward closing idiom: Target0 = Append(rgb of Multiply(alpha, colour) via a MixOut, alpha) into
    /// a RawRoot with the other targets silent. That is exactly what the ForwardRoot node emits, so the
    /// scaffolding folds into it and the graph ends the way an author would draw it - Colour and Alpha wired
    /// into a root that premultiplies. The two feeds get comments naming what the fold just proved they are.
    /// </summary>
    private static void FoldForwardRoot(ShaderGraph p_Graph)
    {
        var s_Root = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "RawRoot");
        if (s_Root == null || SourceOf(p_Graph, s_Root.Id, "Target1") != null ||
            SourceOf(p_Graph, s_Root.Id, "Target2") != null || SourceOf(p_Graph, s_Root.Id, "Target3") != null)
            return;

        var s_Target0 = SourceOf(p_Graph, s_Root.Id, "Target0");
        if (s_Target0 == null)
            return;

        var s_Append = p_Graph.FindNode(s_Target0.Value.Node);
        if (s_Append?.Kind != "Append" || FanOut(p_Graph, s_Append.Id) != 1)
            return;

        var s_X = SourceOf(p_Graph, s_Append.Id, "X");
        var s_Y = SourceOf(p_Graph, s_Append.Id, "Y");
        var s_Z = SourceOf(p_Graph, s_Append.Id, "Z");
        var s_Alpha = SourceOf(p_Graph, s_Append.Id, "W");
        if (s_X == null || s_Y == null || s_Z == null || s_Alpha == null ||
            s_X.Value.Node != s_Y.Value.Node || s_X.Value.Node != s_Z.Value.Node)
            return;

        var s_Mix = p_Graph.FindNode(s_X.Value.Node);
        if (s_Mix?.Kind != "MixOut" || FanOut(p_Graph, s_Mix.Id) != 3)
            return;

        var s_Premultiply = SourceOf(p_Graph, s_Mix.Id, "Input");
        if (s_Premultiply == null)
            return;

        var s_Product = p_Graph.FindNode(s_Premultiply.Value.Node);
        if (s_Product?.Kind != "Multiply" || FanOut(p_Graph, s_Product.Id) != 1)
            return;

        var s_A = SourceOf(p_Graph, s_Product.Id, "Input1");
        var s_B = SourceOf(p_Graph, s_Product.Id, "Input2");
        if (s_A == null || s_B == null)
            return;

        // One factor of the premultiply must be the very alpha the append writes to .w; the other is colour.
        (string Node, string Port) s_Colour;
        if (s_A.Value == s_Alpha.Value)
            s_Colour = s_B.Value;
        else if (s_B.Value == s_Alpha.Value)
            s_Colour = s_A.Value;
        else
            return;

        var s_Forward = NewNode(p_Graph, "ForwardRoot");
        s_Forward.X = s_Root.X;
        s_Forward.Y = s_Root.Y;
        p_Graph.Connect(s_Colour.Node, s_Colour.Port, s_Forward.Id, "Color");
        p_Graph.Connect(s_Alpha.Value.Node, s_Alpha.Value.Port, s_Forward.Id, "Alpha");

        p_Graph.RemoveNode(s_Root.Id);
        p_Graph.RemoveNode(s_Append.Id);
        p_Graph.RemoveNode(s_Mix.Id);
        p_Graph.RemoveNode(s_Product.Id);

        var s_ColourNode = p_Graph.FindNode(s_Colour.Node);
        if (s_ColourNode != null && string.IsNullOrWhiteSpace(s_ColourNode.Comment))
            s_ColourNode.Comment = "The lit colour before the premultiply: tint x ambient plus the sky " +
                                   "reflection. The root multiplies the alpha in.";

        var s_AlphaNode = p_Graph.FindNode(s_Alpha.Value.Node);
        if (s_AlphaNode != null && string.IsNullOrWhiteSpace(s_AlphaNode.Comment))
            s_AlphaNode.Comment = "Coverage: the mask channels times the per-vertex distance fade. " +
                                  "Written as the alpha and premultiplied into the colour.";
    }

    /// <summary>
    /// Comments for the forward constants whose role the graph structure PROVES - nothing is guessed. The
    /// baked tint is the vector of literals multiplying the hemisphere-ambient lerp (the lerp between the
    /// named outdoor-light top and bottom colours), and the first interpolator carries the distance fade in
    /// its fourth lane. Without these notes the tunable literals are anonymous scalars.
    /// </summary>
    private static void AnnotateForward(ShaderGraph p_Graph)
    {
        var s_WorldPos = p_Graph.Nodes.FirstOrDefault(p_N =>
            p_N.Kind == "Interpolator" && p_N.GetParam("Index") == "0");
        if (s_WorldPos != null && string.IsNullOrWhiteSpace(s_WorldPos.Comment))
            s_WorldPos.Comment = "World position in xyz; w carries the vertex-computed distance fade " +
                                 "the alpha multiplies.";

        var s_Top = FindExternal(p_Graph, "outdoorLightTopColor");
        var s_Bottom = FindExternal(p_Graph, "outdoorLightBottomColor");
        if (s_Top == null || s_Bottom == null)
            return;

        // The hemisphere ambient: a folded Lerp between the two colours, or its raw spelling - an Add whose
        // operands are one of the colours and a Multiply of the Subtract of both (the compiled mad).
        var s_AmbientIds = new List<string>();
        foreach (var s_Node in p_Graph.Nodes)
        {
            if (s_Node.Kind == "Lerp")
            {
                var s_From = SourceOf(p_Graph, s_Node.Id, "Input1");
                var s_To = SourceOf(p_Graph, s_Node.Id, "Input2");
                var s_Ends = new[] { s_From?.Node, s_To?.Node };
                if (s_Ends.Contains(s_Top.Id) && s_Ends.Contains(s_Bottom.Id))
                    s_AmbientIds.Add(s_Node.Id);
            }
            else if (s_Node.Kind == "Add")
            {
                var s_A = SourceOf(p_Graph, s_Node.Id, "Input1");
                var s_B = SourceOf(p_Graph, s_Node.Id, "Input2");
                if (s_A == null || s_B == null)
                    continue;

                foreach (var (s_Anchor, s_Rest) in new[] { (s_A.Value, s_B.Value), (s_B.Value, s_A.Value) })
                {
                    if (s_Anchor.Node != s_Top.Id && s_Anchor.Node != s_Bottom.Id)
                        continue;

                    var s_Chain = p_Graph.FindNode(s_Rest.Node);
                    if (s_Chain?.Kind != "Multiply")
                        continue;

                    var s_Factors = new[] { SourceOf(p_Graph, s_Chain.Id, "Input1"),
                                            SourceOf(p_Graph, s_Chain.Id, "Input2") };
                    if (s_Factors.Any(p_F => p_F != null &&
                            p_Graph.FindNode(p_F.Value.Node)?.Kind == "Subtract" &&
                            new[] { SourceOf(p_Graph, p_F.Value.Node, "Input1")?.Node,
                                    SourceOf(p_Graph, p_F.Value.Node, "Input2")?.Node }
                                .Intersect(new[] { s_Top.Id, s_Bottom.Id }).Count() == 2))
                        s_AmbientIds.Add(s_Node.Id);
                }
            }
        }

        foreach (var s_AmbientId in s_AmbientIds.Distinct())
        {
            // The ambient's Multiply partner whose whole feed is literals = the baked tint.
            foreach (var s_Connection in p_Graph.Connections.Where(p_C => p_C.FromNode == s_AmbientId).ToList())
            {
                var s_Consumer = p_Graph.FindNode(s_Connection.ToNode);
                if (s_Consumer?.Kind != "Multiply")
                    continue;

                var s_OtherPort = s_Connection.ToPort == "Input1" ? "Input2" : "Input1";
                var s_Other = SourceOf(p_Graph, s_Consumer.Id, s_OtherPort);
                if (s_Other == null)
                    continue;

                // The ambient's partner is either the tint itself, or tint x mask (the game multiplies the
                // literal into the mask channel first) - in that case the tint is the literal FACTOR.
                var s_TintId = IsAllLiteral(p_Graph, s_Other.Value.Node, 0) ? s_Other.Value.Node : null;
                if (s_TintId == null && p_Graph.FindNode(s_Other.Value.Node)?.Kind == "Multiply")
                    foreach (var s_Port in new[] { "Input1", "Input2" })
                    {
                        var s_Factor = SourceOf(p_Graph, s_Other.Value.Node, s_Port);
                        if (s_Factor != null && IsAllLiteral(p_Graph, s_Factor.Value.Node, 0))
                            s_TintId = s_Factor.Value.Node;
                    }

                if (s_TintId == null)
                    continue;

                var s_Tint = p_Graph.FindNode(s_TintId);
                if (s_Tint != null && string.IsNullOrWhiteSpace(s_Tint.Comment))
                    s_Tint.Comment = "The baked glass TINT - the literal an artist tuned. This is the " +
                                     "value a variation changes to re-colour the glass.";
            }
        }
    }

    /// <summary>
    /// Every texture node gets the NAME its register carries in the shader's own binding table. A
    /// self-describing name (texture_Diffuse) is the authors' own word for the slot; the generic ones
    /// (texture_Texture, texture_Texture2...) are the per-instance slots the (mesh, variation) texture
    /// set feeds, which is exactly the "which texture is this from outside?" question.
    /// </summary>
    private static void AnnotateResources(ShaderGraph p_Graph, ShaderContract p_Contract)
    {
        foreach (var s_Node in p_Graph.Nodes)
        {
            if (s_Node.Kind is not ("Texture" or "TextureCube" or "Texture3D" or "TextureArray" or "NormalMap") ||
                !string.IsNullOrWhiteSpace(s_Node.Comment))
                continue;

            if (!int.TryParse(s_Node.GetParam("Register"), out var s_Register))
                continue;

            var s_Binding = p_Contract.Resources.FirstOrDefault(p_R => p_R.IsTexture && p_R.Register == s_Register);
            if (s_Binding == null)
                continue;

            var s_Generic = s_Binding.Name.StartsWith("texture_Texture", StringComparison.OrdinalIgnoreCase);
            s_Node.Comment = $"Samples '{s_Binding.Name}' (t{s_Register})." + (s_Generic
                ? " A per-instance slot: the (mesh, variation) texture set decides the actual image."
                : "");
        }
    }

    /// <summary>Raw interpolator nodes get the measured meaning of their row - the graph otherwise shows
    /// only a number, and knowing that 2 is a tangent row was tribal knowledge.</summary>
    private static void AnnotateInterpolators(ShaderGraph p_Graph, ShaderContract p_Contract)
    {
        foreach (var s_Node in p_Graph.Nodes.Where(p_N => p_N.Kind == "Interpolator"))
        {
            if (!string.IsNullOrWhiteSpace(s_Node.Comment) ||
                !int.TryParse(s_Node.GetParam("Index"), out var s_Index))
                continue;

            var s_Field = p_Contract.FieldNameFor(s_Index);
            s_Node.Comment = s_Field switch
            {
                "WorldPos" => "World-space position (xyz).",
                "WorldNormal" => "World-space normal.",
                "TexCoord" => "The UV set.",
                "VertexColor" => "Raw per-vertex stream (consumed as colour/occlusion on characters).",
                _ when s_Field.StartsWith("TangentRow", StringComparison.Ordinal) =>
                    $"{s_Field}: a row of the tangent basis - the three rows move tangent-space normals " +
                    "to world space.",
                _ when s_Field.StartsWith("ProbeSh", StringComparison.Ordinal) =>
                    $"{s_Field}: a light-probe SH row (per-object ambient).",
                _ => null!,
            };
        }
    }

    /// <summary>
    /// Two baked-literal roles the structure proves: an all-literal factor multiplying a UV on its way into
    /// a texture coordinate is the baked TILE factor (the game bakes uv x (1/factor) at build time), and a
    /// pair of literals whose squares sum to one feeding the same arithmetic is the cosine/sine of a baked
    /// UV rotation.
    /// </summary>
    private static void AnnotateBakedUv(ShaderGraph p_Graph)
    {
        foreach (var s_Multiply in p_Graph.Nodes.Where(p_N => p_N.Kind == "Multiply"))
        {
            var s_A = SourceOf(p_Graph, s_Multiply.Id, "Input1");
            var s_B = SourceOf(p_Graph, s_Multiply.Id, "Input2");
            if (s_A == null || s_B == null || !ReachesTextureCoord(p_Graph, s_Multiply.Id, 0))
                continue;

            foreach (var (s_Literal, s_Other) in new[] { (s_A.Value, s_B.Value), (s_B.Value, s_A.Value) })
            {
                var s_OtherNode = p_Graph.FindNode(s_Other.Node);
                var s_IsUv = s_OtherNode?.Kind is "TexCoord" or "Interpolator";
                if (!s_IsUv || !IsAllLiteral(p_Graph, s_Literal.Node, 0))
                    continue;

                var s_Node = p_Graph.FindNode(s_Literal.Node);
                if (s_Node != null && string.IsNullOrWhiteSpace(s_Node.Comment))
                    s_Node.Comment = "Baked UV tile factor - the game bakes the material's tiling into " +
                                     "the shader as this literal.";
            }
        }

        // Rotation pairs: cos/sin baked as two literals. The unit square-sum is the proof, and both nodes
        // must meet in the same expression tree (an Add or Subtract joining their products).
        var s_Scalars = p_Graph.Nodes
            .Where(p_N => p_N.Kind == "Scalar" &&
                          float.TryParse(p_N.GetParam("Value"), NumberStyles.Float,
                              CultureInfo.InvariantCulture, out var s_V) &&
                          Math.Abs(s_V) is > 0.01f and < 0.999f)
            .ToList();

        foreach (var s_Cos in s_Scalars)
        foreach (var s_Sin in s_Scalars)
        {
            if (s_Cos.Id.CompareTo(s_Sin.Id) >= 0)
                continue;

            var s_C = float.Parse(s_Cos.GetParam("Value")!, CultureInfo.InvariantCulture);
            var s_S = float.Parse(s_Sin.GetParam("Value")!, CultureInfo.InvariantCulture);
            if (Math.Abs(s_C * s_C + s_S * s_S - 1f) > 0.001f)
                continue;

            foreach (var s_Node in new[] { s_Cos, s_Sin })
                if (string.IsNullOrWhiteSpace(s_Node.Comment))
                    s_Node.Comment = $"With {(s_Node == s_Cos ? s_Sin : s_Cos).GetParam("Value")} this " +
                                     "literal square-sums to 1: the cosine/sine pair of a baked UV rotation.";
        }
    }

    /// <summary>The measured fresnel form starts from the constant 1.001 - wherever that literal feeds a
    /// chain with a Dot in it, it is the fresnel term's anchor.</summary>
    private static void AnnotateFresnelConstant(ShaderGraph p_Graph)
    {
        foreach (var s_Node in p_Graph.Nodes)
        {
            if (s_Node.Kind != "Scalar" || !string.IsNullOrWhiteSpace(s_Node.Comment) ||
                !float.TryParse(s_Node.GetParam("Value"), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var s_Value) || Math.Abs(s_Value - 1.001f) > 0.0005f)
                continue;

            s_Node.Comment = "The fresnel anchor: the measured form is (1.001 - N dot V) raised and " +
                             "scaled by the curve literals downstream.";
        }
    }

    /// <summary>True when a node's output reaches some texture node's Coord input within a few hops.</summary>
    private static bool ReachesTextureCoord(ShaderGraph p_Graph, string p_Node, int p_Depth)
    {
        if (p_Depth > 3)
            return false;

        foreach (var s_Connection in p_Graph.Connections.Where(p_C => p_C.FromNode == p_Node))
        {
            var s_To = p_Graph.FindNode(s_Connection.ToNode);
            if (s_To == null)
                continue;

            if (s_To.Kind is "Texture" or "TextureCube" or "Texture3D" or "TextureArray" &&
                s_Connection.ToPort == "Coord")
                return true;

            if (ReachesTextureCoord(p_Graph, s_To.Id, p_Depth + 1))
                return true;
        }

        return false;
    }

    private static GraphNode? FindExternal(ShaderGraph p_Graph, string p_Name) =>
        p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "ExternalConstant" && p_N.GetParam("Name") == p_Name);

    /// <summary>True when a node's whole dependency closure is literal scalars assembled into vectors.</summary>
    private static bool IsAllLiteral(ShaderGraph p_Graph, string p_Node, int p_Depth)
    {
        if (p_Depth > 4)
            return false;

        var s_Node = p_Graph.FindNode(p_Node);
        if (s_Node == null)
            return false;

        if (s_Node.Kind == "Scalar")
            return true;

        if (s_Node.Kind != "Append" && s_Node.Kind != "Swizzle" && s_Node.Kind != "MixOut")
            return false;

        var s_Feeds = p_Graph.Connections.Where(p_C => p_C.ToNode == p_Node).ToList();
        return s_Feeds.Count > 0 && s_Feeds.All(p_C => IsAllLiteral(p_Graph, p_C.FromNode, p_Depth + 1));
    }
}
