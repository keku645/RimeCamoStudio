using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimeShaderEditor.Graph;
using SharpDX;
using Point = System.Windows.Point;

namespace RimeShaderEditor;

/// <summary>
/// Sticker mode — the emblem editor of the camo studio (modelled on Call of Duty's: pick the sticker's size,
/// rotation and position, full screen, with a gizmo for rotation, translation and scale). The weapon fills
/// the window, a click puts the chosen picture where the mouse is on the body,
/// and the handles drawn on the surface move, turn and size it; G, R and S do the same from the keyboard,
/// the way a modeller expects. Placements live on the GRAPH (per weapon mesh: a texture coordinate plus an
/// anchor on the body), so they come back with the tab, undo covers them, and the bake reads them from the
/// same place the preview does.
///
/// A sticker is a DECAL: projected onto the body from the point it sits on (<see cref="StickerSurface"/>),
/// it runs across the seams of the unwrap and over the parts standing proud beside it, and never lands on a
/// part that only shares its patch of texture. What the shader sees is ONE texture per weapon — the
/// placed stickers composed into the weapon's own texture space by <see cref="StickerLayer"/> — sampled by
/// a texture node this mode adds to the graph and blended over the diffuse. The preview pushes that layer
/// to the node's register live; the bake ships the same composition as a texture and binds it per weapon.
/// The window knows nothing about files in a library or about the bake: the studio hands it a side panel
/// and asks it for the placements.
/// </summary>
public partial class MainWindow
{
    internal bool StickerMode { get; private set; }

    /// <summary>The studio's side panel (its sticker library and the list of placements), shown in the right column.</summary>
    private UIElement? m_StickerPanel;

    /// <summary>What the Stickers button does in camo mode: the studio opens the mode with its panel.</summary>
    private Action? m_CamoStickers;

    /// <summary>The register the weapon's diffuse is sampled from, per preset — the studio's slot knowledge.</summary>
    private Func<string, int?>? m_StickerDiffuseRegisterFor;

    private bool m_StickerRestoreSimple;
    private readonly Dictionary<string, BitmapSource?> m_StickerImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The library picture the next click on the weapon places, or null to only pick and move.</summary>
    internal string? StickerToPlace { get; set; }

    /// <summary>Index into the graph's sticker list of the selected placement, or -1.</summary>
    internal int SelectedSticker { get; private set; } = -1;

    /// <summary>Placements changed (added, moved, sized, turned, removed) — the panel's list follows.</summary>
    internal event Action? StickersChanged;

    /// <summary>The selection changed — the panel highlights the row and shows its numbers.</summary>
    internal event Action? StickerSelectionChanged;

    private enum StickerTool { None, Move, Rotate, Scale }

    private StickerTool m_StickerTool;
    private bool m_StickerToolFromKey;
    private string? m_StickerToolSnapshot;
    private double m_StickerStartAngle;
    private double m_StickerStartRotation;
    private double m_StickerStartWidth;
    private double m_StickerStartHeight;
    private double m_StickerStartDistance;
    private StickerFrame? m_StickerStartFrame;
    private Point? m_StickerGrabLocal;
    private System.Drawing.Point m_StickerLastMouse;

    /// <summary>
    /// A driver's stand-in for the Shift key while sizing: true = keep the shape, false = free, null = the
    /// keyboard decides (Shift held = keep the shape, as requested: a corner sizes the two sides on their own).
    /// </summary>
    internal bool? ForceUniformScale { get; set; }

    private bool UniformScaleWanted => ForceUniformScale ?? Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    /// <summary>Handles of the selected sticker on the panel, for hit tests: kind, and where they are in pixels.</summary>
    private readonly List<(StickerTool Kind, Vector2 At)> m_StickerHandles = new();

    private const double c_HandleHitRadius = 11.0;
    private const double c_DefaultStickerWidth = 0.12;
    private const uint c_OutlineSelected = 0xFFFFD34D;
    private const uint c_OutlineOther = 0xA0FFFFFF;
    private const uint c_RotateHandle = 0xFF6FD3FF;
    private const uint c_ScaleHandle = 0xFFFF7A7A;

    private void OnStickersToggle(object p_Sender, RoutedEventArgs p_Args)
    {
        if (StickerMode)
            SetStickerMode(false);
        else if (m_CamoStickers != null)
            m_CamoStickers.Invoke();
        else
            SetStickerMode(true);

        StickersButton.IsChecked = StickerMode;
    }

    /// <summary>
    /// Enters or leaves sticker mode. On: the palette, the canvas (or the simple form) and the right column's
    /// preview/properties go away, the 3D view is re-parented into the centre so the weapon fills the
    /// window, and the studio's panel takes the right column. Off: everything back where it was, the simple
    /// form included if it was on. Idempotent either way.
    /// </summary>
    internal void SetStickerMode(bool p_On, UIElement? p_Panel = null)
    {
        if (p_Panel != null && !ReferenceEquals(m_StickerPanel, p_Panel))
        {
            if (m_StickerPanel != null)
                RightPanel.Children.Remove(m_StickerPanel);

            m_StickerPanel = p_Panel;
            Grid.SetRow((FrameworkElement) p_Panel, 0);
            Grid.SetRowSpan((FrameworkElement) p_Panel, 4);
            p_Panel.Visibility = Visibility.Collapsed;
            RightPanel.Children.Add(p_Panel);
        }

        if (p_On == StickerMode)
        {
            if (p_On)
                RefreshStickerLayer();
            return;
        }

        // Stickers go on the camo DOCUMENT; while the weapon is shown as it ships the no-camo graph standing
        // in on the canvas gets the same nodes at the SAME register, sharing the document's placements — so
        // stickers are seen and shipped over the weapon as the game ships it, and the picker stays where the
        // user left it (the selected sticker should stay selected).
        // (a subject that takes only an emblem slot — a vehicle — gets no nodes: its graph is not touched, see StickerSlotOnly)
        if (p_On && !StickerSlotOnly)
            EnsureStickerNodesEverywhere();

        var s_Body = (Grid) Canvas.Parent;
        if (p_On)
        {
            m_StickerRestoreSimple = m_CamoSimplePanel?.Visibility == Visibility.Visible;

            // On from here: showing the simple form below re-feeds the camo's values, which re-asks whether
            // the weapon is as shipped — and a mode that is placing stickers never is (or the weapon's own
            // graph would take the canvas back before the sticker nodes are added to the camo's).
            StickerMode = true;

            // The simple form's column logic hides the palette; sticker mode wants the same, then also the
            // centre cell emptied for the view.
            SetCamoSimpleMode(true);
            if (m_CamoSimplePanel != null)
                m_CamoSimplePanel.Visibility = Visibility.Collapsed;
            Canvas.Visibility = Visibility.Collapsed;

            PreviewDock.Children.Remove(PreviewFrame);
            Grid.SetRow(PreviewFrame, Grid.GetRow(Canvas));
            Grid.SetColumn(PreviewFrame, Grid.GetColumn(Canvas));
            s_Body.Children.Add(PreviewFrame);

            PreviewTitle.Visibility = Visibility.Collapsed;
            PreviewDock.Visibility = Visibility.Collapsed;
            RightSplitter.Visibility = Visibility.Collapsed;
            PropertiesDock.Visibility = Visibility.Collapsed;
            if (m_StickerPanel != null)
                m_StickerPanel.Visibility = Visibility.Visible;

            StickerMode = true;
            m_StickerTool = StickerTool.None;
            PreviewStatus.Text = PreviewHelpLine;
            if (StickerSlotOnly)
                Log("Emblem slots: Edit slot, then click on the vehicle where a player's emblem goes. The handles move, turn and size each " +
                    "square (its tick marks the emblem's top); Save slot keeps them. Nothing of the vehicle's shader changes.");
            else
            {
                Log("Stickers: pick a picture on the right, then click on the weapon's body to place it. The handles move, turn and size it; Delete removes it.");
                EnsureStickerNodes();
            }

            RefreshStickerLayer();
        }
        else
        {
            // a slot-only subject (a vehicle) is left as the mode found it: the empty list of placements it shared goes again (its squares
            // were saved to the slot's file, never kept in the camo)
            if (StickerSlotOnly && DocumentGraph.Stickers is { Count: 0 })
            {
                DocumentGraph.Stickers = null;
                Canvas.Graph.Stickers = null;
            }

            StickerMode = false;
            m_StickerTool = StickerTool.None;
            m_Preview.PickEverySection = false;
            m_Preview.ClearOverlay();

            s_Body.Children.Remove(PreviewFrame);
            PreviewDock.Children.Add(PreviewFrame);

            PreviewTitle.Visibility = Visibility.Visible;
            PreviewDock.Visibility = Visibility.Visible;
            RightSplitter.Visibility = Visibility.Visible;
            PropertiesDock.Visibility = Visibility.Visible;
            if (m_StickerPanel != null)
                m_StickerPanel.Visibility = Visibility.Collapsed;

            SetCamoSimpleMode(m_StickerRestoreSimple);
            PreviewStatus.Text = PreviewHelpLine;
        }

        StickersButton.IsChecked = StickerMode;
    }

    /// <summary>The line under the 3D view when the graph compiles: the mouse mapping of the mode that is on.</summary>
    private string PreviewHelpLine => StickerMode
        ? "Stickers: LMB places / picks / drags · handles turn and size · G R S from the keyboard · Del removes · RMB orbits · MMB pans · wheel zooms"
        : "LMB orbit · MMB pan · RMB or wheel zoom · hold L + LMB turns the light · F frames";

    // --- the graph side: the nodes that sample the layer --------------------------------------------------

    /// <summary>
    /// Makes sure the graph samples a sticker layer: a texture node at a free register reading the same
    /// coordinates as the weapon's diffuse, blended over whatever fed the root's Diffuse by the layer's
    /// alpha. Added once per graph and remembered as <see cref="ShaderGraph.StickerRegister"/>; a graph
    /// that already has it is left alone. False when the graph has no root to hang it on.
    /// </summary>
    internal bool EnsureStickerNodes(int? p_Register = null)
    {
        var s_Graph = Canvas.Graph;
        var s_Diffuse = m_StickerDiffuseRegisterFor?.Invoke(ActiveTarget);

        // The undo point is taken only when the graph is about to change (a graph that already samples its
        // layer is left alone, undo history included).
        var s_Already = s_Graph.StickerRegister is { } s_Known &&
                        s_Graph.Nodes.Any(p_N => TextureNodeKinds.Contains(p_N.Kind) && p_N.GetParam("Register") == s_Known.ToString());
        if (!s_Already)
            Canvas.PushUndo();

        var s_Register = StickerGraph.Ensure(s_Graph, s_Diffuse, p_Register, out var s_Note, DocumentGraph.StickerLayerSide);
        if (s_Register == null)
        {
            Log("Stickers: " + s_Note);
            return false;
        }

        if (!s_Already)
        {
            Log("Stickers: " + s_Note);
            TouchGraph();
        }

        return true;
    }

    // --- the layer: what the shader samples ------------------------------------------------------------

    /// <summary>
    /// Composes the current weapon's stickers into its layer and pushes it to the sticker register, then
    /// redraws the gizmo. Called after every placement change, after a weapon is dressed, and when the mode
    /// opens. A graph without a sticker register has nothing to push; a weapon without stickers gets an
    /// EMPTY layer — the register would otherwise show the preview's placeholder gradient over the body.
    /// </summary>
    internal void RefreshStickerLayer()
    {
        // A subject that takes only an emblem slot (a vehicle): its squares lie in space and the overlay alone draws them — no layer,
        // no map, nothing pushed to a shader (and no mesh position handed over: that is the projected weapon slot's)
        // (and every part of it is pickable: the emblem is a decal on a vehicle, it lands on whatever part is there)
        m_Preview.PickEverySection = StickerSlotOnly;
        if (StickerSlotOnly)
        {
            m_Preview.MeshPositionInW = false;
            RebuildStickerOverlay();
            return;
        }

        var s_Graph = Canvas.Graph;
        if (s_Graph.StickerRegister is not { } s_Register)
        {
            m_Preview.ClearOverlay();
            return;
        }

        var s_Mesh = m_PreviewMesh ?? "";
        var s_Side = DocumentGraph.StickerLayerSide;

        // An animated sticker whose every placement is gone takes its sheet with it.
        var s_Document = DocumentGraph;
        if (s_Document.StickerAnimation is { } s_Gif &&
            !(s_Document.Stickers ?? new List<StickerPlacement>()).Any(p_S => p_S.Image.Equals(s_Gif, StringComparison.OrdinalIgnoreCase)))
        {
            s_Document.StickerAnimation = null;
            if (!ReferenceEquals(s_Document, s_Graph))
                s_Graph.StickerAnimation = null;
        }

        // Projected onto the body the preview holds — the same decal maths the bake runs on the weapon's
        // cached dump, so what is looked at is what ships. Animated placements are not in the layer: they
        // are in the coordinate map below, and the shader reads their frames from the sheet.
        var s_Clock = System.Diagnostics.Stopwatch.StartNew();
        var s_Marks = new List<string>();
        void Mark(string p_Stage)
        {
            s_Marks.Add($"{p_Stage} {s_Clock.ElapsedMilliseconds} ms");
            s_Clock.Restart();
        }

        // (the emblem slot being placed by hand rides in the list as "#emblem" placements: never in the layer, it IS the slot below)
        var s_OnMesh = s_Graph.StickersOn(s_Mesh).Where(p_S => !EmblemSlot.IsEmblem(p_S)).ToList();
        var s_EditedSlot = s_Graph.StickersOn(s_Mesh).Where(EmblemSlot.IsEmblem).ToList();
        var s_Layer = StickerLayer.Compose(s_OnMesh.Where(p_S => !StickerGraph.IsAnimated(p_S.Image)), StickerImage, s_Side, m_Preview.Surface);
        Mark("layer");
        PushStickerTexture(s_Register, s_Layer, true);
        Mark("layer push");

        // The emblem slot's placements on this weapon (found on its body once, then kept): in the side map and the
        // coordinate map like an animated sticker's, never in the layer.
        // While it is being placed by hand, the slot is the "#emblem" placements in the list; otherwise the saved or automatic one.
        var s_Emblem = !EmblemSlot.IsOn(s_Graph) ? new List<StickerPlacement>() : s_EditedSlot.Count > 0 ? s_EditedSlot : EmblemPlacementsFor(s_Mesh);

        // A PROJECTED slot (EmblemSlot.Projected): the weapon's frames are the shader's frame constants (as its variation fills them in
        // the game) — fed to the preview, nothing recompiled — and the view hands the mesh position over as the patched vertex shader does.
        var s_Projected = StickerGraph.IsProjected(s_Graph);
        m_Preview.MeshPositionInW = s_Projected;
        if (s_Projected && m_Preview.Surface is { } s_Body)
        {
            var s_Slots = EmblemSlot.ProjectionSlots(s_Emblem, s_Body);
            if (s_Slots != EmblemSlot.PreviewSlots)
            {
                EmblemSlot.SetPreviewSlots(s_Slots);
                RefreshEmblemPreview();
            }
        }

        // The side map of every placement (still and animated): which half of a mirrored unwrap each was
        // painted on, so the shader keeps it off the other half — packed with the animated sticker's x
        // coordinate in its alpha; y in the next texture's alpha; the frame sheet after.
        // The view draws a game mesh through a lateral mirror (ShaderPreview.s_MeshWorld), which flips every
        // triangle's hand: its map stores the opposite of what the package ships.
        var s_SideMap = StickerLayer.ComposeSideMap(s_OnMesh.Concat(s_Emblem), EmblemSlot.ImageOf(StickerImage), s_Side, m_Preview.Surface,
            p_MirrorHand: m_Preview.MeshMirrored);
        Mark("side map");
        BitmapSource? s_Coordinates = null;
        GifAtlas? s_Atlas = null;
        if (s_Document.StickerAnimation is { } s_Animation && StickerAtlasOf(s_Animation) is { } s_Known)
        {
            s_Atlas = s_Known;
            var s_Animated = s_OnMesh.Where(p_S => p_S.Image.Equals(s_Animation, StringComparison.OrdinalIgnoreCase));
            s_Coordinates = StickerLayer.ComposeMap(s_Animated, s_Atlas, s_Side, m_Preview.Surface);
            Mark("coordinate map");
        }
        else if (s_Emblem.Count > 0)
        {
            s_Coordinates = StickerLayer.ComposeMap(s_Emblem, EmblemSlot.Picture, s_Side, m_Preview.Surface);
            Mark("emblem map");
        }

        var s_Packed = StickerLayer.PackMaps(s_SideMap, s_Coordinates);
        Mark("pack");
        PushStickerTexture(s_Register + 1, s_Packed, false);
        Mark("map push");

        // the GIF's frame sheet at the register its Flipbook reads, the slot's shape atlas at the one its EmblemLayers reads — both, beside each
        // other (StickerGraph.GifKeepsEmblem: the slot stays whatever is placed); one register (an older graph) = the GIF's
        var s_SheetAt = StickerGraph.SheetRegisterIn(s_Graph) ?? s_Register + 2;
        var s_AtlasAt = s_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "EmblemLayers") is { } s_Layers &&
                        int.TryParse(s_Layers.GetParam("Register"), out var s_At) ? s_At : s_Register + 2;
        if (s_Coordinates != null && s_Atlas != null)
        {
            PushStickerTexture(s_SheetAt, s_Atlas.Sheet, true);
            Mark("sheet push");
        }

        if (s_Emblem.Count > 0 && (s_Atlas == null || s_AtlasAt != s_SheetAt) && EmblemSlot.AtlasPicture() is { } s_Shapes)
        {
            PushStickerTexture(s_AtlasAt, s_Shapes, false);
            Mark("emblem atlas push");
        }

        RebuildStickerOverlay();
        Mark("overlay");
        LastStickerRefreshTiming = string.Join(", ", s_Marks);
    }

    /// <summary>What the last layer refresh cost, stage by stage — for a driver that measures a drag.</summary>
    internal string LastStickerRefreshTiming { get; private set; } = "";

    /// <summary>Feeds the preview the emblem EmblemSlot holds now (after EmblemSlot.SetPreview) — for a seam that compares emblems.</summary>
    internal void RefreshEmblemPreview()
    {
        PushExternalValues();
        // the look as shipped draws with foreign shaders that build their own constants: they take the emblem too
        m_Preview.RefreshEmblemValues();
    }

    /// <summary>The emblem slot's placements, per weapon mesh (saved by hand, or found on the body the preview holds), kept once found.</summary>
    private readonly Dictionary<string, List<StickerPlacement>> m_EmblemPlacements = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The subjects that take only an emblem SLOT in sticker mode — set by the studio to its vehicles (done as in the tests: literally
    /// the decal on the vehicle with no shader; here the camo studio only supplies the emblems' positions). Null = none: every subject
    /// is a weapon's.
    /// </summary>
    internal Func<string, bool>? SlotOnlySubject { get; set; }

    /// <summary>
    /// Whether the subject on screen takes only an emblem slot: no pictures, no sticker nodes added to its graph, and its squares laid
    /// out in space (<see cref="StickerPlacement.IsSpatial"/>, metres) — what a decal stamped on it in the game needs.
    /// </summary>
    internal bool StickerSlotOnly => m_PreviewMesh is { Length: > 0 } s_Mesh && SlotOnlySubject?.Invoke(s_Mesh) == true;

    /// <summary>The side of a square a click puts on a slot-only subject, in metres (BF4's on the ground vehicles: 0.3–0.5 m).</summary>
    private const double c_DefaultSpatialSlotMetres = 0.4;

    /// <summary>
    /// The emblem slot's placements on a weapon mesh: the one saved by hand for it (EmblemSlot.SavedPlacements), or the automatic one
    /// on the previewed body — said in the Output once.
    /// </summary>
    internal List<StickerPlacement> EmblemPlacementsFor(string p_Mesh)
    {
        if (m_EmblemPlacements.TryGetValue(p_Mesh, out var s_Known))
            return s_Known;

        // a subject that takes only an emblem slot (a vehicle): the slot saved for it — the studio's (BF4's, on the vehicles both games
        // share) or the user's — and never an automatic spot: that one looks for a weapon's receiver
        if (StickerSlotOnly)
        {
            var s_Saved = EmblemSlot.SavedPlacements(p_Mesh, out var s_From) ?? new List<StickerPlacement>();
            Log(s_From != null
                ? $"emblem slot: '{p_Mesh.Split('/')[^1]}' {s_Saved.Count} square(s) ({s_From})."
                : $"emblem slot: '{p_Mesh.Split('/')[^1]}' has none yet — Edit slot, then click on the vehicle where an emblem goes.");
            m_EmblemPlacements[p_Mesh] = s_Saved;
            return s_Saved;
        }

        var s_Found = m_Preview.Surface == null
            ? new List<StickerPlacement>()
            : EmblemSlot.PlacementsFor(m_Preview.Surface, p_Mesh, EmblemSlot.DefaultSizeMetres, p_Line => Log(p_Line));
        if (m_Preview.Surface != null)
            m_EmblemPlacements[p_Mesh] = s_Found;
        return s_Found;
    }

    /// <summary>
    /// Puts the weapon's emblem slot into the placement list as "#emblem" placements, so the handles move, turn and size it like a
    /// sticker (the slot placed by hand, weapon by weapon). Nothing added when it is there already. How many are in the
    /// list for this weapon now.
    /// </summary>
    internal int EditEmblemSlot()
    {
        var s_Mesh = m_PreviewMesh ?? "";
        if (s_Mesh.Length == 0 || !(StickerSlotOnly || EmblemSlot.IsOn(Canvas.Graph)))
            return 0;

        // While the slot is edited a click on bare body puts ANOTHER square (a weapon the automatic spot missed, the other side);
        // a click on a square picks it, as with any sticker.
        StickerToPlace = EmblemSlot.Image;
        var s_List = StickerList();
        var s_Already = s_List.Count(p_S => EmblemSlot.IsEmblem(p_S) && p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase));
        if (s_Already > 0)
            return s_Already;

        Canvas.PushUndo();
        foreach (var s_Slot in EmblemPlacementsFor(s_Mesh))
            s_List.Add(new StickerPlacement
            {
                Mesh = s_Mesh, Image = EmblemSlot.Image, U = s_Slot.U, V = s_Slot.V, Width = s_Slot.Width, Height = s_Slot.Height,
                Rotation = s_Slot.Rotation, Mirror = s_Slot.Mirror, Reach = s_Slot.Reach, Anchor = s_Slot.Anchor, Normal = s_Slot.Normal,
            });

        var s_Count = s_List.Count(p_S => EmblemSlot.IsEmblem(p_S) && p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase));
        SelectSticker(s_List.FindIndex(p_S => EmblemSlot.IsEmblem(p_S) && p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase)), false);
        RefreshStickerLayer();
        StickersChanged?.Invoke();
        Log($"Emblem slot: {s_Count} square(s) on this weapon to move, turn and size — Save slot keeps them for every camo.");
        return s_Count;
    }

    /// <summary>
    /// Saves the weapon's "#emblem" placements as its slot (EmblemSlot.SavePlacements) and takes them out of the list — they are the slot,
    /// not stickers of this camo. The file's path, or null (with the reason in <paramref name="p_Why"/>) when there is nothing to save.
    /// </summary>
    internal string? SaveEmblemSlot(string p_Weapon, out string p_Why)
    {
        var s_Mesh = m_PreviewMesh ?? "";
        var s_List = StickerList();
        var s_Slot = s_List.Where(p_S => EmblemSlot.IsEmblem(p_S) && p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase)).ToList();
        if (s_Mesh.Length == 0 || s_Slot.Count == 0)
        {
            p_Why = "no slot being placed on this weapon — press Edit slot first.";
            return null;
        }

        var s_Path = EmblemSlot.SavePlacements(s_Mesh, p_Weapon, s_Slot);
        if (StickerToPlace == EmblemSlot.Image)
            StickerToPlace = null;
        Canvas.PushUndo();
        s_List.RemoveAll(p_S => EmblemSlot.IsEmblem(p_S) && p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase));
        m_EmblemPlacements.Remove(s_Mesh);
        SelectSticker(-1, false);
        RefreshStickerLayer();
        StickersChanged?.Invoke();
        StickerSelectionChanged?.Invoke();
        p_Why = "";
        Log($"Emblem slot: {s_Slot.Count} square(s) saved for {p_Weapon} ({s_Path}).");
        return s_Path;
    }

    /// <summary>Drops the weapon's hand-placed slot (its file and any "#emblem" placements in the list): the automatic spot comes back.</summary>
    internal bool ResetEmblemSlot()
    {
        var s_Mesh = m_PreviewMesh ?? "";
        if (s_Mesh.Length == 0)
            return false;

        var s_Had = EmblemSlot.ForgetPlacements(s_Mesh);
        if (StickerToPlace == EmblemSlot.Image)
            StickerToPlace = null;
        var s_List = StickerList();
        if (s_List.Any(p_S => EmblemSlot.IsEmblem(p_S) && p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase)))
        {
            Canvas.PushUndo();
            s_List.RemoveAll(p_S => EmblemSlot.IsEmblem(p_S) && p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase));
            s_Had = true;
        }

        m_EmblemPlacements.Remove(s_Mesh);
        SelectSticker(-1, false);
        RefreshStickerLayer();
        StickersChanged?.Invoke();
        StickerSelectionChanged?.Invoke();
        Log(StickerSlotOnly
            ? "Emblem slot: yours forgotten on this vehicle — the studio's slot (BF4's, where the two games share it) or none."
            : "Emblem slot: back to the automatic spot on this weapon.");
        return s_Had;
    }

    /// <summary>The body mesh of the weapon on screen, or null.</summary>
    internal string? CurrentPreviewMesh => m_PreviewMesh;

    /// <summary>Whether the weapon on screen has a slot saved by hand (and where), for the panel.</summary>
    internal string? SavedEmblemSlotFile() =>
        m_PreviewMesh is { Length: > 0 } s_Mesh && EmblemSlot.SavedPlacements(s_Mesh, out var s_From) != null ? s_From : null;

    /// <summary>Which foreign shader each sticker register was last pushed for (see PushStickerTexture).</summary>
    private readonly Dictionary<string, string> m_PushedStickerTextures = new();

    private bool m_StickerRefreshPending;
    private System.Threading.Tasks.TaskCompletionSource? m_StickerRefreshSettled;

    /// <summary>
    /// A layer refresh for a drag: ONE per turn of the dispatcher, however many mouse moves arrive in
    /// between — the moves queue behind a synchronous refresh otherwise, and the sticker follows the mouse
    /// in jumps (it stuck and moved only every 2 s). Only the latest position matters.
    /// </summary>
    private void RequestStickerRefresh()
    {
        if (m_StickerRefreshPending)
            return;

        m_StickerRefreshPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            m_StickerRefreshPending = false;
            if (StickerMode)
                RefreshStickerLayer();
            m_StickerRefreshSettled?.TrySetResult();
            m_StickerRefreshSettled = null;
        });
    }

    /// <summary>Completes once no layer refresh is pending — for a driver that measures a drag to the end.</summary>
    internal System.Threading.Tasks.Task SettleStickerRefresh()
    {
        if (!m_StickerRefreshPending)
            return System.Threading.Tasks.Task.CompletedTask;

        m_StickerRefreshSettled ??= new System.Threading.Tasks.TaskCompletionSource();
        return m_StickerRefreshSettled.Task;
    }

    /// <summary>
    /// A composed sticker texture (the layer, the coordinate map, the frame sheet) to the node thumbnails,
    /// the 3D view's authored shader and, when the weapon as shipped draws with its own shader recompiled
    /// with the stickers, that shader's texture set too.
    /// </summary>
    private void PushStickerTexture(int p_Register, BitmapSource p_Texture, bool p_Srgb)
    {
        var s_Key = p_Register.ToString();
        var s_Foreign = m_Preview.FactoryShader;
        // The same picture already on this register (the GIF's sheet, 32 MB, on every drag move) stays.
        if (m_TexturePreviews.TryGetValue(s_Key, out var s_Shown) && ReferenceEquals(s_Shown, p_Texture) &&
            m_PushedStickerTextures.TryGetValue(s_Key, out var s_PushedFor) && s_PushedFor == (s_Foreign ?? ""))
            return;

        m_TexturePreviews[s_Key] = p_Texture;
        m_TextureSrgb[s_Key] = p_Srgb;
        if (!m_Preview.Ready)
            return;

        m_PushedStickerTextures[s_Key] = s_Foreign ?? "";

        var s_Source = p_Texture.Format == PixelFormats.Bgra32 || p_Texture.Format == PixelFormats.Pbgra32
            ? p_Texture
            : new FormatConvertedBitmap(p_Texture, PixelFormats.Bgra32, null, 0);
        // Rented: a fresh 4 MB array per push went to the large-object heap and came back as pauses in a drag.
        var s_Count = p_Texture.PixelWidth * p_Texture.PixelHeight;
        var s_Pixels = System.Buffers.ArrayPool<uint>.Shared.Rent(s_Count);
        s_Source.CopyPixels(s_Pixels, p_Texture.PixelWidth * 4, 0);
        m_Preview.SetTexture(p_Register, s_Pixels, p_Texture.PixelWidth, p_Texture.PixelHeight, p_Srgb);
        if (s_Foreign != null)
            m_Preview.SetForeignTexture(s_Foreign, p_Register, s_Pixels, p_Texture.PixelWidth, p_Texture.PixelHeight, p_Srgb);
        System.Buffers.ArrayPool<uint>.Shared.Return(s_Pixels);
    }

    // --- animated stickers (GIF) -------------------------------------------------------------------------

    /// <summary>Decoded GIFs by path and alpha treatment: their frame sheet and timeline, or null for a file that is not an animated GIF.</summary>
    private readonly Dictionary<string, GifAtlas?> m_GifAtlases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The frame sheet of an animated GIF in the library, decoded once per alpha treatment (the document's:
    /// see ShaderGraph.StickerAnimationOpaque); null for anything else.
    /// </summary>
    internal GifAtlas? StickerAtlasOf(string p_Path)
    {
        var s_Opaque = DocumentGraph.StickerAnimationOpaque;
        var s_Key = $"{p_Path}|{(s_Opaque ? "opaque" : "alpha")}";
        if (m_GifAtlases.TryGetValue(s_Key, out var s_Known))
            return s_Known;

        GifAtlas? s_Atlas = null;
        if (GifClip.Decode(p_Path) is { } s_Clip && s_Clip.Frames.Count > 1)
        {
            s_Atlas = GifAtlas.Build(p_Path, s_Clip, s_Opaque);
            if (s_Atlas == null)
                Log($"Stickers: '{System.IO.Path.GetFileName(p_Path)}' has {s_Clip.Frames.Count} frames of {s_Clip.Width}×{s_Clip.Height} — too many to fit a 4096² sheet even at 32 texels a frame.");
        }

        m_GifAtlases[s_Key] = s_Atlas;
        return s_Atlas;
    }

    /// <summary>Forgets a decoded GIF (re-imported under the same name), in both alpha treatments.</summary>
    internal void ForgetStickerAtlas(string p_Path)
    {
        m_GifAtlases.Remove($"{p_Path}|opaque");
        m_GifAtlases.Remove($"{p_Path}|alpha");
    }

    /// <summary>
    /// Whether the camo's GIF ignores its alpha channel (transparent pixels drawn as opaque black). The
    /// choice is the DOCUMENT's and rebuilds the sheet at once — in the view, and in the package when baked.
    /// </summary>
    internal void SetStickerAnimationOpaque(bool p_Opaque)
    {
        if (DocumentGraph.StickerAnimationOpaque == p_Opaque)
            return;

        DocumentGraph.StickerAnimationOpaque = p_Opaque;
        if (!ReferenceEquals(DocumentGraph, Canvas.Graph))
            Canvas.Graph.StickerAnimationOpaque = p_Opaque;

        if (DocumentGraph.StickerAnimation is { } s_Gif && StickerAtlasOf(s_Gif) is { } s_Atlas)
            Log($"Stickers: '{System.IO.Path.GetFileName(s_Gif)}' {(p_Opaque ? "ignores its alpha channel — transparent pixels are opaque black" : "keeps its own transparency")}; " +
                $"sheet rebuilt: {s_Atlas.Dimensions}, {s_Atlas.Cells} cell(s) of {s_Atlas.CellWidth}×{s_Atlas.CellHeight}.");

        EnsureStickerAnimationEverywhere();
        RefreshStickerLayer();
        StickersChanged?.Invoke();
    }

    /// <summary>Sets a parameter on a node of the graph on the canvas and recompiles, for a driver that flips a knob.</summary>
    internal void DriveNodeParam(GraphNode p_Node, string p_Name, string p_Value)
    {
        p_Node.Params[p_Name] = p_Value;
        TouchGraph();
    }

    /// <summary>The seconds fed to the preview's shader instead of the clock, for a driver that photographs a frame.</summary>
    internal float? PreviewTimeOverride
    {
        get => m_PreviewTimeOverride;
        set
        {
            m_PreviewTimeOverride = value;
            if (value is { } s_Seconds)
                m_Preview.Time = s_Seconds;
        }
    }

    private float? m_PreviewTimeOverride;

    /// <summary>
    /// A picture just placed: an animated GIF becomes the camo's animated sticker (one GIF per camo: its
    /// sheet is a texture of the package) and the graphs get their animation nodes. False when the
    /// placement cannot stand — a second, different GIF on a camo that already plays one.
    /// </summary>
    private bool AdoptAnimatedSticker(string p_Image)
    {
        if (!StickerGraph.IsAnimated(p_Image))
            return true;

        var s_Atlas = StickerAtlasOf(p_Image);
        if (s_Atlas == null)
        {
            Log($"Stickers: '{System.IO.Path.GetFileName(p_Image)}' is not an animated GIF (or could not be read as one) — placed as its first frame.");
            return true;
        }

        var s_Document = DocumentGraph;
        if (s_Document.StickerAnimation is { } s_Existing && !s_Existing.Equals(p_Image, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Stickers: this camo already plays '{System.IO.Path.GetFileName(s_Existing)}' — one animated GIF per camo for now; " +
                $"'{System.IO.Path.GetFileName(p_Image)}' was not placed.");
            return false;
        }

        if (s_Document.StickerAnimation == null)
        {
            s_Document.StickerAnimation = p_Image;
            Log($"Stickers: '{System.IO.Path.GetFileName(p_Image)}' plays as a flipbook — {s_Atlas.Frames} frame(s) " +
                $"({s_Atlas.Clip.SourceFrames} in the file), {s_Atlas.TotalSeconds:0.##} s per loop, the file's own timing; " +
                $"{s_Atlas.Dimensions} sheet of {s_Atlas.Cells} cell(s) of {s_Atlas.CellWidth}×{s_Atlas.CellHeight} ({s_Atlas.Megabytes:0.#} MB in video memory).");
        }

        EnsureStickerAnimationEverywhere();
        return true;
    }

    /// <summary>The animation nodes on one graph that samples the sticker layer, for the camo's GIF; false when nothing changed.</summary>
    private bool EnsureStickerAnimationOn(ShaderGraph p_Graph)
    {
        if (DocumentGraph.StickerAnimation is not { } s_Gif || StickerAtlasOf(s_Gif) is not { } s_Atlas || p_Graph.StickerRegister == null)
            return false;

        var s_HadSheet = p_Graph.Nodes.Any(p_N => p_N.Kind == "Flipbook");
        if (!StickerGraph.EnsureAnimated(p_Graph, s_Atlas, DocumentGraph.StickerLayerSide, out var s_Note))
        {
            if (s_Note.Length > 0)
                Log("Stickers: " + s_Note);
            return false;
        }

        if (!s_HadSheet && s_Note.Length > 0)
            Log("Stickers: " + s_Note);
        return !s_HadSheet;
    }

    /// <summary>The animation nodes on the document and, while a graph stands in for it on the canvas, on that graph too.</summary>
    private void EnsureStickerAnimationEverywhere()
    {
        var s_Changed = EnsureStickerAnimationOn(DocumentGraph);
        if (!ReferenceEquals(DocumentGraph, Canvas.Graph))
        {
            Canvas.Graph.StickerAnimation = DocumentGraph.StickerAnimation;
            s_Changed |= EnsureStickerAnimationOn(Canvas.Graph);
        }

        if (s_Changed)
            TouchGraph();
    }

    /// <summary>A library picture, decoded once per path; null when it cannot be read.</summary>
    internal BitmapSource? StickerImage(string p_Path)
    {
        // the emblem slot being placed by hand: its square (the outline, the handles and the aspect follow it like any picture's)
        if (p_Path == EmblemSlot.Image)
            return EmblemSlot.Picture;

        if (m_StickerImages.TryGetValue(p_Path, out var s_Known))
            return s_Known;

        var s_Image = LoadCustomTextureImage(p_Path) as BitmapSource;
        m_StickerImages[p_Path] = s_Image;
        return s_Image;
    }

    /// <summary>Forgets a decoded picture (re-imported under the same name), so the next use re-reads the file.</summary>
    internal void ForgetStickerImage(string p_Path) => m_StickerImages.Remove(p_Path);

    private double StickerAspect(StickerPlacement p_Sticker) => StickerLayer.AspectOf(StickerImage(p_Sticker.Image));

    /// <summary>The body of the weapon being looked at, as sticker mode projects onto it; null without a mesh.</summary>
    internal StickerSurface? BodySurface => m_Preview.Surface;

    /// <summary>Whether the view draws the mesh through its lateral mirror — for a driver that aims the camera at a point of the mesh.</summary>
    internal bool PreviewMeshMirrored => m_Preview.MeshMirrored;

    /// <summary>How many picture corners the overlay draws (a vehicle's emblem squares) — for a driver.</summary>
    internal int OverlayPictureVertices => m_Preview.OverlayPictureVertices;

    /// <summary>How many sections the view drew solid in its last frame (what a vehicle's emblem square lies on) — for a driver.</summary>
    internal int SolidSectionCount => m_Preview.SolidSections.Count;

    private readonly List<StickerPlacement> m_DriverStickers = new();

    /// <summary>Puts a placement in the list for a driver's photograph (no undo, no save); <see cref="RemoveDriverStickers"/> takes it out.</summary>
    internal void AddStickerForDriver(StickerPlacement p_Placement)
    {
        StickerList().Add(p_Placement);
        m_DriverStickers.Add(p_Placement);
        RefreshStickerLayer();
    }

    /// <summary>Takes out every placement a driver put in (and nothing else).</summary>
    internal void RemoveDriverStickers()
    {
        var s_List = StickerList();
        foreach (var s_Placement in m_DriverStickers)
            s_List.Remove(s_Placement);
        m_DriverStickers.Clear();
        SelectSticker(-1, false);
        RefreshStickerLayer();
    }

    /// <summary>A point of the body (the view's surface units) on the panel, in pixels — for a driver that clicks on it.</summary>
    internal (float X, float Y)? PanelPointOf(System.Numerics.Vector3 p_Point) =>
        m_Preview.ProjectToScreen(Dx(p_Point)) is { } s_At ? (s_At.X, s_At.Y) : null;

    /// <summary>
    /// Instrumentation for a driver: the body painted red (+1) / green (−1) by the hand the side map stores
    /// (as SHIPPED: the mesh's own, not the mirrored view's), pushed as the sticker layer with the side
    /// gate switched off — a picture to hold against the game's own hand map. False without a mesh.
    /// </summary>
    internal bool ShowHandMap()
    {
        if (Canvas.Graph.StickerRegister is not { } s_Register || m_Preview.Surface is not { } s_Surface)
            return false;

        var s_Side = DocumentGraph.StickerLayerSide;
        var s_Visual = new System.Windows.Media.DrawingVisual();
        using (var s_Context = s_Visual.RenderOpen())
            s_Surface.DrawAllSides(s_Context, s_Side);
        var s_Target = new System.Windows.Media.Imaging.RenderTargetBitmap(s_Side, s_Side, 96, 96, PixelFormats.Pbgra32);
        s_Target.Render(s_Visual);
        var s_Pixels = new byte[s_Side * s_Side * 4];
        s_Target.CopyPixels(s_Pixels, s_Side * 4, 0);
        for (var i = 0; i < s_Pixels.Length; i += 4)
        {
            var s_Alpha = s_Pixels[i + 3];
            var s_Grey = s_Alpha >= 128 ? s_Pixels[i] * 255 / s_Alpha : 0;
            var s_Positive = s_Alpha >= 128 && s_Grey < 128;
            s_Pixels[i] = 0;                                        // B
            s_Pixels[i + 1] = (byte) (s_Alpha < 128 ? 0 : s_Positive ? 0 : 255);   // G = −1
            s_Pixels[i + 2] = (byte) (s_Alpha < 128 ? 0 : s_Positive ? 255 : 0);   // R = +1
            s_Pixels[i + 3] = (byte) (s_Alpha < 128 ? 0 : 255);
        }

        var s_Layer = BitmapSource.Create(s_Side, s_Side, 96, 96, PixelFormats.Pbgra32, null, s_Pixels, s_Side * 4);
        s_Layer.Freeze();
        foreach (var s_Gate in Canvas.Graph.Nodes.Where(p_N => p_N.Kind == "StickerSideGate"))
            s_Gate.Params["Enabled"] = "false";
        TouchGraph();
        PushStickerTexture(s_Register, s_Layer, true);
        return true;
    }

    /// <summary>For a placement: covered triangles whose winding agrees / disagrees with the dump's vertex normals (see StickerSurface.WindingAgreement).</summary>
    internal (int Agree, int Disagree)? StickerWindingAgreement(int p_Index) =>
        StickerAt(p_Index) is { } s_Sticker && FrameOf(s_Sticker) is { } s_Frame && m_Preview.Surface is { } s_Surface && m_Preview.MeshVertexNormals is { } s_Normals
            ? s_Surface.WindingAgreement(s_Frame, s_Normals)
            : null;

    /// <summary>A placement resolved on the body: where it sits, its axes and its box. Null when its centre is off the body.</summary>
    private StickerFrame? FrameOf(StickerPlacement p_Sticker)
    {
        if (m_Preview.Surface is not { } s_Surface)
            return null;

        var s_Aspect = StickerAspect(p_Sticker);
        if (!OverlayCacheFor(s_Surface))
            return s_Surface.FrameOf(p_Sticker, s_Aspect);

        var s_Key = PlacementKey(p_Sticker, s_Aspect);
        if (!m_FrameCache.TryGetValue(s_Key, out var s_Frame))
            m_FrameCache[s_Key] = s_Frame = s_Surface.FrameOf(p_Sticker, s_Aspect);
        return s_Frame;
    }

    // ⏱ Orbiting a vehicle ran at 1–2 fps: every mouse move of
    // an orbit rebuilds the overlay, and every placement's frame and outline cast ~50 rays at the body (64 ms a step on the LAV-25's
    // 30 000 triangles). Neither depends on the camera: both are kept per placement — its numbers — and per body, and an orbit only
    // draws them again. CAMO_STICKER_OVERLAY_CACHE=0 = computed every time, as before.
    private static readonly bool s_OverlayCacheOn = Environment.GetEnvironmentVariable("CAMO_STICKER_OVERLAY_CACHE") != "0";
    private readonly Dictionary<string, StickerFrame?> m_FrameCache = new();
    private readonly Dictionary<string, OverlayShape> m_ShapeCache = new();
    private StickerSurface? m_OverlayCacheSurface;

    /// <summary>
    /// What the overlay draws of one placement, camera aside: the outline's segments on the body (and a spatial square's top arrow), the
    /// four corners where its size handles sit, and — a vehicle's emblem square — the picture's triangles laid on the body.
    /// </summary>
    private sealed record OverlayShape(List<(Vector3 A, Vector3 B)> Segments, Vector3[] Corners, View.ShaderPreview.PictureVertex[]? Picture);

    /// <summary>A placement's numbers as a key: two placements with the same key resolve to the same frame on the same body.</summary>
    private static string PlacementKey(StickerPlacement p_Sticker, double p_Aspect)
    {
        static string Triple(double[]? p_V) => p_V is { Length: 3 }
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p_V[0]:R},{p_V[1]:R},{p_V[2]:R}")
            : "-";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{p_Sticker.Image}|{p_Sticker.U:R}|{p_Sticker.V:R}|{p_Sticker.Width:R}|{p_Sticker.Height:R}|{p_Sticker.Rotation:R}|" +
            $"{p_Sticker.Reach:R}|{(p_Sticker.Mirror ? 1 : 0)}|{Triple(p_Sticker.Anchor)}|{Triple(p_Sticker.Normal)}|{p_Aspect:R}");
    }

    /// <summary>The caches follow the body: another body (another mesh, another target) empties them. Whether they are on.</summary>
    private bool OverlayCacheFor(StickerSurface p_Surface)
    {
        if (!ReferenceEquals(p_Surface, m_OverlayCacheSurface) || m_FrameCache.Count > 4096 || m_ShapeCache.Count > 1024)
        {
            m_FrameCache.Clear();
            m_ShapeCache.Clear();
            m_OverlayCacheSurface = p_Surface;
        }

        return s_OverlayCacheOn;
    }

    /// <summary>
    /// The overlay's shape of a placement (see <see cref="OverlayShape"/>), kept per placement and body. The outline follows the body:
    /// each edge of the decal's rectangle is sampled along its length and every sample dropped onto the surface along the sticker's
    /// normal — round a barrel it curves, over a rib it steps; a sample that overhangs the body (the picture wider than its part) breaks
    /// the line. A vehicle's emblem square also carries its picture as a grid laid on the body the same way, a little above it.
    /// </summary>
    private OverlayShape ShapeOf(StickerPlacement p_Sticker, StickerFrame p_Frame, StickerSurface p_Surface)
    {
        var s_Key = PlacementKey(p_Sticker, StickerAspect(p_Sticker));
        if (OverlayCacheFor(p_Surface) && m_ShapeCache.TryGetValue(s_Key, out var s_Known))
            return s_Known;

        var s_Segments = new List<(Vector3 A, Vector3 B)>();
        var s_Edges = new[]
        {
            (X: -p_Frame.HalfWidth, Y: -p_Frame.HalfHeight), (X: p_Frame.HalfWidth, Y: -p_Frame.HalfHeight),
            (X: p_Frame.HalfWidth, Y: p_Frame.HalfHeight), (X: -p_Frame.HalfWidth, Y: p_Frame.HalfHeight),
        };

        const int c_Samples = 10;
        for (var s_Edge = 0; s_Edge < 4; s_Edge++)
        {
            var s_From = s_Edges[s_Edge];
            var s_To = s_Edges[(s_Edge + 1) % 4];
            var s_Spacing = (float) Math.Sqrt(Math.Pow(s_To.X - s_From.X, 2) + Math.Pow(s_To.Y - s_From.Y, 2)) / c_Samples;
            Vector3? s_Previous = null;
            for (var s_Step = 0; s_Step <= c_Samples; s_Step++)
            {
                var s_T = s_Step / (float) c_Samples;
                var s_On = p_Surface.Contact(p_Frame, s_From.X + (s_To.X - s_From.X) * s_T, s_From.Y + (s_To.Y - s_From.Y) * s_T);
                if (s_On == null)
                {
                    s_Previous = null;
                    continue;
                }

                // ⛔ (a square across the edge of the LAV-25's bow showed broken vertices sticking out): two samples that
                // landed on different parts — one on the plate, the next on a wheel below its edge — are not joined
                var s_Lifted = Dx(s_On.Value.Position + s_On.Value.Normal * 0.002f);
                if (s_Previous != null && (!p_Sticker.IsSpatial || Vector3.Distance(s_Previous.Value, s_Lifted) <= s_Spacing * 3f))
                    s_Segments.Add((s_Previous.Value, s_Lifted));
                s_Previous = s_Lifted;
            }
        }

        // A square laid out in space (a vehicle's emblem slot) carries no picture of its own to tell its top by: an arrow on its top edge
        // does — the emblem stands with its top there
        if (p_Sticker.IsSpatial)
        {
            var s_Lift = p_Frame.Normal * 0.002f;
            var s_Tip = Dx(p_Frame.At(0, -p_Frame.HalfHeight * 1.4f) + s_Lift);
            foreach (var s_From in new[] { (X: 0f, Y: -1f), (X: -0.15f, Y: -1.25f), (X: 0.15f, Y: -1.25f) })
                s_Segments.Add((Dx(p_Frame.At(p_Frame.HalfWidth * s_From.X, p_Frame.HalfHeight * s_From.Y) + s_Lift), s_Tip));
        }

        // the size handles' places: where each corner lands on the body, or the corner itself floating just off a part it overhangs
        var s_Corners = s_Edges.Select(p_C => Dx(p_Surface.Contact(p_Frame, p_C.X, p_C.Y) is { } s_On
            ? s_On.Position + s_On.Normal * 0.003f
            : p_Frame.At(p_C.X, p_C.Y) + p_Frame.Normal * 0.003f)).ToArray();

        // ⭐ A vehicle's emblem square shows the sample emblem, as a weapon's slot does — there its shader draws it, here
        // the picture PROJECTED onto the body's own triangles inside the square's box, as the decal will be in the game: each corner of a
        // covered triangle takes the place it projects to on the square (the pixel shader drops what falls outside it), lifted a hair
        // along the square's normal. ⛔ The first version was a grid of points dropped along the normal: across an edge a point found no
        // plate, or a wheel below it, and the picture grew spikes out of the vehicle (broken vertices sticking out).
        View.ShaderPreview.PictureVertex[]? s_Picture = null;
        if (p_Sticker.IsSpatial && EmblemSlot.IsEmblem(p_Sticker))
        {
            var s_Triangles = new List<View.ShaderPreview.PictureVertex>();
            var s_Lift = p_Frame.Normal * 0.002f;
            View.ShaderPreview.PictureVertex Projected(System.Numerics.Vector3 p_Corner)
            {
                var s_Local = p_Frame.LocalOf(p_Corner);
                var s_U = s_Local.X / (2 * p_Frame.HalfWidth) + 0.5f;
                var s_V = s_Local.Y / (2 * p_Frame.HalfHeight) + 0.5f;
                return new View.ShaderPreview.PictureVertex(Dx(p_Corner + s_Lift), new Vector2(p_Sticker.Mirror ? 1f - s_U : s_U, s_V));
            }

            // ⛔ and only the face the square lies on: a triangle turned more than 70° from its normal (the game's impact decals' clip
            // angle, DecalTemplateData's default) or standing out of its plane by more than a third of its half side — a lamp bracket in
            // front of the LAV-25's bow — took the picture stretched across it and stood out of the body seen edge-on
            var s_Depth = Math.Max(p_Frame.HalfWidth, p_Frame.HalfHeight) * 0.35f;
            foreach (var s_Covered in p_Surface.CoveredTriangles(p_Frame))
            {
                var (s_A, s_B, s_C) = p_Surface.CornersOf(s_Covered);
                if (System.Numerics.Vector3.Dot(p_Surface.FaceNormalOf(s_Covered), p_Frame.Normal) < 0.342f ||
                    Math.Abs(p_Frame.LocalOf((s_A + s_B + s_C) / 3f).Z) > s_Depth)
                    continue;
                s_Triangles.Add(Projected(s_A));
                s_Triangles.Add(Projected(s_B));
                s_Triangles.Add(Projected(s_C));
            }

            s_Picture = s_Triangles.ToArray();
        }

        var s_Shape = new OverlayShape(s_Segments, s_Corners, s_Picture);
        if (s_OverlayCacheOn)
            m_ShapeCache[s_Key] = s_Shape;
        return s_Shape;
    }

    /// <summary>A placement's frame on the body, for a driver that checks where it went.</summary>
    internal StickerFrame? StickerFrameOf(int p_Index) => StickerAt(p_Index) is { } s_Sticker ? FrameOf(s_Sticker) : null;

    private static System.Numerics.Vector3 Numerics(Vector3 p_Vector) => new(p_Vector.X, p_Vector.Y, p_Vector.Z);

    private static Vector3 Dx(System.Numerics.Vector3 p_Vector) => new(p_Vector.X, p_Vector.Y, p_Vector.Z);

    /// <summary>
    /// The list index of the emblem square of a slot-only subject (a vehicle) under a panel pixel — the square as it is SEEN: its corners
    /// projected on the panel with the pixel inside them, facing the camera and not hidden behind another part (the ray from the pixel meets
    /// no body well before it); the nearest when several overlap. A saved square not being edited yet is put into editing first
    /// (EditEmblemSlot, what Edit slot does). Null when the pixel is on none.
    /// </summary>
    private int? SlotSquareAt(Vector2 p_Mouse)
    {
        var s_Mesh = m_PreviewMesh ?? "";
        List<(int Index, StickerPlacement Sticker)> Listed() =>
            StickersOnCurrentWeapon().Where(p_S => EmblemSlot.IsEmblem(p_S.Sticker)).ToList();

        var s_Listed = Listed();
        var s_Editing = s_Listed.Count > 0;
        var s_Candidates = s_Editing ? s_Listed.Select(p_S => p_S.Sticker).ToList() : EmblemPlacementsFor(s_Mesh);
        var s_Camera = m_Preview.CameraPoint;
        var s_Hit = m_Preview.Raycast(p_Mouse.X, p_Mouse.Y);
        int? s_Best = null;
        var s_BestDistance = float.MaxValue;
        for (var i = 0; i < s_Candidates.Count; i++)
        {
            if (FrameOf(s_Candidates[i]) is not { } s_Frame)
                continue;

            var s_Origin = Dx(s_Frame.Origin);
            var s_ToCamera = s_Camera - s_Origin;
            if (Vector3.Dot(Dx(s_Frame.Normal), s_ToCamera) <= 0f)
                continue;

            var s_Distance = s_ToCamera.Length();
            if (s_Hit is { } s_Seen && Vector3.Distance(s_Camera, s_Seen.Position) < s_Distance - Math.Max(s_Frame.HalfWidth, s_Frame.HalfHeight) * 1.5f)
                continue;

            var s_Corners = s_Frame.Corners().Select(p_C => m_Preview.ProjectToScreen(Dx(p_C))).ToArray();
            if (s_Corners.Any(p_C => p_C == null) || !InsideQuad(p_Mouse, s_Corners.Select(p_C => p_C!.Value).ToArray()))
                continue;

            if (s_Distance < s_BestDistance)
            {
                s_BestDistance = s_Distance;
                s_Best = i;
            }
        }

        if (s_Best is not { } s_Chosen)
            return null;

        if (!s_Editing)
        {
            EditEmblemSlot();
            s_Listed = Listed();
        }

        return s_Chosen < s_Listed.Count ? s_Listed[s_Chosen].Index : null;
    }

    /// <summary>Whether a panel point lies inside a convex quadrilateral given corner by corner (either winding).</summary>
    private static bool InsideQuad(Vector2 p_Point, Vector2[] p_Corners)
    {
        var s_Sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var s_A = p_Corners[i];
            var s_B = p_Corners[(i + 1) % 4];
            var s_Cross = (s_B.X - s_A.X) * (p_Point.Y - s_A.Y) - (s_B.Y - s_A.Y) * (p_Point.X - s_A.X);
            var s_Side = Math.Sign(s_Cross);
            if (s_Side == 0)
                continue;
            if (s_Sign == 0)
                s_Sign = s_Side;
            else if (s_Side != s_Sign)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The body's normal at a click, for a square laid out in space: in the mesh's own axes, which the view's copy shares (it is only
    /// recentred and rescaled, and its mirror is the world matrix's — a ray is turned into mesh space, the mesh never is).
    /// </summary>
    private static double[] NormalOf(Vector3 p_Normal)
    {
        var s_Normal = Vector3.Normalize(p_Normal);
        return new double[] { s_Normal.X, s_Normal.Y, s_Normal.Z };
    }

    /// <summary>A point of the body in the mesh's own units, for a placement's anchor; null without a body.</summary>
    private double[]? AnchorOf(System.Numerics.Vector3 p_Point)
    {
        if (m_Preview.Surface is not { } s_Surface)
            return null;

        var s_Raw = s_Surface.ToRaw(p_Point);
        return new double[] { s_Raw.X, s_Raw.Y, s_Raw.Z };
    }

    // --- the gizmo: outline on the surface, handles on the panel ------------------------------------------

    private void RebuildStickerOverlay()
    {
        m_StickerHandles.Clear();
        if (!StickerMode || !m_Preview.HasMesh || m_Preview.Surface is not { } s_Surface)
        {
            m_Preview.ClearOverlay();
            return;
        }

        var s_Lines = new List<View.ShaderPreview.OverlayLine>();
        var s_Pictures = new List<View.ShaderPreview.PictureVertex>();
        var s_Mesh = m_PreviewMesh ?? "";
        var s_All = Canvas.Graph.Stickers ?? new List<StickerPlacement>();

        OverlayShape Draw(StickerPlacement p_Sticker, StickerFrame p_Frame, bool p_Selected)
        {
            var s_Shape = ShapeOf(p_Sticker, p_Frame, s_Surface);
            foreach (var (s_A, s_B) in s_Shape.Segments)
                s_Lines.Add(new View.ShaderPreview.OverlayLine(s_A, s_B, p_Selected ? c_OutlineSelected : c_OutlineOther));
            if (s_Shape.Picture != null)
                s_Pictures.AddRange(s_Shape.Picture);
            return s_Shape;
        }

        // A slot-only subject (a vehicle) has no shader to show its slot while it is not being edited — the weapons' slot is drawn by
        // the test emblem in their shader —, so its saved squares are drawn here (Edit slot puts them in the list to move)
        if (StickerSlotOnly && !s_All.Any(p_S => EmblemSlot.IsEmblem(p_S) && p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase)))
            foreach (var s_Saved in EmblemPlacementsFor(s_Mesh))
                if (FrameOf(s_Saved) is { } s_SavedFrame)
                    Draw(s_Saved, s_SavedFrame, false);

        for (var i = 0; i < s_All.Count; i++)
        {
            var s_Sticker = s_All[i];
            if (!s_Sticker.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) || FrameOf(s_Sticker) is not { } s_Frame)
                continue;

            var s_Selected = i == SelectedSticker;
            var s_Shape = Draw(s_Sticker, s_Frame, s_Selected);

            if (!s_Selected)
                continue;

            // Handles: a square at each corner (size) where the corner lands on the body — or at the corner
            // itself, floating just off a part the picture overhangs — and a ring above the sticker (turn),
            // drawn as small camera-facing shapes so they read at any zoom.
            foreach (var s_Centre in s_Shape.Corners)
            {
                var (s_Right, s_Up) = CameraFrameAt(s_Centre);
                AddHandleAt(s_Lines, s_Centre, s_Right, s_Up, StickerTool.Scale, c_ScaleHandle, 4, 0.02f);
            }

            // The turn handle: a ring hung ABOVE the sticker on the panel, from its centre, in the camera's
            // own up direction — always somewhere to grab, whatever the body does around the sticker.
            {
                var s_Anchor = Dx(s_Frame.Origin + s_Frame.Normal * 0.004f);
                var (s_Right, s_Up) = CameraFrameAt(s_Anchor);
                var s_Reach = (0.05f + (float) s_Sticker.Width * 0.6f) * m_Preview.Distance / 3.2f;
                var s_RingAt = s_Anchor + s_Up * s_Reach;
                s_Lines.Add(new View.ShaderPreview.OverlayLine(s_Anchor, s_RingAt, c_RotateHandle));
                AddHandleAt(s_Lines, s_RingAt, s_Right, s_Up, StickerTool.Rotate, c_RotateHandle, 12, 0.024f);
            }
        }

        m_Preview.SetOverlayLines(s_Lines);
        var s_Sample = s_Pictures.Count > 0 ? EmblemSlot.SamplePicture() : null;
        m_Preview.SetOverlayPictures(s_Sample?.Pixels, s_Sample?.Side ?? 0, s_Pictures);
    }

    /// <summary>Two axes facing the camera at a point: right and up on the panel, in mesh space.</summary>
    private (Vector3 Right, Vector3 Up) CameraFrameAt(Vector3 p_At)
    {
        var s_ToCamera = Vector3.Normalize(m_Preview.CameraPoint - p_At);
        var s_Right = Vector3.Cross(Vector3.UnitY, s_ToCamera);
        if (s_Right.LengthSquared() < 1e-6f)
            s_Right = Vector3.UnitX;
        s_Right = Vector3.Normalize(s_Right);
        var s_Up = Vector3.Normalize(Vector3.Cross(s_ToCamera, s_Right));
        return (s_Right, s_Up);
    }

    /// <summary>A small camera-facing polygon at a mesh-space point, registered as a handle for hit tests.</summary>
    private void AddHandleAt(List<View.ShaderPreview.OverlayLine> p_Lines, Vector3 p_Centre, Vector3 p_Right, Vector3 p_Up,
        StickerTool p_Kind, uint p_Colour, int p_Sides, float p_Radius)
    {
        var s_Radius = p_Radius * m_Preview.Distance / 3.2f;
        Vector3 At(int p_I)
        {
            var s_Angle = (p_I % p_Sides) / (double) p_Sides * Math.PI * 2 + (p_Sides == 4 ? Math.PI / 4 : 0);
            return p_Centre + (p_Right * (float) Math.Cos(s_Angle) + p_Up * (float) Math.Sin(s_Angle)) * s_Radius;
        }

        for (var i = 0; i < p_Sides; i++)
            p_Lines.Add(new View.ShaderPreview.OverlayLine(At(i), At(i + 1), p_Colour));

        if (m_Preview.ProjectToScreen(p_Centre) is { } s_Screen)
            m_StickerHandles.Add((p_Kind, s_Screen));
    }

    // --- mouse and keyboard on the 3D view ------------------------------------------------------------------

    /// <summary>True when the press was consumed by sticker mode (the caller must not orbit on it).</summary>
    private bool OnStickerMouseDown(System.Windows.Forms.MouseEventArgs p_Args)
    {
        if (!StickerMode || p_Args.Button != System.Windows.Forms.MouseButtons.Left)
            return false;

        m_StickerLastMouse = p_Args.Location;

        // A keyboard tool is confirmed by the click.
        if (m_StickerTool != StickerTool.None && m_StickerToolFromKey)
        {
            m_StickerTool = StickerTool.None;
            m_StickerToolFromKey = false;
            StickersChanged?.Invoke();
            return true;
        }

        var s_Mouse = new Vector2(p_Args.X, p_Args.Y);
        var s_Graph = Canvas.Graph;
        if (SelectedSticker >= 0 && SelectedSticker < (s_Graph.Stickers?.Count ?? 0))
        {
            var s_Handle = m_StickerHandles
                .Select(p_H => (p_H.Kind, Distance: Vector2.Distance(p_H.At, s_Mouse)))
                .Where(p_H => p_H.Distance <= c_HandleHitRadius)
                .OrderBy(p_H => p_H.Distance)
                .FirstOrDefault();

            if (s_Handle.Kind != StickerTool.None)
            {
                BeginStickerTool(s_Handle.Kind, false, p_Args.Location);
                return true;
            }
        }

        // ⭐ On a vehicle a left click on a square, right on the mesh it is projected onto, SELECTS it instead of creating a new
        // sticker — on the square as it is seen, whatever the ray
        // meets under it; a saved square not being edited yet is put into editing (Edit slot, by itself) and selected. Only a click on
        // bare body adds one.
        if (StickerSlotOnly && SlotSquareAt(s_Mouse) is { } s_Picked)
        {
            SelectSticker(s_Picked);
            StickerSelectionChanged?.Invoke();
            BeginStickerTool(StickerTool.Move, false, p_Args.Location);
            return true;
        }

        var s_Hit = m_Preview.Raycast(p_Args.X, p_Args.Y);
        if (s_Hit == null)
        {
            SelectSticker(-1);
            return true;
        }

        // The sticker under the click: the one whose projection box holds the point hit on the body. A
        // sticker is a decal in space — another part of the weapon sharing its patch of texture is not it.
        var s_Mesh = m_PreviewMesh ?? "";
        var s_Point = Numerics(s_Hit.Value.Position);
        var s_Under = -1;
        for (var i = (s_Graph.Stickers?.Count ?? 0) - 1; i >= 0; i--)
        {
            var s_Sticker = s_Graph.Stickers![i];
            if (!s_Sticker.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) ||
                FrameOf(s_Sticker) is not { } s_Frame || !s_Frame.Contains(s_Point))
                continue;

            s_Under = i;
            break;
        }

        if (s_Under >= 0)
        {
            SelectSticker(s_Under);
            BeginStickerTool(StickerTool.Move, false, p_Args.Location);
            return true;
        }

        if (StickerToPlace is { Length: > 0 } s_Picture)
        {
            if (StickerImage(s_Picture) == null)
            {
                Log($"Stickers: '{System.IO.Path.GetFileName(s_Picture)}' could not be read.");
                return true;
            }

            if (s_Mesh.Length == 0)
            {
                Log("Stickers: pick a weapon first.");
                return true;
            }

            // a slot-only subject (a vehicle) takes the emblem's squares and nothing else, laid out in space where the click landed
            var s_Spatial = StickerSlotOnly;
            if (s_Spatial && s_Picture != EmblemSlot.Image)
            {
                Log("Emblem slots: a vehicle takes only the emblem's squares here — press Edit slot, then click on it.");
                return true;
            }

            Canvas.PushUndo();
            var s_List = StickerList();
            s_List.Add(new StickerPlacement
            {
                Mesh = s_Mesh, Image = s_Picture, U = s_Hit.Value.Uv.X, V = s_Hit.Value.Uv.Y,
                Width = s_Spatial ? c_DefaultSpatialSlotMetres : c_DefaultStickerWidth,
                Anchor = AnchorOf(s_Point), Normal = s_Spatial ? NormalOf(s_Hit.Value.Normal) : null,
            });
            if (!AdoptAnimatedSticker(s_Picture))
            {
                s_List.RemoveAt(s_List.Count - 1);
                return true;
            }

            SelectSticker(s_List.Count - 1, false);
            RefreshStickerLayer();
            StickersChanged?.Invoke();
            StickerSelectionChanged?.Invoke();
            BeginStickerTool(StickerTool.Move, false, p_Args.Location);
            return true;
        }

        SelectSticker(-1);
        return true;
    }

    private void BeginStickerTool(StickerTool p_Tool, bool p_FromKey, System.Drawing.Point p_Mouse)
    {
        var s_Sticker = SelectedPlacement();
        if (s_Sticker == null)
            return;

        Canvas.PushUndo();
        m_StickerToolSnapshot = Canvas.Graph.ToJson();
        m_StickerTool = p_Tool;
        m_StickerToolFromKey = p_FromKey;
        m_StickerLastMouse = p_Mouse;
        m_StickerStartRotation = s_Sticker.Rotation;
        m_StickerStartWidth = s_Sticker.Width;
        m_StickerStartHeight = StickerLayer.HeightOf(s_Sticker, StickerAspect(s_Sticker));
        m_StickerStartFrame = FrameOf(s_Sticker);
        m_StickerGrabLocal = LocalOfScreen(s_Sticker, new Vector2(p_Mouse.X, p_Mouse.Y));

        var s_Centre = StickerCentreOnScreen(s_Sticker);
        m_StickerStartAngle = s_Centre == null ? 0 : Math.Atan2(p_Mouse.Y - s_Centre.Value.Y, p_Mouse.X - s_Centre.Value.X);
        m_StickerStartDistance = s_Centre == null ? 1 : Math.Max(4, Vector2.Distance(s_Centre.Value, new Vector2(p_Mouse.X, p_Mouse.Y)));
    }

    /// <summary>True when the move was consumed by sticker mode.</summary>
    private bool OnStickerMouseMove(System.Windows.Forms.MouseEventArgs p_Args)
    {
        if (!StickerMode)
            return false;

        var s_Dragging = p_Args.Button == System.Windows.Forms.MouseButtons.Left;
        if (m_StickerTool == StickerTool.None || (!s_Dragging && !m_StickerToolFromKey))
        {
            m_StickerLastMouse = p_Args.Location;
            return p_Args.Button == System.Windows.Forms.MouseButtons.Left;
        }

        var s_Sticker = SelectedPlacement();
        if (s_Sticker == null)
        {
            m_StickerTool = StickerTool.None;
            return true;
        }

        switch (m_StickerTool)
        {
            case StickerTool.Move:
                if (m_Preview.Raycast(p_Args.X, p_Args.Y) is { } s_Hit)
                {
                    s_Sticker.U = s_Hit.Uv.X;
                    s_Sticker.V = s_Hit.Uv.Y;
                    s_Sticker.Anchor = AnchorOf(Numerics(s_Hit.Position));
                    // a square laid out in space faces the body where it is dragged to
                    if (s_Sticker.IsSpatial)
                        s_Sticker.Normal = NormalOf(s_Hit.Normal);
                }
                break;

            case StickerTool.Rotate:
                if (StickerCentreOnScreen(s_Sticker) is { } s_Centre)
                {
                    var s_Angle = Math.Atan2(p_Args.Y - s_Centre.Y, p_Args.X - s_Centre.X);
                    var s_Delta = (s_Angle - m_StickerStartAngle) * 180.0 / Math.PI;
                    // Turning the handle clockwise on the panel turns the sticker clockwise on the panel,
                    // from whichever side the body is being looked at.
                    s_Sticker.Rotation = Wrap(m_StickerStartRotation - s_Delta * StickerScreenHandedness(s_Sticker));
                }
                break;

            case StickerTool.Scale:
                if (UniformScaleWanted)
                {
                    // Shift: the shape is kept, both sides follow the mouse's distance from the centre.
                    if (StickerCentreOnScreen(s_Sticker) is { } s_Mid)
                    {
                        var s_Factor = Vector2.Distance(s_Mid, new Vector2(p_Args.X, p_Args.Y)) / m_StickerStartDistance;
                        s_Sticker.Width = Math.Clamp(m_StickerStartWidth * s_Factor, 0.005, 1.5);
                        s_Sticker.Height = Math.Clamp(m_StickerStartHeight * s_Factor, 0.005, 1.5);
                    }
                }
                else if (m_StickerGrabLocal is { } s_Grab && m_StickerStartFrame is { } s_Start &&
                         LocalOfScreen(s_Sticker, new Vector2(p_Args.X, p_Args.Y)) is { } s_Local)
                {
                    // Free: each side follows the mouse on the panel on its own, RELATIVE to where the handle
                    // was grabbed — the sticker never jumps on the press, and a handle grabbed close to one
                    // axis leaves the other side alone (its factor would be noise).
                    var s_Fx = Math.Abs(s_Grab.X) > s_Start.HalfWidth * 0.15 ? Math.Abs(s_Local.X) / Math.Abs(s_Grab.X) : 1.0;
                    var s_Fy = Math.Abs(s_Grab.Y) > s_Start.HalfHeight * 0.15 ? Math.Abs(s_Local.Y) / Math.Abs(s_Grab.Y) : 1.0;
                    s_Sticker.Width = Math.Clamp(m_StickerStartWidth * s_Fx, 0.005, 1.5);
                    s_Sticker.Height = Math.Clamp(m_StickerStartHeight * s_Fy, 0.005, 1.5);
                }
                break;
        }

        m_StickerLastMouse = p_Args.Location;
        RequestStickerRefresh();
        return true;
    }

    private bool OnStickerMouseUp(System.Windows.Forms.MouseEventArgs p_Args)
    {
        if (!StickerMode || p_Args.Button != System.Windows.Forms.MouseButtons.Left)
            return false;

        if (m_StickerTool != StickerTool.None && !m_StickerToolFromKey)
        {
            m_StickerTool = StickerTool.None;
            StickersChanged?.Invoke();
        }

        return true;
    }

    /// <summary>True when the key was consumed by sticker mode.</summary>
    private bool OnStickerKey(System.Windows.Forms.PreviewKeyDownEventArgs p_Args)
    {
        if (!StickerMode)
            return false;

        var s_Sticker = SelectedPlacement();
        switch (p_Args.KeyCode)
        {
            case System.Windows.Forms.Keys.G when s_Sticker != null:
                BeginStickerTool(StickerTool.Move, true, m_StickerLastMouse);
                return true;

            case System.Windows.Forms.Keys.R when s_Sticker != null:
                BeginStickerTool(StickerTool.Rotate, true, m_StickerLastMouse);
                return true;

            case System.Windows.Forms.Keys.S when s_Sticker != null:
                BeginStickerTool(StickerTool.Scale, true, m_StickerLastMouse);
                return true;

            case System.Windows.Forms.Keys.Escape:
                if (m_StickerTool != StickerTool.None && m_StickerToolSnapshot != null)
                {
                    // A cancelled tool puts the sticker back exactly where it was: the placements of the
                    // snapshot, on the SAME graph (replacing the graph would drop the undo history).
                    Canvas.Graph.Stickers = ShaderGraph.FromJson(m_StickerToolSnapshot).Stickers;
                    m_StickerTool = StickerTool.None;
                    m_StickerToolFromKey = false;
                    RefreshStickerLayer();
                    StickersChanged?.Invoke();
                }
                else
                    SelectSticker(-1);
                return true;

            case System.Windows.Forms.Keys.Delete when s_Sticker != null:
                RemoveSticker(SelectedSticker);
                return true;

            case System.Windows.Forms.Keys.Z when p_Args.Control:
                OnUndoClick(this, new RoutedEventArgs());
                RefreshStickerLayer();
                StickersChanged?.Invoke();
                return true;

            case System.Windows.Forms.Keys.Y when p_Args.Control:
                OnRedoClick(this, new RoutedEventArgs());
                RefreshStickerLayer();
                StickersChanged?.Invoke();
                return true;
        }

        return false;
    }

    /// <summary>A sticker's rotation as the editor keeps it (−180..180), for a driver that compares.</summary>
    internal static double WrapDegrees(double p_Degrees) => Wrap(p_Degrees);

    private static double Wrap(double p_Degrees)
    {
        p_Degrees %= 360.0;
        if (p_Degrees > 180.0) p_Degrees -= 360.0;
        if (p_Degrees < -180.0) p_Degrees += 360.0;
        return p_Degrees;
    }

    private Vector2? StickerCentreOnScreen(StickerPlacement p_Sticker) =>
        FrameOf(p_Sticker) is { } s_Frame ? m_Preview.ProjectToScreen(Dx(s_Frame.Origin)) : null;

    /// <summary>
    /// +1 when the sticker's axes appear right-handed on the panel (x right, y down, the body seen from
    /// outside), -1 when they are seen mirrored (from behind, through a double-sided part) — the sign that
    /// keeps a clockwise drag a clockwise turn.
    /// </summary>
    private double StickerScreenHandedness(StickerPlacement p_Sticker)
    {
        if (AxesOnScreen(p_Sticker) is not var (_, s_Ax, s_Ay))
            return 1;

        var s_Cross = s_Ax.X * s_Ay.Y - s_Ax.Y * s_Ay.X;
        return s_Cross >= 0 ? 1 : -1;
    }

    /// <summary>
    /// A panel point in the sticker's own frame (surface units, centre 0,0): the sticker's two axes are
    /// projected onto the panel and the mouse offset from the centre is solved against them. No ray is
    /// cast — a mouse that has slid off the body or onto another part still reads a sensible coordinate.
    /// Null when the axes cannot be seen (the centre off the body, or the two axes on one line).
    /// </summary>
    private Point? LocalOfScreen(StickerPlacement p_Sticker, Vector2 p_Mouse)
    {
        if (AxesOnScreen(p_Sticker) is not var (s_Centre, s_Ax, s_Ay))
            return null;

        var s_Determinant = s_Ax.X * s_Ay.Y - s_Ay.X * s_Ax.Y;
        if (Math.Abs(s_Determinant) < 1e-3f)
            return null;

        var s_M = p_Mouse - s_Centre;
        var s_Lx = (s_M.X * s_Ay.Y - s_Ay.X * s_M.Y) / s_Determinant;
        var s_Ly = (s_Ax.X * s_M.Y - s_M.X * s_Ax.Y) / s_Determinant;
        return new Point(s_Lx, s_Ly);
    }

    /// <summary>
    /// The sticker's centre on the panel and its two axes as panel pixels per surface unit — its frame on
    /// the body, seen by the camera. Null when the centre is off the body or behind the camera.
    /// </summary>
    private (Vector2 Centre, Vector2 Ax, Vector2 Ay)? AxesOnScreen(StickerPlacement p_Sticker)
    {
        if (FrameOf(p_Sticker) is not { } s_Frame)
            return null;

        var s_Step = Math.Max(1e-5f, s_Frame.HalfWidth * 0.5f);
        var s_Centre = m_Preview.ProjectToScreen(Dx(s_Frame.Origin));
        var s_X = m_Preview.ProjectToScreen(Dx(s_Frame.At(s_Step, 0)));
        var s_Y = m_Preview.ProjectToScreen(Dx(s_Frame.At(0, s_Step)));
        if (s_Centre == null || s_X == null || s_Y == null)
            return null;

        return (s_Centre.Value, (s_X.Value - s_Centre.Value) / s_Step, (s_Y.Value - s_Centre.Value) / s_Step);
    }

    /// <summary>A placement's axes on the panel (pixels per surface unit along its own x and y), for a driver that drags along one.</summary>
    internal ((float X, float Y) Ax, (float X, float Y) Ay)? StickerAxesOnScreen(int p_Index) =>
        StickerAt(p_Index) is { } s_Sticker && AxesOnScreen(s_Sticker) is var (_, s_Ax, s_Ay) ? ((s_Ax.X, s_Ax.Y), (s_Ay.X, s_Ay.Y)) : null;

    // --- what the studio's panel drives -------------------------------------------------------------------

    private StickerPlacement? SelectedPlacement()
    {
        var s_All = Canvas.Graph.Stickers;
        return s_All != null && SelectedSticker >= 0 && SelectedSticker < s_All.Count ? s_All[SelectedSticker] : null;
    }

    /// <summary>The placements on the weapon being looked at, with their index in the graph's list.</summary>
    internal IReadOnlyList<(int Index, StickerPlacement Sticker)> StickersOnCurrentWeapon()
    {
        var s_Mesh = m_PreviewMesh ?? "";
        var s_All = Canvas.Graph.Stickers ?? new List<StickerPlacement>();
        return s_All.Select((p_S, p_I) => (p_I, p_S))
            .Where(p_P => p_P.p_S.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase))
            .Select(p_P => (p_P.p_I, p_P.p_S))
            .ToList();
    }

    internal StickerPlacement? StickerAt(int p_Index)
    {
        var s_All = Canvas.Graph.Stickers;
        return s_All != null && p_Index >= 0 && p_Index < s_All.Count ? s_All[p_Index] : null;
    }

    /// <summary>
    /// THE list of placements: the document's, which the graph standing in on the canvas (the no-camo graph
    /// while the weapon is as shipped) shares — one list, never a copy. Re-shared here on every access,
    /// because a list once emptied and dropped (a sticker removed) left the canvas graph growing a list of
    /// its own that the document never saw: the placement showed in the panel, the document held nothing,
    /// the animated sticker was forgotten and nothing drew (a GIF sticker put on the weapon did not show).
    /// </summary>
    private List<StickerPlacement> StickerList()
    {
        var s_Document = DocumentGraph;
        s_Document.Stickers ??= new List<StickerPlacement>();
        if (!ReferenceEquals(Canvas.Graph, s_Document))
            Canvas.Graph.Stickers = s_Document.Stickers;
        return s_Document.Stickers;
    }

    internal void SelectSticker(int p_Index, bool p_Redraw = true)
    {
        var s_Count = Canvas.Graph.Stickers?.Count ?? 0;
        var s_Next = p_Index >= 0 && p_Index < s_Count ? p_Index : -1;
        if (s_Next == SelectedSticker)
            return;

        SelectedSticker = s_Next;
        m_StickerTool = StickerTool.None;
        if (p_Redraw)
            RebuildStickerOverlay();
        StickerSelectionChanged?.Invoke();
    }

    internal void RemoveSticker(int p_Index)
    {
        var s_All = Canvas.Graph.Stickers;
        if (s_All == null || p_Index < 0 || p_Index >= s_All.Count)
            return;

        Canvas.PushUndo();
        s_All.RemoveAt(p_Index);
        // The list stays, empty: it is the document's, shared with the graph on the canvas, and dropping it
        // here left the two graphs with lists of their own (see StickerList).

        SelectedSticker = -1;
        m_StickerTool = StickerTool.None;
        RefreshStickerLayer();
        StickersChanged?.Invoke();
        StickerSelectionChanged?.Invoke();
    }

    /// <summary>
    /// Sets the numbers of one placement from the panel (null = keep; height 0 = the picture's aspect;
    /// reach 0 = the default), with one undo step.
    /// </summary>
    internal void UpdateSticker(int p_Index, double? p_Width = null, double? p_Rotation = null, bool? p_Mirror = null,
        double? p_Height = null, double? p_Reach = null)
    {
        var s_Sticker = StickerAt(p_Index);
        if (s_Sticker == null)
            return;

        Canvas.PushUndo();
        if (p_Width is { } s_Width)
            s_Sticker.Width = Math.Clamp(s_Width, 0.005, 1.5);
        if (p_Height is { } s_Height)
            s_Sticker.Height = s_Height <= 0 ? 0 : Math.Clamp(s_Height, 0.005, 1.5);
        if (p_Rotation is { } s_Rotation)
            s_Sticker.Rotation = Wrap(s_Rotation);
        if (p_Mirror is { } s_Mirror)
            s_Sticker.Mirror = s_Mirror;
        if (p_Reach is { } s_Reach)
            s_Sticker.Reach = s_Reach <= 0 ? 0 : Math.Clamp(s_Reach, 0.05, 3.0);

        RefreshStickerLayer();
        StickersChanged?.Invoke();
    }

    /// <summary>Places a sticker at a texture coordinate of the current weapon — what a click does, for a driver.</summary>
    internal int PlaceSticker(string p_Image, double p_U, double p_V, double p_Width = c_DefaultStickerWidth)
    {
        var s_Mesh = m_PreviewMesh ?? "";
        if (s_Mesh.Length == 0 || StickerImage(p_Image) == null)
            return -1;

        Canvas.PushUndo();
        var s_List = StickerList();
        var s_Placement = new StickerPlacement { Mesh = s_Mesh, Image = p_Image, U = p_U, V = p_V, Width = p_Width };
        s_List.Add(s_Placement);
        if (!AdoptAnimatedSticker(p_Image))
        {
            s_List.Remove(s_Placement);
            return -1;
        }

        // Anchored where the coordinate first lands on the body, as a click would have anchored it.
        if (FrameOf(s_Placement) is { } s_Frame)
            s_Placement.Anchor = AnchorOf(s_Frame.Origin);

        SelectSticker(s_List.Count - 1, false);
        RefreshStickerLayer();
        StickersChanged?.Invoke();
        return s_List.Count - 1;
    }

    /// <summary>Where a panel pixel lands on the weapon, for a driver that places by clicking.</summary>
    internal (double U, double V)? StickerHitAt(float p_X, float p_Y) =>
        m_Preview.Raycast(p_X, p_Y) is { } s_Hit ? (s_Hit.Uv.X, s_Hit.Uv.Y) : null;

    /// <summary>Everything a panel pixel's hit on the body says — the triangle, the point, the normal — for a driver's report.</summary>
    internal (double U, double V, int Triangle, System.Numerics.Vector3 Position, System.Numerics.Vector3 Normal)? StickerHitInfo(float p_X, float p_Y) =>
        m_Preview.Raycast(p_X, p_Y) is { } s_Hit ? (s_Hit.Uv.X, s_Hit.Uv.Y, s_Hit.Triangle, Numerics(s_Hit.Position), Numerics(s_Hit.Normal)) : null;

    /// <summary>The selected sticker's handles on the panel, for a driver that drags them.</summary>
    internal IReadOnlyList<(string Kind, float X, float Y)> StickerHandlesOnScreen() =>
        m_StickerHandles.Select(p_H => (p_H.Kind.ToString(), p_H.At.X, p_H.At.Y)).ToList();

    /// <summary>A placement's centre on the panel, for a driver that turns or sizes it about that point.</summary>
    internal (float X, float Y)? StickerCentreOnScreen(int p_Index) =>
        StickerAt(p_Index) is { } s_Sticker && StickerCentreOnScreen(s_Sticker) is { } s_At ? (s_At.X, s_At.Y) : null;

    /// <summary>Drives the same press / move / release the mouse does, from panel pixels.</summary>
    internal void DriveStickerMouse(string p_Action, float p_X, float p_Y)
    {
        var s_Location = new System.Drawing.Point((int) p_X, (int) p_Y);
        var s_Args = new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, s_Location.X, s_Location.Y, 0);
        switch (p_Action)
        {
            case "down": OnStickerMouseDown(s_Args); break;
            case "move": OnStickerMouseMove(s_Args); break;
            case "up": OnStickerMouseUp(s_Args); break;
        }
    }

    /// <summary>
    /// Points the 3D view: orbit angles, dolly distance and a pan across the screen plane — for a driver's pictures.
    /// <paramref name="p_Target"/> moves the orbit point in the mesh's own space (the recentred, scaled one the
    /// view draws) BEFORE the pan: a pan only slides across the screen plane, so a camera that has to sit INSIDE
    /// an object (the pilot's seat of a cockpit, looking at its panel) needs the point itself moved along the
    /// view axis — the Mi-28 cockpit's panel could not be photographed from the seat without it (2026-09-22).
    /// </summary>
    internal void SetPreviewView(float p_Yaw, float p_Pitch, float p_Distance, float p_PanX = 0f, float p_PanY = 0f,
        SharpDX.Vector3? p_Target = null)
    {
        m_Preview.ResetView();
        m_Preview.Yaw = p_Yaw;
        m_Preview.Pitch = p_Pitch;
        m_Preview.Distance = Math.Clamp(p_Distance, 0.05f, 100f);
        if (p_Target is { } s_Target)
            m_Preview.Target = s_Target;
        if (p_PanX != 0f || p_PanY != 0f)
            m_Preview.Pan(p_PanX, p_PanY);
        if (StickerMode)
            RebuildStickerOverlay();
    }

    /// <summary>
    /// Puts the view's eye where the first-person camera sits (to simulate how the soldier would look in first person with the weapon
    /// in hand; the camera is the skeleton's CameraJoint). Eye, look and up are in the weapon mesh's OWN raw units — a
    /// 1P weapon mesh lives in the space of the skeleton's weapon root (Wep_Root, whose rest pose is the origin) — and go through the
    /// recentring and the lateral mirror the mesh is drawn with. <paramref name="p_FovDegrees"/> is the vertical field of view. A null
    /// eye goes back to the orbit, and so does any orbit, pan or dolly (<see cref="View.ShaderPreview.ResetView"/> drops it too).
    /// False when no weapon body is loaded to place it against.
    /// </summary>
    internal bool SetFirstPersonCamera(System.Numerics.Vector3? p_Eye, System.Numerics.Vector3 p_Look = default,
        System.Numerics.Vector3 p_Up = default, float? p_FovDegrees = null)
    {
        if (p_Eye is not { } s_Eye)
            m_Preview.FixedCamera = null;
        else
        {
            if (m_Preview.Surface is not { } s_Surface || p_Look == default || p_Up == default)
                return false;

            // a field of view asked for is the bar's from now on: the number over the view is always the one it is drawn with
            if (p_FovDegrees is { } s_Asked)
                FirstPersonFov = s_Asked;

            // directions as differences of converted points: the recentring's scale is uniform, the mirror is not a rotation
            var s_At = s_Surface.FromRaw(s_Eye);
            var s_Ahead = s_Surface.FromRaw(s_Eye + p_Look) - s_At;
            var s_Above = s_Surface.FromRaw(s_Eye + p_Up) - s_At;
            static Vector3 Sharp(System.Numerics.Vector3 p_V) => new(p_V.X, p_V.Y, p_V.Z);
            m_Preview.FixedCamera = (m_Preview.ToDrawn(Sharp(s_At)), Vector3.Normalize(m_Preview.ToDrawn(Sharp(s_Ahead))),
                Vector3.Normalize(m_Preview.ToDrawn(Sharp(s_Above))), VerticalFovOf(FirstPersonFov));
        }

        if (StickerMode)
            RebuildStickerOverlay();
        return true;
    }

    /// <summary>Whether the view is on the first-person eye rather than the orbit.</summary>
    internal bool FirstPersonView => m_Preview.FixedCamera != null;

    /// <summary>
    /// The first-person view's field of view AS THE GAME'S SETTING NAMES IT, in degrees — the FOV bar's slider over the 3D view,
    /// kept in the settings. Setting it moves the slider, the numbers beside it and, on the eye, the view itself.
    /// ⛔ The game's number is NOT the vertical field of view: a game capture at "90" (2560×1440, M416 at rest) fits a vertical 55.6–57.8°
    /// against three landmarks of the weapon (rms 1–11 px) = 86–89° HORIZONTAL at 16:9 — its 90 read as horizontal on a 16:9 screen.
    /// Drawn as vertical 90 the weapon came out 1.8 times too small.
    /// </summary>
    internal float FirstPersonFov
    {
        get => (float) m_Settings.FirstPersonFov;
        set
        {
            var s_Degrees = (float) Math.Round(Math.Clamp(value, FirstPersonFovSlider.Minimum, FirstPersonFovSlider.Maximum));
            m_Settings.FirstPersonFov = s_Degrees;
            if (Math.Abs(FirstPersonFovSlider.Value - s_Degrees) > 0.001)
                FirstPersonFovSlider.Value = s_Degrees;
            FirstPersonFovText.Text = $"{s_Degrees:0}°";
            FirstPersonFovVertical.Text = $"(horizontal at 16:9 = {VerticalFovOf(s_Degrees) * 180f / MathF.PI:0.0}° vertical)";
            if (m_Preview.FixedCamera is { } s_Fixed && Math.Abs(s_Fixed.FovY - VerticalFovOf(s_Degrees)) > 1e-6f)
            {
                m_Preview.FixedCamera = s_Fixed with { FovY = VerticalFovOf(s_Degrees) };
                if (StickerMode)
                    RebuildStickerOverlay();
            }
        }
    }

    /// <summary>The vertical field of view (radians) of the game's FOV setting: that number is horizontal on a 16:9 screen.</summary>
    internal static float VerticalFovOf(float p_GameDegrees) =>
        2f * MathF.Atan(MathF.Tan(p_GameDegrees * MathF.PI / 360f) * 9f / 16f);

    /// <summary>The FOV bar over the 3D view shows while the view is on the soldier's eye, and leaves with it.</summary>
    private void SyncFirstPersonBar() =>
        FirstPersonBar.Visibility = m_Preview.FixedCamera != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What the FOV bar shows, for a driver: whether it is up, and its number.</summary>
    internal (bool Shown, string Text) FirstPersonBarState => (FirstPersonBar.Visibility == Visibility.Visible, FirstPersonFovText.Text);

    /// <summary>
    /// Draws the soldier's first-person arms with the weapon on screen (the soldier in first person, weapon in hand): a dump of the
    /// arms already posed around THIS weapon in its own units, added as CONTEXT — drawn as it ships, with its own
    /// material's art (its mesh names it: <paramref name="p_ArmsMesh"/>), never the edited shader's, never under a click — without moving
    /// the weapon's framing or its surface. A null file takes them away. The error, or null.
    /// </summary>
    internal string? ShowFirstPersonArms(string? p_ArmsFile, string p_ArmsMesh = "")
    {
        if (p_ArmsFile == null)
        {
            if (m_Preview.RemoveAppendedContext())
                RegisterSectionArt();
            return null;
        }

        if (!System.IO.File.Exists(p_ArmsFile))
            return $"no arms dump at '{p_ArmsFile}'";

        var s_Error = m_Preview.AppendContextMesh(p_ArmsFile, p_ArmsMesh);
        if (s_Error != null)
            return s_Error;

        RegisterSectionArt();
        return null;
    }

    /// <summary>Whether the first-person arms are drawn (<see cref="ShowFirstPersonArms"/>).</summary>
    internal bool FirstPersonArmsShown => m_Preview.HasAppendedContext;

    internal void SetStickerLayerSize(int p_Side)
    {
        // The size is the DOCUMENT's; the no-camo graph standing in on the canvas mirrors it.
        var s_Size = p_Side is 512 or 1024 or 2048 && p_Side <= ShaderGraph.MaxStickerLayerSide ? p_Side : (int?) null;
        DocumentGraph.StickerLayerSize = s_Size;
        Canvas.Graph.StickerLayerSize = s_Size;
        // The maps are read at this size too: the nodes carry it.
        EnsureStickerAnimationEverywhere();
        RefreshStickerLayer();
        StickersChanged?.Invoke();
    }
}
