using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor.View;

public class GraphCanvas : GridCanvas
{
    private const double c_NodeWidth = 172;
    private const double c_RowHeight = 16;
    private const double c_TitleHeight = 18;
    private const double c_PortSize = 8;
    private const double c_TopPadding = 4;
    private const double c_PreviewSize = 136;
    private const int c_UndoDepth = 100;

    /// <summary>Marks our own clipboard payload so a paste never tries to read unrelated text.</summary>
    private const string c_ClipboardTag = "RimeShaderEditorGraph/1\n";

    private sealed class PortHit
    {
        public required GraphNode Node { get; init; }
        public required string Port { get; init; }
        public required bool IsInput { get; init; }
        public required Point Anchor { get; init; }
    }

    private ShaderGraph m_Graph = new();
    private readonly HashSet<string> m_Selected = new();
    private string? m_Primary;
    private GraphNode? m_Dragging;
    private Vector m_DragGrab;
    private Dictionary<string, Point>? m_DragStart;

    // ── comment boxes ────────────────────────────────────────────────────────────────────────────────────
    private const double c_GroupTitleHeight = 22;
    private const double c_GroupResizeMargin = 8;
    private const double c_GroupMinSize = 80;
    private const double c_CommentGap = 8;

    private static readonly Brush s_BadgeBrush = new SolidColorBrush(Color.FromArgb(215, 255, 255, 235));

    private string? m_SelectedGroup;
    private GraphGroup? m_GroupDragging;
    private Vector m_GroupGrab;
    private Dictionary<string, Point>? m_GroupContents;
    private GraphGroup? m_GroupResizing;
    private bool m_ResizeLeft, m_ResizeRight, m_ResizeTop, m_ResizeBottom;
    private Rect m_ResizeStart;

    /// <summary>
    /// The comment palette. Indexed, not stored as colours, so files stay valid if the shades are tuned.
    /// </summary>
    private static readonly Color[] s_GroupColours =
    {
        Color.FromRgb(90, 110, 160),   // slate blue
        Color.FromRgb(90, 140, 90),    // green
        Color.FromRgb(150, 120, 70),   // amber
        Color.FromRgb(140, 85, 85),    // brick
        Color.FromRgb(120, 90, 140),   // violet
        Color.FromRgb(80, 130, 140),   // teal
        Color.FromRgb(110, 110, 114),  // grey
    };
    private PortHit? m_WireFrom;
    private Point m_WireCursor;
    private Point? m_BandOrigin;
    private Point m_BandCursor;
    private bool m_BandAdditive;
    private readonly Dictionary<string, FormattedText> m_TextCache = new();
    private readonly List<string> m_Undo = new();
    private readonly List<string> m_Redo = new();

    public event EventHandler? SelectionChanged;
    public event EventHandler? GraphChanged;

    /// <summary>Breaks the wire into a port — what ALT+click on a wire does, callable by a driver.</summary>
    public void BreakWire(string p_ToNode, string p_ToPort)
    {
        PushUndo();
        m_Graph.Disconnect(p_ToNode, p_ToPort);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Raised by a right-CLICK (not a right-drag, which still pans); carries the world point.</summary>
    public event EventHandler<Point>? SearchRequested;

    /// <summary>
    /// Hold-a-letter-and-click placement, following the Unreal material editor's own letters where we have an
    /// equivalent node. L is safe here even though the preview uses L for its light: that is a different panel.
    /// The letter half is remappable from Settings; the number aliases for Scalar/Color are fixed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Key> DefaultShortcuts = new Dictionary<string, Key>
    {
        ["Add"] = Key.A,
        ["Divide"] = Key.D,
        ["Power"] = Key.E,
        ["Lerp"] = Key.L,
        ["Multiply"] = Key.M,
        ["Normalize"] = Key.N,
        ["OneMinus"] = Key.O,
        ["Panner"] = Key.P,
        ["Scalar"] = Key.S,
        ["Texture"] = Key.T,
        ["TexCoord"] = Key.U,
        ["Color"] = Key.V,
    };

    private static Dictionary<Key, string> s_Shortcuts = BuildShortcuts(null);

    /// <summary>
    /// Whether the canvas is authoring in the UDK vocabulary. Static like the shortcut table and the zoom
    /// speed, and set from the same place the palette is rebuilt, so the two can never disagree.
    /// </summary>
    public static bool UdkStyle { get; set; }

    private static Dictionary<Key, string> BuildShortcuts(IReadOnlyDictionary<string, string>? p_Overrides)
    {
        var s_Table = new Dictionary<Key, string>();

        foreach (var (s_Kind, s_Default) in DefaultShortcuts)
        {
            var s_Key = s_Default;
            if (p_Overrides != null && p_Overrides.TryGetValue(s_Kind, out var s_Name) &&
                Enum.TryParse<Key>(s_Name, true, out var s_Parsed))
                s_Key = s_Parsed;

            // Last assignment wins on a collision; the Settings window refuses to create one, so this only
            // decides what a hand-edited file does — and silently dropping a node's shortcut would be worse.
            s_Table[s_Key] = s_Kind;
        }

        s_Table[Key.D1] = "Scalar";
        s_Table[Key.D4] = "Color";
        s_Table[Key.NumPad1] = "Scalar";
        s_Table[Key.NumPad4] = "Color";
        return s_Table;
    }

    /// <summary>Rebuilds the placement table from Settings overrides (kind -> key name).</summary>
    public static void ApplyShortcuts(IReadOnlyDictionary<string, string>? p_Overrides) =>
        s_Shortcuts = BuildShortcuts(p_Overrides);

    public static IEnumerable<(Key Key, string Kind)> Shortcuts =>
        s_Shortcuts.Select(p_Pair => (p_Pair.Key, p_Pair.Value));

    private Point? m_RightDownScreen;

    public Func<string, ImageSource?>? TexturePreviewProvider { get; set; }

    public ShaderGraph Graph
    {
        get => m_Graph;
        set
        {
            m_Graph = value;
            m_Selected.Clear();
            m_Primary = null;
            m_Dragging = null;
            m_WireFrom = null;
            StopEdgePan();
            m_BandOrigin = null;
            m_SelectedGroup = null;
            m_GroupDragging = null;
            m_GroupResizing = null;
            m_GroupContents = null;
            m_Undo.Clear();
            m_Redo.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    /// <summary>The node whose properties are shown — the last one clicked.</summary>
    public GraphNode? Selected => m_Primary == null ? null : m_Graph.FindNode(m_Primary);

    public int SelectedCount => m_Selected.Count;

    /// <summary>How many root nodes the last paste refused, so the window can say so instead of losing them quietly.</summary>
    public int SkippedRoots { get; private set; }
    public bool CanUndo => m_Undo.Count > 0;
    public bool CanRedo => m_Redo.Count > 0;

    public GraphCanvas()
    {
        Focusable = true;
        AllowDrop = true;
    }

    public void PushUndo()
    {
        m_Undo.Add(m_Graph.ToJson());
        if (m_Undo.Count > c_UndoDepth)
            m_Undo.RemoveAt(0);

        m_Redo.Clear();
    }

    /// <summary>
    /// The undo history as data, so a TAB can carry its own: the Graph setter clears the stacks (right for a
    /// fresh document), and switching tabs must restore the leaving tab's history instead of losing it.
    /// </summary>
    public (string[] Undo, string[] Redo) SnapshotUndo() => (m_Undo.ToArray(), m_Redo.ToArray());

    public void RestoreUndo(string[] p_Undo, string[] p_Redo)
    {
        m_Undo.Clear();
        m_Undo.AddRange(p_Undo);
        m_Redo.Clear();
        m_Redo.AddRange(p_Redo);
    }

    public bool Undo() => Restore(m_Undo, m_Redo);
    public bool Redo() => Restore(m_Redo, m_Undo);

    private bool Restore(List<string> p_From, List<string> p_To)
    {
        if (p_From.Count == 0)
            return false;

        p_To.Add(m_Graph.ToJson());
        var s_Json = p_From[^1];
        p_From.RemoveAt(p_From.Count - 1);

        m_Graph = ShaderGraph.FromJson(s_Json);
        m_Selected.RemoveWhere(p_Id => m_Graph.FindNode(p_Id) == null);
        if (m_Primary != null && m_Graph.FindNode(m_Primary) == null)
            m_Primary = null;

        m_Dragging = null;
        m_WireFrom = null;
        StopEdgePan();
        m_BandOrigin = null;

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    public void AddNode(string p_Kind) =>
        AddNodeAt(p_Kind, TransformPoint(new Point(ActualWidth * 0.5, ActualHeight * 0.5)));

    public void AddNodeAt(string p_Kind, Point p_World)
    {
        if (!Palette.Has(p_Kind))
            return;

        PushUndo();

        var s_Node = new GraphNode
        {
            Kind = p_Kind,
            X = Math.Round(p_World.X / 8) * 8,
            Y = Math.Round(p_World.Y / 8) * 8,
        };

        // A NEW texture node gets the first register no other texture node uses, so it starts BLANK and
        // independent — with the palette default every new node aliased register 1 and silently shared the
        // first node's texture. Sharing stays available on purpose: set two nodes to the same register and
        // they sample the same texture, which is the engine's actual "texture parameter" mechanism.
        if (Palette.TextureKinds.Contains(p_Kind))
        {
            var s_Used = m_Graph.Nodes
                .Where(p_N => Palette.TextureKinds.Contains(p_N.Kind))
                .Select(p_N => int.TryParse(p_N.GetParam("Register"), out var s_R) ? s_R : -1)
                .Where(p_R => p_R >= 0)
                .ToHashSet();

            var s_Free = 1;
            while (s_Used.Contains(s_Free))
                s_Free++;

            s_Node.Params["Register"] = s_Free.ToString();
        }

        m_Graph.Nodes.Add(s_Node);
        SelectOnly(s_Node.Id);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void DeleteSelected()
    {
        // With no nodes selected, a selected comment box deletes ALONE - its nodes are organised by it, not
        // owned by it. Shared by the Delete key and the toolbar button, so both behave the same.
        if (m_Selected.Count == 0)
        {
            if (m_SelectedGroup == null)
                return;

            var s_Group = m_Graph.Groups.Find(p_G => p_G.Id == m_SelectedGroup);
            if (s_Group == null)
                return;

            PushUndo();
            m_Graph.Groups.Remove(s_Group);
            m_SelectedGroup = null;
            GraphChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        PushUndo();
        foreach (var s_Id in m_Selected.ToList())
            m_Graph.RemoveNode(s_Id);

        m_Selected.Clear();
        m_Primary = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    /// <summary>Selects one node alone — what a click on it does — so a driver can open its properties.</summary>
    public void SelectNode(string p_Id)
    {
        SelectOnly(p_Id);
        InvalidateVisual();
    }

    private void SelectOnly(string p_Id)
    {
        m_Selected.Clear();
        m_Selected.Add(p_Id);
        m_Primary = p_Id;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Copies the selected nodes plus only the wires whose BOTH ends are selected — a dangling wire could not
    /// be reconnected on paste and would be dropped silently anyway.
    /// </summary>
    public bool CopySelection()
    {
        if (m_Selected.Count == 0 && m_SelectedGroup == null)
            return false;

        var s_Slice = new ShaderGraph { Name = "clipboard", TargetShader = m_Graph.TargetShader };
        foreach (var s_Id in m_Selected)
        {
            var s_Node = m_Graph.FindNode(s_Id);
            if (s_Node != null)
                s_Slice.Nodes.Add(s_Node);
        }

        foreach (var s_Connection in m_Graph.Connections)
            if (m_Selected.Contains(s_Connection.FromNode) && m_Selected.Contains(s_Connection.ToNode))
                s_Slice.Connections.Add(s_Connection);

        // Comment boxes ride along when everything inside them is part of the selection, plus an empty box
        // that is itself the selected thing - the same slice every graph editor's copy takes.
        foreach (var s_Group in m_Graph.Groups)
        {
            var s_Inside = NodesInsideGroup(s_Group).ToList();
            if ((s_Inside.Count > 0 && s_Inside.All(p_N => m_Selected.Contains(p_N.Id))) ||
                (s_Inside.Count == 0 && s_Group.Id == m_SelectedGroup))
                s_Slice.Groups.Add(s_Group);
        }

        try
        {
            Clipboard.SetText(c_ClipboardTag + s_Slice.ToJson());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool PasteSelection()
    {
        string s_Text;
        try
        {
            s_Text = Clipboard.GetText();
        }
        catch
        {
            return false;
        }

        if (!s_Text.StartsWith(c_ClipboardTag, StringComparison.Ordinal))
            return false;

        ShaderGraph s_Slice;
        try
        {
            s_Slice = ShaderGraph.FromJson(s_Text.Substring(c_ClipboardTag.Length));
        }
        catch
        {
            return false;
        }

        if (s_Slice.Nodes.Count == 0 && s_Slice.Groups.Count == 0)
            return false;

        PushUndo();

        // Fresh ids, and the old->new map so the copied wires can be rebuilt between the new nodes.
        var s_IdMap = new Dictionary<string, string>();
        var s_Pasted = new List<string>();
        var s_HasRoot = m_Graph.Nodes.Any(p_N => p_N.Def.IsRoot);
        SkippedRoots = 0;

        foreach (var s_Node in s_Slice.Nodes)
        {
            // A second root would make the emitted target set ambiguous, so it is dropped rather than pasted
            // into a graph that already has one.
            if (s_Node.Def.IsRoot && s_HasRoot)
            {
                SkippedRoots++;
                continue;
            }

            if (s_Node.Def.IsRoot)
                s_HasRoot = true;

            var s_Clone = new GraphNode
            {
                Kind = s_Node.Kind,
                X = s_Node.X + 24,
                Y = s_Node.Y + 24,
                Comment = s_Node.Comment,
                Params = new Dictionary<string, string>(s_Node.Params),
            };

            s_IdMap[s_Node.Id] = s_Clone.Id;
            m_Graph.Nodes.Add(s_Clone);
            s_Pasted.Add(s_Clone.Id);
        }

        foreach (var s_Connection in s_Slice.Connections)
        {
            if (s_IdMap.TryGetValue(s_Connection.FromNode, out var s_From) &&
                s_IdMap.TryGetValue(s_Connection.ToNode, out var s_To))
                m_Graph.Connect(s_From, s_Connection.FromPort, s_To, s_Connection.ToPort);
        }

        // Copied comment boxes land with the same offset as their nodes, so the box still wraps them.
        foreach (var s_Group in s_Slice.Groups)
            m_Graph.Groups.Add(new GraphGroup
            {
                Title = s_Group.Title,
                Colour = s_Group.Colour,
                X = s_Group.X + 24,
                Y = s_Group.Y + 24,
                Width = s_Group.Width,
                Height = s_Group.Height,
            });

        m_Selected.Clear();
        foreach (var s_Id in s_Pasted)
            m_Selected.Add(s_Id);

        m_Primary = s_Pasted.Count > 0 ? s_Pasted[^1] : null;

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private static bool HasPreview(GraphNode p_Node) => p_Node.Def.HasTexturePreview;

    private static double NodeHeight(GraphNode p_Node)
    {
        var s_Rows = Math.Max(p_Node.Def.Inputs.Count, p_Node.Def.Outputs.Count);
        var s_Height = c_TopPadding + s_Rows * c_RowHeight + c_TitleHeight + c_TopPadding;
        return HasPreview(p_Node) ? s_Height + c_PreviewSize + c_TopPadding : s_Height;
    }

    /// <summary>
    /// Lays a generated graph out in dataflow order: one column per depth, stacked by the node's REAL height.
    ///
    /// The translator used to drop nodes on a fixed 240x130 grid, which overlapped badly the moment a texture
    /// node appeared - those carry a 136-pixel preview, so they are three times taller than a Multiply and the
    /// rows collided. The nodes came out stacked, and they had to be dragged apart before anything could be
    /// read. A generated graph nobody can read is only half delivered.
    /// </summary>
    public static void AutoLayout(ShaderGraph p_Graph)
    {
        const double c_ColumnGap = 90;
        const double c_RowGap = 26;

        var s_Producers = p_Graph.Nodes.ToDictionary(p_N => p_N.Id, _ => new List<string>());
        foreach (var s_Connection in p_Graph.Connections)
            if (s_Producers.TryGetValue(s_Connection.ToNode, out var s_List))
                s_List.Add(s_Connection.FromNode);

        // Depth = longest path from a source. Longest rather than shortest so a node never sits left of
        // something it consumes, which is what makes the wires readable.
        var s_Depth = new Dictionary<string, int>(StringComparer.Ordinal);

        int Depth(string p_Id, int p_Guard)
        {
            if (s_Depth.TryGetValue(p_Id, out var s_Known))
                return s_Known;

            if (p_Guard > 256 || !s_Producers.TryGetValue(p_Id, out var s_Inputs) || s_Inputs.Count == 0)
                return s_Depth[p_Id] = 0;

            // Marked before recursing so a cycle - which a well-formed graph cannot have, but a corrupted file
            // could - terminates instead of overflowing the stack.
            s_Depth[p_Id] = 0;
            var s_Max = 0;
            foreach (var s_Input in s_Inputs)
                s_Max = Math.Max(s_Max, Depth(s_Input, p_Guard + 1) + 1);

            return s_Depth[p_Id] = s_Max;
        }

        foreach (var s_Node in p_Graph.Nodes)
            Depth(s_Node.Id, 0);

        var s_X = 40.0;
        foreach (var s_Column in p_Graph.Nodes.GroupBy(p_N => s_Depth[p_N.Id]).OrderBy(p_G => p_G.Key))
        {
            var s_Y = 40.0;
            foreach (var s_Node in s_Column)
            {
                s_Node.X = s_X;
                s_Node.Y = s_Y;
                s_Y += NodeHeight(s_Node) + c_RowGap;
            }

            s_X += c_NodeWidth + c_ColumnGap;
        }
    }

    /// <summary>
    /// Pans and zooms so the whole graph is on screen. A generated graph lands wherever the layout put it, which
    /// is rarely where the view happens to be looking - so without this the first thing after pressing Translate
    /// is hunting for your own graph.
    /// </summary>
    public void FrameGraph() => FrameGraph(ActualWidth, ActualHeight);

    /// <summary>
    /// Size-explicit variant for headless rendering, where the framing must be applied BETWEEN Measure and
    /// Arrange: Measure resets the viewport to centre, and Arrange records the retained drawing - anything set
    /// after that only lands on screen once the dispatcher pumps, which a bitmap capture never waits for.
    /// </summary>
    public void FrameGraph(double p_Width, double p_Height)
    {
        if (Graph.Nodes.Count == 0 || p_Width < 1 || p_Height < 1)
            return;

        var s_Left = Graph.Nodes.Min(p_N => p_N.X);
        var s_Top = Graph.Nodes.Min(p_N => p_N.Y);
        var s_Right = Graph.Nodes.Max(p_N => p_N.X + c_NodeWidth);
        var s_Bottom = Graph.Nodes.Max(p_N => p_N.Y + NodeHeight(p_N));

        // Comment boxes count as content: framing by nodes alone cropped a box's title off the screen.
        foreach (var s_Group in Graph.Groups)
        {
            s_Left = Math.Min(s_Left, s_Group.X);
            s_Top = Math.Min(s_Top, s_Group.Y);
            s_Right = Math.Max(s_Right, s_Group.X + s_Group.Width);
            s_Bottom = Math.Max(s_Bottom, s_Group.Y + s_Group.Height);
        }

        // And so do comment bubbles, which float above their node.
        foreach (var s_Node in Graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(s_Node.Comment))
                continue;

            var s_Bubble = CommentBubbleRect(s_Node);
            s_Left = Math.Min(s_Left, s_Bubble.X);
            s_Top = Math.Min(s_Top, s_Bubble.Y);
            s_Right = Math.Max(s_Right, s_Bubble.Right);
        }

        const double c_Margin = 40;
        var s_Scale = Math.Min((p_Width - c_Margin * 2) / Math.Max(1, s_Right - s_Left),
            (p_Height - c_Margin * 2) / Math.Max(1, s_Bottom - s_Top));

        // Never zoom IN past 1:1 - a four-node graph blown up to fill the window reads as broken, not as helpful.
        m_Scale = Math.Clamp(s_Scale, 0.15, 1.0);
        m_ScaleLevel = m_Scale;
        m_Viewport.X = (p_Width - (s_Right - s_Left) * m_Scale) * 0.5 - s_Left * m_Scale;
        m_Viewport.Y = (p_Height - (s_Bottom - s_Top) * m_Scale) * 0.5 - s_Top * m_Scale;
        InvalidateVisual();
    }

    /// <summary>
    /// Where a graph-space rect lands on screen under the current pan/zoom. Exists so a headless check can
    /// prove the framing actually reached the pixels instead of trusting the fields.
    /// </summary>
    public Rect ScreenRectOf(Rect p_GraphRect) => new(
        p_GraphRect.X * m_Scale + m_Viewport.X, p_GraphRect.Y * m_Scale + m_Viewport.Y,
        p_GraphRect.Width * m_Scale, p_GraphRect.Height * m_Scale);

    /// <summary>
    /// Points the view at a graph-space rect at 1:1. The headless probes use it to inspect a small detail at a
    /// scale where pixels can still discriminate - a probe on a 3-pixel-tall shape measures anti-aliasing, not
    /// the paint. Same Measure-then-view-then-Arrange rule as FrameGraph(w,h).
    /// </summary>
    public void LookAt(Rect p_GraphRect, double p_Width, double p_Height)
    {
        m_Scale = 1.0;
        m_ScaleLevel = 1.0;
        m_Viewport.X = p_Width * 0.5 - (p_GraphRect.X + p_GraphRect.Width * 0.5);
        m_Viewport.Y = p_Height * 0.5 - (p_GraphRect.Y + p_GraphRect.Height * 0.5);
        InvalidateVisual();
    }

    private static Rect NodeRect(GraphNode p_Node) =>
        new(p_Node.X, p_Node.Y, c_NodeWidth, NodeHeight(p_Node));

    private static Rect PreviewRect(GraphNode p_Node)
    {
        var s_Rows = Math.Max(p_Node.Def.Inputs.Count, p_Node.Def.Outputs.Count);
        var s_Top = p_Node.Y + c_TopPadding + s_Rows * c_RowHeight;
        return new Rect(p_Node.X + (c_NodeWidth - c_PreviewSize) * 0.5, s_Top, c_PreviewSize, c_PreviewSize);
    }

    private static Point InputAnchor(GraphNode p_Node, int p_Index) =>
        new(p_Node.X, p_Node.Y + c_TopPadding + p_Index * c_RowHeight + c_RowHeight * 0.5);

    private static Point OutputAnchor(GraphNode p_Node, int p_Index) =>
        new(p_Node.X + c_NodeWidth, p_Node.Y + c_TopPadding + p_Index * c_RowHeight + c_RowHeight * 0.5);

    private static Brush CategoryBrush(string p_Category) => p_Category switch
    {
        "Inputs" => new SolidColorBrush(Color.FromRgb(64, 132, 72)),
        "Textures" => new SolidColorBrush(Color.FromRgb(40, 104, 56)),
        "Constants" => new SolidColorBrush(Color.FromRgb(104, 104, 108)),
        "Output" => new SolidColorBrush(Color.FromRgb(176, 152, 40)),
        "UV" => new SolidColorBrush(Color.FromRgb(120, 84, 148)),
        "Vector" => new SolidColorBrush(Color.FromRgb(44, 112, 132)),
        _ => new SolidColorBrush(Color.FromRgb(52, 96, 148)),
    };

    private FormattedText Text(string p_Text, double p_Size, Brush p_Brush)
    {
        var s_Key = $"{p_Size}|{p_Brush}|{p_Text}";
        if (m_TextCache.TryGetValue(s_Key, out var s_Cached))
            return s_Cached;

        var s_Text = new FormattedText(p_Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), p_Size, p_Brush, 1.0);

        m_TextCache[s_Key] = s_Text;
        return s_Text;
    }

    protected override void Render(DrawingContext p_Context)
    {
        p_Context.PushTransform(GetWorldMatrix());

        // Comment boxes go first: they live BEHIND everything, the way a highlighter mark sits behind ink.
        foreach (var s_Group in m_Graph.Groups)
            RenderGroup(p_Context, s_Group);

        foreach (var s_Connection in m_Graph.Connections)
            RenderWire(p_Context, s_Connection);

        if (m_WireFrom != null)
            DrawCurve(p_Context, m_WireFrom.IsInput ? m_WireCursor : m_WireFrom.Anchor,
                m_WireFrom.IsInput ? m_WireFrom.Anchor : m_WireCursor,
                new Pen(new SolidColorBrush(Color.FromRgb(232, 200, 64)), 1.4));

        foreach (var s_Node in m_Graph.Nodes)
            RenderNode(p_Context, s_Node);

        if (m_BandOrigin.HasValue)
        {
            var s_Rect = BandRect();
            var s_Pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 208, 64)), 1.0 / Math.Max(m_Scale, 0.01))
            {
                DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
            };

            p_Context.DrawRectangle(new SolidColorBrush(Color.FromArgb(36, 255, 208, 64)), s_Pen, s_Rect);
        }

        p_Context.Pop();
    }

    private static Rect GroupRect(GraphGroup p_Group) => new(p_Group.X, p_Group.Y, p_Group.Width, p_Group.Height);

    private static Rect GroupTitleRect(GraphGroup p_Group) =>
        new(p_Group.X, p_Group.Y, p_Group.Width, c_GroupTitleHeight);

    private static Color GroupColour(GraphGroup p_Group) =>
        s_GroupColours[Math.Abs(p_Group.Colour) % s_GroupColours.Length];

    private void RenderGroup(DrawingContext p_Context, GraphGroup p_Group)
    {
        var s_Rect = GroupRect(p_Group);
        var s_Colour = GroupColour(p_Group);
        var s_IsSelected = p_Group.Id == m_SelectedGroup;

        // A translucent body so wires and grid stay readable through it, and an opaque title bar so the label
        // reads at any zoom - the same shape a comment box has in every node editor an author already knows.
        p_Context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(34, s_Colour.R, s_Colour.G, s_Colour.B)),
            new Pen(new SolidColorBrush(s_IsSelected ? Color.FromRgb(255, 208, 64) : s_Colour),
                s_IsSelected ? 2.0 : 1.2),
            s_Rect, 4, 4);

        p_Context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(210, s_Colour.R, s_Colour.G, s_Colour.B)), null,
            new Rect(s_Rect.X, s_Rect.Y, s_Rect.Width, c_GroupTitleHeight), 4, 4);

        var s_Title = Text(p_Group.Title, 12, Brushes.White);
        p_Context.PushClip(new RectangleGeometry(GroupTitleRect(p_Group)));
        p_Context.DrawText(s_Title, new Point(s_Rect.X + 8, s_Rect.Y + (c_GroupTitleHeight - s_Title.Height) * 0.5));
        p_Context.Pop();
    }

    private GraphGroup? HitGroupTitle(Point p_World)
    {
        for (var i = m_Graph.Groups.Count - 1; i >= 0; i--)
            if (GroupTitleRect(m_Graph.Groups[i]).Contains(p_World))
                return m_Graph.Groups[i];

        return null;
    }

    private GraphGroup? HitGroupResize(Point p_World, out bool p_Left, out bool p_Right, out bool p_Top,
        out bool p_Bottom)
    {
        p_Left = p_Right = p_Top = p_Bottom = false;
        var s_Margin = c_GroupResizeMargin / Math.Max(m_Scale, 0.1);

        for (var i = m_Graph.Groups.Count - 1; i >= 0; i--)
        {
            var s_Group = m_Graph.Groups[i];
            var s_Rect = GroupRect(s_Group);
            var s_Outer = Rect.Inflate(s_Rect, s_Margin, s_Margin);
            if (!s_Outer.Contains(p_World))
                continue;

            p_Left = Math.Abs(p_World.X - s_Rect.Left) <= s_Margin;
            p_Right = Math.Abs(p_World.X - s_Rect.Right) <= s_Margin;
            p_Top = Math.Abs(p_World.Y - s_Rect.Top) <= s_Margin;
            p_Bottom = Math.Abs(p_World.Y - s_Rect.Bottom) <= s_Margin;

            if (p_Left || p_Right || p_Top || p_Bottom)
                return s_Group;
        }

        return null;
    }

    /// <summary>Nodes whose rectangle sits fully inside the group - the set a title-bar drag carries along.</summary>
    private List<GraphNode> NodesInsideGroup(GraphGroup p_Group)
    {
        var s_Rect = GroupRect(p_Group);
        return m_Graph.Nodes.Where(p_N => s_Rect.Contains(NodeRect(p_N))).ToList();
    }

    /// <summary>Selects every node - Ctrl+A, and the seam that tests grouping goes through it too.</summary>
    public void SelectAll()
    {
        m_Selected.Clear();
        foreach (var s_Node in m_Graph.Nodes)
            m_Selected.Add(s_Node.Id);

        m_Primary = m_Graph.Nodes.Count > 0 ? m_Graph.Nodes[^1].Id : null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Wraps the current node selection in a new comment box, or drops an empty one at the given point when
    /// nothing is selected - the C key, exactly as the Unreal graph editors have taught everyone's fingers.
    /// </summary>
    public GraphGroup GroupSelection(Point p_Fallback)
    {
        PushUndo();
        var s_Group = new GraphGroup();

        var s_Nodes = m_Selected.Select(p_Id => m_Graph.FindNode(p_Id)).Where(p_N => p_N != null).ToList();
        if (s_Nodes.Count > 0)
        {
            const double c_Padding = 24;
            s_Group.X = s_Nodes.Min(p_N => p_N!.X) - c_Padding;
            s_Group.Y = s_Nodes.Min(p_N => p_N!.Y) - c_GroupTitleHeight - c_Padding;
            s_Group.Width = s_Nodes.Max(p_N => p_N!.X + c_NodeWidth) + c_Padding - s_Group.X;
            s_Group.Height = s_Nodes.Max(p_N => p_N!.Y + NodeHeight(p_N!)) + c_Padding - s_Group.Y;
        }
        else
        {
            s_Group.X = p_Fallback.X;
            s_Group.Y = p_Fallback.Y;
        }

        s_Group.Colour = m_Graph.Groups.Count % s_GroupColours.Length;
        m_Graph.Groups.Add(s_Group);
        m_SelectedGroup = s_Group.Id;
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return s_Group;
    }

    /// <summary>Selects a comment box alone, clearing the node selection - what a title-bar click does.</summary>
    public void SelectGroup(GraphGroup p_Group)
    {
        m_SelectedGroup = p_Group.Id;
        if (m_Selected.Count > 0)
        {
            m_Selected.Clear();
            m_Primary = null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        InvalidateVisual();
    }

    /// <summary>Moves a group AND the nodes captured inside it - what a title-bar drag does, step by step.</summary>
    public void MoveGroup(GraphGroup p_Group, Vector p_Delta)
    {
        // ⛔ Contents are captured BEFORE the rectangle moves. Moving first and asking afterwards tested the
        // nodes against the already-shifted box, so anything near the old boundary fell outside and stayed
        // behind - the group test's drag assertion caught exactly that on its first run.
        var s_Contents = NodesInsideGroup(p_Group);

        p_Group.X += p_Delta.X;
        p_Group.Y += p_Delta.Y;
        foreach (var s_Node in s_Contents)
        {
            s_Node.X += p_Delta.X;
            s_Node.Y += p_Delta.Y;
        }

        InvalidateVisual();
    }

    /// <summary>The rename editor: a popup over the title bar, committed with Enter, dismissed with Esc.</summary>
    private void BeginGroupRename(GraphGroup p_Group)
    {
        var s_Matrix = GetWorldMatrix();
        var s_TopLeft = s_Matrix.Transform(new Point(p_Group.X, p_Group.Y));

        var s_Box = new System.Windows.Controls.TextBox
        {
            Text = p_Group.Title,
            MinWidth = Math.Max(120, p_Group.Width * m_Scale),
            FontSize = 12,
        };

        var s_Popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = s_TopLeft.X,
            VerticalOffset = s_TopLeft.Y,
            Child = s_Box,
            StaysOpen = false,
            IsOpen = true,
        };

        void Commit()
        {
            if (s_Box.Text.Trim() is { Length: > 0 } s_Title && s_Title != p_Group.Title)
            {
                PushUndo();
                p_Group.Title = s_Title;
                GraphChanged?.Invoke(this, EventArgs.Empty);
            }

            s_Popup.IsOpen = false;
            InvalidateVisual();
        }

        s_Box.KeyDown += (_, p_KeyArgs) =>
        {
            if (p_KeyArgs.Key == Key.Enter)
                Commit();
            else if (p_KeyArgs.Key == Key.Escape)
                s_Popup.IsOpen = false;
        };

        s_Box.LostFocus += (_, _) => Commit();
        s_Box.SelectAll();
        s_Box.Focus();
    }

    /// <summary>
    /// Drops an empty comment box at a canvas point and opens its title editor right away - the search menu's
    /// "Comment" entry, so a note can be left anywhere without selecting anything first.
    /// </summary>
    public void AddCommentAt(Point p_World)
    {
        PushUndo();
        var s_Group = new GraphGroup
        {
            X = p_World.X,
            Y = p_World.Y,
            Colour = m_Graph.Groups.Count % s_GroupColours.Length,
        };

        m_Graph.Groups.Add(s_Group);
        m_SelectedGroup = s_Group.Id;
        GraphChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        BeginGroupRename(s_Group);
    }

    private Rect BandRect()
    {
        var s_Origin = m_BandOrigin ?? m_BandCursor;
        return new Rect(
            Math.Min(s_Origin.X, m_BandCursor.X), Math.Min(s_Origin.Y, m_BandCursor.Y),
            Math.Abs(m_BandCursor.X - s_Origin.X), Math.Abs(m_BandCursor.Y - s_Origin.Y));
    }

    private bool WireEndpoints(GraphConnection p_Connection, out Point p_From, out Point p_To)
    {
        p_From = default;
        p_To = default;

        var s_From = m_Graph.FindNode(p_Connection.FromNode);
        var s_To = m_Graph.FindNode(p_Connection.ToNode);
        if (s_From == null || s_To == null)
            return false;

        var s_FromIndex = s_From.Def.Outputs.FindIndex(p_P => p_P.Name == p_Connection.FromPort);
        var s_ToIndex = s_To.Def.Inputs.FindIndex(p_P => p_P.Name == p_Connection.ToPort);
        if (s_FromIndex < 0 || s_ToIndex < 0)
            return false;

        p_From = OutputAnchor(s_From, s_FromIndex);
        p_To = InputAnchor(s_To, s_ToIndex);
        return true;
    }

    private void RenderWire(DrawingContext p_Context, GraphConnection p_Connection)
    {
        if (!WireEndpoints(p_Connection, out var s_From, out var s_To))
            return;

        // The hovered wire fattens and brightens over the fade (smoothstepped so the growth reads as one
        // motion, not a pop) — Unreal's affordance for following a wire through a crowd.
        var s_Thickness = 1.2;
        var s_Colour = Color.FromRgb(158, 158, 162);

        if (ReferenceEquals(p_Connection, m_HoverWire))
        {
            var s_T = HoverProgress();
            s_T = s_T * s_T * (3 - 2 * s_T);
            s_Thickness = 1.2 + 2.6 * s_T;
            s_Colour = Color.FromRgb(
                (byte) (158 + (232 - 158) * s_T),
                (byte) (158 + (226 - 158) * s_T),
                (byte) (162 + (196 - 162) * s_T));
        }

        DrawCurve(p_Context, s_From, s_To, new Pen(new SolidColorBrush(s_Colour), s_Thickness));
    }

    private static double CurveOffset(Point p_A, Point p_B) => Math.Max(24.0, Math.Abs(p_B.X - p_A.X) * 0.45);

    private static void DrawCurve(DrawingContext p_Context, Point p_A, Point p_B, Pen p_Pen)
    {
        var s_Curve = CurveOffset(p_A, p_B);
        var s_Geometry = new StreamGeometry();
        using (var s_Ctx = s_Geometry.Open())
        {
            s_Ctx.BeginFigure(p_A, false, false);
            s_Ctx.BezierTo(new Point(p_A.X + s_Curve, p_A.Y), new Point(p_B.X - s_Curve, p_B.Y), p_B, true, false);
        }

        s_Geometry.Freeze();
        p_Context.DrawGeometry(null, p_Pen, s_Geometry);
    }

    private static Point CurveAt(Point p_A, Point p_B, double p_T)
    {
        var s_Curve = CurveOffset(p_A, p_B);
        var s_C1 = new Point(p_A.X + s_Curve, p_A.Y);
        var s_C2 = new Point(p_B.X - s_Curve, p_B.Y);

        var s_U = 1 - p_T;
        var s_W0 = s_U * s_U * s_U;
        var s_W1 = 3 * s_U * s_U * p_T;
        var s_W2 = 3 * s_U * p_T * p_T;
        var s_W3 = p_T * p_T * p_T;

        return new Point(
            s_W0 * p_A.X + s_W1 * s_C1.X + s_W2 * s_C2.X + s_W3 * p_B.X,
            s_W0 * p_A.Y + s_W1 * s_C1.Y + s_W2 * s_C2.Y + s_W3 * p_B.Y);
    }

    /// <summary>
    /// The bubble a node's comment renders in, in graph space. Public so framing and the headless test measure
    /// the SAME rectangle the renderer paints - a private copy of this math is how probes go hollow.
    /// </summary>
    public Rect CommentBubbleRect(GraphNode p_Node)
    {
        var s_Text = Text(p_Node.Comment ?? "", 10, Brushes.Black);
        return new Rect(p_Node.X, p_Node.Y - s_Text.Height - 8 - c_CommentGap,
            s_Text.Width + 12, s_Text.Height + 8);
    }

    private void RenderNodeComment(DrawingContext p_Context, GraphNode p_Node)
    {
        if (string.IsNullOrWhiteSpace(p_Node.Comment))
            return;

        // The pale speech bubble with a little tail every graph author already reads as "note", floating just
        // above the node and travelling with it.
        var s_Bubble = CommentBubbleRect(p_Node);
        var s_Fill = new SolidColorBrush(Color.FromRgb(238, 238, 230));
        p_Context.DrawRoundedRectangle(s_Fill,
            new Pen(new SolidColorBrush(Color.FromRgb(24, 24, 26)), 1.0), s_Bubble, 3, 3);

        var s_TailX = s_Bubble.X + 14;
        var s_Tail = new StreamGeometry();
        using (var s_Geometry = s_Tail.Open())
        {
            s_Geometry.BeginFigure(new Point(s_TailX - 5, s_Bubble.Bottom), true, true);
            s_Geometry.LineTo(new Point(s_TailX + 5, s_Bubble.Bottom), false, false);
            s_Geometry.LineTo(new Point(s_TailX, s_Bubble.Bottom + c_CommentGap - 1), false, false);
        }

        p_Context.DrawGeometry(s_Fill, null, s_Tail);

        var s_Text = Text(p_Node.Comment!, 10, new SolidColorBrush(Color.FromRgb(32, 32, 34)));
        p_Context.DrawText(s_Text, new Point(s_Bubble.X + 6, s_Bubble.Y + 4));
    }

    private void RenderNode(DrawingContext p_Context, GraphNode p_Node)
    {
        RenderNodeComment(p_Context, p_Node);

        var s_Rect = NodeRect(p_Node);
        var s_IsSelected = m_Selected.Contains(p_Node.Id);
        var s_IsPrimary = p_Node.Id == m_Primary;

        var s_Body = new SolidColorBrush(Color.FromRgb(210, 210, 212));
        var s_Border = new Pen(new SolidColorBrush(s_IsSelected
            ? (s_IsPrimary ? Color.FromRgb(255, 208, 64) : Color.FromRgb(200, 160, 60))
            : Color.FromRgb(24, 24, 26)), s_IsSelected ? 2.0 : 1.0);

        p_Context.DrawRectangle(s_Body, s_Border, s_Rect);

        if (HasPreview(p_Node))
            RenderPreview(p_Context, p_Node);

        var s_TitleRect = new Rect(s_Rect.X, s_Rect.Bottom - c_TitleHeight, s_Rect.Width, c_TitleHeight);
        p_Context.DrawRectangle(CategoryBrush(p_Node.Def.Category), null, s_TitleRect);

        var s_Title = Text(p_Node.Def.TitleFor(UdkStyle), 10, Brushes.White);
        p_Context.DrawText(s_Title, new Point(s_TitleRect.X + 5, s_TitleRect.Y + 3));

        // The parameter that IS this node's meaning (a constant's value, a swizzle's channels, an external's
        // name), right-aligned in the title bar - a graph that only reveals these in the properties panel
        // cannot be read at a glance.
        var s_Badge = p_Node.Def.CanvasBadge?.Invoke(p_Node);
        if (!string.IsNullOrEmpty(s_Badge))
        {
            var s_Available = s_Rect.Width - s_Title.Width - 16;
            var s_Label = s_Badge!;
            var s_BadgeText = Text(s_Label, 9, s_BadgeBrush);
            while (s_BadgeText.Width > s_Available && s_Label.Length > 2)
            {
                s_Label = s_Label[..^2] + "…";
                s_BadgeText = Text(s_Label, 9, s_BadgeBrush);
            }

            if (s_Available > 14)
                p_Context.DrawText(s_BadgeText,
                    new Point(s_Rect.Right - 5 - s_BadgeText.Width, s_TitleRect.Y + 4));
        }

        var s_Dark = new SolidColorBrush(Color.FromRgb(32, 32, 34));
        var s_Dim = new SolidColorBrush(Color.FromRgb(146, 146, 150));

        for (var i = 0; i < p_Node.Def.Inputs.Count; i++)
        {
            var s_Port = p_Node.Def.Inputs[i];
            var s_Anchor = InputAnchor(p_Node, i);
            var s_Unavailable = s_Port.WhyUnavailable(p_Node) != null;
            var s_Connected = m_Graph.ConnectionInto(p_Node.Id, s_Port.Name) != null;

            DrawPort(p_Context, s_Anchor, s_Connected, s_Unavailable);

            var s_Label = Text(s_Port.Name, 9, s_Unavailable ? s_Dim : s_Dark);
            p_Context.DrawText(s_Label, new Point(s_Anchor.X + c_PortSize, s_Anchor.Y - s_Label.Height * 0.5));
        }

        for (var i = 0; i < p_Node.Def.Outputs.Count; i++)
        {
            var s_Port = p_Node.Def.Outputs[i];
            var s_Anchor = OutputAnchor(p_Node, i);
            var s_HasWire = m_Graph.ConnectionsFrom(p_Node.Id, s_Port.Name).Any();

            DrawPort(p_Context, s_Anchor, s_HasWire, false);

            var s_Label = Text(s_Port.Name, 9, s_Dark);
            p_Context.DrawText(s_Label,
                new Point(s_Anchor.X - c_PortSize - s_Label.Width, s_Anchor.Y - s_Label.Height * 0.5));
        }
    }

    private void RenderPreview(DrawingContext p_Context, GraphNode p_Node)
    {
        var s_Rect = PreviewRect(p_Node);
        var s_Image = TexturePreviewProvider?.Invoke(p_Node.GetParam("Register"));

        if (s_Image != null)
        {
            p_Context.DrawImage(s_Image, s_Rect);
        }
        else
        {
            p_Context.DrawRectangle(new SolidColorBrush(Color.FromRgb(120, 120, 124)), null, s_Rect);

            var s_Pale = new SolidColorBrush(Color.FromRgb(240, 240, 240));
            var s_Faint = new SolidColorBrush(Color.FromRgb(200, 200, 204));
            var s_Register = Text($"t{p_Node.GetParam("Register")}", 14, s_Pale);
            var s_Hint = Text("press", 9, s_Faint);
            var s_Hint2 = Text("Reload from game", 9, s_Faint);

            var s_CentreY = s_Rect.Y + s_Rect.Height * 0.5;
            p_Context.DrawText(s_Register,
                new Point(s_Rect.X + (s_Rect.Width - s_Register.Width) * 0.5, s_CentreY - 26));
            p_Context.DrawText(s_Hint,
                new Point(s_Rect.X + (s_Rect.Width - s_Hint.Width) * 0.5, s_CentreY + 2));
            p_Context.DrawText(s_Hint2,
                new Point(s_Rect.X + (s_Rect.Width - s_Hint2.Width) * 0.5, s_CentreY + 14));
        }

        p_Context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(24, 24, 26)), 1.0), s_Rect);
    }

    private static void DrawPort(DrawingContext p_Context, Point p_Anchor, bool p_Connected, bool p_Unavailable)
    {
        var s_Rect = new Rect(p_Anchor.X - c_PortSize * 0.5, p_Anchor.Y - c_PortSize * 0.5, c_PortSize, c_PortSize);

        Brush s_Fill;
        if (p_Unavailable)
            s_Fill = new SolidColorBrush(Color.FromRgb(176, 176, 180));
        else if (p_Connected)
            s_Fill = new SolidColorBrush(Color.FromRgb(248, 208, 56));
        else
            s_Fill = new SolidColorBrush(Color.FromRgb(248, 248, 248));

        var s_Pen = new Pen(new SolidColorBrush(p_Unavailable
            ? Color.FromRgb(140, 140, 144)
            : Color.FromRgb(24, 24, 26)), 1.0);

        p_Context.DrawRectangle(s_Fill, s_Pen, s_Rect);
    }

    private PortHit? HitPort(Point p_World)
    {
        foreach (var s_Node in m_Graph.Nodes)
        {
            for (var i = 0; i < s_Node.Def.Inputs.Count; i++)
            {
                var s_Anchor = InputAnchor(s_Node, i);
                if (Near(p_World, s_Anchor))
                    return new PortHit
                    {
                        Node = s_Node, Port = s_Node.Def.Inputs[i].Name, IsInput = true, Anchor = s_Anchor,
                    };
            }

            for (var i = 0; i < s_Node.Def.Outputs.Count; i++)
            {
                var s_Anchor = OutputAnchor(s_Node, i);
                if (Near(p_World, s_Anchor))
                    return new PortHit
                    {
                        Node = s_Node, Port = s_Node.Def.Outputs[i].Name, IsInput = false, Anchor = s_Anchor,
                    };
            }
        }

        return null;
    }

    private static bool Near(Point p_A, Point p_B) =>
        Math.Abs(p_A.X - p_B.X) <= c_PortSize && Math.Abs(p_A.Y - p_B.Y) <= c_PortSize;

    private GraphNode? HitNode(Point p_World)
    {
        for (var i = m_Graph.Nodes.Count - 1; i >= 0; i--)
            if (NodeRect(m_Graph.Nodes[i]).Contains(p_World))
                return m_Graph.Nodes[i];

        return null;
    }

    private GraphConnection? HitWire(Point p_World, double p_Tolerance = 6.0)
    {
        // ⛔ Distance to the SEGMENTS between samples, never to the sample points alone: on a long wire 24
        // points sit 20-40px apart, and a cursor ON the curve midway between two of them measured farther
        // than the tolerance — which read as "you must hit it pixel-perfect, and only sometimes".
        const int c_Samples = 24;
        var s_Tolerance = p_Tolerance / Math.Max(m_Scale, 0.0001);
        GraphConnection? s_Best = null;
        var s_BestDistance = double.MaxValue;

        foreach (var s_Connection in m_Graph.Connections)
        {
            if (!WireEndpoints(s_Connection, out var s_From, out var s_To))
                continue;

            var s_Previous = CurveAt(s_From, s_To, 0);
            for (var i = 1; i <= c_Samples; i++)
            {
                var s_Point = CurveAt(s_From, s_To, i / (double) c_Samples);
                var s_Distance = DistanceToSegment(p_World, s_Previous, s_Point);
                s_Previous = s_Point;

                if (s_Distance < s_BestDistance)
                {
                    s_BestDistance = s_Distance;
                    s_Best = s_Connection;
                }
            }
        }

        return s_BestDistance <= s_Tolerance ? s_Best : null;
    }

    private static double DistanceToSegment(Point p_Point, Point p_A, Point p_B)
    {
        var s_Ab = p_B - p_A;
        var s_Ap = p_Point - p_A;
        var s_Length2 = s_Ab.LengthSquared;
        var s_T = s_Length2 < 1e-9 ? 0 : Math.Clamp((s_Ap.X * s_Ab.X + s_Ap.Y * s_Ab.Y) / s_Length2, 0, 1);
        return (p_Point - (p_A + s_Ab * s_T)).Length;
    }

    protected override void OnMouseDown(MouseButtonEventArgs p_Args)
    {
        // Remembered so mouse-up can tell a right CLICK (opens the search) from a right DRAG (pans).
        if (p_Args.ChangedButton == MouseButton.Right)
            m_RightDownScreen = p_Args.GetPosition(this);

        if (p_Args.ChangedButton == MouseButton.Left)
        {
            Focus();
            var s_World = TransformPoint(p_Args.GetPosition(this));
            var s_Ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            // A held shortcut letter places its node wherever you click, before any hit-testing.
            foreach (var (s_Key, s_Kind) in s_Shortcuts)
            {
                if (!Keyboard.IsKeyDown(s_Key))
                    continue;

                // ⛔ The shortcut drops the node of the vocabulary being AUTHORED IN. Without this the
                // palette says UDK and the keyboard hands you this engine's node — T pressed in UDK style
                // and got the native texture, with no per-channel outputs.
                AddNodeAt(UdkStyle ? Palette.UdkTwinOf(s_Kind) ?? s_Kind : s_Kind, s_World);
                p_Args.Handled = true;
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                var s_Wire = HitWire(s_World);
                if (s_Wire != null)
                    BreakWire(s_Wire.ToNode, s_Wire.ToPort);

                p_Args.Handled = true;
                return;
            }

            var s_Port = HitPort(s_World);
            if (s_Port != null)
            {
                PushUndo();

                if (s_Port.IsInput)
                    m_Graph.Disconnect(s_Port.Node.Id, s_Port.Port);

                m_WireFrom = s_Port;
                m_WireCursor = s_World;
                Mouse.Capture(this);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }

            var s_Node = HitNode(s_World);
            if (s_Node == null)
            {
                // Comment boxes sit behind nodes, so they only answer where no node is. The TITLE BAR selects,
                // drags (double-click renames); the EDGES resize; the BODY is deliberately click-through so
                // nodes inside stay selectable and a rubber band can start on top of a comment.
                var s_Group = HitGroupTitle(s_World);
                if (s_Group != null)
                {
                    if (p_Args.ClickCount == 2)
                    {
                        BeginGroupRename(s_Group);
                        p_Args.Handled = true;
                        return;
                    }

                    m_SelectedGroup = s_Group.Id;
                    if (m_Selected.Count > 0)
                    {
                        m_Selected.Clear();
                        m_Primary = null;
                        SelectionChanged?.Invoke(this, EventArgs.Empty);
                    }

                    PushUndo();
                    m_GroupDragging = s_Group;
                    m_GroupGrab = new Vector(s_World.X - s_Group.X, s_World.Y - s_Group.Y);

                    // Captured ONCE, at the grab: dragging a box across strangers must not kidnap them.
                    m_GroupContents = NodesInsideGroup(s_Group)
                        .ToDictionary(p_N => p_N.Id, p_N => new Point(p_N.X, p_N.Y));

                    Mouse.Capture(this);
                    p_Args.Handled = true;
                    InvalidateVisual();
                    return;
                }

                var s_Resize = HitGroupResize(s_World, out var s_Left, out var s_Right, out var s_Top,
                    out var s_Bottom);
                if (s_Resize != null)
                {
                    m_SelectedGroup = s_Resize.Id;
                    PushUndo();
                    m_GroupResizing = s_Resize;
                    m_ResizeLeft = s_Left;
                    m_ResizeRight = s_Right;
                    m_ResizeTop = s_Top;
                    m_ResizeBottom = s_Bottom;
                    m_ResizeStart = GroupRect(s_Resize);
                    m_GroupGrab = new Vector(s_World.X, s_World.Y);
                    Mouse.Capture(this);
                    p_Args.Handled = true;
                    InvalidateVisual();
                    return;
                }

                if (m_SelectedGroup != null)
                {
                    m_SelectedGroup = null;
                    InvalidateVisual();
                }

                // Empty space: start a rubber band. Ctrl keeps what is already selected.
                m_BandOrigin = s_World;
                m_BandCursor = s_World;
                m_BandAdditive = s_Ctrl;

                if (!s_Ctrl && m_Selected.Count > 0)
                {
                    m_Selected.Clear();
                    m_Primary = null;
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }

                Mouse.Capture(this);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }

            if (s_Ctrl)
            {
                // Toggle, so a mis-click can be taken back without losing the rest of the selection.
                if (!m_Selected.Remove(s_Node.Id))
                {
                    m_Selected.Add(s_Node.Id);
                    m_Primary = s_Node.Id;
                }
                else if (m_Primary == s_Node.Id)
                {
                    m_Primary = m_Selected.FirstOrDefault();
                }

                SelectionChanged?.Invoke(this, EventArgs.Empty);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }

            // Clicking an already-selected node keeps the group so the whole lot can be dragged.
            if (!m_Selected.Contains(s_Node.Id))
                SelectOnly(s_Node.Id);
            else
            {
                m_Primary = s_Node.Id;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            m_Graph.Nodes.Remove(s_Node);
            m_Graph.Nodes.Add(s_Node);

            PushUndo();
            m_Dragging = s_Node;
            m_DragGrab = new Vector(s_World.X - s_Node.X, s_World.Y - s_Node.Y);
            m_DragStart = m_Selected
                .Select(p_Id => m_Graph.FindNode(p_Id))
                .Where(p_N => p_N != null)
                .ToDictionary(p_N => p_N!.Id, p_N => new Point(p_N!.X, p_N.Y));

            Mouse.Capture(this);
            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        base.OnMouseDown(p_Args);
    }

    protected override void OnMouseMove(MouseEventArgs p_Args)
    {
        if (m_GroupDragging != null && p_Args.LeftButton == MouseButtonState.Pressed)
        {
            var s_World = TransformPoint(p_Args.GetPosition(this));
            var s_NewX = Math.Round((s_World.X - m_GroupGrab.X) / 4) * 4;
            var s_NewY = Math.Round((s_World.Y - m_GroupGrab.Y) / 4) * 4;
            var s_Delta = new Vector(s_NewX - m_GroupDragging.X, s_NewY - m_GroupDragging.Y);

            m_GroupDragging.X = s_NewX;
            m_GroupDragging.Y = s_NewY;

            if (m_GroupContents != null)
                foreach (var (s_Id, _) in m_GroupContents)
                {
                    var s_Inside = m_Graph.FindNode(s_Id);
                    if (s_Inside != null)
                    {
                        s_Inside.X += s_Delta.X;
                        s_Inside.Y += s_Delta.Y;
                    }
                }

            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        if (m_GroupResizing != null && p_Args.LeftButton == MouseButtonState.Pressed)
        {
            var s_World = TransformPoint(p_Args.GetPosition(this));
            var s_Dx = s_World.X - m_GroupGrab.X;
            var s_Dy = s_World.Y - m_GroupGrab.Y;

            var s_Left = m_ResizeStart.Left + (m_ResizeLeft ? s_Dx : 0);
            var s_Right = m_ResizeStart.Right + (m_ResizeRight ? s_Dx : 0);
            var s_Top = m_ResizeStart.Top + (m_ResizeTop ? s_Dy : 0);
            var s_Bottom = m_ResizeStart.Bottom + (m_ResizeBottom ? s_Dy : 0);

            m_GroupResizing.X = Math.Min(s_Left, s_Right - c_GroupMinSize);
            m_GroupResizing.Y = Math.Min(s_Top, s_Bottom - c_GroupMinSize);
            m_GroupResizing.Width = Math.Max(c_GroupMinSize, s_Right - s_Left);
            m_GroupResizing.Height = Math.Max(c_GroupMinSize, s_Bottom - s_Top);

            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        if (m_Dragging != null && p_Args.LeftButton == MouseButtonState.Pressed)
        {
            var s_World = TransformPoint(p_Args.GetPosition(this));
            var s_TargetX = Math.Round((s_World.X - m_DragGrab.X) / 4) * 4;
            var s_TargetY = Math.Round((s_World.Y - m_DragGrab.Y) / 4) * 4;

            // Move the whole selection by the same delta, taken from where each node started.
            if (m_DragStart != null && m_DragStart.TryGetValue(m_Dragging.Id, out var s_Anchor))
            {
                var s_Delta = new Vector(s_TargetX - s_Anchor.X, s_TargetY - s_Anchor.Y);
                foreach (var (s_Id, s_Start) in m_DragStart)
                {
                    var s_Node = m_Graph.FindNode(s_Id);
                    if (s_Node == null)
                        continue;

                    s_Node.X = s_Start.X + s_Delta.X;
                    s_Node.Y = s_Start.Y + s_Delta.Y;
                }
            }
            else
            {
                m_Dragging.X = s_TargetX;
                m_Dragging.Y = s_TargetY;
            }

            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        if (m_WireFrom != null && p_Args.LeftButton == MouseButtonState.Pressed)
        {
            m_WireCursor = TransformPoint(p_Args.GetPosition(this));
            UpdateEdgePan(p_Args.GetPosition(this));
            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        if (m_BandOrigin.HasValue && p_Args.LeftButton == MouseButtonState.Pressed)
        {
            m_BandCursor = TransformPoint(p_Args.GetPosition(this));
            p_Args.Handled = true;
            InvalidateVisual();
            return;
        }

        // Idle hover: show a resize cursor over a comment box's edges, so the grabbable margin is visible
        // before committing to a drag. Nodes sit on top, so only edges with nothing else there answer.
        if (p_Args.LeftButton == MouseButtonState.Released)
        {
            var s_Hover = TransformPoint(p_Args.GetPosition(this));
            if (HitNode(s_Hover) == null && HitPort(s_Hover) == null &&
                HitGroupResize(s_Hover, out var s_Left, out var s_Right, out var s_Top, out var s_Bottom) != null)
            {
                Cursor = (s_Left || s_Right) && (s_Top || s_Bottom)
                    ? (s_Left == s_Top ? Cursors.SizeNWSE : Cursors.SizeNESW)
                    : s_Left || s_Right
                        ? Cursors.SizeWE
                        : Cursors.SizeNS;
            }
            else if (Cursor != null)
            {
                Cursor = null;
            }

            // Hovered wire (Unreal-style): the wire under the cursor fattens after a short fade, which is
            // what lets a wire be followed through a dense graph. Nodes win — a wire passing under one must
            // not light up while the mouse is on the node. The hover zone is wider than the ALT-click break
            // zone on purpose: following is casual, cutting is deliberate.
            var s_Wire = HitNode(s_Hover) == null ? HitWire(s_Hover, 9.0) : null;
            if (!ReferenceEquals(s_Wire, m_HoverWire))
            {
                m_HoverWire = s_Wire;
                m_HoverWireStart = DateTime.UtcNow;
                UpdateHoverAnimation();
                InvalidateVisual();
            }
        }
        else if (m_HoverWire != null)
        {
            m_HoverWire = null;
            UpdateHoverAnimation();
            InvalidateVisual();
        }

        base.OnMouseMove(p_Args);
    }

    // ---- Auto-pan while a wire is being dragged (as in Unreal Engine: dragging a link from a node to the edge of the
    // window moves the canvas so nodes out of sight can be reached, instead of being forced to zoom out). The wire is
    // the only drag that has nowhere to go: a node can be
    // dropped and picked up again, but a wire in flight must reach its port in ONE gesture, so the view has to come
    // to it. The push grows with how far into the margin the cursor is, and holding past the edge keeps it at full
    // speed: the position is POLLED on the tick, not taken from the last move event, which is what makes it keep
    // scrolling while the cursor sits still against the edge.
    private System.Windows.Threading.DispatcherTimer? m_EdgeTimer;

    /// <summary>
    /// Where the follow reads the cursor on each tick. Null = the real pointer, which is what makes it keep
    /// scrolling while the cursor rests against the edge; a seam sets its own so the whole thing can be DRIVEN
    /// without moving the user's pointer (the sign of the push is the part that is easy to get backwards, and
    /// only a driven run shows it).
    /// </summary>
    public Func<Point>? CursorSource { get; set; }

    /// <summary>The pan, in graph units: what a seam measures the follow with.</summary>
    public Point ViewOffset => new(m_Offset.X, m_Offset.Y);

    /// <summary>The loose end of the wire being dragged, in graph space; null when no wire is in flight.</summary>
    public Point? WireEnd => m_WireFrom == null ? null : m_WireCursor;

    /// <summary>Whether the view is following the cursor right now (a seam checks it stops with the drag).</summary>
    public bool EdgePanRunning => m_EdgeTimer != null;

    /// <summary>Starts a wire drag from the port at a graph-space point, as a press on that port does.</summary>
    public bool DragWireFrom(Point p_World)
    {
        var s_Port = HitPort(p_World);

        if (s_Port == null)
            return false;

        m_WireFrom = s_Port;
        m_WireCursor = p_World;
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Starts a wire drag from a node's output port, by index — through the REAL hit test, so a seam that uses it
    /// also proves the port is where the drawing puts it.
    /// </summary>
    public bool DragWireFromOutput(string p_NodeId, int p_Index = 0)
    {
        var s_Node = m_Graph.FindNode(p_NodeId);
        return s_Node != null && DragWireFrom(OutputAnchor(s_Node, p_Index));
    }

    /// <summary>Ends the wire drag with nothing connected, as releasing over empty space does.</summary>
    public void ClearWireDrag()
    {
        m_WireFrom = null;
        StopEdgePan();
        InvalidateVisual();
    }

    /// <summary>Takes the loose end to a point of the WINDOW, as a move with the button down does.</summary>
    public void DragWireTo(Point p_Screen)
    {
        if (m_WireFrom == null)
            return;

        m_WireCursor = TransformPoint(p_Screen);
        UpdateEdgePan(p_Screen);
        InvalidateVisual();
    }

    /// <summary>How deep into the window's edge the cursor has to be for the view to start following, in pixels.</summary>
    private const double c_EdgeMargin = 56;

    /// <summary>Pixels per tick at the very edge (~60 ticks/s), with the ramp below it.</summary>
    private const double c_EdgeSpeed = 16;

    /// <summary>How much the view should move this tick for a cursor at that screen point (0,0 = not near an edge).</summary>
    private Vector EdgePush(Point p_Screen)
    {
        if (ActualWidth <= c_EdgeMargin * 2 || ActualHeight <= c_EdgeMargin * 2)
            return new Vector(0, 0);

        // Past the edge counts as being AT the edge: dragging out of the window keeps it at full speed instead of
        // stopping, which is where the gesture usually ends up when the target node is far away.
        static double Push(double p_Pos, double p_Size)
        {
            if (p_Pos < c_EdgeMargin)
                return -Math.Min(1.0, (c_EdgeMargin - p_Pos) / c_EdgeMargin) * c_EdgeSpeed;

            if (p_Pos > p_Size - c_EdgeMargin)
                return Math.Min(1.0, (p_Pos - (p_Size - c_EdgeMargin)) / c_EdgeMargin) * c_EdgeSpeed;

            return 0;
        }

        return new Vector(Push(p_Screen.X, ActualWidth), Push(p_Screen.Y, ActualHeight));
    }

    /// <summary>Starts, keeps or stops the follow, from where the cursor is right now.</summary>
    private void UpdateEdgePan(Point p_Screen)
    {
        if (EdgePush(p_Screen) == new Vector(0, 0))
        {
            StopEdgePan();
            return;
        }

        if (m_EdgeTimer != null)
            return;

        m_EdgeTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };

        m_EdgeTimer.Tick += (_, _) =>
        {
            // the drag ended (mouse up, Escape, focus lost): nothing left to follow. A driven run has no button
            // down, so the button only speaks when the real pointer is the one being followed.
            if (m_WireFrom == null || (CursorSource == null && Mouse.LeftButton != MouseButtonState.Pressed))
            {
                StopEdgePan();
                return;
            }

            var s_Now = CursorSource == null ? Mouse.GetPosition(this) : CursorSource();
            var s_Push = EdgePush(s_Now);

            if (s_Push == new Vector(0, 0))
            {
                StopEdgePan();
                return;
            }

            // m_Offset is in GRAPH units and screen = (world - offset) * scale, so a push in pixels is divided by
            // the zoom: the view travels the same distance on screen whatever the graph is scaled to.
            m_Offset.X += s_Push.X / Math.Max(m_Scale, 0.01);
            m_Offset.Y += s_Push.Y / Math.Max(m_Scale, 0.01);

            // the loose end stays under the cursor while the world slides beneath it
            m_WireCursor = TransformPoint(s_Now);
            InvalidateVisual();
        };

        m_EdgeTimer.Start();
    }

    private void StopEdgePan()
    {
        m_EdgeTimer?.Stop();
        m_EdgeTimer = null;
    }

    private GraphConnection? m_HoverWire;
    private DateTime m_HoverWireStart;
    private System.Windows.Threading.DispatcherTimer? m_HoverTimer;

    private const double c_HoverDelaySeconds = 0.05;
    private const double c_HoverFadeSeconds = 0.2;

    /// <summary>0 → 1 over the fade window, after the initial delay; how far along the fattening is.</summary>
    private double HoverProgress()
    {
        if (m_HoverWire == null)
            return 0;

        var s_Elapsed = (DateTime.UtcNow - m_HoverWireStart).TotalSeconds - c_HoverDelaySeconds;
        return Math.Clamp(s_Elapsed / c_HoverFadeSeconds, 0, 1);
    }

    /// <summary>Keeps frames coming only WHILE the fade is running; a finished fade stops the timer.</summary>
    private void UpdateHoverAnimation()
    {
        var s_Animating = m_HoverWire != null && HoverProgress() < 1;

        if (s_Animating)
        {
            m_HoverTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            m_HoverTimer.Tick -= OnHoverTick;
            m_HoverTimer.Tick += OnHoverTick;
            m_HoverTimer.Start();
        }
        else
        {
            m_HoverTimer?.Stop();
        }
    }

    private void OnHoverTick(object? p_Sender, EventArgs p_Args)
    {
        InvalidateVisual();
        if (HoverProgress() >= 1)
            m_HoverTimer?.Stop();
    }

    protected override void OnMouseUp(MouseButtonEventArgs p_Args)
    {
        if (p_Args.ChangedButton == MouseButton.Right && m_RightDownScreen.HasValue)
        {
            var s_Screen = p_Args.GetPosition(this);
            var s_Moved = (s_Screen - m_RightDownScreen.Value).Length;
            m_RightDownScreen = null;

            // Let the base release its pan capture first, then treat a stationary click as a search request.
            base.OnMouseUp(p_Args);

            if (s_Moved < 4.0)
            {
                // A right click on a comment's title bar cycles its colour; anywhere else it is the search.
                var s_Group = HitGroupTitle(TransformPoint(s_Screen));
                if (s_Group != null)
                {
                    PushUndo();
                    s_Group.Colour = (s_Group.Colour + 1) % s_GroupColours.Length;
                    GraphChanged?.Invoke(this, EventArgs.Empty);
                    InvalidateVisual();
                    return;
                }

                SearchRequested?.Invoke(this, TransformPoint(s_Screen));
            }

            return;
        }

        if (p_Args.ChangedButton == MouseButton.Left)
        {
            if (m_GroupDragging != null || m_GroupResizing != null)
            {
                m_GroupDragging = null;
                m_GroupResizing = null;
                m_GroupContents = null;
                Mouse.Capture(null);
                GraphChanged?.Invoke(this, EventArgs.Empty);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }

            if (m_Dragging != null)
            {
                m_Dragging = null;
                m_DragStart = null;
                Mouse.Capture(null);
                GraphChanged?.Invoke(this, EventArgs.Empty);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }

            if (m_WireFrom != null)
            {
                var s_Target = HitPort(TransformPoint(p_Args.GetPosition(this)));
                if (s_Target != null && s_Target.IsInput != m_WireFrom.IsInput)
                {
                    var s_Output = m_WireFrom.IsInput ? s_Target : m_WireFrom;
                    var s_Input = m_WireFrom.IsInput ? m_WireFrom : s_Target;
                    m_Graph.Connect(s_Output.Node.Id, s_Output.Port, s_Input.Node.Id, s_Input.Port);
                }

                m_WireFrom = null;
                StopEdgePan();
                Mouse.Capture(null);
                GraphChanged?.Invoke(this, EventArgs.Empty);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }

            if (m_BandOrigin.HasValue)
            {
                var s_Rect = BandRect();
                if (!m_BandAdditive)
                    m_Selected.Clear();

                foreach (var s_Node in m_Graph.Nodes)
                    if (s_Rect.IntersectsWith(NodeRect(s_Node)))
                    {
                        m_Selected.Add(s_Node.Id);
                        m_Primary = s_Node.Id;
                    }

                m_BandOrigin = null;
                Mouse.Capture(null);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                p_Args.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        base.OnMouseUp(p_Args);
    }

    protected override void OnKeyDown(KeyEventArgs p_Args)
    {
        var s_Ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // C wraps the selection in a comment box - the finger habit every graph editor shares. It has to be a
        // plain press: C is not among the hold-to-place letters, so there is no conflict with those.
        if (p_Args.Key == Key.C && !s_Ctrl)
        {
            GroupSelection(TransformPoint(Mouse.GetPosition(this)));
            p_Args.Handled = true;
            return;
        }

        if (p_Args.Key == Key.Delete)
        {
            // Group-alone handling lives in DeleteSelected, shared with the toolbar button.
            DeleteSelected();
            p_Args.Handled = true;
            return;
        }

        if (s_Ctrl && p_Args.Key == Key.A)
        {
            SelectAll();
            p_Args.Handled = true;
            return;
        }

        base.OnKeyDown(p_Args);
    }

    protected override void OnDragOver(DragEventArgs p_Args)
    {
        p_Args.Effects = p_Args.Data.GetDataPresent(DataFormats.StringFormat)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        p_Args.Handled = true;
    }

    protected override void OnDrop(DragEventArgs p_Args)
    {
        if (p_Args.Data.GetData(DataFormats.StringFormat) is string s_Kind && Palette.Has(s_Kind))
        {
            Focus();
            AddNodeAt(s_Kind, TransformPoint(p_Args.GetPosition(this)));
        }

        p_Args.Handled = true;
    }
}
