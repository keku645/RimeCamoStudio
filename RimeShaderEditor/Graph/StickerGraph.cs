using System;
using System.Collections.Generic;
using System.Linq;

namespace RimeShaderEditor.Graph;

/// <summary>
/// The nodes that make a graph sample a sticker layer: a texture node at a free register reading the same
/// coordinates as the weapon's diffuse, blended over whatever fed the root's Diffuse by the layer's alpha
/// (premultiplied: diffuse × (1 − a) + rgb), gated by the SIDE MAP at the next register — a weapon whose two
/// sides share one patch of texture would show every sticker on both, mirrored on the second (the F2000
/// in-game, 2026-09-12) — and, for an animated sticker, the coordinate maps and the frame sheet after it.
/// A graph is extended once; the register is remembered on it.
///
/// Registers, from the layer's R: R+1 the map (red = side, green = y, alpha = x), R+2 the emblem slot's shape
/// atlas, R+3 an animated sticker's frame sheet (SheetRegisterOf; R+2 before the slot stayed beside a GIF) —
/// DXT5, the one lane the engine reads for a material. ⛔ The "seven or eight external textures came out black"
/// of 2026-09-12 was never a cap: those rounds carried two bugs since fixed, and a package with SEVEN (the
/// preset's Diffuse, Camo, Specular + layer, map, sheet and atlas) drew right in game (the crossbow with a
/// GIF and the emblem worked perfectly). See StickerLayer.PackMaps.
///
/// Lives on the GRAPH, not on the window, because the same extension has to be made on more than one graph
/// with the SAME register: the camo document and, while a weapon is shown as it ships, the no-camo graph
/// standing in for it on the canvas (stickers over the weapon as the game ships it).
/// </summary>
public static class StickerGraph
{
    /// <summary>
    /// Extends the graph, or leaves it alone when it already samples its sticker register (an older graph
    /// without the side gate is given one). Returns the register (the graph's own, or the forced one, or
    /// the first free) and a note for the log; null when the graph has no material root to blend into.
    /// <paramref name="p_MapSide"/> is the side, in texels, of the layer and the maps — read one texel at a time.
    /// </summary>
    public static int? Ensure(ShaderGraph p_Graph, int? p_DiffuseRegister, int? p_ForcedRegister, out string p_Note, int p_MapSide = 1024)
    {
        var s_Register = EnsureLayer(p_Graph, p_DiffuseRegister, p_ForcedRegister, out p_Note, p_MapSide);

        // ⭐ Every graph with a sticker layer carries the emblem slot too (every camo is baked with the emblem slot), whichever
        // of its copies is being made — the canvas, the parked document, the as-shipped look, the
        // bake's — so one place decides it. Beside an animated sticker too (GifKeepsEmblem); CAMO_EMBLEM_SLOT=0 = the way back.
        if (s_Register != null && EmblemSlot.Enabled && (GifKeepsEmblem || !p_Graph.Nodes.Any(p_N => p_N.Kind == "Flipbook")) &&
            EnsureEmblem(p_Graph, p_MapSide, out var s_EmblemNote) && s_EmblemNote.Length > 0)
            p_Note = (p_Note.Length > 0 ? p_Note + " " : "") + s_EmblemNote;

        return s_Register;
    }

    /// <summary>
    /// ⭐ AN ANIMATED STICKER AND THE EMBLEM SLOT TOGETHER (placing a new sticker removed the emblem slot and its "sticker"; whatever
    /// happens, the emblem slot must stay unless it is changed with its own buttons — the sticker was a GIF, and
    /// placing it tore the slot off the graph, so the camo baked with no emblem at all). A PROJECTED slot (EmblemSlot.Projected) reads no
    /// coordinate map — the GIF keeps it —, so the two shared only the texture after the map: the GIF's frame sheet or the slot's shape atlas.
    /// The atlas stays where every camo has it (R + 2, the layout measured in game) and the GIF's sheet moves to R + 3 (SheetRegisterOf; every
    /// weapon preset reads t1..t5 and the layer sits from t6, so R + 3 is free on each graph of a package); the slot is drawn over the GIF. A slot
    /// read from the map (the projection off) still steps aside; CAMO_GIF_EMBLEM_OLD=1 = as before (a GIF took the slot off, its sheet at R + 2).
    /// </summary>
    public static bool GifKeepsEmblem => EmblemSlot.Projected && Environment.GetEnvironmentVariable("CAMO_GIF_EMBLEM_OLD") != "1";

    /// <summary>The register an animated sticker's frame sheet is sampled at, for a sticker layer at <paramref name="p_StickerRegister"/>.</summary>
    public static int SheetRegisterOf(int p_StickerRegister) => p_StickerRegister + (GifKeepsEmblem ? 3 : 2);

    /// <summary>The register a graph samples its animated sticker's frame sheet at (the texture the Flipbook reads), or null when it plays none.</summary>
    public static int? SheetRegisterIn(ShaderGraph p_Graph)
    {
        var s_Books = p_Graph.Nodes.Where(p_N => p_N.Kind == "Flipbook").Select(p_N => p_N.Id).ToHashSet();
        return p_Graph.Connections
            .Where(p_C => s_Books.Contains(p_C.FromNode) && p_C.FromPort == "Out" && p_C.ToPort == "Coord")
            .Select(p_C => p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == p_C.ToNode))
            .Select(p_N => int.TryParse(p_N?.GetParam("Register"), out var s_At) ? s_At : (int?) null)
            .FirstOrDefault(p_At => p_At != null);
    }

    /// <summary>
    /// The wire the emblem slot blends OVER (what fed the root's Diffuse before the slot was laid: into its "under" Multiply), or null when the
    /// graph draws no slot — where something that must stay UNDER the slot (an animated sticker) is inserted.
    /// </summary>
    private static GraphConnection? EmblemBlendsOver(ShaderGraph p_Graph, GraphNode p_Root)
    {
        if (p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "EmblemLayers") is not { } s_Emblem)
            return null;

        var s_Downstream = new HashSet<string>();
        var s_Queue = new Queue<string>();
        s_Queue.Enqueue(s_Emblem.Id);
        while (s_Queue.Count > 0)
        {
            var s_Id = s_Queue.Dequeue();
            foreach (var s_Next in p_Graph.Connections.Where(p_C => p_C.FromNode == s_Id && p_C.ToNode != p_Root.Id).Select(p_C => p_C.ToNode))
                if (s_Downstream.Add(s_Next))
                    s_Queue.Enqueue(s_Next);
        }

        // the slot's blend feeds the root's Diffuse; its Input1 is the "under" Multiply, whose Input1 is what the slot blends over
        var s_IntoDiffuse = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == p_Root.Id && p_C.ToPort == "Diffuse");
        if (s_IntoDiffuse == null || !s_Downstream.Contains(s_IntoDiffuse.FromNode))
            return null;

        var s_Under = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_IntoDiffuse.FromNode && p_C.ToPort == "Input1");
        return s_Under == null ? null : p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Under.FromNode && p_C.ToPort == "Input1");
    }

    private static int? EnsureLayer(ShaderGraph p_Graph, int? p_DiffuseRegister, int? p_ForcedRegister, out string p_Note, int p_MapSide)
    {
        var s_TextureKinds = Palette.TextureKinds;
        if (p_Graph.StickerRegister is { } s_Known &&
            p_Graph.Nodes.FirstOrDefault(p_N => s_TextureKinds.Contains(p_N.Kind) && p_N.GetParam("Register") == s_Known.ToString()) is { } s_Existing)
        {
            foreach (var s_Snap in p_Graph.Nodes.Where(p_N => p_N.Kind == "TexelSnap"))
                s_Snap.Params["Side"] = p_MapSide.ToString();

            p_Note = p_Graph.Nodes.Any(p_N => p_N.Kind == "StickerSideGate") || !GateLayer(p_Graph, s_Existing, s_Known, p_MapSide)
                ? ""
                : $"the sticker layer at t{s_Known} is now kept to the side each sticker was placed on (side map at t{s_Known + 1}).";
            return s_Known;
        }

        var s_Root = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "StandardRoot");
        if (s_Root == null)
        {
            p_Note = "this graph has no material root to blend a sticker layer into.";
            return null;
        }

        // The diffuse's own coordinates: the node sampling the diffuse register (the studio names it; the
        // lowest register otherwise), and whatever feeds its Coord pin — the sticker reads the same UV.
        var s_TextureNodes = p_Graph.Nodes
            .Where(p_N => s_TextureKinds.Contains(p_N.Kind) && int.TryParse(p_N.GetParam("Register"), out _))
            .ToList();
        var s_Diffuse = s_TextureNodes.FirstOrDefault(p_N => p_DiffuseRegister != null && p_N.GetParam("Register") == p_DiffuseRegister.ToString())
                        ?? s_TextureNodes.OrderBy(p_N => int.Parse(p_N.GetParam("Register"))).FirstOrDefault();
        var s_CoordSource = s_Diffuse == null
            ? null
            : p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Diffuse.Id && p_C.ToPort == "Coord");

        var s_Used = s_TextureNodes.Select(p_N => int.Parse(p_N.GetParam("Register"))).ToHashSet();
        var s_Register = p_ForcedRegister ?? 1;
        if (p_ForcedRegister == null)
            while (s_Used.Contains(s_Register) || s_Used.Contains(s_Register + 1))
                s_Register++;

        var s_Sampler = new GraphNode
        {
            Kind = "Texture", X = s_Root.X - 700, Y = s_Root.Y + 160,
            Params = { ["Register"] = s_Register.ToString(), ["Tiling"] = "1,1" },
            Comment = "Sticker layer — composed by Stickers mode (premultiplied); the bake ships one per weapon mesh",
        };
        var s_Alpha = new GraphNode { Kind = "Swizzle", X = s_Root.X - 540, Y = s_Root.Y + 220, Params = { ["Channels"] = "wwww" } };
        var s_Colour = new GraphNode { Kind = "Swizzle", X = s_Root.X - 540, Y = s_Root.Y + 140, Params = { ["Channels"] = "xyz" } };
        var s_Keep = new GraphNode { Kind = "OneMinus", X = s_Root.X - 400, Y = s_Root.Y + 220 };
        var s_Under = new GraphNode { Kind = "Multiply", X = s_Root.X - 280, Y = s_Root.Y + 60 };
        var s_Blend = new GraphNode { Kind = "Add", X = s_Root.X - 150, Y = s_Root.Y + 40 };

        foreach (var s_Node in new[] { s_Sampler, s_Alpha, s_Colour, s_Keep, s_Under, s_Blend })
            p_Graph.Nodes.Add(s_Node);

        if (s_CoordSource != null)
            p_Graph.Connections.Add(new GraphConnection
            {
                FromNode = s_CoordSource.FromNode, FromPort = s_CoordSource.FromPort, ToNode = s_Sampler.Id, ToPort = "Coord",
            });

        var s_IntoDiffuse = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Root.Id && p_C.ToPort == "Diffuse");
        if (s_IntoDiffuse != null)
        {
            s_IntoDiffuse.ToNode = s_Under.Id;
            s_IntoDiffuse.ToPort = "Input1";
        }

        Wire(p_Graph, s_Sampler, "Out", s_Alpha, "Input");
        Wire(p_Graph, s_Sampler, "Out", s_Colour, "Input");
        Wire(p_Graph, s_Alpha, "Out", s_Keep, "Input");
        Wire(p_Graph, s_Keep, "Out", s_Under, "Input2");
        Wire(p_Graph, s_Under, "Out", s_Blend, "Input1");
        Wire(p_Graph, s_Colour, "Out", s_Blend, "Input2");
        Wire(p_Graph, s_Blend, "Out", s_Root, "Diffuse");

        p_Graph.StickerRegister = s_Register;
        GateLayer(p_Graph, s_Sampler, s_Register, p_MapSide);
        p_Note = $"the graph now samples a sticker layer at t{s_Register} over the diffuse, kept to the side each sticker was placed on (side map at t{s_Register + 1})" +
                 (s_CoordSource != null ? "." : " (the diffuse's coordinates were not found: the layer reads the mesh UV directly).");
        return s_Register;
    }

    /// <summary>
    /// Puts the side gate on a graph's sticker layer: the side map at register + 1, read one texel at a
    /// time, held against the tangent frame's handedness; the layer's alpha AND premultiplied colour are
    /// multiplied by the gate before they blend. False when the layer's swizzles cannot be found.
    /// </summary>
    private static bool GateLayer(ShaderGraph p_Graph, GraphNode p_Sampler, int p_Register, int p_MapSide)
    {
        var s_Alpha = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "Swizzle" && p_N.GetParam("Channels") == "wwww" &&
                                                           p_Graph.Connections.Any(p_C => p_C.FromNode == p_Sampler.Id && p_C.ToNode == p_N.Id));
        var s_Colour = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "Swizzle" && p_N.GetParam("Channels") == "xyz" &&
                                                            p_Graph.Connections.Any(p_C => p_C.FromNode == p_Sampler.Id && p_C.ToNode == p_N.Id));
        if (s_Alpha == null || s_Colour == null)
            return false;

        var s_Gate = EnsureGate(p_Graph, p_Sampler, p_Register, p_MapSide);
        var s_AlphaGated = new GraphNode { Kind = "Multiply", X = s_Alpha.X + 80, Y = s_Alpha.Y + 60, Comment = "Sticker — alpha only on the side it was placed on" };
        var s_ColourGated = new GraphNode { Kind = "Multiply", X = s_Colour.X + 80, Y = s_Colour.Y - 60 };
        p_Graph.Nodes.Add(s_AlphaGated);
        p_Graph.Nodes.Add(s_ColourGated);

        // Whatever read the swizzles now reads the gated products.
        foreach (var s_Connection in p_Graph.Connections.Where(p_C => p_C.FromNode == s_Alpha.Id).ToList())
            s_Connection.FromNode = s_AlphaGated.Id;
        foreach (var s_Connection in p_Graph.Connections.Where(p_C => p_C.FromNode == s_Colour.Id).ToList())
            s_Connection.FromNode = s_ColourGated.Id;

        Wire(p_Graph, s_Alpha, "Out", s_AlphaGated, "Input1");
        Wire(p_Graph, s_Gate, "Out", s_AlphaGated, "Input2");
        Wire(p_Graph, s_Colour, "Out", s_ColourGated, "Input1");
        Wire(p_Graph, s_Gate, "Out", s_ColourGated, "Input2");
        return true;
    }

    /// <summary>The graph's one side gate (side map at register + 1 against the tangent handedness), made when missing.</summary>
    private static GraphNode EnsureGate(ShaderGraph p_Graph, GraphNode p_Sampler, int p_Register, int p_MapSide)
    {
        if (p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "StickerSideGate") is { } s_Known)
            return s_Known;

        var s_CoordSource = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == p_Sampler.Id && p_C.ToPort == "Coord");
        var s_Snap = new GraphNode
        {
            Kind = "TexelSnap", X = p_Sampler.X - 160, Y = p_Sampler.Y + 120,
            Params = { ["Side"] = p_MapSide.ToString() },
            Comment = "Sticker side map — read one texel at a time",
        };
        var s_Side = new GraphNode
        {
            Kind = "Texture", X = p_Sampler.X, Y = p_Sampler.Y + 120,
            Params = { ["Register"] = (p_Register + 1).ToString(), ["Tiling"] = "1,1", ["in:Lod"] = "0" },
            Comment = "Sticker side map — which half of a mirrored unwrap each sticker was placed on; composed per weapon mesh",
        };
        var s_SideX = new GraphNode { Kind = "Swizzle", X = p_Sampler.X + 160, Y = p_Sampler.Y + 120, Params = { ["Channels"] = "x" } };
        var s_Hand = new GraphNode { Kind = "TangentHandedness", X = p_Sampler.X + 160, Y = p_Sampler.Y + 200 };
        var s_Gate = new GraphNode { Kind = "StickerSideGate", X = p_Sampler.X + 300, Y = p_Sampler.Y + 160 };
        foreach (var s_Node in new[] { s_Snap, s_Side, s_SideX, s_Hand, s_Gate })
            p_Graph.Nodes.Add(s_Node);

        if (s_CoordSource != null)
            p_Graph.Connections.Add(new GraphConnection { FromNode = s_CoordSource.FromNode, FromPort = s_CoordSource.FromPort, ToNode = s_Snap.Id, ToPort = "Coord" });
        Wire(p_Graph, s_Snap, "Out", s_Side, "Coord");
        Wire(p_Graph, s_Side, "Out", s_SideX, "Input");
        Wire(p_Graph, s_SideX, "Out", s_Gate, "Side");
        Wire(p_Graph, s_Hand, "Out", s_Gate, "Hand");
        return s_Gate;
    }

    /// <summary>
    /// Brings a document's animated-sticker nodes from an older layout to this one — the registers moved
    /// twice (one map at R+1 and the sheet at R+2; then x/y maps at R+1/R+2 and the sheet at R+3; now the
    /// side map at R+1, x/y maps at R+2/R+3, the sheet at R+4) and the side gate came in — so a camo saved
    /// with the older studio bakes right without the GIF being placed again. Nodes are found by the notes
    /// this class wrote on them. True when the sheet now sits at R+4.
    /// </summary>
    private static bool MigrateAnimated(ShaderGraph p_Graph, int p_Register, int p_MapSide)
    {
        var s_TextureKinds = Palette.TextureKinds;
        GraphNode? Noted(string p_Kind, string p_Text) =>
            p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == p_Kind && (p_N.Comment ?? "").Contains(p_Text, StringComparison.Ordinal));

        var s_Sheet = Noted("Texture", "frame sheet");
        var s_Book = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "Flipbook");
        var s_Layer = p_Graph.Nodes.FirstOrDefault(p_N => s_TextureKinds.Contains(p_N.Kind) && p_N.GetParam("Register") == p_Register.ToString());
        if (s_Sheet == null || s_Book == null || s_Layer == null)
            return false;

        // (after the emblem slot's atlas when the slot stays beside the GIF — SheetRegisterOf)
        s_Sheet.Params["Register"] = SheetRegisterOf(p_Register).ToString();

        // Today's layout already: the coordinate reader feeds the Flipbook. Its numbers are refreshed and
        // that is all — ⛔ not recognising it here built a WHOLE NEW CHAIN on every refresh (four Flipbooks in
        // one document).
        var s_TodayLink = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Book.Id && p_C.ToPort == "Coord");
        var s_TodayFrom = s_TodayLink == null ? null : p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == s_TodayLink.FromNode);
        if (s_TodayFrom?.Kind == "StickerCoordinates")
        {
            s_TodayFrom.Params["Register"] = (p_Register + 1).ToString();
            s_TodayFrom.Params["Side"] = p_MapSide.ToString();
            return true;
        }

        var s_MapU = Noted("Texture", "x of the GIF");
        var s_MapV = Noted("Texture", "y of the GIF");
        var s_Single = Noted("Texture", "coordinate map (red = x, green = y");
        if (s_Single != null && s_MapU == null)
        {
            // The first layout: one two-channel map, its xy swizzle feeding the Flipbook. It becomes the
            // x map; a y map, its swizzle and an Append are added beside it.
            s_MapU = s_Single;
            s_MapU.Comment = "Animated sticker — x of the GIF frame each texel shows (the map's alpha); composed per weapon mesh";
            var s_Xy = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "Swizzle" && p_N.GetParam("Channels") == "xy" &&
                                                            p_Graph.Connections.Any(p_C => p_C.FromNode == s_Single.Id && p_C.ToNode == p_N.Id));
            var s_Snap = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "TexelSnap" &&
                                                              p_Graph.Connections.Any(p_C => p_C.FromNode == p_N.Id && p_C.ToNode == s_Single.Id));
            if (s_Xy == null)
                return false;

            s_Xy.Params["Channels"] = "w";
            s_MapV = new GraphNode
            {
                Kind = "Texture", X = s_Single.X, Y = s_Single.Y + 100,
                Params = { ["Register"] = (p_Register + 1).ToString(), ["Tiling"] = "1,1", ["in:Lod"] = "0" },
                Comment = "Animated sticker — y of the GIF frame each texel shows (the map's green); composed per weapon mesh",
            };
            var s_MapVx = new GraphNode { Kind = "Swizzle", X = s_Xy.X, Y = s_Xy.Y + 100, Params = { ["Channels"] = "y" } };
            var s_Point = new GraphNode { Kind = "Append", X = s_Xy.X + 140, Y = s_Xy.Y + 40 };
            p_Graph.Nodes.Add(s_MapV);
            p_Graph.Nodes.Add(s_MapVx);
            p_Graph.Nodes.Add(s_Point);
            foreach (var s_Connection in p_Graph.Connections.Where(p_C => p_C.FromNode == s_Xy.Id).ToList())
                s_Connection.FromNode = s_Point.Id;
            if (s_Snap != null)
                Wire(p_Graph, s_Snap, "Out", s_MapV, "Coord");
            Wire(p_Graph, s_MapV, "Out", s_MapVx, "Input");
            Wire(p_Graph, s_Xy, "Out", s_Point, "X");
            Wire(p_Graph, s_MapVx, "Out", s_Point, "Y");
        }

        if (s_MapU == null || s_MapV == null)
            return false;

        // x now lives in the map's alpha and y in its green, both at register + 1: the maps move register
        // and the swizzles that read them read those channels.
        s_MapU.Params["Register"] = (p_Register + 1).ToString();
        s_MapV.Params["Register"] = (p_Register + 1).ToString();
        s_MapU.Comment = "Animated sticker — x of the GIF frame each texel shows (the map's alpha); composed per weapon mesh";
        s_MapV.Comment = "Animated sticker — y of the GIF frame each texel shows (the map's green); composed per weapon mesh";
        foreach (var (s_Map, s_Channel) in new[] { (s_MapU, "w"), (s_MapV, "y") })
        foreach (var s_Swizzle in p_Graph.Nodes.Where(p_N => p_N.Kind == "Swizzle" && p_N.GetParam("Channels") is "x" or "w" or "y" &&
                                                              p_Graph.Connections.Any(p_C => p_C.FromNode == s_Map.Id && p_C.ToNode == p_N.Id)))
            s_Swizzle.Params["Channels"] = s_Channel;

        // The coordinate reader: the two texel-snapped map readers, their swizzles and the Append that fed
        // the Flipbook become ONE StickerCoordinates node (interpolated between texels — the frame's own
        // resolution; even at 2048 it looked quite pixelated).
        var s_CoordLink = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Book.Id && p_C.ToPort == "Coord");
        var s_CoordFrom = s_CoordLink == null ? null : p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == s_CoordLink.FromNode);
        if (s_CoordFrom?.Kind != "StickerCoordinates")
        {
            var s_Doomed = new HashSet<string>();
            if (s_CoordFrom?.Kind == "Append")
                s_Doomed.Add(s_CoordFrom.Id);
            (string Node, string Port)? s_UvSource = null;
            foreach (var s_Map in new[] { s_MapU, s_MapV })
            {
                s_Doomed.Add(s_Map.Id);
                foreach (var s_Swizzle in p_Graph.Nodes.Where(p_N => p_N.Kind == "Swizzle" && p_Graph.Connections.Any(p_C => p_C.FromNode == s_Map.Id && p_C.ToNode == p_N.Id)))
                    s_Doomed.Add(s_Swizzle.Id);
                var s_Feed = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Map.Id && p_C.ToPort == "Coord");
                var s_Snap = s_Feed == null ? null : p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == s_Feed.FromNode && p_N.Kind == "TexelSnap");
                if (s_Snap != null)
                {
                    var s_SnapFeed = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Snap.Id && p_C.ToPort == "Coord");
                    if (s_SnapFeed != null)
                        s_UvSource ??= (s_SnapFeed.FromNode, s_SnapFeed.FromPort);
                    // The snap goes when nothing but the doomed readers hang off it (the gate has its own).
                    if (p_Graph.Connections.Where(p_C => p_C.FromNode == s_Snap.Id).All(p_C => s_Doomed.Contains(p_C.ToNode) || p_C.ToNode == s_MapU.Id || p_C.ToNode == s_MapV.Id))
                        s_Doomed.Add(s_Snap.Id);
                }
                else if (s_Feed != null)
                    s_UvSource ??= (s_Feed.FromNode, s_Feed.FromPort);
            }

            var s_Where = s_CoordFrom ?? s_MapU;
            var s_Coords = new GraphNode
            {
                Kind = "StickerCoordinates", X = s_Where.X, Y = s_Where.Y,
                Params = { ["Register"] = (p_Register + 1).ToString(), ["Side"] = p_MapSide.ToString(), ["XChannel"] = "w", ["YChannel"] = "y" },
                Comment = "Animated sticker — which point of the GIF frame each texel shows (the map's alpha = x, green = y); composed per weapon mesh",
            };
            p_Graph.Nodes.RemoveAll(p_N => s_Doomed.Contains(p_N.Id));
            p_Graph.Connections.RemoveAll(p_C => s_Doomed.Contains(p_C.FromNode) || s_Doomed.Contains(p_C.ToNode));
            p_Graph.Nodes.Add(s_Coords);
            if (s_UvSource is { } s_Uv)
                p_Graph.Connections.Add(new GraphConnection { FromNode = s_Uv.Node, FromPort = s_Uv.Port, ToNode = s_Coords.Id, ToPort = "Coord" });
            Wire(p_Graph, s_Coords, "Out", s_Book, "Coord");
        }
        else
        {
            s_CoordFrom.Params["Register"] = (p_Register + 1).ToString();
            s_CoordFrom.Params["Side"] = p_MapSide.ToString();
        }

        // The side gate on the animated alpha, when the layout predates it.
        if (Noted("Multiply", "and only on the side") == null)
        {
            var s_Covered = Noted("Multiply", "alpha only where a coordinate map");
            if (s_Covered != null)
            {
                var s_Gate = EnsureGate(p_Graph, s_Layer, p_Register, p_MapSide);
                var s_Gated = new GraphNode { Kind = "Multiply", X = s_Covered.X + 80, Y = s_Covered.Y + 60, Comment = "Animated sticker — and only on the side it was placed on" };
                p_Graph.Nodes.Add(s_Gated);
                foreach (var s_Connection in p_Graph.Connections.Where(p_C => p_C.FromNode == s_Covered.Id).ToList())
                    s_Connection.FromNode = s_Gated.Id;
                Wire(p_Graph, s_Covered, "Out", s_Gated, "Input1");
                Wire(p_Graph, s_Gate, "Out", s_Gated, "Input2");
            }
        }

        return true;
    }

    private static void Wire(ShaderGraph p_Graph, GraphNode p_From, string p_FromPort, GraphNode p_To, string p_ToPort) =>
        p_Graph.Connections.Add(new GraphConnection { FromNode = p_From.Id, FromPort = p_FromPort, ToNode = p_To.Id, ToPort = p_ToPort });

    /// <summary>
    /// Removes EVERY animation chain — each Flipbook, its coordinate reader, and everything downstream of
    /// them up to the root — and puts the root's Diffuse back on what the first chain was blending over.
    /// For a graph that ended up with several chains (a migration that did not recognise its own result
    /// built a new one on every refresh; measured 2026-09-12: four in one document), so that one fresh chain
    /// can be built. Returns how many Flipbooks went.
    /// </summary>
    public static int TearDownAnimation(ShaderGraph p_Graph)
    {
        var s_Root = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "StandardRoot");
        var s_Books = p_Graph.Nodes.Where(p_N => p_N.Kind == "Flipbook").ToList();
        if (s_Root == null || s_Books.Count == 0)
            return 0;

        // (the coordinate readers of the Flipbooks only: the emblem slot's own reader — and the slot downstream of it — stay, GifKeepsEmblem)
        var s_Readers = p_Graph.Connections
            .Where(p_C => s_Books.Any(p_B => p_B.Id == p_C.ToNode) && p_C.ToPort == "Coord")
            .Select(p_C => p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == p_C.FromNode))
            .Where(p_N => p_N?.Kind == "StickerCoordinates")
            .Select(p_N => p_N!);
        var s_Doomed = new HashSet<string>();
        var s_Queue = new Queue<GraphNode>(s_Books.Concat(s_Readers));
        while (s_Queue.Count > 0)
        {
            var s_Node = s_Queue.Dequeue();
            if (!s_Doomed.Add(s_Node.Id))
                continue;

            foreach (var s_Next in p_Graph.Connections.Where(p_C => p_C.FromNode == s_Node.Id).Select(p_C => p_C.ToNode).Distinct())
            {
                var s_Target = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == s_Next);
                if (s_Target != null && s_Target.Kind != "StandardRoot" && !s_Doomed.Contains(s_Target.Id))
                    s_Queue.Enqueue(s_Target);
            }
        }

        // What the chains blended over: the one input of a doomed node that comes from outside them and
        // is not the side gate's — the layer's blend, which the root's Diffuse takes back.
        var s_Outside = p_Graph.Connections
            .Where(p_C => s_Doomed.Contains(p_C.ToNode) && !s_Doomed.Contains(p_C.FromNode) && p_C.ToPort == "Input1")
            .Select(p_C => (p_C.FromNode, p_C.FromPort))
            .FirstOrDefault(p_S => p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == p_S.FromNode)?.Kind is not ("StickerSideGate" or "TexCoord"));

        // …and where they fed it on: the root's Diffuse, or — under the emblem slot — the slot's "under" (it gets back what the chains blended over)
        var s_Exit = p_Graph.Connections.FirstOrDefault(p_C => s_Doomed.Contains(p_C.FromNode) && !s_Doomed.Contains(p_C.ToNode));
        var (s_ToNode, s_ToPort) = s_Exit != null ? (s_Exit.ToNode, s_Exit.ToPort) : (s_Root.Id, "Diffuse");

        p_Graph.Nodes.RemoveAll(p_N => s_Doomed.Contains(p_N.Id));
        p_Graph.Connections.RemoveAll(p_C => s_Doomed.Contains(p_C.FromNode) || s_Doomed.Contains(p_C.ToNode));
        if (s_Outside.FromNode != null && !p_Graph.Connections.Any(p_C => p_C.ToNode == s_ToNode && p_C.ToPort == s_ToPort))
            p_Graph.Connections.Add(new GraphConnection { FromNode = s_Outside.FromNode, FromPort = s_Outside.FromPort, ToNode = s_ToNode, ToPort = s_ToPort });

        return s_Books.Count;
    }

    /// <summary>
    /// Removes the emblem slot's chain — its EmblemLayers node, the coordinate reader feeding it, and everything downstream of them up to the
    /// root — and puts the root's Diffuse back on what the slot was blending over. True when there was one.
    /// </summary>
    public static bool TearDownEmblem(ShaderGraph p_Graph)
    {
        var s_Root = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "StandardRoot");
        var s_Emblems = p_Graph.Nodes.Where(p_N => p_N.Kind == "EmblemLayers").ToList();
        if (s_Root == null || s_Emblems.Count == 0)
            return false;

        var s_Readers = p_Graph.Connections
            .Where(p_C => s_Emblems.Any(p_E => p_E.Id == p_C.ToNode) && p_C.ToPort == "Coord")
            .Select(p_C => p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == p_C.FromNode))
            .Where(p_N => p_N?.Kind == "StickerCoordinates")
            .Select(p_N => p_N!)
            .ToList();
        var s_Doomed = new HashSet<string>();
        var s_Queue = new Queue<GraphNode>(s_Emblems.Concat(s_Readers));
        while (s_Queue.Count > 0)
        {
            var s_Node = s_Queue.Dequeue();
            if (!s_Doomed.Add(s_Node.Id))
                continue;

            foreach (var s_Next in p_Graph.Connections.Where(p_C => p_C.FromNode == s_Node.Id).Select(p_C => p_C.ToNode).Distinct())
            {
                var s_Target = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == s_Next);
                if (s_Target != null && s_Target.Kind != "StandardRoot" && !s_Doomed.Contains(s_Target.Id))
                    s_Queue.Enqueue(s_Target);
            }
        }

        // What the slot blended over: the one input of a doomed node from outside the chain that is neither the side gate's nor a UV.
        var s_Outside = p_Graph.Connections
            .Where(p_C => s_Doomed.Contains(p_C.ToNode) && !s_Doomed.Contains(p_C.FromNode) && p_C.ToPort == "Input1")
            .Select(p_C => (p_C.FromNode, p_C.FromPort))
            .FirstOrDefault(p_S => p_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == p_S.FromNode)?.Kind is not ("StickerSideGate" or "TexCoord"));

        p_Graph.Nodes.RemoveAll(p_N => s_Doomed.Contains(p_N.Id));
        p_Graph.Connections.RemoveAll(p_C => s_Doomed.Contains(p_C.FromNode) || s_Doomed.Contains(p_C.ToNode));
        if (s_Outside.FromNode != null && !p_Graph.Connections.Any(p_C => p_C.ToNode == s_Root.Id && p_C.ToPort == "Diffuse"))
            p_Graph.Connections.Add(new GraphConnection { FromNode = s_Outside.FromNode, FromPort = s_Outside.FromPort, ToNode = s_Root.Id, ToPort = "Diffuse" });

        return true;
    }

    /// <summary>Whether a placement's picture is an animated GIF (a sheet and maps, not a texel of the layer).</summary>
    public static bool IsAnimated(string p_Image) => GifClip.IsGif(p_Image);

    /// <summary>
    /// Extends a graph that already samples its sticker layer with the ANIMATED sticker: two coordinate
    /// maps (x at register + 2, y at register + 3, one 8-bit channel each, read texel by texel, no mips) say
    /// which point of the GIF's frame a texel shows; a Flipbook node turns that point and the time into a
    /// UV on the frame sheet (register + 4); the frame's colour, premultiplied by its alpha, gated by the
    /// map's coverage and by the side gate, goes over the layer's result into the root's Diffuse. The
    /// Flipbook carries the sheet's geometry and the GIF's own timeline as parameters, so the compiled
    /// shader plays exactly that GIF. Made once; a later call only refreshes the parameters. False when
    /// the graph has no sticker layer to extend.
    /// </summary>
    public static bool EnsureAnimated(ShaderGraph p_Graph, GifAtlas p_Atlas, int p_MapSide, out string p_Note)
    {
        p_Note = "";
        if (p_Graph.StickerRegister is not { } s_Register)
            return false;

        // The GIF takes the coordinate map. A PROJECTED emblem slot reads none and STAYS, drawn over the GIF, its atlas where it always is
        // (GifKeepsEmblem: whatever happens, the emblem slot stays); a slot read from the map steps aside.
        var s_KeepEmblem = GifKeepsEmblem && EmblemSlot.Enabled;
        if (!s_KeepEmblem && TearDownEmblem(p_Graph))
            p_Note = "the emblem slot was taken off (an animated sticker uses the coordinate map it read); ";

        var s_TextureKinds = Palette.TextureKinds;
        // x in the ALPHA of the map (register + 1), y in its GREEN, the sheet after the emblem slot's atlas (SheetRegisterOf) — one texture
        // for the coordinates and the side (seven external textures draw in game — measured 2026-10-13, the cap feared on 2026-09-12 was the
        // bugs of those rounds), and DXT5's alpha is 8 bits a texel, its green 6.
        var s_MapURegister = (s_Register + 1).ToString();
        var s_SheetRegister = SheetRegisterOf(s_Register).ToString();
        // Several chains (see TearDownAnimation) are torn down first, and one is built fresh below.
        if (p_Graph.Nodes.Count(p_N => p_N.Kind == "Flipbook") > 1)
        {
            var s_Gone = TearDownAnimation(p_Graph);
            p_Note = $"{s_Gone} duplicated animation chains removed; ";
        }

        // A graph that already plays is brought to today's layout every time (idempotent): the first layout
        // had its sheet at the register this one uses, so the register alone cannot tell them apart.
        var s_Flipbook = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "Flipbook");
        var s_HasSheet = s_Flipbook != null && MigrateAnimated(p_Graph, s_Register, p_MapSide);

        if (s_Flipbook != null && s_HasSheet)
        {
            foreach (var (s_Name, s_Value) in p_Atlas.NodeParams())
                s_Flipbook.Params[s_Name] = s_Value;
            foreach (var s_Snap in p_Graph.Nodes.Where(p_N => p_N.Kind == "TexelSnap"))
                s_Snap.Params["Side"] = p_MapSide.ToString();
            foreach (var s_Reader in p_Graph.Nodes.Where(p_N => p_N.Kind == "StickerCoordinates"))
            {
                s_Reader.Params["Side"] = p_MapSide.ToString();
                s_Reader.Params["Register"] = s_MapURegister;
            }
            p_Graph.StickerAnimation = p_Atlas.Path;

            // (and the slot beside it — a document from before, whose GIF had taken it off, gets it back)
            if (s_KeepEmblem && EnsureEmblem(p_Graph, p_MapSide, out var s_Back) && s_Back.Length > 0)
                p_Note += s_Back;
            return true;
        }

        var s_Root = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "StandardRoot");
        var s_Layer = p_Graph.Nodes.FirstOrDefault(p_N => s_TextureKinds.Contains(p_N.Kind) && p_N.GetParam("Register") == s_Register.ToString());
        if (s_Root == null || s_Layer == null)
        {
            p_Note = "this graph has no sticker layer to animate.";
            return false;
        }

        var s_CoordSource = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Layer.Id && p_C.ToPort == "Coord");
        // (UNDER the emblem slot when the graph draws one: the slot stays over everything, the GIF included — the GIF goes where the slot
        // blends over, and its result back into the slot's "under"; otherwise over what feeds the root's Diffuse, as always)
        var s_UnderEmblem = s_KeepEmblem ? EmblemBlendsOver(p_Graph, s_Root) : null;
        var s_IntoDiffuse = s_UnderEmblem ?? p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Root.Id && p_C.ToPort == "Diffuse");
        var (s_OutNode, s_OutPort) = s_UnderEmblem != null ? (s_UnderEmblem.ToNode, s_UnderEmblem.ToPort) : (s_Root.Id, "Diffuse");
        var s_SideGate = EnsureGate(p_Graph, s_Layer, s_Register, p_MapSide);

        // The coordinate map (x of the GIF frame in the map's alpha, y in its green, at register + 1), read
        // at level 0 and interpolated between texels where the field is continuous — the frame's own
        // resolution, however coarse the map (see the StickerCoordinates node); 0 in both means "nothing here".
        var s_Coords = new GraphNode
        {
            Kind = "StickerCoordinates", X = s_Root.X - 700, Y = s_Root.Y + 380,
            Params = { ["Register"] = s_MapURegister, ["Side"] = p_MapSide.ToString(), ["XChannel"] = "w", ["YChannel"] = "y" },
            Comment = "Animated sticker — which point of the GIF frame each texel shows (the map's alpha = x, green = y); composed per weapon mesh",
        };
        var s_Book = new GraphNode
        {
            Kind = "Flipbook", X = s_Root.X - 440, Y = s_Root.Y + 380,
            Comment = "Animated sticker — the GIF's frames and timing, as the file has them",
        };
        foreach (var (s_Name, s_Value) in p_Atlas.NodeParams())
            s_Book.Params[s_Name] = s_Value;
        var s_Sheet = new GraphNode
        {
            Kind = "Texture", X = s_Root.X - 280, Y = s_Root.Y + 380,
            Params = { ["Register"] = s_SheetRegister, ["Tiling"] = "1,1" },
            Comment = "Animated sticker — frame sheet of the GIF; one per camo",
        };
        var s_Alpha = new GraphNode { Kind = "Swizzle", X = s_Root.X - 140, Y = s_Root.Y + 440, Params = { ["Channels"] = "wwww" } };
        var s_Covered = new GraphNode { Kind = "Multiply", X = s_Root.X - 20, Y = s_Root.Y + 500, Comment = "Animated sticker — alpha only where a coordinate map names a point" };
        var s_Gated = new GraphNode { Kind = "Multiply", X = s_Root.X + 60, Y = s_Root.Y + 560, Comment = "Animated sticker — and only on the side it was placed on" };
        var s_Colour = new GraphNode { Kind = "Swizzle", X = s_Root.X - 140, Y = s_Root.Y + 360, Params = { ["Channels"] = "xyz" } };
        var s_Premultiplied = new GraphNode { Kind = "Multiply", X = s_Root.X + 100, Y = s_Root.Y + 380 };
        var s_Keep = new GraphNode { Kind = "OneMinus", X = s_Root.X + 100, Y = s_Root.Y + 460 };
        var s_Under = new GraphNode { Kind = "Multiply", X = s_Root.X + 220, Y = s_Root.Y + 300 };
        var s_Blend = new GraphNode { Kind = "Add", X = s_Root.X + 340, Y = s_Root.Y + 280 };

        foreach (var s_Node in new[] { s_Coords, s_Book, s_Sheet, s_Alpha, s_Covered, s_Gated, s_Colour, s_Premultiplied, s_Keep, s_Under, s_Blend })
            p_Graph.Nodes.Add(s_Node);

        if (s_CoordSource != null)
            p_Graph.Connections.Add(new GraphConnection { FromNode = s_CoordSource.FromNode, FromPort = s_CoordSource.FromPort, ToNode = s_Coords.Id, ToPort = "Coord" });

        if (s_IntoDiffuse != null)
        {
            s_IntoDiffuse.ToNode = s_Under.Id;
            s_IntoDiffuse.ToPort = "Input1";
        }

        Wire(p_Graph, s_Coords, "Out", s_Book, "Coord");
        Wire(p_Graph, s_Book, "Out", s_Sheet, "Coord");
        Wire(p_Graph, s_Sheet, "Out", s_Alpha, "Input");
        Wire(p_Graph, s_Alpha, "Out", s_Covered, "Input1");
        Wire(p_Graph, s_Book, "Inside", s_Covered, "Input2");
        Wire(p_Graph, s_Covered, "Out", s_Gated, "Input1");
        Wire(p_Graph, s_SideGate, "Out", s_Gated, "Input2");
        Wire(p_Graph, s_Sheet, "Out", s_Colour, "Input");
        Wire(p_Graph, s_Colour, "Out", s_Premultiplied, "Input1");
        Wire(p_Graph, s_Gated, "Out", s_Premultiplied, "Input2");
        Wire(p_Graph, s_Gated, "Out", s_Keep, "Input");
        Wire(p_Graph, s_Keep, "Out", s_Under, "Input2");
        Wire(p_Graph, s_Under, "Out", s_Blend, "Input1");
        Wire(p_Graph, s_Premultiplied, "Out", s_Blend, "Input2");
        p_Graph.Connections.Add(new GraphConnection { FromNode = s_Blend.Id, FromPort = "Out", ToNode = s_OutNode, ToPort = s_OutPort });

        p_Graph.StickerAnimation = p_Atlas.Path;
        p_Note += $"the graph now plays an animated sticker: coordinates in the map at t{s_MapURegister} (alpha x, green y), frame sheet at t{s_SheetRegister} " +
                  $"({p_Atlas.Frames} frame(s), {p_Atlas.TotalSeconds:0.##} s per loop, {p_Atlas.Dimensions} sheet of {p_Atlas.Cells} cell(s) of {p_Atlas.CellWidth}×{p_Atlas.CellHeight})" +
                  (s_UnderEmblem != null ? ", under the emblem slot." : ".");

        // (the slot beside it: kept — or laid, on a graph whose GIF had taken it off — over the GIF)
        if (s_KeepEmblem && EnsureEmblem(p_Graph, p_MapSide, out var s_Slot) && s_Slot.Length > 0)
            p_Note += " " + s_Slot;
        return true;
    }

    /// <summary>
    /// Extends a graph that samples its sticker layer with the EMBLEM SLOT (<see cref="EmblemSlot"/>): the coordinate map at register + 1
    /// says which point of the emblem's square a texel shows (the slot's placements are composed into it the way an animated
    /// sticker's are), an EmblemLayers node draws the layers the engine feeds per weapon there — the shape atlas at register + 2,
    /// the layer constants right after the graph's own external constants in the same block — and its colour, kept to the side
    /// the slot was placed on, goes over the diffuse. Beside an animated sticker only when projected (GifKeepsEmblem: a slot read from the
    /// map would share the GIF's coordinate map).
    /// Made once; a later call refreshes the numbers. False when the graph has no sticker layer, or plays a GIF.
    /// </summary>
    public static bool EnsureEmblem(ShaderGraph p_Graph, int p_MapSide, out string p_Note)
    {
        p_Note = "";
        if (p_Graph.StickerRegister is not { } s_Register)
        {
            p_Note = "this graph has no sticker layer to put the emblem slot beside.";
            return false;
        }

        // (beside an animated sticker only a PROJECTED slot, which reads no coordinate map — GifKeepsEmblem)
        if (!GifKeepsEmblem && p_Graph.Nodes.Any(p_N => p_N.Kind == "Flipbook"))
        {
            p_Note = "this camo plays an animated sticker, which uses the coordinate map the emblem slot would need — no emblem slot.";
            return false;
        }

        var s_MapRegister = (s_Register + 1).ToString();
        var s_AtlasRegister = (s_Register + 2).ToString();

        // The layer constants join the block the graph's own external constants live in, RIGHT AFTER the last of them: the engine
        // packs a shader's external values into their buffer one after another in the order of its table, whatever their Index
        // (fb::DxShaderDispatcher / sub_697E50, measured 2026-09-29), and the bake appends the layers to the end of that table — so
        // the first layer is read where the preset's own constants end, and no gap may lie between (a fixed base at c32 would have
        // read every layer twenty registers off). Each graph of a camo has its own count (the no-camo twin's preset has fewer), and
        // the preview feeds each by its own layout. That buffer holds 64 registers: the preset's own plus the layers must fit.
        var s_Externals = p_Graph.Nodes
            .Where(p_N => p_N.Kind == "ExternalConstant" &&
                          (p_N.GetParam("Buffer") is not { Length: > 0 } s_B || s_B.Equals("externalConstants", StringComparison.OrdinalIgnoreCase)))
            .Select(p_N => (Register: int.TryParse(p_N.GetParam("Register"), out var s_R) ? s_R : 1,
                            Element: int.TryParse(p_N.GetParam("Element"), out var s_E) ? s_E : 0))
            .ToList();
        var s_Buffer = s_Externals.Count > 0 ? s_Externals[0].Register : 1;
        var s_Base = s_Externals.Where(p_E => p_E.Register == s_Buffer).Select(p_E => p_E.Element + 1).DefaultIfEmpty(0).Max();
        if (s_Base + EmblemSlot.Layers > EmblemSlot.EngineExternalRegisters)
        {
            p_Note = $"this preset reads {s_Base} external constants: with the emblem slot's {EmblemSlot.Layers} it would pass the " +
                     $"{EmblemSlot.EngineExternalRegisters} the engine holds for a shader — no emblem slot.";
            return false;
        }

        void Numbers(GraphNode p_Emblem)
        {
            p_Emblem.Params["Register"] = s_AtlasRegister;
            p_Emblem.Params["Buffer"] = s_Buffer.ToString();
            p_Emblem.Params["Base"] = s_Base.ToString();
            p_Emblem.Params["Layers"] = EmblemSlot.Layers.ToString();
            // the shape atlas the studio ships (and the bake binds): its layout, so a cell index names the same shape in both
            var s_Atlas = EmblemSlot.AtlasLayout;
            p_Emblem.Params["Grid"] = s_Atlas.Grid.ToString();
            p_Emblem.Params["Cell"] = s_Atlas.Cell.ToString();
            p_Emblem.Params["Inner"] = s_Atlas.Inner.ToString();
            p_Emblem.Params["Side"] = s_Atlas.Side.ToString();
            p_Emblem.Params["MaxLod"] = s_Atlas.MaxLod.ToString();
        }

        if (p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "EmblemLayers") is { } s_Known)
        {
            Numbers(s_Known);
            foreach (var s_Reader in p_Graph.Nodes.Where(p_N => p_N.Kind == "StickerCoordinates"))
            {
                s_Reader.Params["Register"] = s_MapRegister;
                s_Reader.Params["Side"] = p_MapSide.ToString();
            }

            if (EmblemSlot.Projected)
                ProjectEmblem(p_Graph, s_Known);
            return true;
        }

        var s_TextureKinds = Palette.TextureKinds;
        var s_Root = p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "StandardRoot");
        var s_Layer = p_Graph.Nodes.FirstOrDefault(p_N => s_TextureKinds.Contains(p_N.Kind) && p_N.GetParam("Register") == s_Register.ToString());
        if (s_Root == null || s_Layer == null)
        {
            p_Note = "this graph has no sticker layer to put the emblem slot beside.";
            return false;
        }

        var s_CoordSource = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Layer.Id && p_C.ToPort == "Coord");
        var s_IntoDiffuse = p_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == s_Root.Id && p_C.ToPort == "Diffuse");
        var s_SideGate = EnsureGate(p_Graph, s_Layer, s_Register, p_MapSide);

        var s_Coords = new GraphNode
        {
            Kind = "StickerCoordinates", X = s_Root.X - 700, Y = s_Root.Y + 640,
            Params = { ["Register"] = s_MapRegister, ["Side"] = p_MapSide.ToString(), ["XChannel"] = "w", ["YChannel"] = "y" },
            Comment = "Emblem slot — which point of the emblem's square each texel shows (the map's alpha = x, green = y); composed per weapon mesh",
        };
        var s_Emblem = new GraphNode
        {
            Kind = "EmblemLayers", X = s_Root.X - 440, Y = s_Root.Y + 640,
            Comment = "Emblem slot — the owner's BF4 emblem, fed per weapon by the engine (all zero = nothing drawn)",
        };
        Numbers(s_Emblem);
        var s_InsideGate = new GraphNode { Kind = "Multiply", X = s_Root.X - 220, Y = s_Root.Y + 760, Comment = "Emblem slot — only inside the slot and on the side it was placed on" };
        var s_Alpha = new GraphNode { Kind = "Swizzle", X = s_Root.X - 220, Y = s_Root.Y + 700, Params = { ["Channels"] = "wwww" } };
        var s_AlphaGated = new GraphNode { Kind = "Multiply", X = s_Root.X - 80, Y = s_Root.Y + 720 };
        var s_Colour = new GraphNode { Kind = "Swizzle", X = s_Root.X - 220, Y = s_Root.Y + 620, Params = { ["Channels"] = "xyz" } };
        var s_ColourGated = new GraphNode { Kind = "Multiply", X = s_Root.X - 80, Y = s_Root.Y + 620 };
        var s_Keep = new GraphNode { Kind = "OneMinus", X = s_Root.X + 60, Y = s_Root.Y + 720 };
        var s_Under = new GraphNode { Kind = "Multiply", X = s_Root.X + 180, Y = s_Root.Y + 560 };
        var s_Blend = new GraphNode { Kind = "Add", X = s_Root.X + 300, Y = s_Root.Y + 540, Comment = "Emblem slot — over everything below it (camo, stickers)" };

        foreach (var s_Node in new[] { s_Coords, s_Emblem, s_InsideGate, s_Alpha, s_AlphaGated, s_Colour, s_ColourGated, s_Keep, s_Under, s_Blend })
            p_Graph.Nodes.Add(s_Node);

        if (s_CoordSource != null)
            p_Graph.Connections.Add(new GraphConnection { FromNode = s_CoordSource.FromNode, FromPort = s_CoordSource.FromPort, ToNode = s_Coords.Id, ToPort = "Coord" });

        if (s_IntoDiffuse != null)
        {
            s_IntoDiffuse.ToNode = s_Under.Id;
            s_IntoDiffuse.ToPort = "Input1";
        }

        Wire(p_Graph, s_Coords, "Out", s_Emblem, "Coord");
        Wire(p_Graph, s_Emblem, "Inside", s_InsideGate, "Input1");
        Wire(p_Graph, s_SideGate, "Out", s_InsideGate, "Input2");
        Wire(p_Graph, s_Emblem, "Out", s_Alpha, "Input");
        Wire(p_Graph, s_Alpha, "Out", s_AlphaGated, "Input1");
        Wire(p_Graph, s_InsideGate, "Out", s_AlphaGated, "Input2");
        Wire(p_Graph, s_Emblem, "Out", s_Colour, "Input");
        Wire(p_Graph, s_Colour, "Out", s_ColourGated, "Input1");
        Wire(p_Graph, s_InsideGate, "Out", s_ColourGated, "Input2");
        Wire(p_Graph, s_AlphaGated, "Out", s_Keep, "Input");
        Wire(p_Graph, s_Keep, "Out", s_Under, "Input2");
        Wire(p_Graph, s_Under, "Out", s_Blend, "Input1");
        Wire(p_Graph, s_ColourGated, "Out", s_Blend, "Input2");
        Wire(p_Graph, s_Blend, "Out", s_Root, "Diffuse");

        p_Note = $"the graph now draws the emblem slot: coordinates in the map at t{s_MapRegister}, shape atlas at t{s_AtlasRegister}, " +
                 $"{EmblemSlot.Layers} layers as external constants c{s_Base}..c{s_Base + EmblemSlot.Layers - 1} of block b{s_Buffer}.";
        if (EmblemSlot.Projected && ProjectEmblem(p_Graph, s_Emblem))
            p_Note += " The slot is PROJECTED from the mesh position (the vertex shader hands it over), not read from the map.";
        return true;
    }

    /// <summary>
    /// Turns a graph's emblem slot from the coordinate map to the PROJECTION (EmblemSlot.Projected: anywhere on the weapon):
    /// a Mesh Position node feeds an Emblem Projection whose squares (frames in the mesh's own space: constants right after the layers,
    /// filled per weapon by its variation) give the layers their coordinate, and the projection's own "inside" replaces the side gate —
    /// a projected square is kept off the far side by its facing, not by the unwrap's hand. Made once (the map nodes stay, unwired); a
    /// later call refreshes where its constants sit. False when it already is, or when no square fits the engine's registers.
    /// </summary>
    private static bool ProjectEmblem(ShaderGraph p_Graph, GraphNode p_Emblem)
    {
        // the frames right after the layers, in the same block, as many squares as the engine's registers leave room for
        var s_Base = (int.TryParse(p_Emblem.GetParam("Base"), out var s_B) ? s_B : 12) +
                     (int.TryParse(p_Emblem.GetParam("Layers"), out var s_L) ? s_L : EmblemSlot.Layers);
        var s_Squares = Math.Min(EmblemSlot.ProjectedSquares, (EmblemSlot.EngineExternalRegisters - s_Base) / EmblemSlot.FrameRegisters);
        void Numbers(GraphNode p_Projection)
        {
            p_Projection.Params["Buffer"] = p_Emblem.GetParam("Buffer") is { Length: > 0 } s_Buffer ? s_Buffer : "1";
            p_Projection.Params["Base"] = s_Base.ToString();
            p_Projection.Params["Squares"] = Math.Max(0, s_Squares).ToString();
            p_Projection.Params.Remove("Slots");
        }

        if (p_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "EmblemProjection") is { } s_Known)
        {
            Numbers(s_Known);
            return false;
        }

        if (s_Squares < 1)
            return false;

        var s_Position = new GraphNode { Kind = "MeshPosition", X = p_Emblem.X - 520, Y = p_Emblem.Y + 120,
            Comment = "Emblem slot — the point's position in the weapon's own space (from the patched vertex shader)" };
        var s_Projection = new GraphNode { Kind = "EmblemProjection", X = p_Emblem.X - 260, Y = p_Emblem.Y + 120,
            Comment = "Emblem slot — the square projected from the mesh position: runs across the unwrap's seams and mirrored halves; " +
                      "its frames are constants each weapon's variation fills" };
        Numbers(s_Projection);
        p_Graph.Nodes.Add(s_Position);
        p_Graph.Nodes.Add(s_Projection);
        Wire(p_Graph, s_Position, "Out", s_Projection, "MeshPos");

        // the layers' coordinate: the projection's instead of the map's
        p_Graph.Connections.RemoveAll(p_C => p_C.ToNode == p_Emblem.Id && p_C.ToPort == "Coord");
        Wire(p_Graph, s_Projection, "Coord", p_Emblem, "Coord");

        // the gate beside the layers' own "inside" (the side map's hand): the projection's inside instead
        var s_Gate = p_Graph.Connections.FirstOrDefault(p_C => p_C.FromNode == p_Emblem.Id && p_C.FromPort == "Inside");
        if (s_Gate != null)
        {
            var s_Other = s_Gate.ToPort == "Input1" ? "Input2" : "Input1";
            p_Graph.Connections.RemoveAll(p_C => p_C.ToNode == s_Gate.ToNode && p_C.ToPort == s_Other);
            p_Graph.Connections.Add(new GraphConnection { FromNode = s_Projection.Id, FromPort = "Inside", ToNode = s_Gate.ToNode, ToPort = s_Other });
        }

        return true;
    }

    /// <summary>Whether a graph's emblem slot is projected (it has an Emblem Projection node).</summary>
    public static bool IsProjected(ShaderGraph p_Graph) => p_Graph.Nodes.Any(p_N => p_N.Kind == "EmblemProjection");
}
