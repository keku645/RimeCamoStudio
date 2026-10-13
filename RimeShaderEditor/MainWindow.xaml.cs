using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RimeShaderEditor.Emit;
using RimeShaderEditor.Graph;
using RimeShaderEditor.View;

namespace RimeShaderEditor;

public partial class MainWindow : Window
{
    private string? m_GraphPath;
    private readonly Dictionary<string, ImageSource> m_TexturePreviews = new();

    /// <summary>
    /// Beside each thumbnail, the game's sRGB flag for that register's texture (see <see cref="TextureIsSrgb"/>).
    /// Written wherever <see cref="m_TexturePreviews"/> is written, cleared with it, and pushed with the pixels:
    /// the flag is part of the art, not of the preview.
    /// </summary>
    private readonly Dictionary<string, bool> m_TextureSrgb = new();

    /// <summary>Textures whose flag had to be guessed (no .dds beside the thumbnail), each reported once.</summary>
    private readonly HashSet<string> m_SrgbGuessed = new(StringComparer.OrdinalIgnoreCase);
    private readonly EditorSettings m_Settings = EditorSettings.Load();

    public MainWindow()
    {
        InitializeComponent();


        // Settings that live outside this window's own controls, applied before anything reads them (the
        // shortcut help line below prints the LIVE table, so a remapped key shows its real letter).
        View.GridCanvas.ZoomSpeed = m_Settings.CanvasZoomSpeed;
        GraphCanvas.ApplyShortcuts(m_Settings.NodeShortcuts);


        // The graph name reaches disk (the emitted .hlsl/.dxbc are named from it) and is the default mod name at
        // bake time, so it is edited here rather than being stuck at "Untitled" with every graph overwriting
        // the last one's artifacts.
        GraphNameBox.TextChanged += (_, _) =>
        {
            Canvas.Graph.Name = GraphNameBox.Text.Trim();
            RefreshTabBar();
        };
        Closing += (_, _) => PersistSettings();

        // Created-variation NAMES used to persist across restarts while their canvas drafts do not — a
        // stale name selected in a fresh session bakes whatever happens to be on screen under it. The
        // session therefore starts CLEAN: every variation list is wiped, and the dropdown repopulates only
        // from what a loaded master file actually embeds (the user's decision, on the user's request).
        try
        {
            if (Directory.Exists(TexCacheDir))
            {
                var s_Stale = Directory.GetFiles(TexCacheDir, "*.variations.json");
                foreach (var s_File in s_Stale)
                    File.Delete(s_File);

                if (s_Stale.Length > 0)
                    Log($"Cleared {s_Stale.Length} persisted variation list(s) — load a saved graph to " +
                        "bring its variations back.");
            }
        }
        catch
        {
            // A locked cache file only costs a stale dropdown entry; it must never stop the window.
        }

        // The instance library registers its palette entries BEFORE the tree is built, so saved fragments
        // are placeable from the first click of the session.
        var s_Instances = Graph.InstanceLibrary.Refresh(Log);
        BuildPalette();
        BuildLevelList();
        if (s_Instances > 0)
            Log($"{s_Instances} instance graph(s) loaded from {Graph.InstanceLibrary.Directory}.");

        Canvas.TexturePreviewProvider = p_Register =>
            m_TexturePreviews.TryGetValue(p_Register, out var s_Image) ? s_Image : null;

        Canvas.SelectionChanged += (_, _) => BuildProperties();
        Canvas.GraphChanged += (_, _) =>
        {
            PromoteOnGraphEdit();
            SchedulePreview();
        };
        Canvas.SearchRequested += OnSearchRequested;

        SetUpPreview();
        Closing += (_, _) => m_Preview.Dispose();

        // Same rule at startup as after a settings change: the fold is a UDK-vocabulary action, and the
        // canvas has to know the vocabulary too or its shortcuts speak the other one.
        View.GraphCanvas.UdkStyle = m_Settings.UdkNodeStyle;
        CollapseButton.Visibility = m_Settings.UdkNodeStyle ? Visibility.Visible : Visibility.Collapsed;

        // The session opens on a canvas holding nothing but its OUTPUT node — the one node every shader ends
        // at, in whichever vocabulary is being authored in. (It used to open completely empty; that turned out
        // to mean starting every graph by hunting for the root.)
        // (in camo mode the tab the first pick aims is the ORIGINALS' — see EditorTab.Original; the flag means nothing elsewhere)
        m_Tabs.Add(new EditorTab { Graph = NewGraphWithRoot(), Original = true });
        ActivateTab(0);

        Log("Canvas: right-drag pans, wheel zooms, drag a node in from the list on the left.");
        Log("Preview: LMB orbit, MMB pan, RMB/wheel zoom, hold L + LMB to turn the light, F to frame.");
        Log("Drag output square -> input square to wire. ALT + left-click on a wire breaks it.");
        Log("Ctrl+Z undo / Ctrl+Y redo. Right-CLICK the canvas for the node search (right-drag still pans).");
        Log("Node shortcuts (hold the key and left-click): " + string.Join(", ", GraphCanvas.Shortcuts
            .Where(p_S => p_S.Key is >= Key.A and <= Key.Z)
            .OrderBy(p_S => p_S.Key.ToString())
            .Select(p_S => $"{p_S.Key}={Palette.PlacedTitle(p_S.Kind, m_Settings.UdkNodeStyle)}")));

        // ⛔ ASKED BEFORE THE FIRST LOG LINE ABOUT THE GAME, not after: "BF3 install not detected" as the
        // opening line of a program someone just installed reads as a broken tool, when all it means is that
        // nobody has been asked yet. Never in a headless run — a modal dialog there is a hang, not a prompt.
        if (m_Settings.IsFirstRun && !Headless)
        {
            var s_FirstRun = new View.FirstRunWindow(m_Settings);
            s_FirstRun.ShowDialog();

            m_Settings.GamePath = s_FirstRun.GamePath;
            m_Settings.OutputFolder = s_FirstRun.OutputFolder;
            m_Settings.Save();
        }

        if (GamePath.Length > 0)
            Log($"BF3 found at {GamePath}");
        else
            Log("BF3 install not detected — set it in Settings ▸ Folders ▸ Game folder to enable texture previews.");

        // Previews are cached on disk, so only the very first load per shader has to mount the game.
        if (LoadCachedPreviews() == 0)
            Log("Texture thumbnails are empty: press 'Reload from game' to read them from the game.");
    }

    private readonly ShaderPreview m_Preview = new();
    private System.Windows.Forms.Panel? m_PreviewPanel;
    private System.Windows.Threading.DispatcherTimer? m_PreviewDebounce;
    private DateTime m_PreviewClock = DateTime.UtcNow;
    private System.Drawing.Point m_PreviewDragOrigin;
    private bool m_PreviewInitialised;

    private void SetUpPreview()
    {
        m_PreviewPanel = new System.Windows.Forms.Panel { BackColor = System.Drawing.Color.Black };
        PreviewHost.Child = m_PreviewPanel;

        m_PreviewPanel.MouseDown += (_, s_Args) =>
        {
            m_PreviewDragOrigin = s_Args.Location;
            m_PreviewPanel.Focus();
            OnStickerMouseDown(s_Args);
        };

        m_PreviewPanel.MouseUp += (_, s_Args) => OnStickerMouseUp(s_Args);

        // Unreal's asset-preview mapping: LMB orbits, MMB pans, RMB dollies, wheel zooms, and holding L turns
        // the preview light instead of the camera. F frames the object. In sticker mode the left button
        // belongs to the stickers and the RIGHT button orbits instead of dollying (the wheel still zooms).
        m_PreviewPanel.MouseMove += (_, s_Args) =>
        {
            var s_Sensitivity = 0.01f * (float) m_Settings.PreviewOrbitSpeed;
            var s_Dx = (s_Args.X - m_PreviewDragOrigin.X) * s_Sensitivity;
            var s_Dy = (s_Args.Y - m_PreviewDragOrigin.Y) * s_Sensitivity;

            var s_Button = s_Args.Button;
            if (StickerMode && OnStickerMouseMove(s_Args))
            {
                m_PreviewDragOrigin = s_Args.Location;
                return;
            }

            if (s_Button == System.Windows.Forms.MouseButtons.None)
                return;

            var s_LightKey = Keyboard.IsKeyDown(Key.L);
            var s_Orbit = StickerMode ? System.Windows.Forms.MouseButtons.Right : System.Windows.Forms.MouseButtons.Left;

            if (s_Button == System.Windows.Forms.MouseButtons.Left && s_LightKey && !StickerMode)
            {
                m_Preview.LightYaw += s_Dx;
                m_Preview.LightPitch = Math.Clamp(m_Preview.LightPitch - s_Dy, -1.45f, 1.45f);
            }
            else if (s_Button == s_Orbit)
            {
                // the first-person eye is a still picture: orbiting, panning or dollying goes back to the orbit
                m_Preview.FixedCamera = null;
                // Vertical is +dy: dragging down lifts the camera so the top face comes into view.
                m_Preview.Yaw += s_Dx;
                m_Preview.Pitch = Math.Clamp(m_Preview.Pitch + s_Dy, -1.45f, 1.45f);
                if (StickerMode)
                    RebuildStickerOverlay();
            }
            else if (s_Button == System.Windows.Forms.MouseButtons.Middle)
            {
                m_Preview.FixedCamera = null;
                m_Preview.Pan(s_Dx * 0.35f, s_Dy * 0.35f);
                if (StickerMode)
                    RebuildStickerOverlay();
            }
            else if (s_Button == System.Windows.Forms.MouseButtons.Right)
            {
                // Vertical drag dollies, matching orbit-mode RMB. Multiplicative, so each step moves a
                // fraction of the remaining distance: the zoom feels unlimited going in and never crosses zero.
                m_Preview.FixedCamera = null;
                m_Preview.Distance = Math.Clamp(
                    m_Preview.Distance * (float) Math.Exp(s_Dy * 0.75f), 0.05f, 100f);
            }

            m_PreviewDragOrigin = s_Args.Location;
        };

        m_PreviewPanel.MouseWheel += (_, s_Args) =>
        {
            m_Preview.FixedCamera = null;
            m_Preview.Distance = Math.Clamp(
                m_Preview.Distance * (float) Math.Exp(-s_Args.Delta * 0.0011f *
                    (float) m_Settings.PreviewZoomSpeed), 0.05f, 100f);
            if (StickerMode)
                RebuildStickerOverlay();
        };

        m_PreviewPanel.PreviewKeyDown += (_, s_Args) =>
        {
            if (OnStickerKey(s_Args))
                return;

            if (s_Args.KeyCode == System.Windows.Forms.Keys.F)
            {
                m_Preview.ResetView();
                if (StickerMode)
                    RebuildStickerOverlay();
            }
        };

        m_PreviewPanel.Resize += (_, _) =>
        {
            if (m_PreviewInitialised)
                m_Preview.Resize(m_PreviewPanel.Width, m_PreviewPanel.Height);
        };

        // The first-person view's FOV bar: shown while the view is on the soldier's eye, its slider the one knob of that view's
        // field of view (kept in the settings the window saves).
        FirstPersonFov = (float) m_Settings.FirstPersonFov;
        FirstPersonFovSlider.ValueChanged += (_, _) => FirstPersonFov = (float) FirstPersonFovSlider.Value;
        m_Preview.FixedCameraChanged += SyncFirstPersonBar;

        // a vehicle's emblem squares lie on what the view draws solid, known after a frame: when that changes, the squares are laid again
        // (on the next turn of the dispatcher, never inside the frame)
        m_Preview.SolidSectionsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (StickerMode && StickerSlotOnly)
                RebuildStickerOverlay();
        });

        // The panel has no handle until it is shown, so initialise on the first WPF frame.
        System.Windows.Media.CompositionTarget.Rendering += OnPreviewFrame;
    }

    private void OnPreviewFrame(object? p_Sender, EventArgs p_Args)
    {
        if (m_PreviewPanel == null || m_PreviewPanel.Width <= 0 || m_PreviewPanel.Height <= 0)
            return;

        if (!m_PreviewInitialised)
        {
            m_PreviewInitialised = true;
            if (!m_Preview.Initialise(m_PreviewPanel.Handle, m_PreviewPanel.Width, m_PreviewPanel.Height))
            {
                PreviewStatus.Text = $"Preview unavailable: {m_Preview.LastError}";
                System.Windows.Media.CompositionTarget.Rendering -= OnPreviewFrame;
                return;
            }

            // Textures decoded before the device existed were dropped on the floor, so re-push them now that
            // there is somewhere to put them.
            PushPreviewTextures();
            SchedulePreview();
        }

        if (!m_Preview.Ready)
            return;

        m_Preview.Time = PreviewTimeOverride ?? (float) (DateTime.UtcNow - m_PreviewClock).TotalSeconds;
        m_Preview.Render();
    }

    /// <summary>
    /// Recompiles the graph into the preview, debounced so typing in a field does not trigger a compile per
    /// keystroke.
    /// </summary>
    private void SchedulePreview()
    {
        // every change comes through here (or through PushExternalValues): an original tab is looked at again once it settles
        ScheduleOriginalCheck();

        if (!m_Preview.Ready)
            return;

        m_PreviewDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };

        m_PreviewDebounce.Tick -= OnPreviewDebounce;
        m_PreviewDebounce.Tick += OnPreviewDebounce;
        m_PreviewDebounce.Stop();
        m_PreviewDebounce.Start();
    }

    private void OnPreviewDebounce(object? p_Sender, EventArgs p_Args)
    {
        m_PreviewDebounce?.Stop();
        RebuildPreviewShader();
    }

    private void RebuildPreviewShader()
    {
        if (!m_Preview.Ready)
            return;

        // Every graph change comes through here, so this is where "is there a camo to show yet?" is
        // re-asked; the answer only changes what the sections draw with, the compile below is the same.
        // Re-asked while the look is on as well: a registration made before the preview had its device
        // ("no device", the headless window's first pick) is retried here, where the device exists.
        if (CamoMode)
        {
            var s_Factory = CamoFactoryLook();
            if (s_Factory || s_Factory != m_Preview.FactoryLook || s_Factory != m_FactoryLookShown)
                SyncFactoryLook();
        }

        // ⛔ ONE OF THE OBJECT'S OTHER MATERIALS IS ON THE CANVAS: its graph never becomes the shader the camo
        // draws with. It shows on its own section, and only once it really differs from the stock one.
        if (m_MaterialEditKey != null)
        {
            RefreshMaterialEdits();
            return;
        }

        // The detected contract shapes PsIn/PsOut; null falls back to the rigid-mesh layout.
        var s_Result = new HlslEmitter { Contract = m_Contract }.Emit(Canvas.Graph);
        if (!s_Result.Ok)
        {
            PreviewPaused("Preview paused: " + s_Result.Errors[0]);
            return;
        }

        LastPreviewHlsl = s_Result.Hlsl;

        try
        {
            // Compiled in-process rather than by launching fxc, so the loop stays interactive.
            using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                s_Result.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);

            if (s_Compiled.Bytecode == null)
            {
                PreviewPaused("Preview paused: shader did not compile.");
                return;
            }

            if (m_Preview.SetAuthoredShader(s_Compiled.Bytecode.Data))
            {
                PreviewStatus.Text = PreviewHelpLine;
                m_LastPreviewPause = null;

                // ⭐ EVERY MATERIAL WEARS THE CAMO OVER ITS OWN ART. The compiled camo is KEPT (rather than
                // only used here) because the dressing is a function of the STATE, not of this compile:
                // picking a material, leaving a material edit, switching tab and re-picking the same object
                // all change who must wear what without recompiling anything. See RefreshMaterialDress.
                //
                // ⛔ ONLY WHEN IT IS THE CAMO'S. A material being EDITED compiles its own graph through here
                // too, and remembering that one as "the camo" dressed every camo material with the edited
                // material's shader the moment anything re-asserted the dressing — measured on the LAV-25:
                // coming back from M_KitAtlas left the hull wearing the kits shader.
                if (m_MaterialEditKey == null)
                    m_AuthoredBytecode = s_Compiled.Bytecode.Data;

                ApplyMaterialDress(s_Compiled.Bytecode.Data);

                // In camo mode every rebuild is written down: "I edited the graph and nothing changed" is
                // then answerable from the Output — either the recompile is there, or it is not.
                if (CamoMode)
                    Log($"Camo: preview recompiled (#{m_Preview.AuthoredShaderVersion}) from " +
                        $"{Canvas.Graph.Nodes.Count} node(s), {Canvas.Graph.Connections.Count} wire(s).");
            }
            else
                PreviewPaused($"Preview paused: {m_Preview.LastError}");
        }
        catch (Exception s_Exception)
        {
            // A compile error is normal while a graph is half-wired; keep the last good shader on screen.
            PreviewPaused("Preview paused: " + s_Exception.Message.Split('\n')[0].Trim());
        }
    }

    /// <summary>The last pause reason written to the Output, so a stuck graph is reported once, not per keystroke.</summary>
    private string? m_LastPreviewPause;

    /// <summary>What the last dress was built for, so an unchanged object is not re-registered every keystroke.</summary>
    private string? m_MaterialDressKey;

    /// <summary>
    /// The same key WITHOUT the typed numbers, and what each dressed material was registered as: when only the numbers moved
    /// (a slider of the simple form, a value typed on a node) they are re-fed into those registrations and nothing else is
    /// rebuilt — a re-register decodes every texture of every material, and a slider fires on every tick of its drag.
    /// </summary>
    private string? m_MaterialDressArtKey;
    private List<(string Name, string Shader, int MaterialId)> m_MaterialDressEntries = new();

    /// <summary>
    /// The last camo bytecode that compiled, so the per-material dressing can be rebuilt WITHOUT a recompile.
    /// </summary>
    private byte[]? m_AuthoredBytecode;

    /// <summary>
    /// Re-asserts the per-material dressing from the last compiled camo.
    ///
    /// ⛔⛔⛔ WHY THIS EXISTS (LAV-25): on load the first material, main_M, looked wrong; any edit fixed every
    /// material except M_KitAtlas; switching to that one fixed it and broke main_M again — only the
    /// SELECTED material ever looked right. The dressing was
    /// built ONLY inside RebuildPreviewShader, so it existed as a side effect of COMPILING. Everything else
    /// that re-dresses the object does not compile — leaving a material edit, activating a tab, re-picking
    /// the same mesh, the factory-look sync — and two of those actively destroy it: ClearForeignShaders()
    /// empties the registry the dress names, and a material EDIT makes the target its own shader so the
    /// object drops below two camo materials and the dress is cleared. With no dress, every camo section
    /// falls back to the ONE texture set the authored shader holds — and that set is refilled with the
    /// PICKED material's art (OnMaterialPicked / FillMaterialList), which is precisely "the one I selected
    /// is the only one that looks right".
    ///
    /// Idempotent: the key short-circuits an unchanged object, so this can be called from anywhere that
    /// changes what the object wears.
    /// </summary>
    private void RefreshMaterialDress()
    {
        if (s_NoDressRefresh)
            return;

        if (m_AuthoredBytecode is { Length: > 0 } s_Bytecode)
            ApplyMaterialDress(s_Bytecode);
    }

    /// <summary>
    /// The bisection knob that gives this fix its negative control (`CAMO_NO_DRESS_REFRESH=1`): with it set,
    /// the dressing goes back to being a side effect of the recompile and stops noticing that the foreign
    /// registry was emptied — exactly the behaviour that was reported. One binary, two runs, same seam.
    /// </summary>
    private static readonly bool s_NoDressRefresh =
        Environment.GetEnvironmentVariable("CAMO_NO_DRESS_REFRESH") == "1";

    /// <summary>
    /// Registers the compiled camo ONCE PER MATERIAL of the object on screen, each with that material's own
    /// textures and numbers, and points every section at its own.
    ///
    /// ⛔ THE PROBLEM IT SOLVES, MEASURED (2026-09-21, LAV-25): the authored shader carries ONE texture set
    /// for the whole mesh. A weapon has one body material, so nobody noticed; a vehicle has several — its
    /// hull and its ATGM launchers BOTH wear vehiclepreset_mud — and the single set dressed the hull in the
    /// launchers' art. On screen: a flat grey vehicle under any camo, which reads as "the camo does not work".
    ///
    /// ⛔ AND IT NEEDS THE MATERIAL ID, which is why the scan had to learn to print it: two materials on the
    /// same preset are indistinguishable by shader name, and that is all the studio used to have.
    ///
    /// Does nothing when the object has one camo-bearing material (every weapon), so that path is untouched.
    /// </summary>
    /// <summary>(mesh|shader) pairs already warned about a camo preset without cached bytecode — once each, the dress is rebuilt on every edit.</summary>
    private readonly HashSet<string> m_DressWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a section wearing p_Other can be drawn with the document's shader, compiled for p_Target:
    /// every external texture p_Other's own bytecode reads must sit on the SAME register in p_Target's.
    /// Measured on the LAV family: vehiclepreset_nomud and 1uvset_mud read Diffuse t1, Camo t2, Scratches
    /// t3, Specular t4, Normal t6 exactly where vehiclepreset_mud does, so the graph edited on the hull draws
    /// the CROWS and the armour too (what the user saw and approved); vehiclepreset_mud_decals moves the
    /// specular to t5 and adds a Decal at t3, and the jet's _decals sibling shifts CamoA to t4 — those drew
    /// dark blocks under the target's shader. False, conservatively, when either contract is not cached.
    /// </summary>
    private bool RegistersLineUp(string p_Target, string p_Other)
    {
        if (ContractFromCache(p_Target) is not { } s_Target || ContractFromCache(p_Other) is not { } s_Other)
            return false;

        foreach (var s_Resource in s_Other.Resources)
        {
            if (!s_Resource.IsTexture || !s_Resource.Name.StartsWith("texture_", StringComparison.OrdinalIgnoreCase))
                continue;

            if (s_Target.RegisterForExternalTexture(s_Resource.Name["texture_".Length..]) != s_Resource.Register)
                return false;
        }

        return true;
    }

    private void ApplyMaterialDress(byte[] p_Bytecode)
    {
        // ⛔ NOT WHILE ANOTHER MATERIAL'S GRAPH IS ON THE CANVAS — and the dress is LEFT AS IT IS. Looking at a
        // material no longer touches the object (ShowMaterialGraph, 2026-09-23): the dress on screen is the
        // camo's and stays; rebuilding it now would read the material's shader out of the target box. (It used
        // to be CLEARED here, when looking re-aimed the whole preview at the material.)
        if (m_MaterialEditKey != null)
            return;

        if (!CamoMode || m_CamoSlotsForMaterial == null || m_CamoSectionMaterialsOf == null ||
            m_PreviewMesh is not { Length: > 0 } s_Mesh)
        {
            m_Preview.MaterialDress.Clear();
            m_MaterialDressKey = null;
            m_MaterialDressArtKey = null;
            return;
        }

        var s_Sections = m_Preview.MeshSections;
        var s_Known = m_CamoSectionMaterialsOf(s_Mesh);

        // ⛔ The log's list is trusted when it is a PREFIX of the sections — one entry per section OF THIS
        // MESH, same shaders, in order — exactly as the material picker does it. A list that does not line
        // up would dress sections from another mesh; one that is SHORTER is the normal case for a vehicle
        // whose belts are drawn with it, and demanding equal lengths threw the whole dressing away.
        if (s_Known.Count > s_Sections.Count ||
            !s_Known.Zip(s_Sections).All(p_P => p_P.Second.Shader.Equals(p_P.First.Shader, StringComparison.OrdinalIgnoreCase)))
        {
            m_Preview.MaterialDress.Clear();
            m_MaterialDressKey = null;
            m_MaterialDressArtKey = null;
            return;
        }

        var s_Target = TargetShaderBox.Text.Trim();
        var s_Native = DocumentGraph.NativeCamo;
        var s_Wanted = new List<(int Section, int MaterialId)>();
        for (var i = 0; i < s_Known.Count; i++)
            if (m_Preview.IsTargetSection(s_Sections[i]) && s_Known[i].MaterialId >= 0)
                s_Wanted.Add((i, s_Known[i].MaterialId));

        // One material takes the camo: the single set the authored shader already holds IS that material's —
        // unless, on a vehicle, that one material wears a preset whose registers contradict the document's
        // (the AH-1Z's pilot cockpit mesh: its exterior wears vehiclepreset_nomud under a tab aimed at the
        // hull's vehiclepreset_jet, Normal at t6 against t7). Then it goes through the dress below like any
        // sibling, and draws with the game's shader of its own preset, the camo laid on it.
        var s_TargetIsVehicle = s_Target.Contains("vehiclepreset", StringComparison.OrdinalIgnoreCase);
        var s_NeedsOwn = s_TargetIsVehicle && s_Wanted.Any(p_W =>
            !s_Sections[p_W.Section].Shader.Equals(s_Target, StringComparison.OrdinalIgnoreCase) &&
            !RegistersLineUp(s_Target, s_Sections[p_W.Section].Shader));

        // ⛔⛔ …OR when ANOTHER material of this mesh wears the same shader, even one that is not this subject's to edit (reported with
        // a screenshot: a custom snow texture on the camo node broke the whole first-person upper body). The
        // single set is asked by mesh and shader alone, so it MERGES every material on that shader: the first-person sleeves (#0) and
        // trousers (#1) both wear characterroot, and once the trousers became the lower body's (context) the sleeves were the only one
        // wanted, the dress was skipped, and the sleeves drew with the trousers' art. Only a mesh with context of its own — a soldier's —
        // can have that case: a same-shader material that is not the target's is one made context.
        // (CAMO_DRESS_AMBIGUOUS_OLD=1 skips it, for a before/after in one build)
        var s_Ambiguous = Environment.GetEnvironmentVariable("CAMO_DRESS_AMBIGUOUS_OLD") != "1" &&
                          s_Wanted.Any(p_W => Enumerable.Range(0, s_Known.Count).Any(p_I =>
                              p_I != p_W.Section && s_Known[p_I].MaterialId != p_W.MaterialId &&
                              s_Sections[p_I].Shader.Equals(s_Sections[p_W.Section].Shader, StringComparison.OrdinalIgnoreCase)));
        if (s_Wanted.Count < 2 && !s_NeedsOwn && !s_Ambiguous)
        {
            m_Preview.MaterialDress.Clear();
            m_MaterialDressKey = null;
            m_MaterialDressArtKey = null;
            return;
        }

        // Re-registered only when what it is built FROM changes — the object, the target, the camo picked,
        // the PATTERN or the compile — because this runs on every graph change. ⛔ The pattern belongs in
        // the key: without it, choosing another image left every material wearing the previous one.
        // The typed values ride in the KEY as well, or the dressing is not rebuilt when one changes and the
        // new number never reaches the preview — the same trap the pattern had before it went in here.
        // ⛔ AND THE GENERATION OF THE FOREIGN REGISTRY. ClearForeignShaders() throws away the very
        // registrations this dress names (every pick does it, so the second visit to an object hit it), and
        // the key did not notice: it matched, this returned early, and MaterialDress kept naming shaders
        // that no longer existed — every camo section quietly back on the single set.
        // The pattern is NAMED here (register, file, the file's time), not decoded: this runs on every slider tick now, and the
        // decode waits until a registration really has to be rebuilt.
        var s_Typed = DocumentValues();
        var s_Generation = s_NoDressRefresh ? 0 : m_Preview.ForeignGeneration;
        var s_ArtKey = $"{s_Mesh}|{s_Target}|{s_Native}|{m_Preview.AuthoredShaderVersion}|{s_Generation}|" +
                       $"{string.Join(",", s_Wanted.Select(p_W => p_W.MaterialId))}|" +
                       CustomTextureSignature();
        var s_Key = s_ArtKey + "|" +
                    $"{string.Join(",", s_Typed.OrderBy(p_V => p_V.Key, StringComparer.Ordinal).Select(p_V => $"{p_V.Key}={p_V.Value}"))}";

        if (string.Equals(m_MaterialDressKey, s_Key, StringComparison.Ordinal))
            return;

        // ⛔ ONLY THE NUMBERS MOVED (simple-mode settings only showed once simple mode was turned off
        // — on a vehicle the Tiling and Wear sliders left the picture BYTE-IDENTICAL, measured with
        // --simplelivetest, while the M416 moved): the same registrations take the new numbers, the art stays where it is.
        if (!s_NoDressRefresh && string.Equals(m_MaterialDressArtKey, s_ArtKey, StringComparison.Ordinal) &&
            m_MaterialDressEntries.Count > 0 && m_MaterialDressEntries.All(p_E => m_Preview.HasForeignShader(p_E.Name)))
        {
            foreach (var (s_Name, s_Shader, s_MaterialId) in m_MaterialDressEntries)
                m_Preview.SetForeignValues(s_Name, DressNumbers(s_Mesh, s_Shader, s_Native, s_MaterialId, s_Typed));

            m_MaterialDressKey = s_Key;
            return;
        }

        // The camo being authored, decoded once for every material — see where it is laid on below.
        var s_CustomBlocks = CustomTextureBlocks();

        // ⛔⛔ A CAMO SECTION THAT WEARS ANOTHER PRESET OF THE FAMILY DRAWS WITH THAT PRESET'S OWN SHADER.
        // The texture set below is built for the SECTION's shader (its registers), while the bytecode handed
        // in is the DOCUMENT's. On the LAV family that lined up by luck (mud / nomud / 1uvset / mud_decals all
        // keep Diffuse t1, Camo t2, Scratches t3, rgb t4…); on the jet family it does not — vehiclepreset_jet
        // reads t1 Diffuse, t2 CamoB, t3 CamoA, t4 rgb, t5 Scratches, t6 HeatMap, t7 Normal and its _decals
        // sibling inserts the Decal at t3 and shifts everything after it. Fed the decals material's set, the
        // jet shader took the decal sheet for CamoA, the camo for the rgb mask and the heat map for the normal:
        // the F/A-18F's insignia patches came out as DARK BLOCKS under any camo (2026-09-22). And the game
        // never draws those sections with the body's shader anyway: a vehicle variation repoints the camo
        // texture per material and each material keeps its own preset. So a vehicle section wearing a preset
        // other than the target draws with the GAME's bytecode of its preset (already in meshcache\shaders —
        // pixel-exact, nothing to compile), its own art, the camo pattern laid on it and the typed numbers.
        // Weapons keep the old path on purpose: their bake repoints every 1P material's SHADER to the camo
        // twin, so drawing a side-rail with the body's graph is what the game will show.
        var s_Dress = new Dictionary<int, string>();
        var s_Entries = new List<(string Name, string Shader, int MaterialId)>();
        var s_Failed = 0;
        var s_OwnPreset = 0;
        foreach (var (s_Section, s_MaterialId) in s_Wanted)
        {
            var s_Shader = s_Sections[s_Section].Shader;
            var s_Slots = m_CamoSlotsForMaterial(s_Mesh, s_Shader, s_Native, s_MaterialId);
            if (s_Slots is not { Count: > 0 })
                continue;

            var s_Bytecode = p_Bytecode;
            var s_DressShader = s_Target;
            if (s_TargetIsVehicle && !s_Shader.Equals(s_Target, StringComparison.OrdinalIgnoreCase) &&
                !RegistersLineUp(s_Target, s_Shader))
            {
                var s_Dxbc = Path.Combine(MeshCacheDir, "shaders", $"{Sanitize(s_Shader)}.dxbc");
                if (File.Exists(s_Dxbc))
                {
                    s_Bytecode = File.ReadAllBytes(s_Dxbc);
                    s_DressShader = s_Shader;
                    s_OwnPreset++;
                }
                else if (m_DressWarned.Add($"{s_Mesh}|{s_Shader}"))
                    Log($"Camo: material #{s_MaterialId} wears '{s_Shader.Split('/')[^1]}' and its bytecode is not cached — " +
                        $"drawn with the document's '{s_Target.Split('/')[^1]}' shader; its registers may not line up.");
            }

            // ⛔⛔ AND THE CAMO GOES BACK ON TOP. The set above is what that material SHIPS with, camo
            // register included (a vehicle ships one: the LAV-25's is camotank_04) — so registering it as
            // it stands puts the GAME's camo back over the one being authored, and the pattern vanishes
            // from the whole object. It was invisible on weapons because a weapon has ONE camo material and
            // never takes this path: the dye showed on an M416 and not on a LAV-25, which is this line.
            var s_Art = DecodeCachedTextures(s_Slots, TexCacheDir);
            foreach (var s_Custom in s_CustomBlocks)
            {
                // ⛔ BY THE TEXTURE'S NAME when the section draws with another preset: the block's slot is a
                // register of the TARGET's bytecode, and the same texture sits elsewhere in a sibling
                // (texture_CamoA: t3 in vehiclepreset_jet, t4 in _decals; the jet's CamoA/CamoB are the ONE
                // Camo of nomud). A name the sibling does not read (a user picture on the target's diffuse
                // node) is the target's business, not this section's.
                var s_Slots2 = s_DressShader.Equals(s_Target, StringComparison.OrdinalIgnoreCase)
                    ? new[] { s_Custom.Slot }
                    : SlotsOfSameTexture(s_Target, s_DressShader, s_Custom.Slot);
                foreach (var s_Slot in s_Slots2)
                {
                    s_Art.RemoveAll(p_T => p_T.Slot == s_Slot);
                    s_Art.Add((s_Slot, s_Custom.Pixels, s_Custom.Width, s_Custom.Height, s_Custom.Srgb));
                }
            }

            // ⛔⛔ AND SO DO THE NUMBERS, for exactly the reason the textures do. This registration carries
            // what the MATERIAL ships with, so a tiling or a wear typed by the user was overwritten on every
            // material the moment the object had more than one — the pattern appeared and the numbers did
            // nothing. Measured with its control: camotiling 1 against 8 changed the picture on an M416 and
            // left a LAV-25 BYTE-IDENTICAL. A weapon never noticed because this path only runs with two or
            // more camo materials.
            var s_Numbers = DressNumbers(s_Mesh, s_Shader, s_Native, s_MaterialId, s_Typed);

            var s_Name = $"{s_DressShader}#mat{s_MaterialId}";
            var s_Error = m_Preview.RegisterForeignShader(s_Name, s_Bytecode, s_Art, s_Numbers,
                SamplersForShader(s_DressShader));

            if (s_Error == null)
            {
                s_Dress[s_Section] = s_Name;
                s_Entries.Add((s_Name, s_Shader, s_MaterialId));
            }
            else
                s_Failed++;
        }

        m_Preview.MaterialDress = s_Dress;
        m_MaterialDressKey = s_Key;
        m_MaterialDressArtKey = s_ArtKey;
        m_MaterialDressEntries = s_Entries;

        Log($"Camo: {s_Dress.Count} of {s_Wanted.Count} material(s) dressed with their own art" +
            (s_Failed > 0 ? $" ({s_Failed} could not be registered)" : "") +
            (s_OwnPreset > 0
                ? $" — {s_OwnPreset} of them wear another preset of the family and draw with the game's shader of that preset, the camo laid on it."
                : " — the camo is the same on all of them."));
    }

    /// <summary>
    /// The numbers a dressed material draws with: its own (what the material ships with) with the ones typed on the camo on
    /// top — the same merge for a registration and for a re-feed, so a slider shows exactly what a rebuild would.
    /// </summary>
    private IReadOnlyDictionary<string, string>? DressNumbers(string p_Mesh, string p_Shader, string? p_Native, int p_MaterialId,
        IReadOnlyDictionary<string, string> p_Typed)
    {
        var s_Numbers = m_CamoValuesForMaterial?.Invoke(p_Mesh, p_Shader, p_Native, p_MaterialId);
        if (s_Numbers == null || p_Typed.Count == 0)
            return s_Numbers;

        var s_Merged = new Dictionary<string, string>(s_Numbers, StringComparer.OrdinalIgnoreCase);
        foreach (var (s_Constant, s_Text) in p_Typed)
            s_Merged[s_Constant] = s_Text;

        return s_Merged;
    }

    /// <summary>
    /// A paused preview shows in the status line under it — and ALSO in the Output, once per reason. The
    /// status line is easy to miss and gets overwritten; a report of "editing has no effect" needs the
    /// reason to still be readable afterwards.
    /// </summary>
    private void PreviewPaused(string p_Reason)
    {
        PreviewStatus.Text = p_Reason;
        if (p_Reason == m_LastPreviewPause)
            return;

        m_LastPreviewPause = p_Reason;
        Log(p_Reason + " — the last shader that compiled stays on screen.");
    }

    /// <summary>Pushes the decoded BF3 textures into the preview so the cube shows real art.</summary>
    private void PushPreviewTextures()
    {
        // ⛔ Not while one of the object's other materials is on the canvas: the thumbnails are then THAT
        // material's, and the 3D keeps drawing the camo with the camo's own (ShowMaterialGraph, 2026-09-23).
        if (!m_Preview.Ready || m_MaterialEditKey != null)
            return;

        foreach (var (s_Register, s_Image) in m_TexturePreviews)
        {
            if (s_Image is not BitmapSource s_Bitmap)
                continue;

            var s_Converted = new FormatConvertedBitmap(s_Bitmap, PixelFormats.Bgra32, null, 0);
            var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);

            if (int.TryParse(s_Register, out var s_Slot))
                m_Preview.SetTexture(s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight,
                    m_TextureSrgb.TryGetValue(s_Register, out var s_Srgb) && s_Srgb);
        }
    }

    /// <summary>
    /// Whether the game samples a cached texture through an sRGB view (stored values decoded to linear) or as
    /// stored. The .dds beside the thumbnail carries the game's own flag; the thumbnail PNG does not.
    ///
    /// ⛔ THIS IS WHAT MADE EVERY CAMO PREVIEW AS WORN OFF AT THE GAME'S OWN VALUES. The presets' wear mask is
    /// saturate((specular.g × WearAmount) ^ WearPower) and the weapons' specular maps are flagged sRGB: the
    /// game reads a stored 0.12 as 0.013, so WearAmount 10–30 keeps most of the body under the pattern. Every
    /// view here was plain UNORM, the shader got the 0.12 as-is, and the same numbers saturated the mask nearly
    /// everywhere — the math was the game's, the INPUT was not. Measured over the cache: 477 textures flagged
    /// (diffuse, specular, camo, scratches), 174 not (normal maps, character masks), and no name rule tells them
    /// apart (a weapon's "_s" is flagged, a soldier's "_s" is not): the flag is read per file, never inferred.
    /// </summary>
    private bool TextureIsSrgb(string p_AssetName)
    {
        var (s_Srgb, s_FromHeader) = DdsImage.SrgbOf(
            Path.Combine(TexCacheDir, $"{Sanitize(p_AssetName)}.dds"), p_AssetName);

        if (!s_FromHeader && m_SrgbGuessed.Add(p_AssetName))
            Log($"No .dds beside the thumbnail of '{p_AssetName}': previewing it as " +
                $"{(s_Srgb ? "sRGB" : "linear")} by its name.");

        return s_Srgb;
    }

    /// <summary>
    /// A user's file is sampled the way the bake ships it (<see cref="PrepareCustomTextures"/>: normal maps
    /// linear, everything else sRGB), so what the preview shows is what the game will show; a .dds brings its
    /// own flag.
    /// </summary>
    private static bool CustomTextureIsSrgb(GraphNode p_Node, string p_Path)
    {
        // ⭐ declared on the node (the RU engineer's legs: his mask is the game's us_lowerbody_m1, which the game samples
        // LINEAR — sRGB it darkens everything the camo does not cover): what the bake takes too (CamoSession's picture rule)
        if (p_Node.Params.TryGetValue("CustomTextureSrgb", out var s_Declared) && bool.TryParse(s_Declared, out var s_Declares))
            return s_Declares;

        if (p_Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && DdsImage.IsSrgb(p_Path) is { } s_Flag)
            return s_Flag;

        return p_Node.Kind != "NormalMap";
    }

    private Point m_SearchWorldPoint;

    private sealed class SearchEntry
    {
        public required string Kind { get; init; }
        public required string Label { get; init; }
        public override string ToString() => Label;
    }

    private void OnSearchRequested(object? p_Sender, Point p_World)
    {
        m_SearchWorldPoint = p_World;

        var s_Screen = Mouse.GetPosition(Canvas);
        var s_Offset = Canvas.TranslatePoint(s_Screen, this);

        SearchPopup.PlacementTarget = this;
        SearchPopup.HorizontalOffset = s_Offset.X;
        SearchPopup.VerticalOffset = s_Offset.Y;

        SearchBox.Text = "";
        FillSearchResults("");
        SearchPopup.IsOpen = true;
        SearchBox.Focus();
    }

    private void FillSearchResults(string p_Filter)
    {
        var s_Needle = p_Filter.Trim().ToLowerInvariant();
        SearchResults.Items.Clear();

        var s_Matches = Palette.VisibleFor(m_Settings.UdkNodeStyle)
            .Where(p_D => s_Needle.Length == 0 ||
                          p_D.Title.ToLowerInvariant().Contains(s_Needle) ||
                          p_D.Kind.ToLowerInvariant().Contains(s_Needle) ||
                          p_D.CategoryFor(m_Settings.UdkNodeStyle).ToLowerInvariant().Contains(s_Needle) ||
                          p_D.Aliases.Any(p_A => p_A.ToLowerInvariant().Contains(s_Needle)))
            // Names that START with what was typed are what you usually meant — measured against the name
            // actually shown, or the ranking answers for a title the user cannot see. The foreign group sinks
            // to the bottom for the same reason it sits last in the palette.
            .OrderByDescending(p_D => s_Needle.Length > 0 &&
                                      p_D.TitleFor(m_Settings.UdkNodeStyle).ToLowerInvariant().StartsWith(s_Needle))
            .ThenBy(p_D => p_D.CategoryFor(m_Settings.UdkNodeStyle) == Palette.c_ForeignCategory ? 1 : 0)
            .ThenBy(p_D => p_D.CategoryFor(m_Settings.UdkNodeStyle))
            .ThenBy(p_D => p_D.TitleFor(m_Settings.UdkNodeStyle));

        foreach (var s_Def in s_Matches)
            SearchResults.Items.Add(new SearchEntry
            {
                Kind = s_Def.Kind,
                Label = $"{s_Def.TitleFor(m_Settings.UdkNodeStyle)}   ({s_Def.CategoryFor(m_Settings.UdkNodeStyle)})",
            });

        // Not a palette node: drops a comment box at the clicked point. Empty Kind is the sentinel.
        if (s_Needle.Length == 0 || "comment".Contains(s_Needle) || "note".Contains(s_Needle))
            SearchResults.Items.Add(new SearchEntry
            {
                Kind = "",
                Label = "Comment   (Canvas)",
            });

        if (SearchResults.Items.Count > 0)
            SearchResults.SelectedIndex = 0;
    }

    private void OnSearchTextChanged(object p_Sender, TextChangedEventArgs p_Args) =>
        FillSearchResults(SearchBox.Text);

    private void OnSearchKeyDown(object p_Sender, KeyEventArgs p_Args)
    {
        switch (p_Args.Key)
        {
            case Key.Escape:
                SearchPopup.IsOpen = false;
                p_Args.Handled = true;
                break;

            case Key.Enter:
                PlaceSearchSelection();
                p_Args.Handled = true;
                break;

            // Arrows move through the list while the caret stays in the box.
            case Key.Down:
                if (SearchResults.Items.Count > 0)
                    SearchResults.SelectedIndex =
                        Math.Min(SearchResults.SelectedIndex + 1, SearchResults.Items.Count - 1);

                p_Args.Handled = true;
                break;

            case Key.Up:
                if (SearchResults.Items.Count > 0)
                    SearchResults.SelectedIndex = Math.Max(SearchResults.SelectedIndex - 1, 0);

                p_Args.Handled = true;
                break;
        }
    }

    private void OnSearchPick(object p_Sender, MouseButtonEventArgs p_Args) => PlaceSearchSelection();

    private void PlaceSearchSelection()
    {
        if (SearchResults.SelectedItem is not SearchEntry s_Entry)
            return;

        SearchPopup.IsOpen = false;

        if (s_Entry.Kind.Length == 0)
        {
            // The comment entry: the box opens its own title editor, so no focus steal afterwards.
            Canvas.AddCommentAt(m_SearchWorldPoint);
            Log("Added comment.");
            return;
        }

        Canvas.AddNodeAt(s_Entry.Kind, m_SearchWorldPoint);
        Canvas.Focus();
        Log($"Added {Palette.Get(s_Entry.Kind).TitleFor(m_Settings.UdkNodeStyle)}.");
    }

    /// <summary>
    /// The shape icons behave as a radio group by hand: WPF's RadioButton can't wear the toggle-chrome
    /// template without re-templating twice, and the group is four buttons.
    /// </summary>
    private void OnShapeIcon(object p_Sender, RoutedEventArgs p_Args)
    {
        if (p_Sender is not System.Windows.Controls.Primitives.ToggleButton s_Button)
            return;

        ShowShape((s_Button.Tag as string) switch
        {
            "Sphere" => View.PreviewShape.Sphere,
            "Cylinder" => View.PreviewShape.Cylinder,
            "Plane" => View.PreviewShape.Plane,
            _ => View.PreviewShape.Cube,
        });
    }

    /// <summary>A primitive shape icon: the preview shows that primitive. One entry, so a driver takes the icon's path.</summary>
    internal void ShowShape(View.PreviewShape p_Shape)
    {
        m_MeshMode = false;
        m_Preview.Shape = p_Shape;
        SyncShapeIcons(m_Preview.Shape);
    }

    /// <summary>
    /// The mesh icon's effect: the mesh view is on, and the loaded mesh is what the preview draws again.
    ///
    /// ⛔ THE SHAPE HAS TO BE SET BACK, not only the icon. In camo mode the icon used to light up and stop
    /// there — the preview stayed on whatever primitive was clicked last, while the toolbar said "mesh"
    /// (clicking the mesh icon again did not bring the preview back). The geometry never
    /// left the preview; switching back is a shape change and nothing else — no dump, no re-pick.
    /// </summary>
    internal void ShowMeshView()
    {
        m_MeshMode = true;
        if (m_Preview.HasMesh)
            m_Preview.Shape = View.PreviewShape.Mesh;

        SyncShapeIcons(View.PreviewShape.Mesh);
    }

    /// <summary>The mesh the user picked from the linked-objects list, for the mesh-render preview phase.</summary>
    private string? m_PreviewMesh;

    /// <summary>
    /// Lists the DISTINCT meshes that wear the current target shader, from the same cached slot map that
    /// feeds the Variation picker — "which objects is this shader on" is data the editor already has.
    /// </summary>
    private void OnMeshIcon(object p_Sender, RoutedEventArgs p_Args)
    {
        // ⛔ IN CAMO MODE THE LIST IS NOT THIS ICON'S TO FILL. It holds the 59 weapons the studio put there;
        // rebuilding it from the shader's mesh users replaced them with whatever wears the preset — 1p AND
        // 3p, sights, magazines — which is exactly what the user saw after clicking here.
        if (CamoMode)
        {
            ShowMeshView();
            return;
        }

        var s_Target = TargetShaderBox.Text.Trim();
        var s_Meshes = s_Target.Length > 0
            ? ReadSlotMapFull(SlotMapPath(s_Target))?.Variations?.Values
                .Select(p_V => p_V.Mesh)
                .Where(p_M => !string.IsNullOrWhiteSpace(p_M))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p_M => p_M, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : null;

        // ⛔ THE LIST USED TO BE A POPUP THAT OPENED ON THIS ICON, and a popup is the wrong home for it:
        // it hides the choice the moment you look away, so the object being previewed is invisible until you
        // click again — and there was nowhere to say what the list even means. It lives in the properties
        // column now, always on screen; this icon only turns the mesh view on.
        MeshListNote.Visibility = s_Meshes is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

        m_MeshListFilling = true;
        MeshList.Items.Clear();
        foreach (var s_Mesh in s_Meshes ?? new List<string?>())
            MeshList.Items.Add(s_Mesh);

        MeshList.SelectedItem = m_PreviewMesh != null && s_Meshes != null && s_Meshes.Contains(m_PreviewMesh)
            ? m_PreviewMesh
            : null;
        m_MeshListFilling = false;

        if (s_Meshes == null || s_Meshes.Count == 0)
            Log("No linked objects known for this shader yet — press 'Reload from game' to build its map.");

        // ⛔ COMING BACK TO THE MESH IS NOT LOADING IT AGAIN. The geometry stays in the preview while the
        // shape is a cube, so switching back is a shape change and nothing else — no dump, no re-pick, no
        // mount. Re-dumping here would charge a game mount for pressing an icon twice.
        m_MeshMode = true;
        if (m_Preview.HasMesh)
            m_Preview.Shape = View.PreviewShape.Mesh;

        SyncShapeIcons(m_Preview.Shape);
    }

    /// <summary>Filling the list raises SelectionChanged; without this it would re-dump the mesh on every fill.</summary>
    private bool m_MeshListFilling;

    /// <summary>
    /// A mesh read from a file instead of from the game, for LOOKING at the shader on it. It is deliberately
    /// not an import: nothing here reaches a bake, a bundle or the game's own data.
    /// </summary>
    private void OnPickCustomMesh(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Mesh to preview",
            Filter = "Meshes (*.rsm;*.obj)|*.rsm;*.obj|Rime mesh dump (*.rsm)|*.rsm|Wavefront (*.obj)|*.obj|" +
                     "All files (*.*)|*.*",
        };

        if (s_Dialog.ShowDialog(this) != true)
            return;

        LoadCustomMesh(s_Dialog.FileName);
    }

    private void LoadCustomMesh(string p_Path)
    {
        var s_Extension = Path.GetExtension(p_Path).ToLowerInvariant();

        // ⚠ FBX IS NOT READ. Saying so here, by name, beats a parse error from a file the dialog offered:
        // it is a container format (binary or ASCII, versioned) and reading it honestly is a parser, not an
        // afternoon. Every exporter writes OBJ.
        if (s_Extension == ".fbx")
        {
            Log("FBX is not read yet — export it as OBJ (every tool does) or dump a game mesh to .rsm.");
            return;
        }

        var s_Error = s_Extension == ".obj"
            ? m_Preview.LoadObj(p_Path)
            : m_Preview.LoadMeshSections(p_Path);

        if (s_Error != null)
        {
            Log($"Could not preview '{Path.GetFileName(p_Path)}': {s_Error}");
            return;
        }

        m_PreviewMesh = null;
        m_MeshListFilling = true;
        MeshList.SelectedItem = null;
        m_MeshListFilling = false;

        CustomMeshName.Text = Path.GetFileName(p_Path);
        CustomMeshClear.IsEnabled = true;

        m_MeshMode = true;
        m_Preview.Shape = View.PreviewShape.Mesh;
        SyncShapeIcons(View.PreviewShape.Mesh);
        Log($"Previewing '{Path.GetFileName(p_Path)}' — preview only; nothing about it is baked or shipped.");
    }

    private void OnClearCustomMesh(object p_Sender, RoutedEventArgs p_Args)
    {
        CustomMeshName.Text = "(none)";
        CustomMeshClear.IsEnabled = false;
        m_Preview.Shape = View.PreviewShape.Cube;
        SyncShapeIcons(View.PreviewShape.Cube);
        Log("Custom mesh cleared — pick a linked object above to go back to a game mesh.");
    }

    private string MeshCacheDir => Path.Combine(OutputFolder, "meshcache");

    /// <summary>
    /// Whether a cached dump is of the CURRENT format — its magic is the version stamp. A dump from an older
    /// one re-dumps automatically: telling the user to go delete a cache file by hand is not an error
    /// message, it is homework.
    /// </summary>
    private static bool IsCurrentRsm(string p_Path)
    {
        try
        {
            using var s_Stream = File.OpenRead(p_Path);
            var s_Magic = new byte[4];
            if (s_Stream.Read(s_Magic, 0, 4) != 4)
                return false;

            // Each stamp only ADDS a field: RSM5 the second texture coordinate set, RSM6 the part index per
            // vertex. An older dump stays readable and is not re-fetched for a field its mesh has not got.
            var s_Read = System.Text.Encoding.ASCII.GetString(s_Magic);
            return s_Read is "RSM4" or "RSM5" or "RSM6" or "RSM7";
        }
        catch
        {
            return false;
        }
    }

    private async void OnMeshPicked(object p_Sender, SelectionChangedEventArgs p_Args)
    {
        if (m_MeshListFilling)
            return;

        // Two kinds of item live here: plain mesh paths in the editor, and named weapons in camo mode.
        var s_Mesh = MeshList.SelectedItem switch
        {
            string s_Path => s_Path,
            CamoWeapon s_Weapon => s_Weapon.Mesh,
            _ => null,
        };

        if (s_Mesh == null)
            return;

        // A weapon pick resets the accessory picker to that weapon's rows, on "(the weapon itself)", so the
        // two pickers never disagree about what is on the canvas. Filled silently: the pick below is the one
        // that loads the mesh.
        if (MeshList.SelectedItem is CamoWeapon)
            FillAccessoryList(s_Mesh);

        // The work is a Task so that a seam can drive the SAME selection the user makes and wait for it to
        // finish; awaited here so an exception still surfaces through the dispatcher, as it always did.
        var s_Pick = PickMeshAsync(s_Mesh, p_Args);
        LastMeshPick = s_Pick;
        await s_Pick;
        // (the part's option asked again with the part, its document and its look now on screen)
        SyncPartOption();
    }

    /// <summary>
    /// Refills the accessory picker with the entries of a weapon (the weapon itself first) and leaves it on
    /// the first one without picking anything. Nothing happens where the studio brought no accessory table.
    /// </summary>
    private void FillAccessoryList(string p_WeaponMesh)
    {
        if (m_CamoAccessoriesOf == null)
            return;

        m_AccessoryListFilling = true;
        try
        {
            AccessoryList.Items.Clear();
            AccessoryList.ItemTemplate = null;
            var s_Entries = m_CamoAccessoriesOf(p_WeaponMesh);
            foreach (var s_Item in s_Entries)
                AccessoryList.Items.Add(s_Item);

            // A weapon the studio has no rows for still gets its own entry, so the list never opens empty.
            if (AccessoryList.Items.Count == 0)
                AccessoryList.Items.Add(new CamoWeapon { Display = "(no accessory table for this weapon)", Mesh = "" });

            AccessoryList.SelectedIndex = 0;
            ShowAccessoryValues();
            SyncCockpitBox();

            // The first entry is the weapon itself; the rest are its rows' unlocks.
            var s_Accessories = s_Entries.Skip(1).ToList();
            Log($"Accessories: {s_Accessories.Count} under {p_WeaponMesh.Split('/')[^1]} " +
                $"({s_Accessories.Count(p_E => p_E.Mesh.Length > 0)} with a model to paint) — pick one under Preview accessory.");
        }
        finally
        {
            m_AccessoryListFilling = false;
        }
    }

    private async void OnAccessoryPicked(object p_Sender, SelectionChangedEventArgs p_Args)
    {
        if (m_AccessoryListFilling || AccessoryList.SelectedItem is not CamoWeapon s_Item)
            return;

        // The Cockpit box is a second hand on the same picker: it follows every pick, by the entry's tag.
        SyncCockpitBox();

        // The placeholder before any weapon is picked says what to do; choosing it does nothing.
        if (s_Item.Mesh.Length == 0 && MeshList.SelectedItem is not CamoWeapon)
            return;

        // An unlock with nothing visible (heavy barrel, extended magazine, the "No…" placeholders) is on
        // the list so the user learns THAT, not a silent no-op.
        // ⛔⛔ AND THE VIEW SHOWS THE WEAPON, NOT WHATEVER WAS UP BEFORE (switching between a weapon's
        // accessories broke and previewed the wrong materials and shaders). It used to keep the
        // current mesh, so after the M145 "iron sights (no visible model)" left the M145 — its graph, its
        // textures, its frame — under a picker that named something else. Measured with --piecewalk: every such
        // entry reached from another piece differed from the same entry opened fresh, which shows the weapon.
        // The picker is the truth: nothing of this piece to show, so the weapon it goes on is what is shown.
        if (s_Item.Mesh.Length == 0)
        {
            var s_Host = (MeshList.SelectedItem as CamoWeapon)!.Mesh;
            AccessoryValuesPanel.Visibility = Visibility.Collapsed;
            Log($"'{s_Item.Display.Trim()}' changes nothing visible on the weapon — there is no model to paint; " +
                "the weapon itself is shown.");
            if (!string.Equals(m_PreviewMesh, s_Host, StringComparison.OrdinalIgnoreCase))
            {
                var s_Back = PickMeshAsync(s_Host, p_Args);
                LastMeshPick = s_Back;
                await s_Back;
            }

            return;
        }

        // Its own numbers, next to the piece they are about: whatever this attachment overrides of the
        // weapon's wear and tiling, before the mesh is even picked, so the boxes never lag the selection.
        ShowAccessoryValues();

        // ⛔ A PIECE THAT LIVES INSIDE THE SUBJECT'S OWN MESH (the reactive armour) has nothing to load: its
        // entry carries the BODY mesh, so picking it through the mesh path would re-pick the vehicle and
        // show it whole. What changes is what is DRAWN of the mesh already on screen.
        if (s_Item.Tag == PieceInMeshTag)
        {
            ApplyPieceVisibility();
            SchedulePreview();
            Log($"Camo: showing the {s_Item.Display.Trim()} on its own — it is part of the vehicle's own mesh, " +
                "so the body is left out rather than another model loaded.");
            return;
        }

        // The same entry a weapon pick takes: the accessory is a mesh like any other to the canvas, and
        // the studio answers which preset it wears (the third-person family, in both views — the
        // first-person one blacks accessories out) through the same ShaderFor callback.
        var s_Pick = PickMeshAsync(s_Item.Mesh, p_Args);
        LastMeshPick = s_Pick;
        await s_Pick;
        // (the part's option asked again with the part, its document and its look now on screen)
        SyncPartOption();
    }

    private bool m_CockpitBoxSyncing;

    /// <summary>
    /// The Cockpit box against the accessory picker: shown only while the picker lists a cockpit entry (an
    /// aircraft is on screen), ticked exactly while that entry is the one picked. One state, two hands.
    /// </summary>
    /// <summary>
    /// The Cockpit entry for the part on screen: a soldier has one per part his first-person model belongs to (the upper body's sleeves,
    /// the lower body's trousers) and none for his head; an aircraft has exactly one, with no part.
    /// </summary>
    private CamoWeapon? CockpitEntryFor(string p_Part) =>
        AccessoryList.Items.OfType<CamoWeapon>().FirstOrDefault(p_A => p_A.Tag == CockpitTag && p_A.Part == p_Part);

    /// <summary>The part of the entry picked ("" for an aircraft, a weapon, or nothing picked).</summary>
    private string PickedPart => (AccessoryList.SelectedItem as CamoWeapon)?.Part ?? "";

    private void SyncCockpitBox()
    {
        var s_Any = AccessoryList.Items.OfType<CamoWeapon>().Any(p_A => p_A.Tag == CockpitTag);
        var s_Entry = CockpitEntryFor(PickedPart);
        m_CockpitBoxSyncing = true;
        try
        {
            // its words are the family's (a soldier's is "First person"), the aircraft's otherwise
            m_CockpitWords ??= (CockpitBox.Content, CockpitBox.ToolTip);
            CockpitBox.Content = m_CamoFamily?.CockpitLabel ?? m_CockpitWords.Value.Label;
            CockpitBox.ToolTip = m_CamoFamily?.CockpitTooltip ?? m_CockpitWords.Value.Tip;

            // shown wherever the subject HAS a first-person model; greyed on a part that has none of it (a soldier's head)
            CockpitBox.Visibility = s_Any ? Visibility.Visible : Visibility.Collapsed;
            CockpitBox.IsEnabled = s_Entry != null;
            CockpitBox.IsChecked = s_Entry != null && ReferenceEquals(AccessoryList.SelectedItem, s_Entry);
        }
        finally
        {
            m_CockpitBoxSyncing = false;
        }

        SyncPartButtons();
        SyncPartOption();
    }

    /// <summary>The Cockpit box's own words from the XAML, kept the first time a family puts its own on it.</summary>
    private (object Label, object Tip)? m_CockpitWords;

    private bool m_PartSyncing;

    /// <summary>
    /// The part buttons against the accessory picker: one button per PART its entries name (a soldier's Head, Upper body,
    /// Lower body), in their order, pressed on the part of the entry picked — the first-person arms stand under "Upper body". Where the
    /// entries are parts the picker itself is hidden: the buttons ARE the picker there. No parts, no row, and the picker as it always was.
    /// </summary>
    private void SyncPartButtons()
    {
        // in the family's own order (the picker's first entry stays the subject itself, whatever button reads first)
        var s_Order = m_CamoFamily?.PartOrder ?? Array.Empty<string>();
        var s_Words = AccessoryList.Items.OfType<CamoWeapon>().Select(p_A => p_A.Part).Where(p_P => p_P.Length > 0).Distinct()
            .Select((p_P, p_I) => (Word: p_P, At: s_Order.Contains(p_P) ? s_Order.ToList().IndexOf(p_P) : s_Order.Count + p_I))
            .OrderBy(p_W => p_W.At)
            .Select(p_W => p_W.Word)
            .ToList();
        var s_Shown = CamoPartPanel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Select(p_T => p_T.Tag as string ?? "").ToList();
        if (!s_Shown.SequenceEqual(s_Words))
        {
            CamoPartPanel.Children.Clear();
            foreach (var s_Word in s_Words)
            {
                var s_Button = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Content = s_Word,
                    Style = (Style) FindResource("FamilyTab"),
                    Tag = s_Word,
                    ToolTip = $"Edit the skin on the {s_Word.ToLowerInvariant()} — the rest of him stays around it as the game ships it.",
                };
                s_Button.Click += OnPartButton;
                CamoPartPanel.Children.Add(s_Button);
            }
        }

        CamoPartPanel.Visibility = s_Words.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccessoryList.Visibility = s_Words.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var s_Pressed = (AccessoryList.SelectedItem as CamoWeapon)?.Part ?? "";
        m_PartSyncing = true;
        foreach (var s_Button in CamoPartPanel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
            s_Button.IsChecked = s_Pressed.Length > 0 && (s_Button.Tag as string) == s_Pressed;
        m_PartSyncing = false;
    }

    /// <summary>A part button picks that part's own entry of the picker — the same path a pick on the picker takes.</summary>
    private void OnPartButton(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_PartSyncing || p_Sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string s_Word })
            return;

        // the part's own mesh — or, while the first-person view is on, that part's first-person entry when it has one (the view stays;
        // a part with none, the head, goes back to the soldier seen from outside)
        var s_InFirstPerson = AccessoryList.SelectedItem is CamoWeapon { Tag: CockpitTag };
        var s_Entry = (s_InFirstPerson ? CockpitEntryFor(s_Word) : null) ??
                      AccessoryList.Items.OfType<CamoWeapon>().FirstOrDefault(p_A => p_A.Part == s_Word && p_A.Tag != CockpitTag);
        if (s_Entry == null || ReferenceEquals(AccessoryList.SelectedItem, s_Entry))
        {
            // pressing the part on screen again changes nothing (and a toggle button would otherwise show it released)
            SyncPartButtons();
            return;
        }

        AccessoryList.SelectedItem = s_Entry;
    }

    /// <summary>Presses a part button THE WAY THE USER DOES (its own Click). False when the row has no such button.</summary>
    internal bool PressCamoPart(string p_Word)
    {
        foreach (var s_Button in CamoPartPanel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
        {
            if (!(s_Button.Tag as string ?? "").Equals(p_Word, StringComparison.OrdinalIgnoreCase))
                continue;

            s_Button.IsChecked = true;
            s_Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ticking the box picks the cockpit entry of the accessory picker, unticking it picks the aircraft
    /// itself — the same path a click on the picker takes, so the load, the materials and the canvas all
    /// follow exactly as for any other piece. A driver reads <see cref="LastMeshPick"/> after either.
    /// </summary>
    private void OnCockpitToggled(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_CockpitBoxSyncing || m_AccessoryListFilling)
            return;

        var s_Entries = AccessoryList.Items.OfType<CamoWeapon>().ToList();
        var s_Part = PickedPart;
        var s_Cockpit = CockpitEntryFor(s_Part);
        if (s_Cockpit == null)
            return;

        // unticked: back to the part's own entry (the aircraft, for an aircraft — its first entry, as it always was)
        var s_Wanted = CockpitBox.IsChecked == true
            ? s_Cockpit
            : (s_Part.Length > 0 ? s_Entries.FirstOrDefault(p_A => p_A.Part == s_Part && p_A.Tag != CockpitTag) : null) ?? s_Entries.FirstOrDefault();
        if (s_Wanted == null || ReferenceEquals(AccessoryList.SelectedItem, s_Wanted))
            return;

        AccessoryList.SelectedItem = s_Wanted;
    }

    /// <summary>Whether the Cockpit box is on screen and ticked — what a seam checks after driving it.</summary>
    internal (bool Shown, bool Ticked) CockpitBoxState =>
        (CockpitBox.Visibility == Visibility.Visible, CockpitBox.IsChecked == true);

    /// <summary>Ticks or unticks the Cockpit box the way the user does — through its own handler.</summary>
    internal void SetCockpitBox(bool p_Ticked) => CockpitBox.IsChecked = p_Ticked;

    // ---- ⭐ A PART'S OWN OPTION (the Soldiers tab's "camo on all the cloth", seen before the bake) --------------------------------------------
    private CheckBox? m_PartOptionBox;
    private TextBlock? m_PartOptionNote;
    private Func<string, string?, (bool? On, string? Note)>? m_PartOptionGet;
    private Action<string, bool>? m_PartOptionSet;
    private bool m_PartOptionSyncing;

    /// <summary>
    /// A box under the Cockpit box that the camo host answers PER PART (the studio: a soldier part's camo on all its cloth): (the part's
    /// document key, its camo of the picker) -> ticked or not — null: not offered on that part — and a note said under it. A tick goes to the
    /// host, and the subject on screen is dressed again THE WAY A PICK DRESSES IT (every section and every part around it asks the host
    /// again) — what the preview shows is what the host will bake.
    /// </summary>
    internal void SetCamoPartOption(string p_Label, string p_Tooltip, Func<string, string?, (bool? On, string? Note)> p_Get, Action<string, bool> p_Set)
    {
        m_PartOptionGet = p_Get;
        m_PartOptionSet = p_Set;
        if (m_PartOptionBox == null && CockpitBox.Parent is Panel s_Panel)
        {
            m_PartOptionBox = new CheckBox { Content = p_Label, ToolTip = p_Tooltip, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            m_PartOptionBox.Checked += OnPartOptionToggled;
            m_PartOptionBox.Unchecked += OnPartOptionToggled;
            m_PartOptionNote = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(18, 2, 0, 0), Visibility = Visibility.Collapsed,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xA8, 0x48)),
            };
            var s_At = s_Panel.Children.IndexOf(CockpitBox);
            s_Panel.Children.Insert(s_At + 1, m_PartOptionBox);
            s_Panel.Children.Insert(s_At + 2, m_PartOptionNote);
        }

        SyncPartOption();
    }

    /// <summary>The part on screen's key for the host (a soldier's "soldiers/US_Assault/lower"), or null outside camo mode.</summary>
    /// <remarks>By the ENTRY picked (DocumentKeyOf), not the mesh: his first-person trousers are the lower body's although the mesh is the
    /// upper body's sleeves too — the box read and set the upper body's (review 2026-09-29, D11). CAMO_ENTRY_BY_SUBJECT_OLD=1 = by the mesh.</remarks>
    private string? PartOptionKey => CamoMode && m_PreviewMesh is { Length: > 0 } s_Mesh
        ? Environment.GetEnvironmentVariable("CAMO_ENTRY_BY_SUBJECT_OLD") == "1" ? m_CamoDocumentKeyFor?.Invoke(s_Mesh) : DocumentKeyOf(s_Mesh)
        : null;

    private void SyncPartOption()
    {
        if (m_PartOptionBox == null || m_PartOptionNote == null)
            return;

        var s_Key = PartOptionKey;
        var (s_On, s_Note) = s_Key != null && m_PartOptionGet != null ? m_PartOptionGet(s_Key, DocumentGraph.NativeCamo) : ((bool?) null, (string?) null);

        // ⭐ a picture on the document's Mask node decides where the camo goes — not the game's mask of the look, nor the box (the bake
        // takes the picture and leaves "all the cloth" aside): the box steps back and the note says so (e.g. the US support's
        // legs start from a mask that keeps his boots, knee pads and belt out)
        var s_MaskRegister = m_Contract?.RegisterForExternalTexture("Mask") ?? -1;
        var s_MaskPictured = s_On != null && s_MaskRegister > 0 &&
                             DocumentGraph.Nodes.Any(p_N => TextureNodeKinds.Contains(p_N.Kind) && p_N.GetParam("Register") == s_MaskRegister.ToString() &&
                                                            p_N.Params.TryGetValue("CustomTexture", out var s_Picture) && s_Picture.Length > 0);
        if (s_MaskPictured)
            s_Note = "The picture on the Mask node puts the camo where it says (its alpha) — not the game's mask of this look; " +
                     "take it off the node for the game's mask or all the cloth.";

        m_PartOptionSyncing = true;
        try
        {
            m_PartOptionBox.Visibility = s_On != null && !s_MaskPictured ? Visibility.Visible : Visibility.Collapsed;
            m_PartOptionBox.IsChecked = s_On == true && !s_MaskPictured;
            m_PartOptionNote.Text = s_Note ?? "";
            m_PartOptionNote.Visibility = s_On != null && !string.IsNullOrEmpty(s_Note) ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            m_PartOptionSyncing = false;
        }
    }

    private async void OnPartOptionToggled(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_PartOptionSyncing || m_PartOptionBox == null || m_PreviewMesh is not { Length: > 0 } s_Mesh || PartOptionKey is not { } s_Key)
            return;

        m_PartOptionSet?.Invoke(s_Key, m_PartOptionBox.IsChecked == true);
        SyncPartOption();
        // dressed again the way a pick dresses it: every section and every part around it asks the host again
        var s_Pick = PickMeshAsync(s_Mesh, p_Args);
        LastMeshPick = s_Pick;
        await s_Pick;
        SyncPartOption();
    }

    /// <summary>The part option as the user sees it — what a seam checks after driving it.</summary>
    internal (bool Shown, bool Ticked, string Note) PartOptionState => m_PartOptionBox == null || m_PartOptionNote == null
        ? (false, false, "")
        : (m_PartOptionBox.Visibility == Visibility.Visible, m_PartOptionBox.IsChecked == true,
            m_PartOptionNote.Visibility == Visibility.Visible ? m_PartOptionNote.Text : "");

    /// <summary>Ticks or unticks the part option the way the user does — through its own handler.</summary>
    internal void SetPartOptionBox(bool p_Ticked)
    {
        if (m_PartOptionBox != null)
            m_PartOptionBox.IsChecked = p_Ticked;
    }

    /// <summary>The most recent selection's work, for a headless driver to wait on. Null before any pick.</summary>
    internal Task? LastMeshPick { get; private set; }

    /// <summary>
    /// What the canvas raises after a user edit, for a driver that pokes the graph model directly (a seam
    /// changing a node's value the way the properties panel would): the preview recompiles from the graph.
    /// </summary>
    /// <summary>
    /// A graph edit is a user action on the camo, and on some subjects it is the ONLY one available.
    ///
    /// ⛔ WHY IT IS NOT UNCONDITIONAL. On a WEAPON, "as shipped" puts the NO-CAMO TWIN on the canvas — a
    /// VIEW of the paint under the camo, whose edits deliberately do not ship (that rule took three rounds
    /// to settle). Promoting there would start a camo behind the user's back.
    /// ⛔ WHY IT WAS NEEDED. A VEHICLE has no twin ("as shipped" IS its own preset), so every edit the user made was
    /// drawn with the game's bytecode and nothing changed on screen, even with every pin disconnected
    /// (2026-09-23) — the answer then was to move the picker to the basic camo. Since
    /// 2026-09-25 as shipped is a camo like any other and editable: the edit draws under "as shipped"
    /// itself (AsShippedDocumentEdited), and only an attachment's painted body still moves (below).
    /// </summary>
    private void PromoteOnGraphEdit()
    {
        // ⛔ The test is the VIEW's look, not the canvas: on a weapon "as shipped" PARKS the document and
        // shows the no-camo twin, but on a vehicle there is no twin to park — the canvas already holds the
        // document while the 3D still draws the game's bytecode. Guarding on the parked graph looked right
        // and fired on neither.
        // ⛔ And never for one of the object's OTHER materials: an edit of its graph is that material's, not
        // the camo's — it shows on its own section (RefreshMaterialEdits) and starts no camo.
        // ⭐ …EXCEPT AN ATTACHMENT'S PAINTED BODY: its graph IS its camo (the 3P preset it takes one with), and as
        // shipped the piece is the game's, so an edit there would be invisible — the vehicle
        // case above, same answer: the picker moves to the basic camo and the stand-in shows the edit.
        if (!CamoFactoryLook())
            return;

        if (m_MaterialEditKey != null)
        {
            if (m_PreviewMesh is { Length: > 0 } s_Piece && m_CamoWeaponOfAccessory?.Invoke(s_Piece) != null &&
                MaterialList.SelectedItem is MaterialEntry { TakesCamo: true } s_Body &&
                string.Equals(m_MaterialEditKey, ShaderGraph.MaterialKey(s_Piece, s_Body.MaterialId), StringComparison.OrdinalIgnoreCase) &&
                !DocumentGraph.IsMaterialOff(s_Piece, s_Body.MaterialId) &&
                !DrawsTheSame(Canvas.Graph, m_MaterialOpened))
            {
                // Only the look moves: the canvas keeps this graph and its undo history (no re-dress), and the
                // stand-in compiles the live graph from here on.
                PromoteToBasicCamo("the attachment's own graph was edited");
                SyncFactoryLook();
                ShowAccessoryValues();
            }

            return;
        }

        // ⛔ A VEHICLE AS SHIPPED NO LONGER MOVES TO THE BASIC CAMO (it did from 2026-09-23 to 2026-09-25): as shipped is a camo like any
        // other and editable — the edited document draws in that look (AsShippedDocumentEdited), under "as shipped".
    }

    /// <summary>The game's own translation of a preset, as the cache has it — the yardstick an edit is told by. Null when not cached.</summary>
    private ShaderGraph? PristineOf(string p_Preset)
    {
        if (m_PristineTranslations.TryGetValue(p_Preset, out var s_Known))
            return s_Known;

        // ⛔⛔ A TRANSLATION NOT CACHED YET IS NOT REMEMBERED AS "NONE" (2026-09-28, the US support's legs on the pick that translated
        // characterroot_xp4): asked before the pick wrote the file, the answer stayed null for the whole session — nothing counted as
        // edited against it, the document with its picture drew "as shipped" (the game's sand03 on all the cloth) while the same steps on a
        // cached translation drew the picture. Only what was READ (or found unusable) is kept.
        if (!File.Exists(CamoGraphCachePath(p_Preset)))
            return null;

        ShaderGraph? s_Graph = null;
        try
        {
            s_Graph = LoadCamoGraph(p_Preset);
        }
        catch
        {
            // an unusable cache has no yardstick: nothing counts as edited against it
        }

        m_PristineTranslations[p_Preset] = s_Graph;
        return s_Graph;
    }

    private readonly Dictionary<string, ShaderGraph?> m_PristineTranslations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// As shipped, when the canvas holds the camo DOCUMENT itself as the subject's own shader (a vehicle: the preset it wears is the camo's,
    /// there is no twin to show instead) and that document was edited: it draws, as an edited as-shipped graph of a weapon does.
    /// </summary>
    private bool AsShippedDocumentEdited
    {
        get
        {
            if (!CamoMode || !CamoFactoryLook() || FactoryGraphShown || m_MaterialEditKey != null)
                return false;

            // the document is the as-shipped graph when nothing else stands in for it: no worn preset of its own (a weapon whose body wears
            // the camo's preset — see FactoryPresetOf), or the camo's preset itself (a vehicle)
            var s_Target = TargetShaderBox.Text.Trim();
            return s_Target.Length > 0 &&
                   (FactoryPresetOf() is not { Length: > 0 } s_Worn || s_Worn.Equals(s_Target, StringComparison.OrdinalIgnoreCase)) &&
                   PristineOf(s_Target) is { } s_Pristine && !DrawsTheSame(Canvas.Graph, WithCanvasStickerLayer(s_Pristine));
        }
    }

    internal void NotifyGraphEdited()
    {
        PromoteOnGraphEdit();
        // Both halves of a real edit: the constants are re-fed (what a Preview value box does — a value never
        // recompiles) and the graph is recompiled (what a wire or a node value does).
        PushExternalValues();
        Canvas.Refresh();
        SchedulePreview();
    }

    private async Task PickMeshAsync(string s_Mesh, RoutedEventArgs p_Args)
    {
        // ⛔⛔ ONE SELECTION AT A TIME. This handler is async: it awaits mesh dumps, foreign shaders and a
        // translate. Picking another weapon while one of those is in flight leaves TWO runs interleaving —
        // the older one finishes last and stamps ITS textures, contract and mesh over the newer weapon.
        // That is the shape of "it went fine for six and broke on the seventh": nothing is wrong with the
        // seventh, it is the sixth landing on top of it. Each run takes a ticket and abandons its effects
        // as soon as a newer one exists.
        var s_Ticket = ++m_MeshPickTicket;

        // ⛔ EVERY EARLY EXIT SAYS SO. "I click it and nothing happens" is unusable as a report and
        // impossible to reproduce from outside; with a line per bail-out, the Output names the step that
        // gave up and the next round starts from a fact instead of a guess.
        bool Superseded()
        {
            if (s_Ticket == m_MeshPickTicket)
                return false;

            if (CamoMode)
                Log($"Camo: '{s_Mesh}' abandoned — another weapon was picked while it was loading.");

            return true;
        }

        if (CamoMode)
            Log($"Camo: picked {s_Mesh}");

        // ⛔ A MATERIAL OF THE OBJECT BEING LEFT IS PUT AWAY FIRST, before anything of the new one is loaded. It used to be put away
        // halfway through the pick (SaveActiveTab when the new subject needs a tab of its own, FillMaterialList otherwise), so the
        // new mesh was loaded, aimed and given its foreign sections while the window still counted as editing the OLD object's
        // material — the state in which the preview takes no textures or values. Measured 2026-09-24 (--switchtest
        // "xp3/2s25-sprut-sd,mat:3,M240"): only OPENING the Sprut's glass and then picking the M240 drew the M240 wrong (8356E8CF
        // against A476EBC2 as picked with no material open); the LAV-25's CROWS the same (27AC7435). Saved into the document, as
        // every other exit does, and left without re-dressing: the object being dressed next is the new one.
        // ⛔⛔ AND WITH THE NEW SUBJECT ALREADY THE ONE ON SCREEN (m_PreviewMesh), as the old exits had it: left with the old one still
        // named, leaving an ATTACHMENT's material re-dressed its stand-in (its textures pushed into the view: "t1=kobra_d t3=kobra_s")
        // and the weapon picked next kept a trace of it — AKS74u under ABU, Kobra then iron sights: B576B607 against 13666F64
        // (--piecewalk, 2026-09-25; measured with the early exit switched off: the frame came back).
        var s_PreviousMesh = m_PreviewMesh;
        var s_LeaveMaterial = CamoMode && m_MaterialEditKey != null && !string.Equals(m_PreviewMesh, s_Mesh, StringComparison.OrdinalIgnoreCase);
        m_PreviewMesh = s_Mesh;
        if (s_LeaveMaterial)
        {
            SaveEditedMaterial();
            LeaveMaterialEdit(false);
        }

        // (s_PreviousMesh: what the tab being left was showing, for the one case below that saves that tab — it must remember ITS
        // weapon, not the one just picked)
        CustomMeshName.Text = "(none)";
        CustomMeshClear.IsEnabled = false;

        // An accessory of some weapon (studio only): dumped unposed below, and its picker kept in step.
        var s_IsAccessory = m_CamoWeaponOfAccessory?.Invoke(s_Mesh) != null;

        var s_File = Path.Combine(MeshCacheDir, $"{Sanitize(s_Mesh)}.rsm");
        if (!File.Exists(s_File) || !IsCurrentRsm(s_File))
        {
            var s_GamePath = GamePath;
            var s_Repl = FindRimeRepl();
            if (s_Repl == null || s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
            {
                Log("ERROR: mesh dump needs RimeREPL and a valid BF3 path.");
                return;
            }

            Log($"Mounting BF3 to dump '{s_Mesh}' — one mount, ~20 s, please wait…");
            Directory.CreateDirectory(MeshCacheDir);
            var s_LevelDb = ResolveShaderDb(TargetShaderBox.Text.Trim());

            // While the mount runs: the cube stands in, and the mesh icon + list grey out so a second
            // click cannot start a competing mount. The bar walks an ESTIMATE (mounting reports no
            // progress) and completes on arrival.
            ShapeMesh.IsEnabled = false;
            MeshList.IsEnabled = false;
            MeshProgress.Value = 0;
            MeshProgress.Visibility = Visibility.Visible;
            OnShapeIcon(ShapeCube, p_Args);

            var s_Timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            s_Timer.Tick += (_, _) => MeshProgress.Value = Math.Min(95.0, MeshProgress.Value + 95.0 / 150.0);
            s_Timer.Start();

            try
            {
                var s_Script = Path.Combine(MeshCacheDir, "dump_mesh.rime");
                await Task.Run(() =>
                {
                    // A weapon is dumped ASSEMBLED: its idle pose (from its own animation package, on the
                    // shared weapon skeleton) is written first and the dump applies it; anything else is
                    // dumped as stored. Same recipe as the studio's cache step, so the two never differ.
                    // An accessory lives under weapons/ too but is shown ALONE, in its bind pose — the
                    // studio's accessory cache dumps it unposed, and so does this.
                    var s_Pose = "";
                    var s_PoseLine = "";
                    if (s_Mesh.StartsWith("weapons/", StringComparison.OrdinalIgnoreCase) && !s_IsAccessory)
                    {
                        var s_PoseFile = Path.Combine(MeshCacheDir, "poses", $"{Sanitize(s_Mesh)}.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(s_PoseFile)!);
                        s_PoseLine = $"dump_weapon_pose {s_Mesh} animations/skeletons/weapon/weaponske01 \"{s_PoseFile}\"\n";
                        s_Pose = $" \"{s_PoseFile}\"";
                    }

                    File.WriteAllText(s_Script,
                        $"mount_game \"{s_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
                        s_PoseLine +
                        $"dump_mesh_sections {s_Mesh} \"{s_File}\" {s_LevelDb}{s_Pose}\nexit\n");
                    Run(s_Repl, new[] { s_Script });
                });
            }
            finally
            {
                s_Timer.Stop();
                MeshProgress.Value = 100;
                MeshProgress.Visibility = Visibility.Collapsed;
                ShapeMesh.IsEnabled = true;
                MeshList.IsEnabled = true;
            }

            if (!File.Exists(s_File))
            {
                Log($"ERROR: the game did not yield '{s_Mesh}' (level-only or DLC-mounted meshes are a " +
                    "known gap here). The preview keeps the cube.");
                return;
            }
        }

        // A dump can take a mount; by the time it lands the user may be on another weapon.
        if (Superseded())
            return;

        // ⭐ AND WHAT THE OBJECT CARRIES THAT IS NOT IN ITS MESH — a tracked vehicle's two running belts,
        // which are their own mesh placed by the hull's part anchors. Nothing for a weapon or a wheeled one.
        var s_Extras = m_CamoExtraMeshesFor?.Invoke(s_Mesh)?
            .Select(p_E => new View.ShaderPreview.MeshInstance(p_E.Path,
                new SharpDX.Vector3(p_E.OffsetX, 0f, 0f), p_E.MirrorX, p_E.Context))
            .ToList();

        var s_Error = m_Preview.LoadMeshSections(s_File, s_Extras);
        if (s_Error != null)
        {
            Log($"ERROR: could not load the dumped mesh ({s_Error}).");
            return;
        }

        // the family on screen is the subject's (ShowFamilyListing), and it says how its foreign materials' empty textures draw
        m_Preview.NeutralEmptySlots = m_CamoFamily?.NeutralForeignSlots == true;

        if (s_Extras is { Count: > 0 })
            Log($"Camo: {s_Extras.Count} extra mesh(es) drawn with the body " +
                $"({string.Join(", ", s_Extras.Select(p_E => $"{p_E.Path.Split('\\', '/')[^1]} at x={p_E.Offset.X:0.00}"))}).");

        // ⭐ …AND THE MATERIALS OF THIS MESH THAT ARE ANOTHER PART'S: a soldier's first-person model is one mesh under two
        // parts, and the entry picked says which of its materials are not its own — context, by the game's material id.
        if (AccessoryList.SelectedItem is CamoWeapon { ContextMaterials.Count: > 0 } s_Part &&
            s_Part.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) && m_CamoSectionMaterialsOf != null)
        {
            var s_Ids = m_CamoSectionMaterialsOf(s_Mesh);
            var s_Others = Enumerable.Range(0, Math.Min(s_Ids.Count, m_Preview.MeshSections.Count))
                .Where(p_I => s_Part.ContextMaterials.Contains(s_Ids[p_I].MaterialId))
                .ToList();
            m_Preview.MarkContext(s_Others, s_Mesh);
            Log($"Camo: {s_Part.Display.Trim()} — material(s) #{string.Join(", #", s_Part.ContextMaterials)} belong to another part: " +
                $"{s_Others.Count} section(s) drawn around it as they ship.");
        }

        m_MeshMode = true;
        m_Preview.HideForeignSections = m_Settings.PreviewOnlyEditedShader;
        m_Preview.Shape = View.PreviewShape.Mesh;
        SyncShapeIcons(View.PreviewShape.Mesh);
        Log($"Preview mesh: '{s_Mesh}'.");

        // ⭐ CAMO MODE: picking a weapon brings its SHADER too — translated to nodes, with its textures —
        // instead of leaving a bare mesh on screen. A camo is authored ON a weapon's material, so the
        // graph the user needs is the one that weapon already wears; making them go and find it would be
        // asking for a step that has exactly one right answer.
        // Said BEFORE the shader loads: if this weapon cannot wear a camo with vanilla shaders, that is the
        // first thing the user needs to know, not something to find out in a match.
        if (m_CamoNoteFor?.Invoke(s_Mesh) is { } s_Note)
            Log(s_Note);

        // Resolved BEFORE the section shaders are sorted into own and foreign: which ones are foreign
        // depends on it.
        string? s_CamoShader = null;
        if (m_CamoShaderFor != null)
        {
            s_CamoShader = m_CamoShaderFor(s_Mesh);
            if (s_CamoShader == null)
            {
                Log($"Camo: no shader resolved for '{s_Mesh}' — the material scan does not cover it.");
                return;
            }
        }

        // ⛔⛔ THE PRESET BEING AUTHORED REPLACES THE ONE THE BODY WEARS. 41 of the 60 weapons wear a NoCamo
        // preset on the body; the studio opens its camo twin, which is what the mod substitutes in-game. To
        // the preview the two names were simply different shaders, so the body counted as FOREIGN: it drew
        // with the game's NoCamo bytecode and the graph being edited never touched it — a camo authored on
        // the M416 would have shown on its bullets and nowhere else. The sections wearing a preset this one
        // replaces are the edited shader's own, in every pass and for the foreign sort below.
        AimPreviewAt(s_CamoShader ?? TargetShaderBox.Text.Trim());
        m_Preview.MeshTargetAliases = CamoAliasesFor(s_CamoShader);

        // ⛔ IN CAMO MODE THE FOREIGN SHADERS BELONG TO THE WEAPON, NOT TO THE SESSION. They are cached by
        // shader NAME with the art of whoever registered them first, and every weapon wears the same few
        // (bullets_base, aimingdots, black_tape) — so the second weapon opened inherited the first one's
        // textures on those sections. Dropped here so each weapon registers its own; the sources are all
        // cached, so re-registering costs no mount.
        if (m_CamoShaderFor != null)
            m_Preview.ClearForeignShaders();

        if (!m_Settings.PreviewOnlyEditedShader)
            await RegisterForeignShadersAsync(s_CamoShader);

        if (Superseded())
            return;

        // The editor's own mesh pick ends here; only camo mode goes on to bring the shader with it.
        if (s_CamoShader == null)
            return;

        // ⭐⭐ IN CAMO MODE A TAB IS A CAMO, AND THE WEAPON IS WHAT YOU LOOK AT IT ON. Picking a weapon never
        // leaves the tab the user is working in: the graph on the canvas is theirs, and it is drawn on
        // whichever weapon they pick. It used to be "a tab per PRESET" — a pick jumped to the first tab
        // carrying the weapon's preset and dressed THAT one, so a second camo opened with New never got the
        // weapon (opening a new tab kept affecting the original one).
        //
        // Three cases, by what the active tab holds:
        //   · a graph aimed at a preset      -> stay; the weapon is dressed for THIS tab's preset, whose
        //                                       contract is the one the graph compiles against;
        //   · a fresh graph (New, or startup) -> aim it: the weapon's preset graph is loaded INTO this tab,
        //                                       which is what "new camo, then pick a weapon" means;
        //   · an untargeted graph with edits  -> the preset opens beside it, so nothing of theirs is lost.
        // ⛔ The CAMO's target, not the box's: under a camo an attachment opens its own graph on the canvas and
        // the box then names the attachment's preset — read here, picking the weapon back ("(W) itself", an
        // entry with no model) found a 3P "tab", opened a fresh one and lost the camo (--piecewalk AKS74u ABU).
        var s_ActiveTarget = CamoTarget;
        var s_Fresh = s_ActiveTarget.Length == 0 && Canvas.Graph.Nodes.Count <= 1 &&
                      Canvas.Graph.Connections.Count == 0 && !Canvas.CanUndo;

        // Whether a tab took the window over on this pick — in camo mode ActivateTab dresses the weapon.
        var s_TabActivated = false;
        var s_DressShader = s_CamoShader;

        // ⭐ A TAB PER PART OF A SOLDIER, REACHED BY ITS BUTTON (the request: the first change to an original opens a new tab,
        // and clicking Upper body, Lower body or Head leads to that part's own tab, other soldiers' parts included): a part with
        // changes goes to the tab that holds them, from ANY tab; a part never changed goes to the game's original — an original tab that draws it, or one of its own
        // opened below — where its first change opens its own tab (ForkOriginalTab). Before, a tab of changes kept every part it could draw:
        // his torso came up inside the tab of his helmet and stayed mixed in it. The part already on screen stays (a tab brought back, the
        // "all the cloth" box re-dressing it). Only where each subject keeps its own camo (a soldier's parts). CAMO_PART_TAB_OLD=1 = as before.
        var s_PartKey = DocumentKeyOf(s_Mesh);
        // (review 2026-09-29) ⛔ D5: the SAME part on a shader this tab cannot draw — an Aftermath soldier's first-person arms (characterroot)
        // from his torso's tab (characterroot_xp4) — "stayed", and the check below then sent it to whichever tab drew that shader, another
        // soldier's among them: it is routed like any other part now (its own tab of changes of that shader, else an original).
        // CAMO_PART_SHADER_OLD=1 = as before.
        var s_PartShaderOld = Environment.GetEnvironmentVariable("CAMO_PART_SHADER_OLD") == "1";
        var s_DrawnHere = s_PartShaderOld || (s_ActiveTarget.Length > 0 && TabDrawsSubject(s_ActiveTarget, s_CamoShader));
        if (s_ActiveTarget.Length > 0 && m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count && NativePerSubject(s_PartKey) &&
            !(string.Equals(s_PartKey, m_DocumentSubject, StringComparison.OrdinalIgnoreCase) && s_DrawnHere) &&
            Environment.GetEnvironmentVariable("CAMO_PART_TAB_OLD") != "1" && !(TabHoldsChangesOf(m_ActiveTab, s_PartKey) && s_DrawnHere))
        {
            var s_Holder = TabWithChangesOf(s_PartKey, s_CamoShader);
            var s_Here = m_Tabs[m_ActiveTab].Original && TabDrawsSubject(s_ActiveTarget, s_CamoShader);
            var s_To = s_Holder >= 0 ? s_Holder : s_Here ? m_ActiveTab : OriginalTabThatDraws(s_CamoShader);
            var s_PartWord = s_PartKey.Split('/')[^1];
            if (s_To >= 0 && s_To != m_ActiveTab)
            {
                Log(s_Holder >= 0
                    ? $"Camo: {s_PartWord} goes to its own tab, '{TabTitleOf(s_To)}' — with its changes."
                    : $"Camo: {s_PartWord} has not been changed — shown as the game ships it, in '{TabTitleOf(s_To)}'; its first change opens a tab of its own.");
                m_PreviewMesh = s_PreviousMesh;
                SaveActiveTab();
                m_PreviewMesh = s_Mesh;
                MoveTabToSubject(m_Tabs[s_To], s_PartKey);
                m_Tabs[s_To].PreviewMesh = s_Mesh;
                m_Tabs[s_To].PreviewWeapon = (MeshList.SelectedItem as CamoWeapon)?.Mesh;
                ActivateTab(s_To);
                s_TabActivated = true;
                s_ActiveTarget = "";
            }
            else if (s_To < 0)
            {
                // no original tab draws it yet: the game's original opens in a tab of its own (the branch below, as for a subject of another
                // shader), and a change on it opens its own tab of changes
                Log($"Camo: {s_PartWord} has not been changed — the game's original opens in a tab of its own; its first change opens another.");
                s_ActiveTarget = "";
            }
        }

        // ⛔⛔ …UNLESS THIS TAB'S PRESET CANNOT DRAW THE SUBJECT (a vehicle camo under a weapon, the F-35B's
        // own shader under any vehicle preset — see TabDrawsSubject): kept, the subject came out grey. Then
        // the subject goes to a tab that can draw it, or its own preset opens beside this one, exactly as for
        // an untargeted tab with edits. This tab is left as it is, with the subject it was showing.
        if (!s_TabActivated && s_ActiveTarget.Length > 0 && !TabDrawsSubject(s_ActiveTarget, s_CamoShader))
        {
            var s_Other = TabThatDrawsSubject(s_CamoShader);
            Log($"Camo: {s_Mesh.Split('/')[^1]} wears '{s_CamoShader.Split('/')[^1]}', which this tab's " +
                $"'{s_ActiveTarget.Split('/')[^1]}' graph cannot draw — " +
                (s_Other >= 0
                    ? $"shown in the tab of '{m_Tabs[s_Other].Graph.TargetShader.Split('/')[^1]}'."
                    : "its own graph opens in a tab of its own."));

            if (s_Other >= 0)
            {
                m_PreviewMesh = s_PreviousMesh;
                SaveActiveTab();
                m_PreviewMesh = s_Mesh;
                // that tab shows THIS subject's own document (see EditorTab.Subjects)
                MoveTabToSubject(m_Tabs[s_Other], DocumentKeyOf(s_Mesh));
                m_Tabs[s_Other].PreviewMesh = s_Mesh;
                m_Tabs[s_Other].PreviewWeapon = (MeshList.SelectedItem as CamoWeapon)?.Mesh;
                ActivateTab(s_Other);
                s_TabActivated = true;
            }

            s_ActiveTarget = "";
        }

        var s_FollowedDocument = false;
        if (s_TabActivated)
        {
            // The tab that can draw it took the window over and dressed the subject for its own preset.
        }
        else if (s_ActiveTarget.Length > 0)
        {
            // ⭐ this subject's OWN document takes the canvas (an edit belongs to that subject only — see EditorTab.Subjects)
            s_FollowedDocument = FollowSubjectDocument(DocumentKeyOf(s_Mesh));
            // ⭐ where each subject keeps its own camo (a soldier's parts on their stock looks), the picker reads the one of the document that
            // just took the canvas — the torso's Ninja is not the legs' (measured with --switchtest: the picker kept the part left's)
            if (s_FollowedDocument && NativePerSubject(m_DocumentSubject))
                SyncNativeCamoBox();
            s_DressShader = s_ActiveTarget;
            if (!s_ActiveTarget.Equals(s_CamoShader, StringComparison.OrdinalIgnoreCase))
                Log($"Camo: {s_Mesh.Split('/')[^1]} wears '{s_CamoShader.Split('/')[^1]}' — shown with this " +
                    $"tab's '{s_ActiveTarget.Split('/')[^1]}' graph.");
        }
        else
        {
            // ⛔ TRANSLATING ALWAYS MOUNTS — it has no cache of its own, by design ("read it from the game").
            // For camo work that is the wrong default: the same handful of presets would be re-read from the
            // game on every weapon, forever. So the TRANSLATED GRAPH is kept.
            var s_Cached = CamoGraphCachePath(s_CamoShader);
            var s_Opened = false;

            if (File.Exists(s_Cached))
                try
                {
                    // Aimed at the preset BEFORE the tab takes it: ActivateTab dresses the weapon for the
                    // graph's target, so a cached graph naming anything else would dress it wrong.
                    var s_Graph = LoadCamoGraph(s_CamoShader);

                    if (s_Fresh)
                        AimActiveTab(s_Graph);
                    else
                    {
                        // Beside the edited tab: that tab is saved with the weapon IT was showing, and the
                        // new one activates with the weapon just picked.
                        m_PreviewMesh = s_PreviousMesh;
                        SaveActiveTab();
                        m_PreviewMesh = s_Mesh;
                        // a tab the studio opens for a subject shows the game's originals (EditorTab.Original)
                        m_Tabs.Add(new EditorTab
                        {
                            Graph = s_Graph, GraphPath = null, PreviewMesh = s_Mesh,
                            PreviewWeapon = (MeshList.SelectedItem as CamoWeapon)?.Mesh, Original = true,
                        });
                        ActivateTab(m_Tabs.Count - 1);
                    }

                    TargetShaderBox.Text = s_CamoShader;
                    Canvas.Refresh();
                    Log($"Camo: '{s_CamoShader}' from the cache — no mount needed" +
                        (s_Fresh ? ", into this tab." : ", in its own tab."));
                    s_Opened = true;
                    s_TabActivated = true;
                }
                catch (Exception s_Exception)
                {
                    Log($"Camo: cached graph unusable ({s_Exception.Message}) — reading it from the game.");
                }

            if (!s_Opened)
            {
                Log($"Camo: loading '{s_CamoShader}', the shader this weapon wears.");

                // ⛔⛔ THE SAME TAB THE CACHED GRAPH WOULD HAVE OPENED IN (2026-09-28: the US support's legs, the first pick after
                // characterroot_xp4's translation was retired): beside the edited tab — which is saved with the subject IT was showing — a tab
                // of the studio's own that shows the game's original (EditorTab.Original), the translation landing in it. SelectTargetAsync
                // opened a plain tab instead: no Original, so a picture put on it never forked into the working tab, the part was re-dressed
                // "as shipped" when the "all the cloth" box re-picked it (sand03 where the picture was — --allclothtest, only on the pick
                // that translated), and the tab left behind remembered this subject's mesh. (A fresh tab is aimed by the translation itself.)
                var s_OwnTab = !s_Fresh;
                if (s_OwnTab)
                {
                    m_PreviewMesh = s_PreviousMesh;
                    SaveActiveTab();
                    m_PreviewMesh = s_Mesh;
                    m_Tabs.Add(new EditorTab
                    {
                        Graph = new ShaderGraph { TargetShader = s_CamoShader }, GraphPath = null, PreviewMesh = s_Mesh,
                        PreviewWeapon = (MeshList.SelectedItem as CamoWeapon)?.Mesh, Original = true,
                    });
                    ActivateTab(m_Tabs.Count - 1);
                }

                await SelectTargetAsync(s_CamoShader);
                if (Superseded())
                    return;

                // (the game's original has nothing to undo back to: the empty graph it was opened with is not a document)
                if (s_OwnTab)
                    Canvas.RestoreUndo(Array.Empty<string>(), Array.Empty<string>());

                // Keep what that cost, so no weapon pays for this preset again.
                //
                // ⛔⛔ BUT NEVER A FAILED TRANSLATION. Translating MOUNTS the game; when it cannot (another
                // process holds the mount) the canvas is left with a stub — a root and a few loose texture
                // nodes — and "more than zero nodes" happily wrote it to the cache. From then on every open
                // used the stub, whose root pins are EMPTY, so the preview drew the pin DEFAULTS: a flat
                // grey object that reads as "the camo does not work". Measured on vehiclepreset_mud: the
                // stub was 8 nodes / 3 wires where the real translation is 119 / 152.
                // The test is the ROOT'S PINS, not the node count: a graph whose root reaches nothing is
                // not a small graph, it is a failure.
                var s_Root = Canvas.Graph.Root;
                var s_PinsFed = RootPinsFed(Canvas.Graph);

                if (s_Root != null && s_PinsFed < 2)
                    Log($"Camo: '{s_CamoShader}' came out of the game with only {Canvas.Graph.Nodes.Count} node(s) " +
                        $"and {s_PinsFed} pin(s) on its root — that is a FAILED translation (the game was " +
                        "probably busy), so it is NOT cached. Try again with nothing else mounting it.");

                var s_BigEnough = TranslationLooksComplete(Canvas.Graph, s_CamoShader, out var s_Small, out var s_Measured);
                if (!s_BigEnough)
                    Log($"Camo: '{s_CamoShader}' translated to {s_Small} — that is a FAILED translation, so it " +
                        "is NOT cached. Try again with nothing else mounting the game.");

                // One fed pin is enough once the size was measured (a forward shader has one output); two
                // without a yardstick — see TranslationLooksComplete.
                if (Canvas.Graph.Nodes.Count > 0 && s_PinsFed >= (s_Measured ? 1 : 2) && s_BigEnough &&
                    Canvas.Graph.TargetShader.Equals(s_CamoShader, StringComparison.OrdinalIgnoreCase))
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(s_Cached)!);
                        File.WriteAllText(s_Cached, Canvas.Graph.ToJson());
                        Log($"Camo: '{s_CamoShader}' cached — the next weapon wearing it opens instantly.");
                    }
                    catch (Exception s_Exception)
                    {
                        Log($"Camo: could not cache the graph ({s_Exception.Message}).");
                    }
            }
        }

        // ⭐ THE WEAPON DRESSES ITSELF — AFTER the tab work, never before. Activating a tab restores that
        // tab's own state (its variation, its contract and, until it was stopped, its remembered MESH), so
        // anything set for the weapon beforehand was overwritten: the AUG came up as the ACR, wearing the
        // map's base set. In camo mode ActivateTab now dresses the picked weapon itself, so this is only
        // repeated when no tab was activated (same tab kept) or when the preset came out of the game.
        if (!s_TabActivated)
            DressCamoWeapon(s_Mesh, s_DressShader);

        // another document on the canvas: whoever owns the camo's own settings (the studio's simple form) shows ITS pattern and numbers
        if (s_FollowedDocument)
            CamoSimpleShown?.Invoke(this, EventArgs.Empty);

        // the tab's first subject: the document as it is now, before any change on it, is what every other subject starts from
        if (m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count && m_Tabs[m_ActiveTab].DocumentSubject == null &&
            string.Equals(m_PreviewMesh, s_Mesh, StringComparison.OrdinalIgnoreCase))
        {
            m_DocumentSubject = DocumentKeyOf(s_Mesh);
            m_Tabs[m_ActiveTab].DocumentSubject = m_DocumentSubject;
            m_Tabs[m_ActiveTab].Common ??= ShaderGraph.FromJson(DocumentGraph.ToJson());

            // ⭐ …and a subject with a starting document of its own (the RHIB, SubjectPresetIn) shows IT — after the common one was taken
            // from the preset's graph, never from its own: the common one is what every OTHER subject starts from
            // (asked by the SUBJECT'S key — a soldier's part is "soldiers/US_Support/lower", not his legs' mesh; a vehicle's key is its mesh)
            if (m_MaterialEditKey == null && SubjectPresetIn(m_Tabs[m_ActiveTab], m_DocumentSubject, NativeKeyOf(DocumentGraph)) is { } s_Start &&
                SubjectFingerprint(s_Start) != SubjectFingerprint(DocumentGraph))
            {
                m_Tabs[m_ActiveTab].Graph = s_Start;
                LoadGraph(s_Start);
                Canvas.RestoreUndo(Array.Empty<string>(), Array.Empty<string>());

                // ⛔ DRESSED AGAIN, for THIS document: the dress above ran with the preset's graph, and its pictures (the RHIB's mask on
                // t7) only reach the node's thumbnail and the preview through the dress (LoadCamoTextures → ApplyCustomTextureOverrides).
                // Without it the mask's register sampled nothing — 0, so 1 − mask = 1 and the camo covered the WHOLE boat (seen in
                // a screenshot: the node said "press Reload from game"), while a subject reached from another one (FollowSubjectDocument,
                // dressed after its document loads) came out right. The same dress on both paths: the frame is the same.
                DressCamoWeapon(s_Mesh, s_DressShader);
                if (m_SubjectPresetSaid.Add(s_Mesh))
                    Log($"Camo: {s_Mesh.Split('/')[^1]} opens from ITS OWN document ({s_Start.Nodes.Count} node(s)), not the stock " +
                        $"'{s_Start.TargetShader.Split('/')[^1]}' — it is this vehicle's original in the studio, and a bake takes it.");

                // another document on the canvas: the simple form shows ITS pattern and numbers — the tab's activation above filled it from
                // the common document, before this one took the canvas (a soldier's part with a starting document of its own, 2026-09-29;
                // CAMO_SOLDIER_SIMPLE_OLD=1 = not asked again, as before)
                if (Environment.GetEnvironmentVariable("CAMO_SOLDIER_SIMPLE_OLD") != "1")
                    CamoSimpleShown?.Invoke(this, EventArgs.Empty);
            }

            RefreshTabBar();
        }

        // ⭐ …and the form follows the subject AS IT LANDED (review 2026-09-29): a click on a tab shows the form while that tab's subject is
        // still being picked, with the numbers of the one on screen before — the legs' form read the torso's tiling, and a pattern chosen
        // then committed it. Showing it again writes nothing (SetCamoOverrides), so this only corrects what it shows.
        // CAMO_FORM_WRITES_OLD=1 = not shown again, as before.
        if (CamoMode && Environment.GetEnvironmentVariable("CAMO_FORM_WRITES_OLD") != "1")
            CamoSimpleShown?.Invoke(this, EventArgs.Empty);

        SyncReferenceNote();
        SchedulePreview();
    }

    /// <summary>
    /// Puts a graph into the ACTIVE tab in place of a fresh one — the tab keeps its place, the document
    /// changes. Everything the tab carries is reset with it; ActivateTab then takes the window over as usual.
    /// </summary>
    private void AimActiveTab(ShaderGraph p_Graph)
    {
        if (m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count)
        {
            OpenInNewTab(p_Graph, null);
            return;
        }

        var s_Tab = m_Tabs[m_ActiveTab];
        s_Tab.Graph = p_Graph;
        s_Tab.Undo = Array.Empty<string>();
        s_Tab.Redo = Array.Empty<string>();
        s_Tab.Contract = null;
        s_Tab.Variation = "0";
        s_Tab.GraphPath = null;
        // a new document: no subject has changed anything on it yet
        s_Tab.DocumentSubject = null;
        s_Tab.Common = null;
        s_Tab.Subjects.Clear();
        ActivateTab(m_ActiveTab);
    }

    /// <summary>
    /// Dresses the picked weapon for a preset: its own texture set and material values, the preset's contract,
    /// the mesh view. Everything the weapon owns, in ONE place — run after a weapon is picked and again whenever
    /// a tab takes the window over, because in camo mode the tab does not own the art, the weapon does.
    ///
    /// ⛔ NO FALLING BACK TO THE MAP'S BASE SET. That set belongs to whichever weapon came first in the map —
    /// literally the M4A1 for weaponpresetshadowfp, the M2 Browning for weaponpreset3p — so using it dresses
    /// THIS weapon in another one's art over its own UVs, silently. If the studio cannot name this weapon's
    /// textures, it says so and leaves the view alone.
    /// </summary>
    private void DressCamoWeapon(string p_Mesh, string p_Shader)
    {
        // ⛔ RE-ASSERTED, not assumed: the tab machinery puts ITS remembered mesh (or none) in here.
        m_PreviewMesh = p_Mesh;
        m_PreviewWeapon = (MeshList.SelectedItem as CamoWeapon)?.Mesh;

        // "|0" is the shipped look; the studio's sets are keyed by mesh, so the tab's key means nothing here.
        m_Variation = $"{p_Mesh}|0";

        // ⛔⛔ THE CONTRACT, OR NONE OF THE MATERIAL VALUES LAND. PushInstanceValues resolves a parameter
        // NAME to a constant-buffer slot through m_Contract — and m_Contract is normally filled from the
        // shader INDEX, which camo mode never loads (the shader browser is hidden). So it stayed null, every
        // value was dropped, and the weapons drew green. It was invisible in the off-screen sweep because
        // THAT path reads the contract from the cached dxbc; the window did not. Same source here now.
        // ⛔⛔ RE-READ WHEN THE SHADER CHANGES, NOT ONLY WHEN IT IS NULL. The contract says which interpolator
        // carries the UVs and where each material value lands, so keeping the previous weapon's contract
        // feeds THIS weapon the wrong ones — the textures land on the wrong coordinates. It only showed up
        // as "it depends on the order I open them in, and reopening the tool fixes it", because the first
        // weapon of a session set the contract and every later one with a different preset inherited it.
        if (!string.Equals(m_CamoContractShader, p_Shader, StringComparison.OrdinalIgnoreCase) ||
            m_Contract == null)
        {
            m_Contract = ContractFromCache(p_Shader);
            m_CamoContractShader = m_Contract != null ? p_Shader : null;
        }

        // ⛔ Loading the thumbnails is not SHOWING them: LoadCamoTextures fills the node previews and pushes
        // them into the 3D view, which is the step whose absence drew the weapon with no art at all.
        // The document's native camo, if any, rides along: its texture and its tuned numbers.
        var s_Native = DocumentGraph.NativeCamo;
        if (m_CamoSlotsFor?.Invoke(p_Mesh, p_Shader, s_Native) is { } s_Explicit)
        {
            var s_Loaded = LoadCamoTextures(s_Explicit, m_CamoValuesFor?.Invoke(p_Mesh, p_Shader, s_Native));

            // ⛔ The thumbnails are the CAMO's set now: a no-camo graph still standing on the canvas is no longer
            // dressed, whatever mesh it was dressed for — re-picking the SAME piece left the as-shipped graph
            // showing the camo's textures (--piecewalk, the flash suppressor), because ShowFactoryGraph only
            // re-dressed on a mesh CHANGE.
            m_ShownFactoryMesh = null;
            Log($"Camo: {p_Mesh.Split('/')[^1]} — {s_Loaded} texture(s) for '{p_Shader.Split('/')[^1]}'" +
                (s_Native == null ? "." : $", starting from the game's '{s_Native}'."));
        }
        else
        {
            Log($"Camo: no texture set known for '{p_Mesh}' — leaving the preview as it is rather than " +
                "dressing it in another weapon's art.");
        }

        // ⛔ RE-ASSERTED AFTER the tab opens: opening a document restores ITS shape, undoing the mesh view
        // set moments ago — the extra click on the mesh icon the user should not have to make. The target
        // and what it replaces follow the preset on the canvas, so a tab switch re-sorts the sections too.
        m_MeshMode = true;
        AimPreviewAt(p_Shader);
        m_Preview.MeshTargetAliases = CamoAliasesFor(p_Shader);
        if (m_Preview.HasMesh)
            m_Preview.Shape = View.PreviewShape.Mesh;

        SyncShapeIcons(View.PreviewShape.Mesh);

        // The material picker follows the object on screen and THIS tab's choices for its materials.
        FillMaterialList();

        // A graph that samples a sticker layer gets THIS weapon's layer (its own placements, or an empty
        // one) — the dressing above put the game's art back on every other register, and the sticker
        // register would otherwise wear the previous weapon's stickers or the placeholder.
        if (DocumentGraph.StickerRegister != null)
            RefreshStickerLayer();

        // Nothing chosen yet means the weapon as it ships, drawn with the game's own shaders and its own
        // graph on the canvas (see SyncFactoryLook); the foreign registrations were cleared by the pick, so
        // they are made again here. Not re-entered from the dress SyncFactoryLook itself makes.
        if (!m_InFactorySync)
            SyncFactoryLook();

        // ⭐ EVERY MATERIAL WITH ITS OWN ART, IN BOTH LOOKS. The shipped side first (the game's own shader per
        // material, for the shaders more than one material wears), then the camo side.
        RegisterSectionArt();

        // …and the rest of a soldier with the documents of HIS OTHER PARTS on it (the whole skin, not only the part being edited)
        RefreshContextDress();

        // ⭐ AND SO ARE THE PER-MATERIAL ONES. The pick that got here emptied the foreign registry, and the
        // dress named entries in it: without this, everything with more than one camo material came back
        // wearing the single set — the last material picked — instead of its own art.
        RefreshMaterialDress();

        // …and the materials whose own graph this camo edits, on their sections.
        RefreshMaterialEdits();

        // ⭐ AND THE KIT THE GAME EQUIPS SEPARATELY IS NOT ON THE SUBJECT UNLESS IT IS THE ONE BEING LOOKED
        // AT. Last, because it reads the sections the pick above loaded.
        ApplyPieceVisibility();
    }

    /// <summary>True while SyncFactoryLook is dressing the weapon, so that dress does not re-enter it.</summary>
    private bool m_InFactorySync;

    /// <summary>
    /// Whether the preview should show the weapon AS IT SHIPS — no camo at all — instead of the graph on the
    /// canvas: true while the document holds nothing that would ship. A native camo, a pattern on a texture
    /// node, a sticker, a value typed over the weapon's own, a live value or pattern from the simple form, or
    /// nodes and wires that differ from the preset's are each something the bake would put in the game, and
    /// the moment one exists the graph is drawn (with the weapon's shipped textures where nothing replaces
    /// them — the "basic" look the studio names in the picker).
    ///
    /// ⛔ WITHOUT THIS EVERY WEAPON PREVIEWED CAMOUFLAGED. The graph on the canvas is the camo-sampling twin
    /// of the preset the body wears, and its camo register is fed the slot map's base texture when the weapon
    /// binds none of its own — so a weapon nobody had touched yet came up wearing another gun's desert
    /// pattern (the rule: by default every weapon wears its default skin, with no camo).
    /// </summary>
    private bool CamoFactoryLook() => CamoMode && DocumentGraph.NativeCamo == null;

    /// <summary>
    /// The picker is the truth (seen after a native camo on one weapon, another weapon and
    /// "as shipped" again, the camo's graph stayed — a slider moved earlier kept "something to ship" true
    /// while the picker said no camo). So: "as shipped" selected means the weapon as it ships, always; and
    /// the moment the camo gets something to ship while "as shipped" is selected — a value, a pattern, a
    /// sticker — the picker moves to the basic camo by itself, which is what those things are seen on.
    /// </summary>
    private void PromoteToBasicCamo(string p_Why)
    {
        if (!CamoMode || DocumentGraph.NativeCamo != null || m_CamoBasicKey == null)
            return;

        // ⛔ THE SUBJECT MAY ALREADY HAVE ITS DOCUMENT UNDER THE BASIC CAMO in this tab — changed there, then the picker moved back to as shipped:
        // the change goes on THAT one (review 2026-09-29, B2). Relabelling this one made it the subject's basic document: parked over the
        // other at the next move — the earlier work gone — and its camo settings (a weapon's pattern, tiling and wear are the tab's) shared to
        // every subject of the tab's basic camo, the pattern taken off all of them. Only when there is one: with none, this document — a
        // part's own starting document with its mask (the US support's legs) — is what the basic camo starts from here, as before.
        // (The tab's own camo is left as it was: a fork reads it as the camo the original was on.) CAMO_PROMOTE_RELABEL_OLD=1 = relabel always.
        if (m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count && m_DocumentSubject is { } s_Subject &&
            Environment.GetEnvironmentVariable("CAMO_PROMOTE_RELABEL_OLD") != "1" &&
            m_Tabs[m_ActiveTab].Set(m_CamoBasicKey).Subjects.ContainsKey(s_Subject) && FollowNativeDocument(m_CamoBasicKey))
        {
            SyncNativeCamoBox();
            Log($"Camo: {p_Why} — the picker moves to '{m_CamoBasicDisplay ?? m_CamoBasicKey}', and {s_Subject.Split('/')[^1]} goes on with its " +
                "document there, changed on it earlier.");
            return;
        }

        DocumentGraph.NativeCamo = m_CamoBasicKey;
        SyncNativeCamoBox();
        Log($"Camo: {p_Why} — the picker moves to '{m_CamoBasicDisplay ?? m_CamoBasicKey}', which is what it is seen on.");
    }

    /// <summary>The picker key of the basic camo (the camo preset with the weapon's shipped textures), or null when the studio names none.</summary>
    private string? m_CamoBasicKey;

    /// <summary>In camo mode: the preset a weapon is shown with when it wears no camo (target -> the game's NoCamo preset of that view).</summary>
    private Func<string, string>? m_CamoNoCamoPresetFor;

    /// <summary>
    /// The preset whose graph the canvas shows while the weapon is as shipped: the studio's no-camo preset
    /// for the tab's target — the weapon before any camo, one rule for every weapon — or, in the plain
    /// editor, the preset the body wears when that is not the target, or nothing (the document stays).
    /// </summary>
    private string? FactoryPresetOf()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
            return null;

        // The plain editor: the worn preset when it is not the target, else the document stays.
        if (m_CamoNoCamoPresetFor == null)
            return WornPresetOf();

        // ⛔⛔ AN ATTACHMENT IS SHIPPED WITH ITS OWN SHADER, NOT WITH THE WEAPON'S (found with the studio and
        // the game side by side: the "as shipped" preview must be literally the game's default shader for that
        // weapon, attachment or whatever it is, never a camo's forced on it — the PKA drew flat grey here and
        // grainy in game). "As shipped" used to answer
        // with the no-camo preset of whatever the target box held, which on an accessory is the WEAPON's body
        // preset: a different shader, with the weapon's own art behind it. What the game ships this piece with
        // is the shader ITS material wears, and that is what the canvas and the view have to show.
        if (PickedAccessory() != null && WornPresetOf() is { Length: > 0 } s_Own)
            return s_Own;

        // ⭐ A WEAPON SHOWS WHAT ITS BODY REALLY WEARS (found on the F2000: as shipped became editable,
        // and its no-camo graph, edited, drew the F2000 grey — its olive is the Camo slot of the camo preset it really wears): the worn
        // preset when it is not the camo's (the M416's no-camo one), else NOTHING — the camo's preset IS what it wears (the F2000, the
        // M240, ~28 weapons), so the document itself stays on the canvas, drawn as shipped, as a vehicle's is (AsShippedDocumentEdited).
        // This retires, for those weapons, the rule of 2026-09-12 (as shipped, every graph exactly vanilla with no camo; the FAMAS had
        // switched itself to the default camo for no reason), which held while as shipped could not be edited.
        if (s_Target.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase))
            return WornPresetOf();

        // A vehicle: the studio's answer for its preset (it wears the camo's own; no twin stands in for it).
        return m_CamoNoCamoPresetFor.Invoke(s_Target);
    }

    /// <summary>The (mesh, preset, register) the sticker factory shader was last built for; a change rebuilds it.</summary>
    private string? m_StickerFactoryKey;

    /// <summary>
    /// Registers (once per weapon) the shader that draws the body AS SHIPPED WITH ITS STICKERS: the pristine
    /// graph of the preset the body wears (no-camo on 27 bodies, the camo preset with its own art — the
    /// F2000's olive — on the rest), extended with the sticker nodes at the document's register, compiled
    /// here and registered as a foreign shader with this weapon's own textures and numbers for that preset.
    /// The canvas is untouched (it keeps the no-camo graph); only the draw changes. Returns the registered
    /// name, or null when it cannot be built (the graph on the canvas draws then).
    /// </summary>
    private string? EnsureStickerFactoryShader()
    {
        var s_Mesh = m_PreviewMesh;
        var s_Preset = BodyPresetOf();
        var s_Register = DocumentGraph.StickerRegister;
        if (s_Mesh is not { Length: > 0 } || s_Preset == null || s_Register == null)
            return null;

        // Rebuilt when the weapon, the preset, the register OR the animated sticker changes: the GIF's
        // flipbook is compiled into this shader, so a GIF placed after the shader was built needs a new one.
        var s_Name = s_Preset + "#stickers";
        var s_Key = $"{s_Mesh}|{s_Preset}|{s_Register}|{DocumentGraph.StickerAnimation ?? ""}|{DocumentGraph.StickerAnimationOpaque}|{DocumentGraph.StickerLayerSide}";
        if (string.Equals(m_StickerFactoryKey, s_Key, StringComparison.OrdinalIgnoreCase) && m_Preview.HasForeignShader(s_Name))
            return s_Name;

        if (!File.Exists(CamoGraphCachePath(s_Preset)))
            return null;

        try
        {
            var s_Graph = LoadCamoGraph(s_Preset);
            if (StickerGraph.Ensure(s_Graph, m_StickerDiffuseRegisterFor?.Invoke(s_Preset), s_Register, out _, DocumentGraph.StickerLayerSide) == null)
                return null;

            // The animated sticker plays on the shipped look too: the same map and sheet, the same timeline.
            if (DocumentGraph.StickerAnimation is { } s_Gif && StickerAtlasOf(s_Gif) is { } s_Atlas)
                StickerGraph.EnsureAnimated(s_Graph, s_Atlas, DocumentGraph.StickerLayerSide, out _);

            var s_Contract = ContractFromCache(s_Preset);
            if (s_Contract == null)
                return null;

            var s_Result = new HlslEmitter { Contract = s_Contract }.Emit(s_Graph);
            if (!s_Result.Ok)
            {
                Log($"Camo: stickers over '{s_Preset.Split('/')[^1]}' as shipped: {s_Result.Errors[0]} — the canvas graph draws instead.");
                return null;
            }

            using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                s_Result.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);
            if (s_Compiled.Bytecode == null)
                return null;

            var s_Slots = m_CamoSlotsFor?.Invoke(s_Mesh, s_Preset, null) ?? new Dictionary<string, string>();
            var s_Textures = DecodeCachedTextures(s_Slots, TexCacheDir);
            var s_Values = m_CamoValuesFor?.Invoke(s_Mesh, s_Preset, null);
            var s_Error = m_Preview.RegisterForeignShader(s_Name, s_Compiled.Bytecode.Data, s_Textures, s_Values,
                SamplersForShader(s_Preset));
            if (s_Error != null)
            {
                Log($"Camo: stickers over '{s_Preset.Split('/')[^1]}' as shipped: {s_Error} — the canvas graph draws instead.");
                return null;
            }

            m_StickerFactoryKey = s_Key;
            Log($"Camo: stickers over the weapon as shipped — the body draws with '{s_Preset.Split('/')[^1]}' (its own textures and " +
                $"numbers) plus the sticker layer at t{s_Register}; the canvas keeps the no-camo graph.");

            // The layer, the coordinate map and the frame sheet as they stand go on it now; every later
            // placement refreshes them (RefreshStickerLayer).
            for (var s_Slot = s_Register.Value; s_Slot <= s_Register.Value + 2 && m_Preview.Ready; s_Slot++)
            {
                if (!m_TexturePreviews.TryGetValue(s_Slot.ToString(), out var s_Texture) || s_Texture is not BitmapSource s_Bitmap)
                    continue;

                var s_Source = s_Bitmap.Format == PixelFormats.Bgra32 || s_Bitmap.Format == PixelFormats.Pbgra32
                    ? s_Bitmap
                    : new FormatConvertedBitmap(s_Bitmap, PixelFormats.Bgra32, null, 0);
                var s_Pixels = new uint[s_Bitmap.PixelWidth * s_Bitmap.PixelHeight];
                s_Source.CopyPixels(s_Pixels, s_Bitmap.PixelWidth * 4, 0);
                m_Preview.SetForeignTexture(s_Name, s_Slot, s_Pixels, s_Bitmap.PixelWidth, s_Bitmap.PixelHeight,
                    m_TextureSrgb.TryGetValue(s_Slot.ToString(), out var s_Srgb) && s_Srgb);
            }

            return s_Name;
        }
        catch (Exception s_Exception)
        {
            Log($"Camo: stickers over '{s_Preset.Split('/')[^1]}' as shipped: {s_Exception.Message.Split('\n')[0].Trim()} — the canvas graph draws instead.");
            return null;
        }
    }

    /// <summary>What the accessory shader was last built for (mesh, preset, native camo, pattern); a change rebuilds it.</summary>
    private string? m_AccessoryShaderKey;

    /// <summary>The numbers last fed to the accessory shader, so a slider tick that changes nothing costs nothing.</summary>
    private string? m_AccessoryValuesKey;

    /// <summary>Camo Studio only: the register a preset samples its camo pattern from (measured by the studio, never assumed).</summary>
    private Func<string, string?>? m_CamoRegisterFor;

    /// <summary>
    /// The constants an accessory takes from its host weapon rather than from its own material: the
    /// third-person variation's baseline (the same three the studio's bake writes on a weapon's 3P variation).
    /// </summary>
    private static readonly string[] AccessoryHostConstants = { "CamoTiling", "WearAmount", "WearPower" };

    /// <summary>The accessory picked in the list, or null when the entry is a weapon (or nothing is picked).</summary>
    private CamoWeapon? PickedAccessory() =>
        AccessoryList.SelectedItem as CamoWeapon is { Tag.Length: > 0 } s_Item ? s_Item : null;

    /// <summary>
    /// The tag of a picker entry that is a piece of the SUBJECT'S OWN MESH rather than a mesh of its own —
    /// today the vehicles' reactive armour, which lives inside the hull dump as whole sections and parts.
    /// </summary>
    internal const string PieceInMeshTag = "ReactiveArmour";

    /// <summary>
    /// The tag of the picker entry that is an aircraft's INTERIOR cockpit — the model the game draws around
    /// the pilot, a mesh of its own (aircraft have two different meshes: one while flying it, another when a
    /// player sees it from outside). The Cockpit box ticks it.
    /// </summary>
    internal const string CockpitTag = "Cockpit";

    /// <summary>What the studio says that piece is, for the mesh on screen: whole sections, and parts per section.</summary>
    private Func<string, (IReadOnlyList<int> Sections, IReadOnlyDictionary<int, int[]> Parts)?>? m_CamoPieceInMesh;

    /// <summary>
    /// Shows the subject WEARING that piece or without it, the way the game equips it.
    ///
    /// ⛔ NOT A COSMETIC TOGGLE: the piece is a kit the player fits, so the vehicle on its own must not carry
    /// it (the reactive armour is an accessory, not part of the tank), and
    /// picking it in the accessory list must show it ALONE, like any attachment. Both are the same data read
    /// two ways, so they live in one place.
    /// </summary>
    private void ApplyPieceVisibility()
    {
        m_Preview.HiddenSections.Clear();
        m_Preview.VisibleParts.Clear();

        if (m_PreviewMesh is not { Length: > 0 } s_Mesh)
            return;

        HideBlurDiscs(s_Mesh);
        ApplyPieceInMesh(s_Mesh);
        HideSpinningRotorParts(s_Mesh);
    }

    /// <summary>The piece that lives inside the subject's own mesh (the reactive armour), worn or picked.</summary>
    private void ApplyPieceInMesh(string p_Mesh)
    {
        if (m_CamoPieceInMesh == null || m_CamoPieceInMesh(p_Mesh) is not { } s_Piece)
            return;

        var s_Picked = PickedAccessory()?.Tag == PieceInMeshTag;
        var s_Sections = m_Preview.MeshSections;

        for (var s_Index = 0; s_Index < s_Sections.Count; s_Index++)
        {
            var s_WholeIsPiece = s_Piece.Sections.Contains(s_Index);
            var s_HasPieceParts = s_Piece.Parts.TryGetValue(s_Index, out var s_PieceParts);

            if (s_Picked)
            {
                // Only the piece: sections that are not it at all go, and a mixed one keeps just its parts.
                if (!s_WholeIsPiece && !s_HasPieceParts)
                    m_Preview.HiddenSections.Add(s_Index);
                else if (s_HasPieceParts)
                    m_Preview.VisibleParts[s_Index] = new HashSet<int>(s_PieceParts!);
            }
            else
            {
                // The subject without it: the piece's own sections go, and a mixed one keeps everything else.
                if (s_WholeIsPiece)
                    m_Preview.HiddenSections.Add(s_Index);
                else if (s_HasPieceParts)
                    m_Preview.VisibleParts[s_Index] = new HashSet<int>(
                        s_Sections[s_Index].Parts.Select(p_P => p_P.Part).Except(s_PieceParts!));
            }
        }
    }

    /// <summary>
    /// Leaves out of the drawing every section whose game shader is a ROTOR BLUR DISC (asked on the AH-1Z:
    /// remove the rotor disc, and the tail rotor's too).
    ///
    /// Recognised by what the BYTECODE does, not by a name: the pixel shader writes one render target whose
    /// colour is a constant black (`mov o0.xyz, l(0,0,0,0)`) and whose alpha is computed — measured on
    /// `vehicles/ah1z/ah1z_rotor` (all 32 permutations, 800–968 bytes: alpha = saturate(t3.r) × the engine's
    /// DISTANCE fade in v5.w, nothing else). It is the translucent dark disc the game draws in place of the
    /// blades while the rotor spins, a part the engine switches on in flight; the customisation view shows the
    /// aircraft stopped, so the disc is not there. A material is still listed and still editable: picking it
    /// (edit, isolate) draws it, see ShaderPreview.HiddenSections.
    /// </summary>
    private void HideBlurDiscs(string p_Mesh)
    {
        var s_Sections = m_Preview.MeshSections;
        var s_Hidden = new List<string>();
        for (var s_Index = 0; s_Index < s_Sections.Count; s_Index++)
        {
            if (!IsBlurDiscShader(s_Sections[s_Index].Shader))
                continue;

            m_Preview.HiddenSections.Add(s_Index);
            s_Hidden.Add($"{s_Sections[s_Index].Material} ({s_Sections[s_Index].Shader.Split('/')[^1]})");
        }

        if (s_Hidden.Count > 0 && m_BlurDiscsLogged.Add(p_Mesh))
            Log($"Camo: {s_Hidden.Count} section(s) left out — {string.Join(", ", s_Hidden)}: the shader draws only a " +
                "black translucent disc (the blur of a spinning rotor, which the game shows in flight, not on a parked aircraft). " +
                "Pick that material to see it.");
    }

    /// <summary>For a mesh, the composite parts the game draws only while a rotor SPINS.</summary>
    private Func<string, IReadOnlyCollection<int>?>? m_CamoPartsHiddenAtRest;

    /// <summary>
    /// Leaves out the parts the game itself swaps in only while a rotor spins — measured in the blueprints, not
    /// guessed from a shader: every `RotorComponentData` names a `LowRpmModel.PartIndex` (the blades, drawn
    /// when the rotor is still) and a `HighRpmModel.PartIndex` (drawn above `ChangeModelRpm`). On the AH-6J
    /// the high parts are a hub with stub blades PLUS the translucent blur square (2026-09-22), and its blur
    /// shader paints a coloured texture — so the bytecode rule above, made on the AH-1Z's black disc, does not
    /// see it, and the "remove the rotor disc, and the tail's too" request came back on the first aircraft
    /// with a different blur shader. The customisation view shows the aircraft parked, so the high parts go —
    /// all four helicopters' blur sections lie 100 % inside them. A section wholly made of such parts is
    /// left out like a disc (picking its material still draws it); a mixed one keeps its other parts.
    /// </summary>
    private void HideSpinningRotorParts(string p_Mesh)
    {
        if (m_CamoPartsHiddenAtRest?.Invoke(p_Mesh) is not { Count: > 0 } s_Spinning)
            return;

        var s_Sections = m_Preview.MeshSections;
        var s_Hidden = new List<string>();
        var s_Trimmed = 0;
        for (var s_Index = 0; s_Index < s_Sections.Count; s_Index++)
        {
            if (m_Preview.HiddenSections.Contains(s_Index))
                continue;

            var s_All = s_Sections[s_Index].Parts.Select(p_P => p_P.Part).ToHashSet();
            if (!s_All.Overlaps(s_Spinning))
                continue;

            var s_Visible = m_Preview.VisibleParts.TryGetValue(s_Index, out var s_Already)
                ? new HashSet<int>(s_Already)
                : s_All;
            s_Visible.ExceptWith(s_Spinning);

            if (s_Visible.Count == 0)
            {
                m_Preview.HiddenSections.Add(s_Index);
                m_Preview.VisibleParts.Remove(s_Index);
                s_Hidden.Add($"{s_Sections[s_Index].Material} ({s_Sections[s_Index].Shader.Split('/')[^1]})");
            }
            else
            {
                m_Preview.VisibleParts[s_Index] = s_Visible;
                s_Trimmed++;
            }
        }

        if ((s_Hidden.Count > 0 || s_Trimmed > 0) && m_RotorPartsLogged.Add(p_Mesh))
            Log($"Camo: the rotor parts the game draws only while spinning are left out (parts " +
                $"{string.Join(", ", s_Spinning.OrderBy(p_P => p_P))}; the aircraft is shown parked)" +
                (s_Hidden.Count > 0 ? $" — {s_Hidden.Count} whole section(s): {string.Join(", ", s_Hidden)}" : "") +
                (s_Trimmed > 0 ? $"; {s_Trimmed} section(s) keep their other parts" : "") +
                ". Pick that material to see it.");
    }

    private readonly HashSet<string> m_RotorPartsLogged = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> m_BlurDiscsLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> m_BlurDiscShaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a shader's cached pixel bytecode is a blur disc: a single render target, its colour written as
    /// an all-zero literal and its alpha from something computed. Read from the bytecode once per shader;
    /// false without a cached dump (an unknown shader is drawn, never hidden on a guess).
    /// </summary>
    private bool IsBlurDiscShader(string p_Shader)
    {
        if (p_Shader.Length == 0)
            return false;

        if (m_BlurDiscShaders.TryGetValue(p_Shader, out var s_Known))
            return s_Known;

        var s_Result = false;
        var s_Dxbc = Path.Combine(MeshCacheDir, "shaders", $"{Sanitize(p_Shader)}.dxbc");
        if (File.Exists(s_Dxbc))
        {
            try
            {
                var s_Instructions = Translate.DxbcAsm.Parse(
                    new SharpDX.D3DCompiler.ShaderBytecode(File.ReadAllBytes(s_Dxbc)).Disassemble().Split('\n'));

                var s_Outputs = s_Instructions
                    .Where(p_I => p_I.Destination is { Kind: Translate.OperandKind.Output })
                    .ToList();
                var s_OneTarget = s_Outputs.Count > 0 && s_Outputs.All(p_I => p_I.Destination!.Index == 0);
                var s_BlackColour = s_Outputs.Any(p_I =>
                    p_I.Opcode == "mov" && p_I.Sources.Count == 1 &&
                    p_I.Sources[0].Kind == Translate.OperandKind.Literal &&
                    p_I.Sources[0].Literal.All(p_L => p_L == 0f) &&
                    new[] { 0, 1, 2 }.All(p_I.Destination!.WriteMask.Contains) &&
                    !p_I.Destination.WriteMask.Contains(3));
                var s_ComputedAlpha = s_Outputs.Any(p_I =>
                    p_I.Destination!.WriteMask.Contains(3) &&
                    p_I.Sources.Any(p_S => p_S.Kind != Translate.OperandKind.Literal));

                s_Result = s_OneTarget && s_BlackColour && s_ComputedAlpha;
            }
            catch (Exception)
            {
                // Unreadable bytecode: the section is drawn as it always was.
            }
        }

        m_BlurDiscShaders[p_Shader] = s_Result;
        return s_Result;
    }

    /// <summary>
    /// The boxes of "This attachment's wear" showing what the document keeps for the attachment on the list
    /// (empty where it takes the weapon's). Silent: filling a box must not look like the user typing in it.
    /// </summary>
    private void ShowAccessoryValues()
    {
        var s_Item = PickedAccessory();

        // ⛔ NOT WHILE THE VIEW IS "AS SHIPPED" (the 'This attachment's wear' options do not exist as shipped,
        // so they are removed while the preview is as shipped). Those three boxes
        // are the CAMO's numbers for this piece; as shipped there is no camo, and the object on screen draws
        // with the game's own — so a box offering to change them would name a state that is not there.
        // The values are not cleared, only hidden: what the document keeps is still there when a camo comes back
        // (the law of the knob that goes quiet without losing what the user chose).
        AccessoryValuesPanel.Visibility = s_Item == null || CamoFactoryLook()
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (s_Item == null)
            return;

        var s_Values = DocumentGraph.AccessoryValuesOf(s_Item.Weapon, s_Item.Tag);
        m_AccessoryValuesFilling = true;
        try
        {
            AccessoryWearBox.Text = s_Values.TryGetValue("WearAmount", out var s_Wear) ? s_Wear : "";
            AccessoryWearPowerBox.Text = s_Values.TryGetValue("WearPower", out var s_Power) ? s_Power : "";
            AccessoryTilingBox.Text = s_Values.TryGetValue("CamoTiling", out var s_Tiling) ? s_Tiling : "";
        }
        finally
        {
            m_AccessoryValuesFilling = false;
        }
    }

    private bool m_AccessoryValuesFilling;

    /// <summary>Enter commits a box, as everywhere else in this window.</summary>
    private void OnAccessoryValueKey(object p_Sender, System.Windows.Input.KeyEventArgs p_Args)
    {
        if (p_Args.Key == System.Windows.Input.Key.Enter && p_Sender is TextBox s_Box)
        {
            OnAccessoryValueEdited(s_Box, new RoutedEventArgs());
            p_Args.Handled = true;
        }
    }

    /// <summary>
    /// One of this attachment's own numbers: kept in the document (so it is saved, and the bake writes it into
    /// the attachment's variation) and fed to the preview at once, which is the only way to judge it.
    /// </summary>
    private void OnAccessoryValueEdited(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_AccessoryValuesFilling || p_Sender is not TextBox s_Box)
            return;

        var s_Item = PickedAccessory();
        if (s_Item == null)
            return;

        var s_Constant = ReferenceEquals(s_Box, AccessoryWearBox) ? "WearAmount"
            : ReferenceEquals(s_Box, AccessoryWearPowerBox) ? "WearPower"
            : ReferenceEquals(s_Box, AccessoryTilingBox) ? "CamoTiling" : null;

        if (s_Constant == null)
            return;

        // (one number: "0,5" is 0.5 — DecimalComma; it was refused as "not a number")
        var s_Text = DecimalComma(s_Box.Text.Trim(), true);

        // A number, or nothing at all: anything else would reach the shader as a value the compiler chokes on,
        // and the box would look accepted.
        if (s_Text.Length > 0 && !double.TryParse(s_Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            Log($"'{s_Text}' is not a number: {s_Constant} for {s_Item.Tag} is left as it was.");
            ShowAccessoryValues();
            return;
        }

        DocumentGraph.SetAccessoryValue(s_Item.Weapon, s_Item.Tag, s_Constant, s_Text);
        Log(s_Text.Length > 0
            ? $"{s_Item.Tag}: {s_Constant} = {s_Text} (this attachment, on every weapon that offers it)."
            : $"{s_Item.Tag}: {s_Constant} back to the weapon's own number.");

        // straight to the eye: the stand-in re-feeds its numbers without recompiling anything
        SyncFactoryLook();
    }

    /// <summary>
    /// ⭐ AN ACCESSORY WITH A CAMO ON IT (fase F, 2026-09-18). What the bake ships for an accessory is the game's
    /// own third-person preset — measured in-game: the first-person family multiplies by the specular as
    /// occlusion and blacks an accessory out, so accessories take the 3P family in both views — with the camo's
    /// PATTERN in its camo register and the camo's NUMBERS over the accessory's own. The graph on the canvas
    /// (the weapon's first-person camo) never reaches an accessory, so drawing the accessory with it would
    /// show a look the game cannot have. This registers (once per accessory, preset, native camo and pattern)
    /// the 3P preset's graph compiled with the accessory's textures and the camo's pattern, and re-feeds the
    /// numbers on every change. Returns the registered name, or null where it cannot be built.
    /// </summary>
    private string? EnsureAccessoryShader()
    {
        var s_Mesh = m_PreviewMesh;
        if (s_Mesh is not { Length: > 0 } || m_CamoShaderFor == null || m_CamoWeaponOfAccessory?.Invoke(s_Mesh) == null)
            return null;

        // The tab authored ON the accessory preset draws it with its own graph: nothing to stand in for.
        // ⛔ The CAMO's target, not the box's: while the accessory's own graph is on the canvas the box names
        // the accessory preset, and reading it here dropped the stand-in exactly while that graph was looked at.
        var s_Preset = m_CamoShaderFor(s_Mesh);
        if (s_Preset == null || s_Preset.Equals(CamoTarget, StringComparison.OrdinalIgnoreCase))
            return null;

        var s_Native = DocumentGraph.NativeCamo;
        var s_Pattern = DocumentPatternPath();
        var s_PatternStamp = s_Pattern == null ? "" : File.GetLastWriteTimeUtc(s_Pattern).Ticks.ToString();
        var s_Name = s_Preset + "#accessory";

        // ⭐ THE ACCESSORY'S OWN GRAPH WHEN THE CAMO CARRIES ONE (its external constants are
        // editable like a weapon's). Kept per material like any other material graph; the stand-in compiles
        // THAT instead of the cache's pristine translation, so what the user edits is what the accessory shows
        // once the camo's graph is back on the canvas.
        var s_MaterialId = AccessoryMaterialId(s_Mesh);

        // ⭐ LIVE while that graph is on the canvas: the stand-in IS how the accessory's body is seen, so an edit
        // there shows through it (the document takes the graph when the canvas leaves it, SaveEditedMaterial).
        var s_Live = s_MaterialId is { } s_LiveId &&
                     string.Equals(m_MaterialEditKey, ShaderGraph.MaterialKey(s_Mesh, s_LiveId), StringComparison.OrdinalIgnoreCase)
            ? Canvas.Graph
            : null;
        var s_Own = s_Live ?? (s_MaterialId != null ? DocumentGraph.MaterialGraphOf(s_Mesh, s_MaterialId.Value) : null);

        // ⭐ THE PIECE'S OWN STARTING POINT, and the camo ADOPTS it the moment it is seen on it: the preview
        // draws with it, so the bake has to ship it — a preview that is not what ships is the oldest bug in
        // this tool. Only until the camo has its own graph for that material, which then wins as always.
        if (s_Own == null && s_MaterialId is { } s_Id && AttachmentPresetOf(s_Mesh) is { } s_Base)
        {
            s_Own = s_Base;
            DocumentGraph.SetMaterialGraph(s_Mesh, s_Id, ShaderGraph.FromJson(s_Base.ToJson()));

            MarkMaterialEdited(s_Id);

            if (m_AttachmentPresetSaid.Add(s_Mesh))
                Log($"Camo: {s_Mesh.Split('/')[^1]} takes YOUR saved preset for this piece ({s_Base.Nodes.Count} " +
                    "node(s)) — it is part of this camo now, and the bake ships it for this attachment.");
        }
        var s_OwnJson = s_Own?.ToJson();
        var s_Key = $"{s_Mesh}|{s_Preset}|{s_Native}|{s_Pattern}|{s_PatternStamp}|{s_OwnJson?.Length ?? 0}:{s_OwnJson?.GetHashCode() ?? 0}";

        // The numbers, layered the way the accessory's variation ships them: the preset's defaults under the
        // accessory's own material (ValuesFor), then the WEAR AND TILING OF THE HOST WEAPON — measured over
        // the 259 accessory materials: 83 set WearPower to 0, which under the 3P preset's
        // 0.6·(1−min((spec·WearAmount)^WearPower, 1)) is x⁰ = 1, a camo weight of exactly zero (the foregrip
        // came up bare); an accessory wears its weapon's wear, as the game's own 3P variations carry the
        // first-person numbers — then the camo's own values over everything.
        var s_Values = new Dictionary<string, string>(
            m_CamoValuesFor?.Invoke(s_Mesh, s_Preset, s_Native) ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);

        var s_HostMesh = (MeshList.SelectedItem as CamoWeapon)?.Mesh ?? m_CamoWeaponOfAccessory?.Invoke(s_Mesh);
        var s_HostTarget = CamoTarget;
        if (s_HostMesh != null && s_HostTarget.Length > 0 &&
            m_CamoValuesFor?.Invoke(s_HostMesh, s_HostTarget, s_Native) is { } s_HostValues)
            foreach (var s_Constant in AccessoryHostConstants)
                if (s_HostValues.TryGetValue(s_Constant, out var s_HostValue))
                    s_Values[s_Constant] = s_HostValue;

        foreach (var (s_Constant, s_Text) in DocumentValues())
            s_Values[s_Constant] = s_Text;

        // …then THIS ATTACHMENT's own numbers (the boxes under the accessory list), which are about the piece
        // and therefore beat both the weapon's and the camo's…
        if (PickedAccessory() is { } s_Picked)
            foreach (var (s_Constant, s_Text) in DocumentGraph.AccessoryValuesOf(s_Picked.Weapon, s_Picked.Tag))
                s_Values[s_Constant] = m_CamoExpandValue?.Invoke(s_Constant, s_Text) ?? s_Text;

        // …and the accessory's OWN typed numbers last: they are about this accessory, not about the weapon.
        if (s_Own != null)
            foreach (var (s_Constant, s_Text) in ValuesOfGraph(s_Own))
                s_Values[s_Constant] = s_Text;

        var s_ValuesKey = string.Join(";", s_Values.OrderBy(p_V => p_V.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p_V => $"{p_V.Key}={p_V.Value}"));

        if (string.Equals(m_AccessoryShaderKey, s_Key, StringComparison.OrdinalIgnoreCase) && m_Preview.HasForeignShader(s_Name))
        {
            if (!string.Equals(m_AccessoryValuesKey, s_ValuesKey, StringComparison.Ordinal) &&
                m_Preview.SetForeignValues(s_Name, s_Values))
                m_AccessoryValuesKey = s_ValuesKey;

            return s_Name;
        }

        if (!File.Exists(CamoGraphCachePath(s_Preset)))
        {
            if (m_FactoryGraphMissing.Add(s_Preset))
                Log($"Camo: the graph of '{s_Preset.Split('/')[^1]}' (what an accessory takes a camo with) is not " +
                    "translated yet — the accessory draws with this tab's graph, which is NOT what the bake ships. " +
                    "Run the studio's pretranslate step.");
            return null;
        }

        try
        {
            var s_Graph = s_Own != null ? ShaderGraph.FromJson(s_OwnJson!) : LoadCamoGraph(s_Preset);
            var s_Contract = ContractFromCache(s_Preset);
            if (s_Contract == null)
                return null;

            var s_Result = new HlslEmitter { Contract = s_Contract }.Emit(s_Graph);
            if (!s_Result.Ok)
            {
                Log($"Camo: '{s_Preset.Split('/')[^1]}' on the accessory: {s_Result.Errors[0]} — this tab's graph draws instead.");
                return null;
            }

            using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                s_Result.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);
            if (s_Compiled.Bytecode == null)
                return null;

            // The accessory's own art for THAT preset (the native camo's texture in the camo register when
            // one is picked), then the camo's pattern over the camo register when the camo has one.
            var s_Slots = m_CamoSlotsFor?.Invoke(s_Mesh, s_Preset, s_Native) ?? new Dictionary<string, string>();
            var s_Textures = DecodeCachedTextures(s_Slots, TexCacheDir);
            var s_Error = m_Preview.RegisterForeignShader(s_Name, s_Compiled.Bytecode.Data, s_Textures, s_Values,
                SamplersForShader(s_Preset));
            if (s_Error != null)
            {
                Log($"Camo: '{s_Preset.Split('/')[^1]}' on the accessory: {s_Error} — this tab's graph draws instead.");
                return null;
            }

            var s_PatternShown = false;
            if (s_Pattern != null)
            {
                var s_Register = m_CamoRegisterFor?.Invoke(s_Preset);
                if (s_Register != null && int.TryParse(s_Register, out var s_Slot) &&
                    LoadCustomTextureImage(s_Pattern) is BitmapSource s_Image)
                {
                    var s_Converted = new FormatConvertedBitmap(s_Image, PixelFormats.Bgra32, null, 0);
                    var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
                    s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);

                    // A pattern file is shipped sRGB by the bake (PrepareCustomTextures), so it is sampled so here.
                    s_PatternShown = m_Preview.SetForeignTexture(s_Name, s_Slot, s_Pixels, s_Converted.PixelWidth,
                        s_Converted.PixelHeight, true);
                }

                if (!s_PatternShown)
                    Log($"Camo: the pattern could not be put on '{s_Preset.Split('/')[^1]}' " +
                        (s_Register == null ? "(the studio names no camo register for it)." : "(the file could not be read)."));
            }

            // ⭐ …and the pictures of the accessory's OWN graph last, at the registers its nodes read (weapon, accessory or vehicle alike,
            // a custom texture put through a texture node must just work) — the bake binds them there
            if (s_Own != null)
                foreach (var s_Picture in CustomTextureBlocks(s_Own))
                    m_Preview.SetForeignTexture(s_Name, s_Picture.Slot, s_Picture.Pixels, s_Picture.Width, s_Picture.Height, s_Picture.Srgb);

            m_AccessoryShaderKey = s_Key;
            m_AccessoryValuesKey = s_ValuesKey;
            Log($"Camo: {s_Mesh.Split('/')[^1]} draws with '{s_Preset.Split('/')[^1]}' — the game's third-person preset, " +
                "what the bake ships for an accessory — with its own textures" +
                (s_PatternShown ? ", this camo's pattern" : s_Native != null && s_Native != m_CamoBasicKey ? $", the game's '{s_Native}'" : "") +
                " and this camo's numbers" +
                (s_Own != null ? ", THIS ACCESSORY'S OWN EDITED GRAPH." : ". Pick its material to edit its own graph."));
            Log($"Camo: accessory stand-in — textures [{string.Join(" ", s_Slots.OrderBy(p_S => p_S.Key).Select(p_S => $"t{p_S.Key}={p_S.Value.Split('/')[^1]}"))}]" +
                $" numbers [{string.Join(" ", s_Values.OrderBy(p_V => p_V.Key, StringComparer.OrdinalIgnoreCase).Select(p_V => $"{p_V.Key}={p_V.Value}"))}].");
            return s_Name;
        }
        catch (Exception s_Exception)
        {
            Log($"Camo: '{s_Preset.Split('/')[^1]}' on the accessory: {s_Exception.Message.Split('\n')[0].Trim()} — this tab's graph draws instead.");
            return null;
        }
    }

    /// <summary>
    /// The picker says NOW that the camo carries a graph for that material: it was filled before the camo
    /// adopted a piece's saved preset, so the first visit read "not edited" and the second "[edited]" for the
    /// same camo (--piecewalk, the suppressor).
    /// </summary>
    private void MarkMaterialEdited(int p_MaterialId)
    {
        if (MaterialList.Items.OfType<MaterialEntry>().FirstOrDefault(p_M => p_M.MaterialId == p_MaterialId) is not { Edited: false } s_Entry)
            return;

        var s_WasFilling = m_MaterialListFilling;
        m_MaterialListFilling = true;
        try
        {
            s_Entry.Edited = true;
            MaterialList.Items.Refresh();
        }
        finally
        {
            m_MaterialListFilling = s_WasFilling;
        }
    }

    /// <summary>
    /// The camo's pattern file: the custom texture on the document's camo-register node, else the simple
    /// form's live pattern. Null when the camo has none (its native camo or the weapon's own art shows).
    /// </summary>
    private string? DocumentPatternPath()
    {
        var s_Register = m_CamoRegisterFor?.Invoke(DocumentGraph.TargetShader) ?? m_CamoLivePattern.Register;
        if (s_Register != null)
            foreach (var s_Node in DocumentGraph.Nodes)
                if (TextureNodeKinds.Contains(s_Node.Kind) && s_Node.GetParam("Register") == s_Register &&
                    s_Node.Params.TryGetValue("CustomTexture", out var s_Path) && s_Path.Length > 0 && File.Exists(s_Path))
                    return s_Path;

        // ⛔ THE DOCUMENT'S, NEVER THE WINDOW'S MEMORY OF IT (review 2026-09-29): the form's last pattern stood in when the node had none — a
        // picture cleared on the node (its Clear button, an undo) went on showing on the attachments, which the bake then shipped without it.
        // CAMO_LIVE_PATTERN_FALLBACK_OLD=1 = as before.
        return Environment.GetEnvironmentVariable("CAMO_LIVE_PATTERN_FALLBACK_OLD") == "1" &&
               m_CamoLivePattern.Path is { Length: > 0 } s_Live && File.Exists(s_Live)
            ? s_Live
            : null;
    }

    /// <summary>
    /// The camo's numbers as the bake reads them: every external constant of the document with a preview
    /// value (the form's sliders write there), expanded to the vector the game expects, plus the window's
    /// live values for names the graph has no node for.
    /// </summary>
    private Dictionary<string, string> DocumentValues()
    {
        var s_Values = ValuesOfGraph(DocumentGraph);
        if (m_CamoLiveValues != null)
            foreach (var (s_Constant, s_Text) in m_CamoLiveValues)
                s_Values[s_Constant] = m_CamoExpandValue?.Invoke(s_Constant, s_Text) ?? s_Text;

        return s_Values;
    }

    /// <summary>Every external constant of a graph that carries a typed preview value, expanded to its vector.</summary>
    private Dictionary<string, string> ValuesOfGraph(ShaderGraph p_Graph)
    {
        var s_Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Node in p_Graph.Nodes)
        {
            if (s_Node.Kind != "ExternalConstant" ||
                !s_Node.Params.TryGetValue("PreviewValue", out var s_Text) || string.IsNullOrWhiteSpace(s_Text))
                continue;

            var s_Constant = BareExternalName(s_Node);
            s_Values[s_Constant] = m_CamoExpandValue?.Invoke(s_Constant, s_Text.Trim()) ?? s_Text.Trim();
        }

        return s_Values;
    }

    /// <summary>
    /// The material ID of the loaded ACCESSORY's painted body — the one whose graph is the accessory's own.
    /// Read from the material picker (the same list the user sees); null before it is filled, or for a mesh
    /// with no painted material. An accessory with several painted materials previews with the FIRST one's
    /// graph; the bake ships each material's own.
    /// </summary>
    private int? AccessoryMaterialId(string p_Mesh)
    {
        if (!string.Equals(m_MaterialListMesh, p_Mesh, StringComparison.OrdinalIgnoreCase))
            return null;

        return MaterialList.Items.OfType<MaterialEntry>().FirstOrDefault(p_M => p_M.TakesCamo)?.MaterialId;
    }

    /// <summary>The shader most of the loaded weapon's own sections wear — the target or one of its twins — or null.</summary>
    private string? BodyPresetOf() =>
        m_Preview.MeshSections
            .Where(m_Preview.IsTargetSection)
            .Select(p_S => p_S.Shader)
            .Where(p_S => p_S.Length > 0)
            .GroupBy(p_S => p_S, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p_G => p_G.Count())
            .Select(p_G => p_G.Key)
            .FirstOrDefault();

    private string? m_CamoBasicDisplay;

    /// <summary>What the preview last showed, so the Output says it once per change and not per frame.</summary>
    private bool? m_FactoryLookShown;

    /// <summary>
    /// The camo document parked while the canvas shows the shader the weapon wears as shipped (see
    /// <see cref="ShowFactoryGraph"/>), with its undo history and contract; null while the canvas holds the
    /// document itself.
    /// </summary>
    private ShaderGraph? m_ParkedCamoGraph;

    private (string[] Undo, string[] Redo) m_ParkedUndo = (Array.Empty<string>(), Array.Empty<string>());
    private ShaderContract? m_ParkedContract;

    /// <summary>The worn preset whose graph is on the canvas in place of the document, or null.</summary>
    private string? m_ShownFactoryPreset;

    /// <summary>Worn presets whose translated graph is missing, said once each.</summary>
    private readonly HashSet<string> m_FactoryGraphMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The graph that IS the camo being authored: the canvas's, unless the canvas is showing the weapon's
    /// own shader in its place. Everything that reads or writes the camo — its native key, its stickers, its
    /// values, the bake — goes through this, never through the canvas directly.
    /// </summary>
    internal ShaderGraph DocumentGraph => m_MaterialParkedCamo ?? m_ParkedCamoGraph ?? Canvas.Graph;

    /// <summary>
    /// The preset the CAMO is authored on — what the object on screen is dressed for. The target box says the
    /// same except while one of the object's other materials is on the canvas, when it names that material's
    /// shader; anything that dresses or draws the object asks this, not the box.
    /// </summary>
    private string CamoTarget => (m_MaterialParkedTarget ?? TargetShaderBox.Text).Trim();

    /// <summary>
    /// A painted material of a VEHICLE that wears another preset of the family than the one the camo is
    /// authored on (the LAV-25's CROWS on vehiclepreset_nomud under a vehiclepreset_mud camo). In the game it
    /// keeps that preset, so that preset's graph is the one that shows it (ShowMaterialGraph).
    /// </summary>
    private bool WearsOwnVehiclePreset(string p_Mesh, MaterialEntry p_Entry)
    {
        var s_Target = CamoTarget;
        return p_Entry.TakesCamo && p_Entry.Shader.Length > 0 &&
               !DocumentGraph.IsMaterialOff(p_Mesh, p_Entry.MaterialId) &&
               m_CamoWeaponOfAccessory?.Invoke(p_Mesh) == null &&
               s_Target.Contains("vehiclepreset", StringComparison.OrdinalIgnoreCase) &&
               !p_Entry.Shader.Equals(s_Target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the canvas is showing the weapon's own shader instead of the camo document.</summary>
    internal bool FactoryGraphShown => m_ParkedCamoGraph != null;

    /// <summary>
    /// The preset the loaded weapon's body wears when it is NOT the one the camo is authored on — the
    /// NoCamo preset the camo replaces — by section count; null when the body wears the target itself.
    /// </summary>
    private string? WornPresetOf()
    {
        // ⛔ THE PRESET THAT DRAWS THE MOST OF THE BODY, THE TARGET INCLUDED (found on the crossbow: the graphs did not update when
        // switching between materials): leaving the target out FIRST answered with whatever small piece wore another
        // preset — the crossbow's body is 3,490 triangles of weaponpresetshadowfp (the camo's own preset) and its bolt's T-UGS head
        // 498 of nocamo3p, so "as shipped" put the T-UGS's graph on the canvas for every material. When the target itself draws the
        // most, the body wears the camo's preset and there is no other graph to show (the F2000/M240 case): null.
        var s_Target = TargetShaderBox.Text.Trim().Replace('\\', '/');
        var s_Worn = m_Preview.MeshSections
            .Where(m_Preview.IsTargetSection)
            .Where(p_S => p_S.Shader.Length > 0)
            .GroupBy(p_S => p_S.Shader, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p_G => p_G.Sum(p_S => p_S.IndexCount))
            .Select(p_G => p_G.Key)
            .FirstOrDefault();
        return s_Worn == null || s_Worn.Equals(s_Target, StringComparison.OrdinalIgnoreCase) ? null : s_Worn;
    }

    /// <summary>
    /// Puts the graph of the shader the weapon wears as shipped on the canvas, parking the camo document
    /// (the graph shown must follow each weapon too, not only the preview). Dressed with the weapon's own
    /// textures and numbers for THAT shader, so the nodes say what
    /// the game draws. Needs the preset's translated graph in the cache (the studio's pretranslate makes it);
    /// without it the camo's graph stays and the Output says why, once.
    /// </summary>
    private void ShowFactoryGraph(string p_Preset, bool p_Quiet = false)
    {
        if (string.Equals(m_ShownFactoryPreset, p_Preset, StringComparison.OrdinalIgnoreCase))
        {
            // Same worn preset, another weapon (AS VAL → AEK971: the canvas kept the AS
            // VAL's graph while the pick had loaded the AEK's CAMO set into the thumbnails — a desert
            // pattern on the NoCamo graph's specular node): the graph stays, its art follows the weapon.
            if (!string.Equals(m_ShownFactoryMesh, m_PreviewMesh, StringComparison.OrdinalIgnoreCase))
                DressFactoryGraph(p_Preset);

            return;
        }

        if (m_ParkedCamoGraph != null)
            RestoreCamoGraph();

        if (!File.Exists(CamoGraphCachePath(p_Preset)))
        {
            if (m_FactoryGraphMissing.Add(p_Preset))
                Log($"Camo: the graph of '{p_Preset.Split('/')[^1]}' (what this weapon wears as shipped) is not " +
                    "translated yet — the canvas keeps the camo's graph. Run the studio's pretranslate step.");
            return;
        }

        ShaderGraph s_Graph;
        ShaderGraph s_Pristine;
        try
        {
            // ⭐ AS SHIPPED IS A CAMO LIKE ANY OTHER: the subject's own edit of this graph, when it has one, is
            // what the canvas shows — the game's otherwise; the game's is kept aside to tell an edit from a look (FactoryGraphEdited).
            s_Pristine = LoadCamoGraph(p_Preset);
            s_Graph = Canvas.Graph.ShippedGraph is { } s_Edited &&
                      s_Edited.TargetShader.Equals(s_Pristine.TargetShader, StringComparison.OrdinalIgnoreCase)
                ? s_Edited
                : LoadCamoGraph(p_Preset);
        }
        catch (Exception s_Exception)
        {
            if (m_FactoryGraphMissing.Add(p_Preset))
                Log($"Camo: the cached graph of '{p_Preset}' is unusable ({s_Exception.Message}) — the canvas keeps the camo's graph.");
            return;
        }

        m_ParkedCamoGraph = Canvas.Graph;
        m_ParkedUndo = Canvas.SnapshotUndo();
        m_ParkedContract = m_Contract;
        m_ShownFactoryPreset = p_Preset;

        // The tab is still the camo's: its header keeps the camo's name while the worn graph stands in; and
        // a document that samples a sticker layer is stood in for by a graph that samples the same one.
        s_Graph.Name = m_ParkedCamoGraph.Name;
        s_Pristine.Name = s_Graph.Name;
        Canvas.Graph = s_Graph;
        ShareStickersWithView();
        if (m_ParkedCamoGraph.StickerRegister is { } s_StickerRegister)
            foreach (var s_Each in new[] { s_Graph, s_Pristine })
            {
                StickerGraph.Ensure(s_Each, m_StickerDiffuseRegisterFor?.Invoke(ActiveTarget), s_StickerRegister, out _, m_ParkedCamoGraph.StickerLayerSide);
                EnsureStickerAnimationOn(s_Each);
            }

        m_FactoryPristine = s_Pristine;

        DressFactoryGraph(p_Preset);
        SyncReferenceNote();
        if (!p_Quiet)
            Log($"Camo: the canvas shows '{p_Preset.Split('/')[^1]}' — the shader this weapon's body wears as shipped, with " +
                "its own textures and numbers. The 3D view draws it as the game ships it until it is edited; an edit is this " +
                "subject's as-shipped look (in a tab of changes). Pick a camo and the camo's graph comes back.");
    }

    /// <summary>
    /// The note over the canvas of an ORIGINAL tab (EditorTab.Original): every camo there is the game's, and a change opens a copy in a tab
    /// of its own. (It first said "as shipped is a reference, edits are neither drawn nor baked" — until, the same night, every camo
    /// of the picker became editable in a copy, as shipped included.) The Output line alone scrolls away.
    /// </summary>
    private void SyncReferenceNote()
    {
        var s_Shown = CamoMode && m_DocumentSubject != null && m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count && m_Tabs[m_ActiveTab].Original;
        if (s_Shown)
            ReferenceNoteText.Text = "ORIGINAL — every camo of the picker here is the game's, as it ships. Any change opens a copy in a " +
                                     "new tab;\nthis one stays as it is.";
        ReferenceNote.Visibility = s_Shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The note over the canvas, as shown, or null when it is hidden — what a seam reads.</summary>
    internal string? ReferenceNoteShown => ReferenceNote.Visibility == Visibility.Visible ? ReferenceNoteText.Text : null;

    /// <summary>Whether the active tab is one of the game's originals (EditorTab.Original), and what its tab says — what a seam reads.</summary>
    internal bool ActiveTabOriginal => m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count && m_Tabs[m_ActiveTab].Original;

    internal string ActiveTabTitle => m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count ? TabTitleOf(m_ActiveTab) : "";

    /// <summary>The tab strip as the user reads it — each tab's title and whether it is one of the game's originals — and the active one.</summary>
    internal (IReadOnlyList<(string Title, bool Original)> Tabs, int Active) TabStripState =>
        (Enumerable.Range(0, m_Tabs.Count).Select(p_I => (TabTitleOf(p_I), m_Tabs[p_I].Original)).ToList(), m_ActiveTab);

    /// <summary>
    /// Every subject key a document is held under in the session — each tab's subject (the canvas's for the one on screen), the ones kept per
    /// camo of the picker, and the ones a tab remembers the camo of: where the bake of a soldier skin looks for the soldiers with something to
    /// bake (with several soldiers open, the user has to know who the bake is for).
    /// </summary>
    internal IReadOnlyCollection<string> SubjectKeysInSession()
    {
        var s_Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var t = 0; t < m_Tabs.Count; t++)
        {
            if ((t == m_ActiveTab ? m_DocumentSubject : m_Tabs[t].DocumentSubject) is { Length: > 0 } s_Subject)
                s_Keys.Add(s_Subject);
            foreach (var s_Set in m_Tabs[t].Natives.Values)
                s_Keys.UnionWith(s_Set.Subjects.Keys);
            s_Keys.UnionWith(m_Tabs[t].SubjectNatives.Keys);
        }

        return s_Keys;
    }

    /// <summary>
    /// Brings on screen the first tab of CHANGES whose subject is under a key prefix ("soldiers/US_Engineer/") — the tab's own click, which puts
    /// its subject and its part back (the pick it starts is LastMeshPick). False when the canvas already holds one of those, or no tab does.
    /// </summary>
    internal bool ShowChangesTabOf(string p_Prefix)
    {
        if (m_DocumentSubject?.StartsWith(p_Prefix, StringComparison.OrdinalIgnoreCase) == true)
            return false;

        for (var t = 0; t < m_Tabs.Count; t++)
            if (t != m_ActiveTab && !m_Tabs[t].Original && m_Tabs[t].DocumentSubject?.StartsWith(p_Prefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                SwitchToTab(t);
                return true;
            }

        return false;
    }

    /// <summary>The weapon the worn graph on the canvas is dressed for, so another weapon re-dresses it.</summary>
    private string? m_ShownFactoryMesh;

    /// <summary>The game's own graph of what the subject wears as shipped, as the canvas got it before any edit (ShowFactoryGraph).</summary>
    private ShaderGraph? m_FactoryPristine;

    /// <summary>
    /// Whether the as-shipped graph on the canvas was EDITED: it draws differently from the game's (the bake's yardstick — HLSL and typed
    /// values; moving a node is not an edit). Then the 3D draws it instead of the game's bytecode, and it is the document's (ShippedGraph).
    /// </summary>
    private bool FactoryGraphEdited => FactoryGraphShown && m_FactoryPristine != null && !DrawsTheSame(Canvas.Graph, WithCanvasStickerLayer(m_FactoryPristine));

    /// <summary>
    /// The game's graph with the canvas's sticker layer on it and nothing else — the yardstick of "the as-shipped graph was edited". The
    /// layer (and an animated sticker's nodes) goes on the graph on the canvas when sticker mode opens or a GIF is placed, after the game's
    /// copy was taken, and it is no edit of the game's graph: the body still draws as shipped plus the layer (EnsureStickerFactoryShader).
    /// Compared against the copy as taken, the canvas counted as EDITED the moment sticker mode opened over a weapon as shipped — the 3D
    /// stopped drawing the shipped look, and the graph was kept as the subject's own as-shipped edit (review 2026-09-29: --stickernativetest
    /// failed there, on the M416's no-camo graph and on the F2000's own document, since as shipped became editable on 2026-09-25).
    /// CAMO_STICKER_PRISTINE_OLD=1 = the copy as taken.
    /// </summary>
    private ShaderGraph WithCanvasStickerLayer(ShaderGraph p_Pristine)
    {
        if (Canvas.Graph.StickerRegister is not { } s_Register || Environment.GetEnvironmentVariable("CAMO_STICKER_PRISTINE_OLD") == "1")
            return p_Pristine;

        var s_Copy = ShaderGraph.FromJson(p_Pristine.ToJson());
        StickerGraph.Ensure(s_Copy, m_StickerDiffuseRegisterFor?.Invoke(ActiveTarget), s_Register, out _, DocumentGraph.StickerLayerSide);
        if (Canvas.Graph.Nodes.Any(p_N => p_N.Kind == "Flipbook") && DocumentGraph.StickerAnimation is { } s_Gif && StickerAtlasOf(s_Gif) is { } s_Atlas)
            StickerGraph.EnsureAnimated(s_Copy, s_Atlas, DocumentGraph.StickerLayerSide, out _);
        return s_Copy;
    }

    /// <summary>Puts the as-shipped graph on the canvas into the document it stands in for, when it was edited (and takes it out when not).</summary>
    private void SyncShippedGraph()
    {
        if (m_ParkedCamoGraph != null)
            m_ParkedCamoGraph.ShippedGraph = FactoryGraphEdited ? Canvas.Graph : null;
    }

    /// <summary>
    /// Dresses the worn graph on the canvas for the picked weapon: that preset's contract, and the weapon's
    /// own textures AND numbers for THAT preset on its nodes (exactly the game's values of
    /// each weapon — the preset's defaults where the weapon's material overrides nothing). The camo
    /// document's own numbers come back with it when a camo is picked.
    /// </summary>
    private void DressFactoryGraph(string p_Preset)
    {
        m_Contract = ContractFromCache(p_Preset);
        m_CamoContractShader = m_Contract != null ? p_Preset : null;
        m_ShownFactoryMesh = m_PreviewMesh;
        if (m_PreviewMesh is { Length: > 0 } s_Mesh)
        {
            var s_Slots = m_CamoSlotsFor?.Invoke(s_Mesh, p_Preset, null);
            var s_Values = m_CamoValuesFor?.Invoke(s_Mesh, p_Preset, null);

            // A material that is NOT a weapon preset (glass, glow, the reticle HUD) is outside the studio's
            // material scan; its art and numbers come from the shader's own slot map — the set recorded for
            // THIS mesh when the map has one, the map's base set otherwise — the same source the 3D view
            // draws that section from.
            if (s_Slots == null && ReadSlotMapFull(SlotMapPath(p_Preset)) is { } s_Map)
            {
                var s_Set = s_Map.Variations?.GetValueOrDefault($"{s_Mesh}|0");
                s_Slots = s_Set?.Slots ?? s_Map.Slots;
                s_Values ??= LoadForeignMaterialValues(SlotMapPath(p_Preset), s_Mesh);
            }

            LoadCamoTextures(s_Slots ?? new Dictionary<string, string>(), s_Values ?? m_CamoWeaponValues);
        }

        BuildProperties();
        Canvas.Refresh();
    }

    /// <summary>
    /// While the canvas holds an EDITABLE material graph (a material that is not the camo's — glass, glow, the
    /// HUD, a preset material kept off), the key ("mesh|id") it belongs to; null while the canvas holds the
    /// camo document. The camo document is parked in the fields below, so it is still DocumentGraph.
    /// </summary>
    private string? m_MaterialEditKey;

    private int m_MaterialEditSection = -1;
    private ShaderGraph? m_MaterialParkedCamo;
    private (string[] Undo, string[] Redo) m_MaterialParkedUndo = (Array.Empty<string>(), Array.Empty<string>());
    private ShaderContract? m_MaterialParkedContract;
    private string? m_MaterialParkedTarget;

    /// <summary>Whether the canvas is currently editing one of the object's other materials, not the camo.</summary>
    internal bool MaterialEditing => m_MaterialEditKey != null;

    /// <summary>
    /// Puts the picked material's own graph on the canvas TO EDIT (those graphs must be editable too, live in
    /// the preview, and the user's changes must stay when switching between material ID tabs): the shader
    /// that material wears (its saved edits from
    /// this camo, else the cache's translation), dressed with the material's own textures. The object on screen
    /// does not change by looking; a real edit shows on that material's section (RefreshMaterialEdits). A painted
    /// material picked again comes back to the camo's graph. Edits persist per material and ship at the bake.
    /// </summary>
    private void ShowMaterialGraph(MaterialEntry p_Entry)
    {
        if (!CamoMode || m_PreviewMesh is not { Length: > 0 } s_Mesh)
            return;

        var s_Off = p_Entry.TakesCamo && DocumentGraph.IsMaterialOff(s_Mesh, p_Entry.MaterialId);

        // ⭐ AN ACCESSORY'S PAINTED MATERIAL OPENS ITS OWN GRAPH, not the camo's (the request: do for the
        // accessories what was done with the external constants on the camos' nodes). The camo's graph is
        // the WEAPON's first-person preset and an accessory does not draw
        // with it — its constants (NormalTiling, ScopeOcc, the smoothness set) reach nothing on an accessory.
        // What the accessory draws with is the third-person preset (AccessoryPreset), so that is what opens:
        // its constants are editable like any other, and the stand-in compiles exactly this graph.
        var s_AccessoryPreset = m_CamoWeaponOfAccessory?.Invoke(s_Mesh) != null ? m_CamoShaderFor?.Invoke(s_Mesh) : null;

        // ⭐ AND SO DOES A VEHICLE'S PAINTED MATERIAL THAT WEARS ANOTHER PRESET (picking another material must
        // really update the graph, its nodes and textures included). Picking the
        // LAV-25's CROWS (vehiclepreset_nomud), its reactive armour (1uvset_mud) or its decals (mud_decals) left
        // the hull's vehiclepreset_mud graph on the canvas — same nodes, and as shipped the same thumbnails too.
        // A vehicle's material keeps ITS preset in the game (the variation repoints the camo texture, never the
        // shader), so that preset's graph is what draws it and what the canvas shows. A weapon's do not: its bake
        // puts the camo's shader on every first-person material, so they stay on the camo's graph.
        var s_OwnPreset = WearsOwnVehiclePreset(s_Mesh, p_Entry);
        var s_EditsThis = !p_Entry.TakesCamo || s_Off || s_AccessoryPreset != null || s_OwnPreset;
        var s_Key = ShaderGraph.MaterialKey(s_Mesh, p_Entry.MaterialId);

        // Already on this material: nothing to do.
        if (m_MaterialEditKey == s_Key)
            return;

        // Leaving whatever material was being edited: save its graph into the document first.
        SaveEditedMaterial();

        if (!s_EditsThis)
        {
            // A painted material: back to the camo's graph, dressed for the camo again.
            LeaveMaterialEdit(true);
            return;
        }

        // A painted material of an accessory is edited on the preset it DRAWS with, not on the one it wears
        // as shipped; everything else is edited on its own.
        var s_Shader = p_Entry.TakesCamo && !s_Off && s_AccessoryPreset is { Length: > 0 }
            ? s_AccessoryPreset
            : p_Entry.Shader;

        if (s_Shader.Length == 0)
        {
            LeaveMaterialEdit(true);
            return;
        }

        // The material's graph: its saved edits from THIS camo, else the user's own starting point for this
        // PIECE (kept per mesh, so it comes up on every weapon that carries it), else the cache's translation.
        ShaderGraph s_Graph;
        if (DocumentGraph.MaterialGraphOf(s_Mesh, p_Entry.MaterialId) is { } s_Saved)
        {
            s_Graph = ShaderGraph.FromJson(s_Saved.ToJson());
            s_Graph.TargetShader = s_Shader;
        }
        else if (AttachmentPresetOf(s_Mesh) is { } s_Mine)
        {
            s_Graph = s_Mine;
            s_Graph.TargetShader = s_Shader;
            Log($"Camo: {s_Mesh.Split('/')[^1]} opens from YOUR saved preset for this piece " +
                $"({s_Graph.Nodes.Count} node(s)), not the stock '{s_Shader.Split('/')[^1]}'.");

            // The camo ADOPTS it the moment it is seen (the stand-in's rule): leaving the material would save it
            // anyway, since it differs from the stock graph — so it is the camo's from here, and the picker says so.
            DocumentGraph.SetMaterialGraph(s_Mesh, p_Entry.MaterialId, ShaderGraph.FromJson(s_Mine.ToJson()));
            MarkMaterialEdited(p_Entry.MaterialId);
        }
        else if (File.Exists(CamoGraphCachePath(s_Shader)))
        {
            s_Graph = LoadCamoGraph(s_Shader);
        }
        else
        {
            Log($"Material #{p_Entry.MaterialId}: the graph of '{s_Shader.Split('/')[^1]}' is not translated yet — " +
                "run the studio's pretranslate step (it covers every shader the cached objects wear).");
            LeaveMaterialEdit(true);
            return;
        }

        // Park the camo document (once — a second material switch already saved and stayed parked).
        if (m_MaterialParkedCamo == null)
        {
            // ⛔ THE CAMO FIRST. While the weapon is "as shipped" the canvas holds the NO-CAMO graph, not the
            // document: parking that one hid the real document (with its material edits) behind it, and the
            // next visit to the material found nothing saved (measured on the M240, 2026-09-18).
            if (m_ParkedCamoGraph != null)
                RestoreCamoGraph();

            m_MaterialParkedCamo = Canvas.Graph;
            m_MaterialParkedUndo = Canvas.SnapshotUndo();
            m_MaterialParkedContract = m_Contract;
            m_MaterialParkedTarget = TargetShaderBox.Text.Trim();
        }

        m_MaterialEditKey = s_Key;
        m_MaterialEditSection = p_Entry.Section;

        // ⛔ The yardstick is the PRISTINE translation, never what was loaded: coming back to a material that
        // already carries an edit and comparing against THAT would call the edit "unchanged" and drop it.
        m_MaterialEditOriginal = File.Exists(CamoGraphCachePath(s_Shader)) ? LoadCamoGraph(s_Shader).ToJson() : null;

        // ⛔⛔ THE CANVAS CHANGES, THE OBJECT DOES NOT (switching between a mesh's materials to see their graphs
        // must not change how the mesh looks — only a real edit of the graph may). This used to re-aim the
        // whole preview at the material: its section ran the
        // TRANSLATED graph instead of the game's bytecode, and every other section was put back as shipped — so
        // merely looking at a glass took the camo off the body (M240 under ABU: 0A59F346 -> the as-shipped
        // frame), and a forward glass or light drew differently from the game even as shipped. Now nothing of
        // the preview moves: its shader, its textures, its numbers and its interpolator feed stay the camo's
        // (PushPreviewTextures / PushInstanceValues hold while a material is on the canvas), and the contract
        // is read WITHOUT handing its interpolators to the camo's feed. What the material's graph changes shows
        // through RefreshMaterialEdits, on its section only, the moment it really differs from the stock one.
        Canvas.Graph = s_Graph;
        m_MaterialOpened = ShaderGraph.FromJson(s_Graph.ToJson());
        m_Contract = ReadContractFromCache(s_Shader);
        m_CamoContractShader = m_Contract != null ? s_Shader : null;
        TargetShaderBox.Text = s_Shader;
        m_Preview.IsolatedSection = MaterialIsolate.IsChecked == true ? p_Entry.Section : null;

        // The material's own art and numbers on the canvas's thumbnails — for an accessory, the SAME layering
        // the stand-in uses (the tab's native camo and pattern, the host weapon's wear).
        var s_AccessoryDress = s_AccessoryPreset != null && p_Entry.TakesCamo && !s_Off;
        DressMaterialGraph(s_Mesh, s_Shader, s_AccessoryDress, s_OwnPreset ? p_Entry : null);

        BuildProperties();
        Canvas.Refresh();
        SchedulePreview();
        Log($"Canvas: editing material #{p_Entry.MaterialId} '{p_Entry.Name}' — the graph of '{s_Shader.Split('/')[^1]}'" +
            (s_AccessoryPreset != null && p_Entry.TakesCamo && !s_Off
                ? ", the preset this ACCESSORY takes a camo with (not the weapon's). Its external constants are " +
                  "editable here: type a Preview value and the accessory follows, live."
                : s_OwnPreset
                    ? ", the preset this material keeps in the game (the camo goes on it). Edit it and the change " +
                      "shows live on that part only"
                    : ". Edit it and the change shows live on that part") +
            " It is saved with the camo and ships for this material at the bake. Pick the weapon's own material " +
            "(or another accessory) to go back to the camo's graph.");
    }

    /// <summary>
    /// Loads the picked material's textures and numbers onto the canvas, from its slot map (the 3D keeps the
    /// camo's). A vehicle's painted material on its own preset (<paramref name="p_OwnPreset"/>) is dressed the
    /// way the view dresses it: ITS set under the picked native camo, the camo's numbers, the pattern on the
    /// registers of its preset that read the same texture.
    /// </summary>
    private void DressMaterialGraph(string p_Mesh, string p_Shader, bool p_AsAccessory = false, MaterialEntry? p_OwnPreset = null)
    {
        // The accessory's body is looked at UNDER THE CAMO: the tab's native camo in its camo register, the
        // host weapon's wear and tiling, the camo's own numbers — the same layering EnsureAccessoryShader
        // gives it once the camo's graph is back on the canvas.
        var s_Native = p_AsAccessory || p_OwnPreset != null ? DocumentGraph.NativeCamo : null;
        var s_Slots = p_OwnPreset != null
            ? m_CamoSlotsForMaterial?.Invoke(p_Mesh, p_Shader, s_Native, p_OwnPreset.MaterialId)
            : null;
        s_Slots ??= m_CamoSlotsFor?.Invoke(p_Mesh, p_Shader, s_Native);
        var s_Values = p_OwnPreset != null
            ? m_CamoValuesForMaterial?.Invoke(p_Mesh, p_Shader, s_Native, p_OwnPreset.MaterialId)
            : null;
        s_Values ??= m_CamoValuesFor?.Invoke(p_Mesh, p_Shader, s_Native);
        if (p_OwnPreset != null && !CamoFactoryLook())
        {
            var s_Merged = new Dictionary<string, string>(s_Values ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var (s_Constant, s_Text) in DocumentValues())
                s_Merged[s_Constant] = s_Text;

            s_Values = s_Merged;
        }

        if (s_Slots == null && ReadSlotMapFull(SlotMapPath(p_Shader)) is { } s_Map)
        {
            var s_Set = s_Map.Variations?.GetValueOrDefault($"{p_Mesh}|0");
            s_Slots = s_Set?.Slots ?? s_Map.Slots;
            s_Values ??= LoadForeignMaterialValues(SlotMapPath(p_Shader), p_Mesh);
        }

        if (p_AsAccessory)
        {
            var s_Merged = new Dictionary<string, string>(s_Values ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            var s_HostMesh = (MeshList.SelectedItem as CamoWeapon)?.Mesh ?? m_CamoWeaponOfAccessory?.Invoke(p_Mesh);
            var s_HostTarget = m_MaterialParkedTarget ?? TargetShaderBox.Text.Trim();
            if (s_HostMesh != null && s_HostTarget.Length > 0 &&
                m_CamoValuesFor?.Invoke(s_HostMesh, s_HostTarget, s_Native) is { } s_HostValues)
                foreach (var s_Constant in AccessoryHostConstants)
                    if (s_HostValues.TryGetValue(s_Constant, out var s_HostValue))
                        s_Merged[s_Constant] = s_HostValue;

            foreach (var (s_Constant, s_Text) in DocumentValues())
                s_Merged[s_Constant] = s_Text;

            s_Values = s_Merged;
        }

        LoadCamoTextures(s_Slots ?? new Dictionary<string, string>(), s_Values);

        // The camo's own pictures on a vehicle material's own preset: by the texture's NAME in the two
        // bytecodes, as the view lays them (ApplyMaterialDress), and only when a camo is on.
        if (p_OwnPreset != null)
        {
            if (CamoFactoryLook())
                return;

            foreach (var s_Node in DocumentGraph.Nodes)
            {
                if (!TextureNodeKinds.Contains(s_Node.Kind) ||
                    !s_Node.Params.TryGetValue("CustomTexture", out var s_Path) || s_Path.Length == 0 ||
                    !int.TryParse(s_Node.GetParam("Register"), out var s_Slot) ||
                    LoadCustomTextureImage(s_Path) is not BitmapSource s_Picture)
                    continue;

                foreach (var s_Own in SlotsOfSameTexture(CamoTarget, p_Shader, s_Slot, ReadContractFromCache))
                {
                    m_TexturePreviews[s_Own.ToString()] = s_Picture;
                    m_TextureSrgb[s_Own.ToString()] = CustomTextureIsSrgb(s_Node, s_Path);
                }
            }

            Canvas.Refresh();
            return;
        }

        // The camo's own pattern, straight onto the camo register of THIS preset — the picture only, never
        // written into the accessory's graph (a pattern belongs to the camo, not to the accessory).
        if (!p_AsAccessory || DocumentPatternPath() is not { } s_Pattern ||
            m_CamoRegisterFor?.Invoke(p_Shader) is not { } s_Register ||
            LoadCustomTextureImage(s_Pattern) is not BitmapSource s_Image)
            return;

        m_TexturePreviews[s_Register] = s_Image;
        m_TextureSrgb[s_Register] = true;
        PushPreviewTextures();
        Canvas.Refresh();
    }

    /// <summary>
    /// The camo's edited material graphs that actually CHANGE their shader — for the bake, which ships one
    /// cloned shader per entry. A graph whose emission still hashes like the cache's translation changed
    /// nothing and is left out (it would cost a clone and ship the game's own logic back to itself).
    /// Called by the studio at bake time; it flushes whatever material is open on the canvas first.
    /// </summary>
    internal IReadOnlyList<(string Mesh, int MaterialId, string Shader, ShaderGraph Graph)> EditedMaterialGraphs(ShaderGraph? p_Document = null)
    {
        if (m_MaterialEditKey != null)
            SaveEditedMaterial();

        var s_Result = new List<(string, int, string, ShaderGraph)>();
        if ((p_Document ?? DocumentGraph).MaterialGraphs is not { Count: > 0 } s_Graphs)
            return s_Result;

        foreach (var (s_Key, s_Graph) in s_Graphs.OrderBy(p_G => p_G.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!ShaderGraph.TryParseMaterialKey(s_Key, out var s_Mesh, out var s_Id) ||
                s_Graph.TargetShader is not { Length: > 0 } s_Shader)
                continue;

            // Changed? Against the CACHE'S OWN TRANSLATION of that shader, both emitted through the SAME
            // contract. ⛔ Not against the graph's TranslatedHlslHash: that stamp was made with the contract
            // of the translating session (the level's index), and emitting through the cached bytecode's
            // contract hashes differently for every graph — so every material the user merely LOOKED at
            // counted as edited and would have shipped a clone (measured 2026-09-18, the ACOG's glass).
            if (!File.Exists(CamoGraphCachePath(s_Shader)))
                continue;

            var s_Contract = ContractFromCache(s_Shader);
            var s_Now = HlslFingerprint(s_Graph, s_Contract);
            var s_Pristine = HlslFingerprint(LoadCamoGraph(s_Shader), s_Contract);
            var s_Changed = s_Now == null || s_Pristine == null
                ? ShaderGraph.FromJson(File.ReadAllText(CamoGraphCachePath(s_Shader))) is var s_Cached &&
                  (s_Cached.Nodes.Count != s_Graph.Nodes.Count || s_Cached.Connections.Count != s_Graph.Connections.Count)
                : s_Now != s_Pristine;

            if (s_Changed)
                s_Result.Add((s_Mesh, s_Id, s_Shader, s_Graph));
        }

        return s_Result;
    }

    /// <summary>
    /// The document a SUBJECT (weapon, vehicle or attachment mesh) of the active tab bakes with (one document per
    /// subject — see EditorTab.Subjects): the one on the canvas for the subject on screen, its own when it was changed on it, else the
    /// tab's common one. The tab's shared camo settings are brought up to date first (ShareCamoSettings), so every document answers
    /// with the pattern and numbers the form shows now.
    /// </summary>
    internal ShaderGraph DocumentOfSubject(string p_Mesh)
    {
        if (m_MaterialEditKey != null)
            SaveEditedMaterial();

        // the as-shipped graph on the canvas, edited, is the document's (ShippedGraph)
        SyncShippedGraph();

        if (!CamoMode || m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count ||
            string.Equals(p_Mesh, m_DocumentSubject, StringComparison.OrdinalIgnoreCase))
            return DocumentGraph;

        var s_Tab = m_Tabs[m_ActiveTab];
        ShareCamoSettings(s_Tab, DocumentGraph);
        return s_Tab.Subjects.TryGetValue(p_Mesh, out var s_Own)
            ? s_Own.Graph
            : SubjectPresetIn(s_Tab, p_Mesh, s_Tab.NativeKey) ?? ForeignSubjectPresetIn(s_Tab, p_Mesh, s_Tab.NativeKey) ?? s_Tab.Common ?? DocumentGraph;
    }

    /// <summary>
    /// ⭐ A subject's own starting document authored on ANOTHER shader than this tab's (m_SubjectPresetFor — the Rhino's van body: its own
    /// shader with the camo layer the catalogue adds), with the camo's shared settings on it (the pattern on ITS camo register,
    /// the native camo, the simple form's numbers — CopyCamoSettings). This tab cannot draw it (TabDrawsSubject sends the subject to a tab of
    /// its own), but a bake of this tab's camo that ticks it takes it: its pieces' copies are compiled from it against its own shader (the bake's
    /// per-vehicle graph target). Null when it has none, or when its own is on this tab's shader (then SubjectPresetIn stands for it).
    /// </summary>
    private ShaderGraph? ForeignSubjectPresetIn(EditorTab p_Tab, string p_Subject, string p_Native)
    {
        var s_Common = p_Tab.Set(p_Native).Common;
        if (s_Common == null || m_SubjectPresetFor?.Invoke(p_Subject) is not { } s_Own ||
            string.Equals(s_Common.TargetShader, s_Own.TargetShader, StringComparison.OrdinalIgnoreCase))
            return null;

        CopyCamoSettings(s_Common, s_Own);
        s_Own.NativeCamo = p_Native.Length == 0 ? null : p_Native;
        return s_Own;
    }

    /// <summary>A document of the session CHANGED from the game's own translation of its shader, as a soldier skin's bake reads it.</summary>
    /// <param name="Tab">The tab it is held in, as the tab strip names it.</param>
    /// <param name="LogicEdited">Its nodes or wires emit another shader than the game's (a skin carries pictures and numbers, not a shader).</param>
    /// <param name="Values">The numbers typed on it that differ from the translation's.</param>
    internal sealed record ChangedDocument(string Tab, ShaderGraph Document, bool LogicEdited, IReadOnlyDictionary<string, string> Values);

    /// <summary>
    /// Every CHANGED document held under a subject KEY in the session, one per tab that holds one — ⭐ ALL the tabs, not only the one on
    /// screen (a soldier skin): a soldier's part can be drawn on two shaders (his Aftermath torso on characterroot_xp4, his
    /// first-person arms on characterroot), each opening its own tab, and the skin is the whole soldier. The one on the canvas for the active
    /// tab's subject, the tab's own copy otherwise. Changed = it does not draw like the game's translation of its shader (DrawsTheSame: logic,
    /// typed numbers, pictures); an untouched part answers nothing, whatever camo of the picker the tab shows.
    /// </summary>
    internal IReadOnlyList<ChangedDocument> ChangedDocumentsUnder(string p_Key)
    {
        if (m_MaterialEditKey != null)
            SaveEditedMaterial();
        SyncShippedGraph();

        var s_Result = new List<ChangedDocument>();
        if (!CamoMode)
            return s_Result;

        // ⛔ THE TABS OF CHANGES FIRST (review 2026-09-29, D6): an original tab keeps nothing but the looks it was browsed on, and those were
        // read as the part "on a look" beside the part's own tab of changes — two documents of one part (the skin shipped the first of them,
        // said in a note). The originals answer only for a part no tab of changes holds (a look picked and nothing else: the look is the
        // template). CAMO_CHANGED_ORIGINALS_OLD=1 = every tab alike, as before.
        var s_OriginalsLast = Environment.GetEnvironmentVariable("CAMO_CHANGED_ORIGINALS_OLD") != "1";
        foreach (var t in Enumerable.Range(0, m_Tabs.Count).OrderBy(p_T => s_OriginalsLast && m_Tabs[p_T].Original).ToList())
        {
            var s_Tab = m_Tabs[t];
            if (s_OriginalsLast && s_Tab.Original && s_Result.Count > 0)
                break;

            var s_Active = t == m_ActiveTab;
            var s_Subject = s_Active ? m_DocumentSubject : s_Tab.DocumentSubject;
            // (under the camo the tab holds that subject on: its own one where each subject keeps its own — a soldier's part on a stock look)
            var s_Held = s_Tab.Set(NativeHeldFor(s_Tab, p_Key));
            var s_Document = string.Equals(s_Subject, p_Key, StringComparison.OrdinalIgnoreCase)
                ? s_Active ? DocumentGraph : s_Tab.Graph
                : s_Held.Subjects.TryGetValue(p_Key, out var s_Own) ? s_Own.Graph
                // ⭐ an untouched part on a camo of its own (a stock look) is that camo's common document: it says which look the part is on
                : NativePerSubject(p_Key) && s_Held.Common?.NativeCamo is { } s_HeldCamo && s_HeldCamo != m_CamoBasicKey ? s_Held.Common
                : null;
            // ⭐ a part on one of the game's looks says something even drawn as the translation (the look is the template)
            var s_OnLook = s_Document?.NativeCamo is { } s_Camo && s_Camo != m_CamoBasicKey;
            // ⭐ …and a part with a starting document of its own (the US support's legs and the mask that keeps his boots, knee pads and belt
            // out — SubjectPresetIn) is changed only against THAT: untouched, it is not in the skin (a skin of his torso
            // alone must not change his legs); touched, its document — the picture on its Mask included — is what the skin takes
            // (CAMO_SUBJECT_START_OLD=1: measured against the translation, as before — the way back, and this rule's negative control)
            var s_Start = Environment.GetEnvironmentVariable("CAMO_SUBJECT_START_OLD") == "1" ? null : SubjectPresetIn(s_Tab, p_Key, NativeHeldFor(s_Tab, p_Key));
            if (s_Document == null || PristineOf(s_Document.TargetShader) is not { } s_Pristine ||
                (!s_OnLook && !SubjectChanged(s_Document, s_Start ?? s_Pristine)))
                continue;

            var s_Logic = LogicFingerprint(s_Document);
            var s_PristineLogic = LogicFingerprint(s_Pristine);
            var s_LogicEdited = s_Logic == null || s_PristineLogic == null
                ? s_Document.Nodes.Count != s_Pristine.Nodes.Count || s_Document.Connections.Count != s_Pristine.Connections.Count
                : s_Logic != s_PristineLogic;

            var s_Was = ValuesOfGraph(s_Pristine);
            var s_Values = ValuesOfGraph(s_Document)
                .Where(p_V => !s_Was.TryGetValue(p_V.Key, out var s_Old) || s_Old != p_V.Value)
                .ToDictionary(p_V => p_V.Key, p_V => p_V.Value, StringComparer.OrdinalIgnoreCase);

            s_Result.Add(new ChangedDocument(TabTitleOf(t), s_Document, s_LogicEdited, s_Values));
        }

        return s_Result;
    }

    /// <summary>The values typed on a document's external constants (PreviewValue), by bare name — CamoSnapshotView.PreviewValues for any document.</summary>
    internal Dictionary<string, string> PreviewValuesOf(ShaderGraph p_Graph) => p_Graph.Nodes
        .Where(p_N => p_N.Kind == "ExternalConstant" &&
                      p_N.Params.TryGetValue("PreviewValue", out var s_Preview) && s_Preview.Length > 0)
        .GroupBy(BareExternalName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Params["PreviewValue"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The LOGIC of a camo document: its HLSL emitted through the cached contract of its target — two documents that draw alike
    /// (the same sticker nodes laid on two weapons) answer alike whatever their node ids or positions. Null when the contract or
    /// the emission is not there.
    /// </summary>
    internal string? LogicFingerprint(ShaderGraph p_Graph) =>
        ReadContractFromCache(p_Graph.TargetShader) is { } s_Contract ? HlslFingerprint(CanonicalIds(p_Graph), s_Contract) : null;

    /// <summary>
    /// A copy of a graph whose node ids say WHAT each node is, not when it was made: each node renamed after its kind, its parameters
    /// (the note and the position aside) and, recursively, what feeds each of its inputs. ⛔ The emitter numbers its temporaries in the
    /// order it walks the nodes, and that order follows the ids — the SAME tint added to two weapons (two fresh ids) emitted two HLSL
    /// texts, and the bake took one logic for two (--weaponbaketest, 2026-09-25).
    /// </summary>
    private static ShaderGraph CanonicalIds(ShaderGraph p_Graph)
    {
        var s_Copy = ShaderGraph.FromJson(p_Graph.ToJson());

        // ⛔ and nothing that is not logic: the NAME rides into the emitted text, and the bake renames the document on screen after the
        // camo — the same tint on two weapons came out as two logics until this (357408b1 against fb8ad012, --weaponbaketest)
        s_Copy.Name = "";
        s_Copy.PreviewMesh = null;
        s_Copy.PreviewWeapon = null;
        s_Copy.Variations = null;
        s_Copy.MaterialGraphs = null;
        var s_Inputs = s_Copy.Connections.GroupBy(p_C => p_C.ToNode).ToDictionary(p_G => p_G.Key, p_G => p_G.ToList());
        var s_Keys = new Dictionary<string, string>();
        var s_Busy = new HashSet<string>();

        string KeyOf(GraphNode p_Node)
        {
            if (s_Keys.TryGetValue(p_Node.Id, out var s_Known))
                return s_Known;
            if (!s_Busy.Add(p_Node.Id))
                return $"cycle:{p_Node.Kind}";

            var s_Params = string.Join(";", p_Node.Params.Where(p_P => p_P.Key != "Comment")
                .OrderBy(p_P => p_P.Key, StringComparer.Ordinal).Select(p_P => $"{p_P.Key}={p_P.Value}"));
            var s_Feeds = string.Join(";", (s_Inputs.GetValueOrDefault(p_Node.Id) ?? new List<GraphConnection>())
                .Select(p_C => (p_C.ToPort, From: s_Copy.Nodes.FirstOrDefault(p_N => p_N.Id == p_C.FromNode), p_C.FromPort))
                .Select(p_I => $"{p_I.ToPort}<{(p_I.From == null ? "?" : KeyOf(p_I.From))}.{p_I.FromPort}")
                .OrderBy(p_S => p_S, StringComparer.Ordinal));
            var s_Text = $"{p_Node.Kind}[{s_Params}]({s_Feeds})";
            var s_Key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s_Text)))[..16];
            s_Busy.Remove(p_Node.Id);
            s_Keys[p_Node.Id] = s_Key;
            return s_Key;
        }

        foreach (var s_Node in s_Copy.Nodes)
            KeyOf(s_Node);

        // the same structure twice (two identical constants) gets one name each, in their order
        var s_Renamed = new Dictionary<string, string>();
        foreach (var s_Group in s_Copy.Nodes.GroupBy(p_N => s_Keys[p_N.Id]))
        {
            var s_At = 0;
            foreach (var s_Node in s_Group)
                s_Renamed[s_Node.Id] = $"{s_Group.Key}_{s_At++}";
        }

        foreach (var s_Connection in s_Copy.Connections)
        {
            s_Connection.FromNode = s_Renamed.GetValueOrDefault(s_Connection.FromNode, s_Connection.FromNode);
            s_Connection.ToNode = s_Renamed.GetValueOrDefault(s_Connection.ToNode, s_Connection.ToNode);
        }

        foreach (var s_Node in s_Copy.Nodes)
            s_Node.Id = s_Renamed[s_Node.Id];

        s_Copy.Nodes.Sort((p_A, p_B) => string.CompareOrdinal(p_A.Id, p_B.Id));
        return s_Copy;
    }

    /// <summary>
    /// The subjects of the active tab whose AS-SHIPPED graph was edited (ShaderGraph.ShippedGraph), under the camo on screen — the one on
    /// screen included. Only a weapon's or an attachment's: a vehicle's as-shipped graph is its document itself.
    /// </summary>
    internal IReadOnlyList<string> ShippedEditsOfSession()
    {
        SyncShippedGraph();
        var s_Subjects = new List<string>();
        if (DocumentGraph.ShippedGraph != null && m_DocumentSubject != null)
            s_Subjects.Add(m_DocumentSubject);

        if (CamoMode && m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count)
            s_Subjects.AddRange(m_Tabs[m_ActiveTab].Subjects
                .Where(p_S => p_S.Value.Graph.ShippedGraph != null && !string.Equals(p_S.Key, m_DocumentSubject, StringComparison.OrdinalIgnoreCase))
                .Select(p_S => p_S.Key));

        return s_Subjects;
    }

    /// <summary>Whether a subject of the active tab has a document of its own (something was changed on it).</summary>
    internal bool HasOwnDocument(string p_Mesh) =>
        CamoMode && m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count &&
        (string.Equals(p_Mesh, m_DocumentSubject, StringComparison.OrdinalIgnoreCase) && m_Tabs[m_ActiveTab].Common is { } s_Common
            ? SubjectFingerprint(DocumentGraph) != SubjectFingerprint(s_Common)
            : m_Tabs[m_ActiveTab].Subjects.ContainsKey(p_Mesh));

    /// <summary>
    /// The edited material graphs of EVERY subject of the active tab, each read off its own subject's document (DocumentOfSubject): a
    /// material "mesh|id" belongs to that mesh, so its graph is the one the mesh's document carries. What the bake ships.
    /// </summary>
    internal IReadOnlyList<(string Mesh, int MaterialId, string Shader, ShaderGraph Graph)> EditedMaterialGraphsOfSession()
    {
        if (m_MaterialEditKey != null)
            SaveEditedMaterial();

        var s_Documents = new List<ShaderGraph> { DocumentGraph };
        if (CamoMode && m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count)
        {
            var s_Tab = m_Tabs[m_ActiveTab];
            s_Documents.AddRange(s_Tab.Subjects.Values.Select(p_S => p_S.Graph));
            if (s_Tab.Common != null)
                s_Documents.Add(s_Tab.Common);
        }

        var s_Meshes = s_Documents
            .SelectMany(p_D => p_D.MaterialGraphs?.Keys ?? Enumerable.Empty<string>())
            .Select(p_K => ShaderGraph.TryParseMaterialKey(p_K, out var s_Mesh, out _) ? s_Mesh : null)
            .Where(p_M => p_M != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return s_Meshes
            .SelectMany(p_Mesh => EditedMaterialGraphs(DocumentOfSubject(p_Mesh!))
                .Where(p_E => p_E.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Every material graph of the active tab's subjects that carries a PICTURE on a texture node (a custom texture put through a
    /// texture node must just work), whether or not its logic changed: a picture alone changes nothing the
    /// shader emits — no copy of the shader for it — but the bake still has to ship and bind it on that material. One per (mesh, id).
    /// </summary>
    internal IReadOnlyList<(string Mesh, int MaterialId, string Shader, ShaderGraph Graph)> MaterialGraphsWithPicturesOfSession()
    {
        if (m_MaterialEditKey != null)
            SaveEditedMaterial();

        var s_Documents = new List<ShaderGraph> { DocumentGraph };
        if (CamoMode && m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count)
            s_Documents.AddRange(m_Tabs[m_ActiveTab].Subjects.Values.Select(p_S => p_S.Graph));

        var s_Result = new List<(string, int, string, ShaderGraph)>();
        var s_Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Mesh in s_Documents
                     .SelectMany(p_D => p_D.MaterialGraphs?.Keys ?? Enumerable.Empty<string>())
                     .Select(p_K => ShaderGraph.TryParseMaterialKey(p_K, out var s_M, out _) ? s_M : null)
                     .Where(p_M => p_M != null)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var (s_Key, s_Graph) in DocumentOfSubject(s_Mesh!).MaterialGraphs ?? new Dictionary<string, ShaderGraph>())
                if (ShaderGraph.TryParseMaterialKey(s_Key, out var s_Of, out var s_Id) && s_Of.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) &&
                    s_Graph.TargetShader is { Length: > 0 } s_Shader && CustomTextureSignature(s_Graph).Length > 0 && s_Seen.Add(s_Key))
                    s_Result.Add((s_Of, s_Id, s_Shader, s_Graph));

        return s_Result;
    }

    /// <summary>
    /// Every material graph of the active tab's subjects with a value TYPED on one of its constants — "Mesh #id": Name, … . The preview draws
    /// the material with them; no bake writes a material's own numbers (its instance names its clone or its pictures, never constants), so
    /// the bake says it (review 2026-09-29, C3).
    /// </summary>
    internal IReadOnlyList<(string Mesh, int MaterialId, IReadOnlyList<string> Constants)> MaterialGraphValuesOfSession()
    {
        if (m_MaterialEditKey != null)
            SaveEditedMaterial();

        var s_Documents = new List<ShaderGraph> { DocumentGraph };
        if (CamoMode && m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count)
            s_Documents.AddRange(m_Tabs[m_ActiveTab].Subjects.Values.Select(p_S => p_S.Graph));

        var s_Result = new List<(string, int, IReadOnlyList<string>)>();
        var s_Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Document in s_Documents)
            foreach (var (s_Key, s_Graph) in s_Document.MaterialGraphs ?? new Dictionary<string, ShaderGraph>())
                if (ShaderGraph.TryParseMaterialKey(s_Key, out var s_Mesh, out var s_Id) && PreviewValuesOf(s_Graph) is { Count: > 0 } s_Typed &&
                    s_Seen.Add(s_Key))
                    s_Result.Add((s_Mesh, s_Id, s_Typed.Keys.OrderBy(p_K => p_K, StringComparer.OrdinalIgnoreCase).ToList()));

        return s_Result;
    }

    /// <summary>The material graph exactly as it was opened, so a material only LOOKED at is not written into the camo.</summary>
    private string? m_MaterialEditOriginal;

    /// <summary>Writes the material graph currently on the canvas back into the camo document, keyed by its material.</summary>
    private void SaveEditedMaterial()
    {
        if (m_MaterialEditKey == null || m_MaterialParkedCamo == null ||
            !ShaderGraph.TryParseMaterialKey(m_MaterialEditKey, out var s_Mesh, out var s_Id))
            return;

        var s_Json = Canvas.Graph.ToJson();
        var s_Untouched = s_Json == m_MaterialEditOriginal;
        m_MaterialParkedCamo.SetMaterialGraph(s_Mesh, s_Id, s_Untouched ? null : ShaderGraph.FromJson(s_Json));

        // What the document ended up with, including the case where it ended up with NOTHING: "the material
        // was not edited" and "the edit was dropped" are the same silence otherwise, and that silence is what
        // a lost graph looks like.
        Log($"Camo: material #{s_Id} of {s_Mesh.Split('/')[^1]} " + (s_Untouched
            ? "is the stock preset — nothing of it is kept in the camo."
            : $"kept in the camo ({Canvas.Graph.Nodes.Count} node(s))."));
    }

    /// <summary>
    /// The material graph exactly as it came onto the canvas. While the canvas still says the same (in what it
    /// draws), the material is being LOOKED at and draws with whatever the camo carries for it — which is not
    /// always that graph: a piece's own saved preset opens on the canvas before the camo has adopted it.
    /// </summary>
    private ShaderGraph? m_MaterialOpened;

    /// <summary>What each material-edit registration was built from (name -> shader hash), so an unchanged one is not recompiled.</summary>
    private readonly Dictionary<string, string> m_MaterialEditBuilt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The stock translation's HLSL and typed values per shader, read once — what "really edited" is measured against.</summary>
    private readonly Dictionary<string, (string? Hlsl, Dictionary<string, string>? Typed)> m_PristineGraphs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Contracts read for material edits, per shader (reading one disassembles the bytecode).</summary>
    private readonly Dictionary<string, ShaderContract?> m_MaterialEditContracts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ⭐ WHAT A MATERIAL'S OWN GRAPH CHANGES SHOWS ON THAT MATERIAL, AND NOTHING ELSE MOVES (only when the
    /// graph is really edited). For every material of the object on screen whose own
    /// graph this camo carries — the one on the canvas, live, and every one saved in the document — that REALLY
    /// differs from the stock translation (its HLSL, or the preview values typed on its constants; moving a
    /// node is not an edit), the graph is compiled and drawn on that material's sections alone, with that
    /// material's own art and numbers. A material only looked at keeps drawing exactly as before — the game's
    /// own shader — and so does the rest of the object, whatever look it is in. The edits stay on screen while
    /// other materials are looked at: what the object shows is what the camo carries, not what is selected.
    /// An accessory's painted body is the exception, drawn by its stand-in (EnsureAccessoryShader), which
    /// compiles the same graph; it only comes here when there is no stand-in to do it.
    /// </summary>
    private void RefreshMaterialEdits()
    {
        var s_Overrides = new Dictionary<int, string>();
        if (!CamoMode || m_PreviewMesh is not { Length: > 0 } s_Mesh ||
            !string.Equals(m_MaterialListMesh, s_Mesh, StringComparison.OrdinalIgnoreCase))
        {
            m_Preview.SectionOverrides = s_Overrides;
            return;
        }

        var s_IsPiece = m_CamoWeaponOfAccessory?.Invoke(s_Mesh) != null;
        var s_StandIn = m_Preview.FactoryShader?.EndsWith("#accessory", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var s_Entry in MaterialList.Items.OfType<MaterialEntry>())
        {
            if (!s_Entry.IdKnown || s_Entry.Section >= m_Preview.MeshSections.Count)
                continue;

            // A painted material wears the camo's graph, except an accessory's body (the stand-in draws it when
            // there is one) and a vehicle's material on a preset of its own (ShowMaterialGraph).
            // ⛔ As shipped, an accessory's body is the GAME's piece (as shipped must be literally the game's
            // default shader, attachment or whatever it is) — its own graph is part of the
            // camo and shows as soon as one is on, through the stand-in (below: except while being edited).
            var s_Painted = s_Entry.TakesCamo && !DocumentGraph.IsMaterialOff(s_Mesh, s_Entry.MaterialId);
            var s_OwnPreset = s_Painted && WearsOwnVehiclePreset(s_Mesh, s_Entry);

            // The one on the canvas counts as it stands once it draws differently from how it was opened;
            // until then it is only being looked at, and draws with what the camo carries for it.
            var s_Key = ShaderGraph.MaterialKey(s_Mesh, s_Entry.MaterialId);
            var s_LiveEdit = string.Equals(m_MaterialEditKey, s_Key, StringComparison.OrdinalIgnoreCase) &&
                             !DrawsTheSame(Canvas.Graph, m_MaterialOpened);

            // As shipped the accessory's body is the game's piece; an edit of its graph there moves the picker to
            // the basic camo (PromoteOnGraphEdit), and the stand-in shows it.
            if (s_Painted && !s_OwnPreset && (!s_IsPiece || s_StandIn || CamoFactoryLook()))
                continue;

            var s_Saved = DocumentGraph.MaterialGraphOf(s_Mesh, s_Entry.MaterialId);
            var s_Graph = s_LiveEdit ? Canvas.Graph : s_Saved;
            if (s_Graph?.TargetShader is not { Length: > 0 } s_Shader)
                continue;

            if (RegisterMaterialEdit(s_Mesh, s_Entry, s_Graph, s_Shader, s_Painted) is { } s_Name)
                s_Overrides[s_Entry.Section] = s_Name;
        }

        m_Preview.SectionOverrides = s_Overrides;
    }

    /// <summary>Whether two graphs of one shader put the same thing on screen: the same HLSL and the same typed preview values.</summary>
    private bool DrawsTheSame(ShaderGraph p_Graph, ShaderGraph? p_Other)
    {
        if (p_Other == null || !string.Equals(p_Graph.TargetShader, p_Other.TargetShader, StringComparison.OrdinalIgnoreCase))
            return false;

        // ⛔ THE NAME IS NOT THE LOGIC — and the emitted text carries it (measured 2026-09-25: a New tab's "Untitled" document against the
        // game's translation, named after its preset, counted as EDITED as shipped). Compared under one name.
        if (!string.Equals(p_Graph.Name, p_Other.Name, StringComparison.Ordinal))
        {
            p_Other = ShaderGraph.FromJson(p_Other.ToJson());
            p_Other.Name = p_Graph.Name;
        }

        if (!m_MaterialEditContracts.TryGetValue(p_Graph.TargetShader, out var s_Contract))
            m_MaterialEditContracts[p_Graph.TargetShader] = s_Contract = ReadContractFromCache(p_Graph.TargetShader);

        var s_Values = ValuesOfGraph(p_Graph);
        var s_OtherValues = ValuesOfGraph(p_Other);
        // ⭐ …and the same PICTURES on its texture nodes (a custom texture anywhere must not be a problem): a picture
        // changes nothing in the emitted text, and without this a picture-only edit was "the same" — never forked, never kept as shipped
        return HlslFingerprint(p_Graph, s_Contract) is { } s_Hash && s_Hash == HlslFingerprint(p_Other, s_Contract) &&
               s_Values.Count == s_OtherValues.Count &&
               s_Values.All(p_V => s_OtherValues.TryGetValue(p_V.Key, out var s_Was) && s_Was == p_V.Value) &&
               CustomTextureSignature(p_Graph) == CustomTextureSignature(p_Other);
    }

    /// <summary>
    /// Compiles one material's edited graph and registers it for its section — or returns null when the graph
    /// is the stock one in everything that reaches the screen (then the section keeps drawing as it did). A
    /// graph that does not compile keeps the last registration that did, like the camo's own preview.
    /// </summary>
    private string? RegisterMaterialEdit(string p_Mesh, MaterialEntry p_Entry, ShaderGraph p_Graph, string p_Shader, bool p_Painted)
    {
        var s_Name = $"{p_Shader}#edit{p_Entry.MaterialId}";
        if (!m_MaterialEditContracts.TryGetValue(p_Shader, out var s_Contract))
            m_MaterialEditContracts[p_Shader] = s_Contract = ReadContractFromCache(p_Shader);

        if (s_Contract == null)
            return null;

        var s_Emit = new HlslEmitter { Contract = s_Contract }.Emit(p_Graph);
        if (!s_Emit.Ok)
        {
            PreviewPaused($"Preview paused (material #{p_Entry.MaterialId}): {s_Emit.Errors[0]}");
            return m_Preview.HasForeignShader(s_Name) ? s_Name : null;
        }

        // The stock translation, emitted through the SAME contract (the bake's own yardstick, EditedMaterialGraphs).
        if (!m_PristineGraphs.TryGetValue(p_Shader, out var s_Pristine))
        {
            s_Pristine = (null, null);
            if (File.Exists(CamoGraphCachePath(p_Shader)))
                try
                {
                    var s_StockGraph = LoadCamoGraph(p_Shader);
                    var s_Stock = new HlslEmitter { Contract = s_Contract }.Emit(s_StockGraph);
                    s_Pristine = (s_Stock.Ok ? s_Stock.Hlsl : null, ValuesOfGraph(s_StockGraph));
                }
                catch (Exception)
                {
                    // No yardstick: every graph counts as edited, which is the safe side (it shows).
                }

            // (a translation not cached YET is asked again next time — see PristineOf)
            if (File.Exists(CamoGraphCachePath(p_Shader)))
                m_PristineGraphs[p_Shader] = s_Pristine;
        }

        var s_Typed = ValuesOfGraph(p_Graph);
        var s_ValuesStock = s_Pristine.Typed is { } s_StockTyped && s_Typed.Count == s_StockTyped.Count &&
                            s_Typed.All(p_V => s_StockTyped.TryGetValue(p_V.Key, out var s_Was) && s_Was == p_V.Value);
        // ⭐ a picture on one of its texture nodes is an edit too (any custom texture on a texture node must just work) — the
        // stock translation carries none
        var s_OwnPictures = CustomTextureSignature(p_Graph);
        if (s_Pristine.Hlsl != null && s_Emit.Hlsl == s_Pristine.Hlsl && s_ValuesStock && s_OwnPictures.Length == 0)
        {
            if (m_MaterialEditBuilt.Remove(s_Name))
                Log($"Camo: material #{p_Entry.MaterialId} '{p_Entry.Name}' is the stock graph again — it draws as it ships.");

            return null;
        }

        // Its own art and numbers: the same the section is drawn with as shipped (the material's own set, else
        // the shader's slot map for this mesh); a painted accessory body, the camo's layering on its preset; a
        // vehicle's material on its own preset, ITS set under the picked native camo with the camo's numbers and
        // pictures laid on — what the view dresses it with (ApplyMaterialDress), so the edit is all that changes.
        var s_OwnPreset = p_Painted && WearsOwnVehiclePreset(p_Mesh, p_Entry);
        var s_UnderCamo = s_OwnPreset && !CamoFactoryLook();
        var s_Native = p_Painted ? DocumentGraph.NativeCamo : null;
        var s_Slots = s_OwnPreset
            ? m_CamoSlotsForMaterial?.Invoke(p_Mesh, p_Entry.Shader, s_Native, p_Entry.MaterialId) ??
              m_CamoSlotsFor?.Invoke(p_Mesh, p_Shader, s_Native)
            : p_Painted
                ? m_CamoSlotsFor?.Invoke(p_Mesh, p_Shader, s_Native)
                : m_CamoSlotsForMaterial?.Invoke(p_Mesh, p_Entry.Shader, null, p_Entry.MaterialId);
        var s_Values = new Dictionary<string, string>(
            (s_OwnPreset
                ? m_CamoValuesForMaterial?.Invoke(p_Mesh, p_Entry.Shader, s_Native, p_Entry.MaterialId) ??
                  m_CamoValuesFor?.Invoke(p_Mesh, p_Shader, s_Native)
                : p_Painted
                    ? m_CamoValuesFor?.Invoke(p_Mesh, p_Shader, s_Native)
                    : m_CamoValuesForMaterial?.Invoke(p_Mesh, p_Entry.Shader, null, p_Entry.MaterialId))
            ?? LoadForeignMaterialValues(SlotMapPath(p_Shader), p_Mesh)
            ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        if (s_UnderCamo)
            foreach (var (s_Constant, s_Text) in DocumentValues())
                s_Values[s_Constant] = s_Text;

        foreach (var (s_Constant, s_Text) in s_Typed)
            s_Values[s_Constant] = s_Text;

        var s_Pictures = s_UnderCamo ? CustomTextureBlocks(DocumentGraph) : new();
        var s_ShaderKey = $"{p_Mesh}|{s_Emit.Hlsl.Length}:{s_Emit.Hlsl.GetHashCode()}|{s_Native}|" +
                          string.Join(",", s_Pictures.Select(p_P => $"{p_P.Slot}:{p_P.Width}x{p_P.Height}:{p_P.Pixels.Length}")) +
                          "|" + s_OwnPictures;
        if (m_MaterialEditBuilt.TryGetValue(s_Name, out var s_Built) && s_Built == s_ShaderKey &&
            m_Preview.HasForeignShader(s_Name))
        {
            // Only the numbers moved: re-fed, nothing recompiles.
            m_Preview.SetForeignValues(s_Name, s_Values);
            return s_Name;
        }

        try
        {
            using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                s_Emit.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);
            if (s_Compiled.Bytecode == null)
                return m_Preview.HasForeignShader(s_Name) ? s_Name : null;

            var s_Textures = s_Slots != null
                ? DecodeCachedTextures(s_Slots, TexCacheDir)
                : LoadForeignTexturesFromCache(SlotMapPath(p_Shader), TexCacheDir, p_Mesh);
            foreach (var s_Picture in s_Pictures)
                foreach (var s_Slot in SlotsOfSameTexture(CamoTarget, p_Shader, s_Picture.Slot, ReadContractFromCache))
                {
                    s_Textures.RemoveAll(p_T => p_T.Slot == s_Slot);
                    s_Textures.Add((s_Slot, s_Picture.Pixels, s_Picture.Width, s_Picture.Height, s_Picture.Srgb));
                }

            // …and the pictures of THIS material's own graph last, at the registers its nodes read: what the bake binds on that material
            if (s_OwnPictures.Length > 0)
                foreach (var s_Picture in CustomTextureBlocks(p_Graph))
                {
                    s_Textures.RemoveAll(p_T => p_T.Slot == s_Picture.Slot);
                    s_Textures.Add((s_Picture.Slot, s_Picture.Pixels, s_Picture.Width, s_Picture.Height, s_Picture.Srgb));
                }

            var s_Error = m_Preview.RegisterForeignShader(s_Name, s_Compiled.Bytecode.Data, s_Textures, s_Values,
                SamplersForShader(p_Shader));
            if (s_Error != null)
            {
                PreviewPaused($"Preview paused (material #{p_Entry.MaterialId}): {s_Error}");
                return null;
            }

            var s_First = !m_MaterialEditBuilt.ContainsKey(s_Name);
            m_MaterialEditBuilt[s_Name] = s_ShaderKey;
            if (s_First)
                Log($"Camo: material #{p_Entry.MaterialId} '{p_Entry.Name}' draws with its EDITED graph of " +
                    $"'{p_Shader.Split('/')[^1]}' — on that material only; the rest of the object is untouched.");

            return s_Name;
        }
        catch (Exception s_Exception)
        {
            PreviewPaused($"Preview paused (material #{p_Entry.MaterialId}): " + s_Exception.Message.Split('\n')[0].Trim());
            return m_Preview.HasForeignShader(s_Name) ? s_Name : null;
        }
    }

    /// <summary>
    /// Puts the parked camo document back on the canvas and, optionally, re-dresses the weapon for the camo.
    /// The saved material graph is left in the document (SaveEditedMaterial did that); the preview goes back
    /// to the camo's target and the whole object.
    /// </summary>
    private void LeaveMaterialEdit(bool p_Dress)
    {
        if (m_MaterialParkedCamo == null)
        {
            m_MaterialEditKey = null;
            m_MaterialEditSection = -1;
            return;
        }

        Canvas.Graph = m_MaterialParkedCamo;
        Canvas.RestoreUndo(m_MaterialParkedUndo.Undo, m_MaterialParkedUndo.Redo);
        m_Contract = m_MaterialParkedContract;
        m_CamoContractShader = m_Contract != null && m_MaterialParkedTarget is { Length: > 0 } ? m_MaterialParkedTarget : null;
        m_Preview.SetInterpolatorMeanings(m_Contract?.InterpolatorMeanings);
        TargetShaderBox.Text = m_MaterialParkedTarget ?? "";

        m_MaterialParkedCamo = null;
        m_MaterialParkedContract = null;
        m_MaterialParkedTarget = null;
        m_MaterialEditKey = null;
        m_MaterialEditSection = -1;
        m_Preview.TargetSectionOnly = null;

        // The edits the document now carries — the one just left included — stay on the object.
        RefreshMaterialEdits();

        if (p_Dress && m_PreviewMesh is { Length: > 0 } s_Mesh)
        {
            AimPreviewAt(TargetShaderBox.Text.Trim());
            m_Preview.MeshTargetAliases = CamoAliasesFor(TargetShaderBox.Text.Trim());
            if (TargetShaderBox.Text.Trim() is { Length: > 0 } s_Target)
            {
                m_InFactorySync = true;
                try
                {
                    DressCamoWeapon(s_Mesh, s_Target);
                }
                finally
                {
                    m_InFactorySync = false;
                }
            }

            ApplyMaterialChoices();
        }

        BuildProperties();
        Canvas.Refresh();
        SyncFactoryLook();

        // ⛔⛔⛔ AND THE CAMO HAS TO BE COMPILED AGAIN. Putting its graph back on the canvas is not compiling
        // it: the preview kept running the MATERIAL's shader (measured on the LAV-25 — coming back from
        // M_KitAtlas, the Output has no `preview recompiled` line at all and the hull went on drawing with
        // the kits shader). This is the root of "the previously selected one looks wrong again";
        // everything downstream — the dressing, the values — was then built on the wrong shader.
        if (!s_NoDressRefresh)
            SchedulePreview();

        // The dressing the edit cleared, re-asserted for the paths that do not reach a recompile.
        RefreshMaterialDress();
    }

    /// <summary>
    /// The way OUT of the weapon's own graph when the user does something to the camo on it: the picker
    /// moves to the basic camo (the truth of what will be on screen), the camo's graph comes back, and the
    /// weapon is dressed for the camo's preset again. Nothing happens when the camo's graph is already there.
    /// </summary>
    private void LeaveFactoryGraph(string p_Why)
    {
        // A user action on the camo while "as shipped" is selected: the picker moves to the basic camo
        // (whether or not the no-camo graph is standing in on the canvas) and the camo's graph comes back
        // where it was parked. Nothing happens when a camo is already picked and the document is shown.
        var s_Shipped = CamoMode && DocumentGraph.NativeCamo == null && m_CamoBasicKey != null;
        if (m_ParkedCamoGraph == null && !s_Shipped)
            return;

        PromoteToBasicCamo(p_Why);
        if (m_ParkedCamoGraph != null)
            RestoreCamoGraph();

        // The CAMO's target (a material's graph may be on the canvas, and the box then names its shader).
        if (m_PreviewMesh is { Length: > 0 } s_Mesh && CamoTarget is { Length: > 0 } s_Target)
        {
            m_InFactorySync = true;
            try
            {
                DressCamoWeapon(s_Mesh, s_Target);
            }
            finally
            {
                m_InFactorySync = false;
            }
        }

        // ⛔ AND THE PIECE GETS ITS GRAPH BACK HERE. While "as shipped" is picked an attachment shows the GAME's
        // object and no graph of ours is opened over it (FillMaterialList, 2026-09-20); the moment the user
        // leaves that look — a camo, a pattern, a value — the piece is being AUTHORED again, and the graph it
        // draws with is its own preset's, not the weapon's (the 09-18 rule). Without this the two rules met in
        // the middle and left the canvas on a graph whose constants reach nothing.
        if (m_MaterialEditKey == null && m_PreviewMesh is { Length: > 0 } s_Piece &&
            m_CamoWeaponOfAccessory?.Invoke(s_Piece) != null &&
            MaterialList.SelectedItem is MaterialEntry s_Body && s_Body.TakesCamo &&
            !DocumentGraph.IsMaterialOff(s_Piece, s_Body.MaterialId))
            ShowMaterialGraph(s_Body);

        // there is a camo again, so the piece's own numbers are a question again
        ShowAccessoryValues();
    }

    /// <summary>
    /// Extends the camo document with its sticker nodes and, when the no-camo graph is standing in for it on
    /// the canvas, that graph too at the same register, the two sharing one list of placements.
    /// </summary>
    private void EnsureStickerNodesEverywhere()
    {
        if (m_ParkedCamoGraph == null)
        {
            EnsureStickerNodes();
            EnsureStickerAnimationEverywhere();
            return;
        }

        var s_Diffuse = m_StickerDiffuseRegisterFor?.Invoke(ActiveTarget);
        var s_Register = StickerGraph.Ensure(m_ParkedCamoGraph, s_Diffuse, null, out var s_Note, m_ParkedCamoGraph.StickerLayerSide);
        if (s_Register == null)
        {
            Log("Stickers: " + s_Note);
            return;
        }

        if (s_Note.Length > 0)
            Log("Stickers: " + s_Note);

        ShareStickersWithView();
        EnsureStickerNodes(s_Register);
        EnsureStickerAnimationEverywhere();
    }

    /// <summary>The no-camo graph on the canvas carries the document's placements, size and register - one list, never a copy.</summary>
    private void ShareStickersWithView()
    {
        if (m_ParkedCamoGraph == null)
            return;

        m_ParkedCamoGraph.Stickers ??= new List<StickerPlacement>();
        Canvas.Graph.Stickers = m_ParkedCamoGraph.Stickers;
        Canvas.Graph.StickerLayerSize = m_ParkedCamoGraph.StickerLayerSize;
        Canvas.Graph.StickerAnimation = m_ParkedCamoGraph.StickerAnimation;
        Canvas.Graph.StickerAnimationOpaque = m_ParkedCamoGraph.StickerAnimationOpaque;
    }

    /// <summary>Puts the parked camo document back on the canvas, with its undo history and contract. No re-dress.</summary>
    private void RestoreCamoGraph()
    {
        if (m_ParkedCamoGraph == null)
            return;

        // an edit of the as-shipped graph is the document's, and goes with it wherever it is kept
        SyncShippedGraph();
        m_FactoryPristine = null;

        Canvas.Graph = m_ParkedCamoGraph;
        Canvas.RestoreUndo(m_ParkedUndo.Undo, m_ParkedUndo.Redo);
        m_Contract = m_ParkedContract;
        m_CamoContractShader = m_Contract != null ? Canvas.Graph.TargetShader : null;
        m_Preview.SetInterpolatorMeanings(m_Contract?.InterpolatorMeanings);
        m_ParkedCamoGraph = null;
        m_ParkedContract = null;
        m_ShownFactoryPreset = null;
        m_ShownFactoryMesh = null;
        SyncReferenceNote();
        TargetShaderBox.Text = Canvas.Graph.TargetShader;
        BuildProperties();
        Canvas.Refresh();
    }

    /// <summary>
    /// The node of the camo DOCUMENT a value edit is meant for. On the canvas's own document that is the
    /// node itself; while the weapon's own graph is shown, an edit on one of its constants is an edit of the
    /// camo (the document comes back on the canvas and the constant of the same name takes the value), and
    /// a node held from before the swap is mapped by name too. Null when the camo has no such constant.
    /// </summary>
    private GraphNode? DocumentNodeFor(GraphNode p_Node)
    {
        // A value typed on one of the object's OTHER materials belongs to that material's graph: it is no
        // reason to take the object out of "as shipped" (that would repaint the whole object).
        if (m_MaterialEditKey != null)
            return Canvas.Graph.Nodes.Contains(p_Node) ? p_Node : null;

        // ⭐ …and one typed on the as-shipped graph is an edit OF it (as shipped is a camo like any other, editable),
        // no longer a reason to move to the basic camo
        if (CamoMode && FactoryGraphShown && Canvas.Graph.Nodes.Contains(p_Node))
            return p_Node;

        LeaveFactoryGraph("a value was typed");

        if (Canvas.Graph.Nodes.Contains(p_Node))
            return p_Node;

        var s_Name = BareExternalName(p_Node);
        var s_Match = Canvas.Graph.Nodes.FirstOrDefault(p_N =>
            p_N.Kind == "ExternalConstant" && BareExternalName(p_N).Equals(s_Name, StringComparison.OrdinalIgnoreCase));
        if (s_Match == null)
            Log($"Camo: the camo's graph has no '{s_Name}' constant — the value has nowhere to go.");

        return s_Match;
    }

    /// <summary>
    /// Puts the preview in or out of the factory look after anything that can change the answer: a weapon
    /// dressed, a native camo picked, a value or pattern set, a sticker placed, the graph edited. Going in
    /// registers the game's own shader for every section the graph would otherwise draw.
    /// </summary>
    private void SyncFactoryLook()
    {
        if (!CamoMode)
            return;

        // ⛔ While the canvas holds one of the object's OTHER materials, the VIEW still follows the camo — that
        // material is only being looked at (ShowMaterialGraph, 2026-09-23) — and only the CANVAS is left alone:
        // it holds that material's graph, which the parts below that put the camo's or the no-camo graph back
        // must not replace. It used to return here outright, back when looking re-aimed the whole preview.
        var s_MaterialOnCanvas = m_MaterialEditKey != null;

        // The picker is the truth, and only the USER moves it (a slider moved earlier in the
        // camo's life bounced "as shipped" to the basic camo on its own, and leaving sticker mode did the
        // same through the form's re-feed). A value typed, a slider moved or a pattern chosen WHILE "as
        // shipped" is selected promotes the picker at that moment (LeaveFactoryGraph); values that merely
        // exist in the document do not — they wait, unseen, until a camo is picked. Stickers never promote.
        var s_Factory = CamoFactoryLook();
        var s_Stickers = StickerMode || DocumentGraph.StickerRegister != null || DocumentGraph.Stickers is { Count: > 0 };
        string? s_StickerShader = null;

        if (s_Factory && m_PreviewMesh is { Length: > 0 })
        {
            // Two rules, both for every weapon (settled over three rounds): the 3D view draws the weapon
            // AS THE GAME SHIPS IT — each own section with the game's bytecode of the shader it wears and the
            // game's default bindings (the F2000's olive lives in its Camo slot) — and the canvas shows the
            // game's NO-CAMO preset graph, the weapon before any camo, never the camo's graph. When the
            // studio names no no-camo preset (plain editor) the worn preset's graph stands in, if any.
            // With stickers the game's bytecode cannot draw the layer, so the body draws with the SAME shader
            // it wears, recompiled with the sticker nodes: the shipped look plus the stickers, what the bake ships.
            RegisterFactoryShaders(null);
            s_StickerShader = s_Stickers ? EnsureStickerFactoryShader() : null;
            m_Preview.FactoryShader = s_StickerShader;

            var s_Preset = s_MaterialOnCanvas ? null : FactoryPresetOf();
            if (s_Preset != null && !s_Preset.Equals(TargetShaderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                ShowFactoryGraph(s_Preset);
            else if (m_ParkedCamoGraph != null && !s_MaterialOnCanvas)
            {
                RestoreCamoGraph();
                if (TargetShaderBox.Text.Trim() is { Length: > 0 } s_Own)
                {
                    m_InFactorySync = true;
                    try
                    {
                        DressCamoWeapon(m_PreviewMesh, s_Own);
                    }
                    finally
                    {
                        m_InFactorySync = false;
                    }
                }
            }
        }
        else if (!s_Factory && m_ParkedCamoGraph != null && !s_MaterialOnCanvas)
        {
            // Something to ship exists again: the camo's graph comes back, dressed for its own preset.
            RestoreCamoGraph();
            if (m_PreviewMesh is { Length: > 0 } s_Mesh && TargetShaderBox.Text.Trim() is { Length: > 0 } s_Target)
            {
                m_InFactorySync = true;
                try
                {
                    DressCamoWeapon(s_Mesh, s_Target);
                }
                finally
                {
                    m_InFactorySync = false;
                }
            }
        }

        // An accessory with a camo on it draws with the accessory preset plus the camo's pattern and numbers
        // (see EnsureAccessoryShader) — the same stand-in mechanism, with the camo instead of the shipped look.
        string? s_AccessoryShader = null;
        if (!s_Factory)
        {
            s_AccessoryShader = EnsureAccessoryShader();
            m_Preview.FactoryShader = s_AccessoryShader;
        }

        // With stickers the shipped shader recompiled with the layer draws the body; only when that could not
        // be built does the graph on the canvas draw it instead (the no-camo graph with the weapon's own art).
        // (The view renders every frame, so the accessory stand-in needs no recompile of the canvas graph.)
        // ⭐ …and an EDITED as-shipped graph draws the body too (as shipped is a camo like any other, editable — it
        // used to be a reference whose edits drew nothing): the graph on the canvas, dressed with the subject's own art for it.
        m_Preview.FactoryLook = (s_Factory && (!s_Stickers || s_StickerShader != null) && !FactoryGraphEdited && !AsShippedDocumentEdited) ||
                                s_AccessoryShader != null;

        // the piece's wear boxes follow the look: they are the camo's numbers, and as shipped there is no camo
        ShowAccessoryValues();

        // which of a soldier's other parts wear their document follows the look too (as shipped, only the edited ones)
        RefreshContextDress();

        if (m_FactoryLookShown == s_Factory)
            return;

        m_FactoryLookShown = s_Factory;
        Log(s_Factory
            ? "Camo: the weapon as it ships — its own shader, textures and numbers, as the game draws it. Pick 'Default " +
              "basic camo' or one of the game's camos, load a pattern, change a value or add a sticker, and the camo shows."
            : "Camo: the camo being authored is on the weapon.");
        SchedulePreview();
    }

    /// <summary>
    /// Registers, as foreign shaders, the game's own bytecode of every preset the loaded weapon's own
    /// sections wear (the NoCamo preset on the body, the camo one where the body already wears it), dressed
    /// with THIS weapon's textures and numbers for THAT shader — the studio's set when it has one, the slot
    /// map's otherwise. Idempotent: a name already registered is left alone; the pick clears them all.
    /// </summary>
    private void RegisterFactoryShaders(string? p_Preset)
    {
        var s_Mesh = m_PreviewMesh;
        if (s_Mesh is not { Length: > 0 })
            return;

        // The no-camo preset when the studio names one (every own section draws with it), else each own
        // section's own shader.
        var s_Shaders = p_Preset != null
            ? new[] { p_Preset }
            : m_Preview.MeshSections.Where(m_Preview.IsTargetSection).Select(p_S => p_S.Shader).ToArray();

        foreach (var s_Shader in s_Shaders
                     .Where(p_S => p_S.Length > 0 && !m_Preview.HasForeignShader(p_S))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var s_Dxbc = Path.Combine(MeshCacheDir, "shaders", $"{Sanitize(s_Shader)}.dxbc");
            var s_MapPath = SlotMapPath(s_Shader);
            if (!File.Exists(s_Dxbc))
            {
                Log($"Camo: no cached shader for '{s_Shader}' — its sections stay neutral until it is fetched.");
                continue;
            }

            try
            {
                var s_Slots = m_CamoSlotsFor?.Invoke(s_Mesh, s_Shader, null);
                var s_Textures = s_Slots != null
                    ? DecodeCachedTextures(s_Slots, TexCacheDir)
                    : LoadForeignTexturesFromCache(s_MapPath, TexCacheDir, s_Mesh);
                var s_Values = m_CamoValuesFor?.Invoke(s_Mesh, s_Shader, null)
                               ?? LoadForeignMaterialValues(s_MapPath, s_Mesh);

                var s_Error = m_Preview.RegisterForeignShader(s_Shader, File.ReadAllBytes(s_Dxbc), s_Textures,
                    s_Values, SamplersForShader(s_Shader));
                Log(s_Error == null
                    ? $"Camo: '{s_Shader.Split('/')[^1]}' as shipped — {s_Textures.Count} texture(s), " +
                      $"{s_Values?.Count ?? 0} value(s){(s_Slots == null ? " (slot map's set)" : "")}; " +
                      $"interpolators {m_Preview.ForeignInterpolatorsOf(s_Shader)}; " +
                      $"samplers {m_Preview.ForeignSamplersOf(s_Shader)}."
                    : $"Camo: '{s_Shader}': {s_Error} — its sections stay neutral.");
            }
            catch (Exception s_Exception)
            {
                Log($"Camo: '{s_Shader}': {s_Exception.Message} — its sections stay neutral.");
            }
        }
    }

    /// <summary>
    /// The section shaders of the loaded mesh that the camo being authored OWNS besides its target — every
    /// weapon preset the body wears, as the studio defines "weapon preset"; the window has no opinion of its
    /// own on shader names. Whatever preset tab is on the canvas, the body follows the graph: a body left to
    /// the game's own bytecode is a body that ignores every edit.
    /// </summary>
    private string[] CamoAliasesFor(string? p_Shader)
    {
        if (p_Shader == null || m_CamoOwnsSection == null)
            return Array.Empty<string>();

        // ⛔ A DOCUMENT THAT IS NOT A CAMO OWNS NOTHING BESIDES ITSELF. The F-35B opens as shipped on its own
        // f35b_main, which takes no camo; counted by the family test below it read as a WEAPON target (no
        // "vehiclepreset" in its name), so an M240 picked next adopted it as its own and stayed on the
        // F-35B's graph, and read by its path it would have adopted every vehicle's bodywork instead
        // (measured 2026-09-23 with --switchtest). Neither is drawable with that graph.
        if (!m_CamoOwnsSection(p_Shader))
            return Array.Empty<string>();

        // ⛔⛔ AND OF THE SAME FAMILY AS THE TARGET. "Takes the camo" is not "is drawn by THIS camo": a
        // vehicle can carry a bolted-on WEAPON whose material wears a weaponpreset (the BTR-90's remote gun),
        // and that preset takes camo in its own right — but the shader authored here is compiled against the
        // VEHICLE preset's contract (other interpolators, other registers), so drawing the gun with it feeds
        // it the wrong everything. Measured 2026-09-22: the BTR-90's remote gun came out MAGENTA. A section of
        // the other family is foreign, which is what it is in the game too.
        var s_TargetIsVehicle = p_Shader.Contains("vehiclepreset", StringComparison.OrdinalIgnoreCase);

        return m_Preview.MeshSections
            .Select(p_S => p_S.Shader)
            .Where(p_S => p_S.Length > 0 && !p_S.Equals(p_Shader, StringComparison.OrdinalIgnoreCase) &&
                          m_CamoOwnsSection(p_S) &&
                          p_S.Contains("vehiclepreset", StringComparison.OrdinalIgnoreCase) == s_TargetIsVehicle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Whether a tab aimed at <paramref name="p_TabTarget"/> can take the subject on screen, whose own preset
    /// is <paramref name="p_SubjectShader"/>: every section that preset would draw must be one the tab's
    /// preset draws too.
    ///
    /// ⛔⛔ WHY (switching between vehicles or weapons sometimes left the selected mesh unshaded and grey,
    /// an M240 flat grey). The pick sorts the sections into own and
    /// foreign by the SUBJECT's preset and fetches only the foreign ones; then the tab's preset re-sorted
    /// them. Where the two disagree, a section was own for the fetch and foreign for the draw — never
    /// fetched, so drawn with the grey stand-in. Measured with --switchtest: the LAV-25 then the M240 (the
    /// tab on vehiclepreset_mud, which cannot draw a weaponpreset), the reverse, and the F-35B after any
    /// vehicle (its f35b_main takes no camo, so no vehicle preset draws it). Weapon after weapon and vehicle
    /// after vehicle agree, and those picks keep the tab as before.
    /// </summary>
    private bool TabDrawsSubject(string p_TabTarget, string p_SubjectShader)
    {
        // ⛔ A THIRD-PERSON TAB DOES NOT TAKE A FIRST-PERSON BODY. A tab aimed at the preset attachments take a
        // camo with (weaponpreset3p — a fresh tab picked on an attachment, or New with one on screen) counted
        // every weapon preset as its own, so the M416 picked there was dressed with the 3P graph and its
        // as-shipped canvas showed weaponpresetnocamo3p instead of its body's shadownocamofp (it previewed the
        // wrong materials and shaders; --switchtest "AKS74u,acc:9,new,M416"). The other
        // way round is by design: a first-person camo shows its attachments through the 3P stand-in. The view
        // is told by the preset's name, as the studio itself does it (NoCamoPresetOf, BindingsOf).
        static bool ThirdPerson(string p_Shader) =>
            p_Shader.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase) &&
            p_Shader.Contains("3p", StringComparison.OrdinalIgnoreCase);
        if (ThirdPerson(p_TabTarget) && !ThirdPerson(p_SubjectShader))
            return false;

        var s_TabAliases = CamoAliasesFor(p_TabTarget);
        var s_OwnAliases = CamoAliasesFor(p_SubjectShader);
        return m_Preview.MeshSections
            .Select(p_S => p_S.Shader)
            .Where(p_S => p_S.Length > 0 && (p_S.Equals(p_SubjectShader, StringComparison.OrdinalIgnoreCase) ||
                                             s_OwnAliases.Contains(p_S, StringComparer.OrdinalIgnoreCase)))
            .All(p_S => p_S.Equals(p_TabTarget, StringComparison.OrdinalIgnoreCase) ||
                        s_TabAliases.Contains(p_S, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Another open tab that can take the subject on screen (see <see cref="TabDrawsSubject"/>) — the one aimed
    /// at its own preset first, else the first that draws it. -1 when none can.
    /// </summary>
    /// <summary>The first ORIGINAL tab that can draw a subject (the game's originals of its shader), the active one included; -1 when none.</summary>
    private int OriginalTabThatDraws(string p_SubjectShader)
    {
        for (var i = 0; i < m_Tabs.Count; i++)
            if (m_Tabs[i].Original && (i == m_ActiveTab ? CamoTarget : m_Tabs[i].Graph.TargetShader) is { Length: > 0 } s_Target &&
                (s_Target.Equals(p_SubjectShader, StringComparison.OrdinalIgnoreCase) || TabDrawsSubject(s_Target, p_SubjectShader)))
                return i;

        return -1;
    }

    /// <summary>The first tab, not the active one, that can draw a subject and holds a CHANGED document of it (TabHoldsChangesOf); -1 when none.</summary>
    private int TabWithChangesOf(string p_Key, string p_SubjectShader)
    {
        for (var i = 0; i < m_Tabs.Count; i++)
            if (i != m_ActiveTab && m_Tabs[i].Graph.TargetShader is { Length: > 0 } s_Target &&
                (s_Target.Equals(p_SubjectShader, StringComparison.OrdinalIgnoreCase) || TabDrawsSubject(s_Target, p_SubjectShader)) &&
                TabHoldsChangesOf(i, p_Key))
                return i;

        return -1;
    }

    /// <summary>
    /// Whether a tab of changes holds a CHANGED document of a subject: the one it shows, or the one kept for it under the camo it is on
    /// (NativeHeldFor) — changed against what the subject starts from (its own starting document, else the game's translation), the rule
    /// ChangedDocumentsUnder reads. An original tab holds none.
    /// </summary>
    private bool TabHoldsChangesOf(int p_Index, string p_Key)
    {
        if (p_Index < 0 || p_Index >= m_Tabs.Count || m_Tabs[p_Index].Original)
            return false;

        var s_Tab = m_Tabs[p_Index];
        var s_Active = p_Index == m_ActiveTab;
        var s_Subject = s_Active ? m_DocumentSubject : s_Tab.DocumentSubject;
        var s_Document = string.Equals(s_Subject, p_Key, StringComparison.OrdinalIgnoreCase)
            ? s_Active ? DocumentGraph : s_Tab.Graph
            : s_Tab.Set(NativeHeldFor(s_Tab, p_Key)).Subjects.TryGetValue(p_Key, out var s_Own) ? s_Own.Graph : null;
        if (s_Document == null || PristineOf(s_Document.TargetShader) is not { } s_Pristine)
            return false;

        // (…and, as the bake reads it, a part on one of the game's looks says something even drawn as the translation — ChangedDocumentsUnder)
        if (Environment.GetEnvironmentVariable("CAMO_EXTRAS_UNSEEN_OLD") != "1" && NativePerSubject(p_Key) &&
            s_Document.NativeCamo is { } s_Camo && s_Camo != m_CamoBasicKey)
            return true;

        return SubjectChanged(s_Document, SubjectPresetIn(s_Tab, p_Key, NativeKeyOf(s_Document)) ?? s_Pristine);
    }

    private int TabThatDrawsSubject(string p_SubjectShader)
    {
        // ⭐ an ORIGINAL tab sends a subject of another family to the originals of that family, and a tab of changes to one of changes
        // (EditorTab.Original): browsing the game's camos must never land in a tab where something was changed, nor the other way round
        var s_Original = m_ActiveTab >= 0 && m_ActiveTab < m_Tabs.Count && m_Tabs[m_ActiveTab].Original;
        foreach (var s_Same in new[] { true, false })
        {
            bool Kind(int p_Index) => !s_Same || m_Tabs[p_Index].Original == s_Original;

            for (var i = 0; i < m_Tabs.Count; i++)
                if (i != m_ActiveTab && Kind(i) &&
                    m_Tabs[i].Graph.TargetShader.Equals(p_SubjectShader, StringComparison.OrdinalIgnoreCase))
                    return i;

            for (var i = 0; i < m_Tabs.Count; i++)
                if (i != m_ActiveTab && Kind(i) && m_Tabs[i].Graph.TargetShader is { Length: > 0 } s_Target &&
                    TabDrawsSubject(s_Target, p_SubjectShader))
                    return i;

            // an original tab never borrows a tab of changes: a subject with no original tab of its own gets one (the caller opens it)
            if (s_Original)
                return -1;
        }

        return -1;
    }

    /// <summary>
    /// Puts on screen the family whose picker lists this subject (an attachment is listed by its weapon), when
    /// it is not the one showing. A tab opened on a vehicle and clicked while the weapons are listed has to
    /// bring its vehicle back; with the weapons listed, the vehicle is not there to select and the weapon on
    /// screen was re-dressed for the vehicle's preset instead — the same grey as a pick across families.
    /// </summary>
    private void ShowFamilyListing(string p_Mesh)
    {
        if (MeshList.Items.OfType<CamoWeapon>().Any(p_W => p_W.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)))
            return;

        var s_Listed = m_CamoWeaponOfAccessory?.Invoke(p_Mesh) ?? p_Mesh;

        // ⭐ …or the same family on its other side: a tab on a RU soldier clicked while the US ones are listed.
        if (m_CamoFamily is { Groups.Count: > 0 } s_Current &&
            s_Current.Subjects.FirstOrDefault(p_S => p_S.Mesh.Equals(s_Listed, StringComparison.OrdinalIgnoreCase)) is { } s_Sided)
        {
            m_CamoGroups[s_Current.Key] = s_Sided.Group;
            SyncGroupTabs();
            FillCamoSubjects(s_Current);
            RefillGroupNatives(s_Current);
            return;
        }

        if (m_CamoFamilies.FirstOrDefault(p_F => p_F.Unavailable == null && !ReferenceEquals(p_F, m_CamoFamily) &&
                p_F.Subjects.Any(p_S => p_S.Mesh.Equals(s_Listed, StringComparison.OrdinalIgnoreCase))) is { } s_Family)
        {
            // the side it is listed under comes with it
            if (s_Family.Groups is { Count: > 0 } &&
                s_Family.Subjects.FirstOrDefault(p_S => p_S.Mesh.Equals(s_Listed, StringComparison.OrdinalIgnoreCase)) is { Group.Length: > 0 } s_Subject)
                m_CamoGroups[s_Family.Key] = s_Subject.Group;

            ShowCamoFamily(s_Family.Key);
        }
    }

    /// <summary>
    /// Selects a weapon in the picker by its mesh — the same entry the user's click takes, so everything a
    /// pick does (load, foreign sections, dressing for the active tab) follows. False when the picker does
    /// not list it. A weapon already selected is re-selected, because the combo raises nothing otherwise.
    /// </summary>
    internal bool SelectCamoWeapon(string p_Mesh, string? p_Weapon = null, string? p_Subject = null)
    {
        var s_Item = MeshList.Items.OfType<CamoWeapon>()
            .FirstOrDefault(p_W => p_W.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));
        if (s_Item == null)
            return SelectCamoAccessory(p_Mesh, p_Weapon, p_Subject);

        if (ReferenceEquals(MeshList.SelectedItem, s_Item))
            MeshList.SelectedItem = null;

        MeshList.SelectedItem = s_Item;
        return true;
    }

    /// <summary>
    /// The accessory counterpart: an accessory mesh is reached through ITS weapon — the weapon is selected
    /// silently (no load), its rows fill the accessory picker, and the accessory's entry is selected, which
    /// takes the same path a click takes. False when no weapon lists that mesh.
    /// </summary>
    private bool SelectCamoAccessory(string p_Mesh, string? p_Weapon = null, string? p_Subject = null)
    {
        if (m_CamoAccessoriesOf == null)
            return false;

        // Many accessories are shared (one ACOG serves thirty rifles); a tab coming back to one comes back
        // under the weapon IT remembers (p_Weapon), else the one in the picker, and only when neither lists it
        // under the first weapon that does. ⛔ The picker alone was not enough: after a family switch it is
        // EMPTY, and the AKS74u's M145 came back under the A91.
        bool Lists(string p_Candidate) =>
            m_CamoAccessoriesOf(p_Candidate).Any(p_A => p_A.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));

        // ⭐ A PART OF A SUBJECT that is no attachment (a soldier's head or legs, 2026-09-28) has no weapon of its own to answer with: it
        // comes back only under the subject the tab remembers, or the one in the picker.
        var s_WeaponMesh = m_CamoWeaponOfAccessory?.Invoke(p_Mesh) ??
                           (p_Weapon is { Length: > 0 } && Lists(p_Weapon) ? p_Weapon
                               : MeshList.SelectedItem is CamoWeapon s_Picked && Lists(s_Picked.Mesh) ? s_Picked.Mesh
                               : null);
        if (s_WeaponMesh == null)
            return false;

        if (p_Weapon is { Length: > 0 } && Lists(p_Weapon))
            s_WeaponMesh = p_Weapon;
        else if (MeshList.SelectedItem is CamoWeapon s_Current && Lists(s_Current.Mesh))
            s_WeaponMesh = s_Current.Mesh;

        var s_Weapon = MeshList.Items.OfType<CamoWeapon>()
            .FirstOrDefault(p_W => p_W.Mesh.Equals(s_WeaponMesh, StringComparison.OrdinalIgnoreCase));
        if (s_Weapon == null)
            return false;

        m_MeshListFilling = true;
        try
        {
            MeshList.SelectedItem = s_Weapon;
        }
        finally
        {
            m_MeshListFilling = false;
        }

        FillAccessoryList(s_WeaponMesh);
        // ⛔ one mesh, two entries (review 2026-09-29, B5): a soldier's first-person model is his upper body's sleeves AND his lower body's
        // trousers — a tab of the trousers came back on the first entry of that mesh, the sleeves, and the part on screen was not its own.
        // The entry of the tab's own part first (EntryKeyOf); CAMO_ENTRY_BY_SUBJECT_OLD=1 = the first entry, as before.
        var s_Entries = AccessoryList.Items.OfType<CamoWeapon>().Where(p_A => p_A.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)).ToList();
        var s_Item = (p_Subject != null && Environment.GetEnvironmentVariable("CAMO_ENTRY_BY_SUBJECT_OLD") != "1"
                         ? s_Entries.FirstOrDefault(p_A => string.Equals(EntryKeyOf(p_A), p_Subject, StringComparison.OrdinalIgnoreCase))
                         : null) ??
                     s_Entries.FirstOrDefault();
        if (s_Item == null)
            return false;

        if (ReferenceEquals(AccessoryList.SelectedItem, s_Item))
            AccessoryList.SelectedItem = null;

        AccessoryList.SelectedItem = s_Item;
        return true;
    }

    /// <summary>
    /// In camo mode, the thing to do wherever the editor would reload the thumbnails from the slot map: put
    /// the picked weapon's own art back for whatever preset is now on the canvas. Nothing to do before a
    /// weapon is picked or on a tab with no preset.
    /// </summary>
    private void RedressCamoWeapon()
    {
        if (!CamoMode || m_PreviewMesh is not { Length: > 0 } s_Mesh)
            return;

        // The camo's preset even while another material's graph is on the canvas (a native camo picked then).
        if (CamoTarget is { Length: > 0 } s_Shader)
            DressCamoWeapon(s_Mesh, s_Shader);
    }

    /// <summary>
    /// Fetches the REAL shaders for the loaded mesh's foreign sections — each one's own game bytecode
    /// (chosen permutation) plus its texture set — so the whole object previews the way the game draws
    /// it. Everything lands in the caches (meshcache\shaders + the shared texcache), so only the first
    /// object that needs a given shader pays its mounts.
    /// </summary>
    /// <summary>What the last section-art pass was built for, so an unchanged object is not re-registered.</summary>
    private string? m_SectionArtKey;

    /// <summary>
    /// Registers the GAME's own shader once per MATERIAL for every shader that more than one material of this
    /// object wears, and points those sections at their own.
    ///
    /// ⛔ THE PROBLEM: the end result must be every material looking right, whatever happens. A foreign
    /// registration is keyed by shader NAME, and a name is not a material: the
    /// LAV-25's hull and its ATGM launchers are both vehiclepreset_mud, so whichever registered first lent the
    /// other its textures — the hull drew in the launchers' art, which is what "as shipped" showed at load.
    /// The camo path already had this (<see cref="ApplyMaterialDress"/>); this is the same for the shipped art.
    ///
    /// Only for the shaders that ARE shared: one material on a shader is already named correctly by the
    /// by-name registration, and registering every section twice would cost a texture decode per pick on
    /// every object for nothing.
    /// </summary>
    private void RegisterSectionArt()
    {
        var s_Mesh = m_PreviewMesh;
        if (!CamoMode || s_Mesh is not { Length: > 0 } || m_CamoSectionMaterialsOf == null ||
            m_CamoSlotsForMaterial == null)
        {
            m_Preview.SectionArt.Clear();
            m_SectionArtKey = null;
            return;
        }

        var s_Sections = m_Preview.MeshSections;
        var s_Known = m_CamoSectionMaterialsOf(s_Mesh);

        // Every section with an identity — (preview index, mesh, material id) — and whether it is CONTEXT (the rest of a soldier).
        // The subject's own only when its list LINES UP with its sections, the guard the dressing uses (shorter is normal: anything
        // drawn with the object that is not in it, a belt, has no entry and no id); a context section by its own mesh's list, at its
        // own index, when that one agrees on the shader.
        var s_Identified = new List<(int Index, string Mesh, int MaterialId, bool Context)>();
        if (s_Known.Count <= s_Sections.Count &&
            s_Known.Zip(s_Sections).All(p_P => p_P.Second.Shader.Equals(p_P.First.Shader, StringComparison.OrdinalIgnoreCase)))
            for (var i = 0; i < s_Known.Count; i++)
                // (a section of this mesh made context — another part's material, MarkContext — is identified below, as context)
                if (s_Sections[i].Context.Length == 0)
                    s_Identified.Add((i, s_Mesh, s_Known[i].MaterialId, false));

        var s_ContextLists = new Dictionary<string, IReadOnlyList<(int MaterialId, string Shader)>>(StringComparer.OrdinalIgnoreCase);
        var s_ContextUnknown = 0;
        for (var i = 0; i < s_Sections.Count; i++)
        {
            if (s_Sections[i].Context is not { Length: > 0 } s_Context)
                continue;

            if (!s_ContextLists.TryGetValue(s_Context, out var s_List))
                s_ContextLists[s_Context] = s_List = m_CamoSectionMaterialsOf(s_Context);

            var s_At = s_Sections[i].ContextSection;
            if (s_At < s_List.Count && s_List[s_At].Shader.Equals(s_Sections[i].Shader, StringComparison.OrdinalIgnoreCase))
                s_Identified.Add((i, s_Context, s_List[s_At].MaterialId, true));
            else
                s_ContextUnknown++;
        }

        if (s_Identified.Count == 0)
        {
            m_Preview.SectionArt.Clear();
            m_SectionArtKey = null;
            return;
        }

        // A shader is SHARED when more than one material wears it — of this mesh, or of this mesh and the ones around it (a soldier's
        // torso, helmet and legs are all CharacterRoot). Without context this is exactly the set it always was.
        var s_Shared = s_Identified
            .Where(p_S => p_S.MaterialId >= 0 && s_Sections[p_S.Index].Shader.Length > 0)
            .GroupBy(p_S => s_Sections[p_S.Index].Shader, StringComparer.OrdinalIgnoreCase)
            .Where(p_G => p_G.Select(p_S => (p_S.Mesh.ToLowerInvariant(), p_S.MaterialId)).Distinct().Count() > 1)
            .Select(p_G => p_G.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var s_Key = $"{s_Mesh}|{m_Preview.ForeignGeneration}|" +
                    string.Join(",", s_Shared.OrderBy(p_S => p_S, StringComparer.Ordinal)) + "|" +
                    string.Join(",", s_ContextLists.Keys.OrderBy(p_M => p_M, StringComparer.OrdinalIgnoreCase));
        if (string.Equals(m_SectionArtKey, s_Key, StringComparison.Ordinal))
            return;

        var s_Art = new Dictionary<int, string>();
        var s_Failed = 0;
        var s_ContextDrawn = 0;
        foreach (var (s_Index, s_Of, s_Id, s_IsContext) in s_Identified)
        {
            var s_Shader = s_Sections[s_Index].Shader;

            // ⛔ A CONTEXT SECTION IS REGISTERED WHATEVER IT WEARS: its by-name fallback is the SUBJECT's art for that shader (the
            // torso's textures on the helmet), or none at all when the subject does not wear it.
            if (s_Id < 0 || s_Shader.Length == 0 || (!s_IsContext && !s_Shared.Contains(s_Shader)))
                continue;

            var s_Name = s_IsContext ? $"{s_Shader}#art{s_Of}#{s_Id}" : $"{s_Shader}#art{s_Id}";
            try
            {
                if (!m_Preview.HasForeignShader(s_Name))
                {
                    // The GAME's art: no native camo of ours rides on the shipped look.
                    var s_Dxbc = Path.Combine(MeshCacheDir, "shaders", $"{Sanitize(s_Shader)}.dxbc");
                    var s_Slots = m_CamoSlotsForMaterial(s_Of, s_Shader, null, s_Id);
                    if (!File.Exists(s_Dxbc) || s_Slots is not { Count: > 0 })
                    {
                        if (s_IsContext)
                            s_Failed++;
                        continue;
                    }

                    var s_Error = m_Preview.RegisterForeignShader(s_Name, File.ReadAllBytes(s_Dxbc),
                        DecodeCachedTextures(s_Slots, TexCacheDir),
                        m_CamoValuesForMaterial?.Invoke(s_Of, s_Shader, null, s_Id),
                        SamplersForShader(s_Shader));

                    if (s_Error != null)
                    {
                        s_Failed++;
                        continue;
                    }
                }

                s_Art[s_Index] = s_Name;
                if (s_IsContext)
                    s_ContextDrawn++;
            }
            catch (Exception s_Exception)
            {
                s_Failed++;
                Log($"Camo: material #{s_Id} of '{s_Shader.Split('/')[^1]}': {s_Exception.Message} — it keeps the shared art.");
            }
        }

        m_Preview.SectionArt = s_Art;
        m_SectionArtKey = s_Key;

        if (s_Art.Count - s_ContextDrawn > 0 || (s_Failed > 0 && s_ContextLists.Count == 0))
            Log($"Camo: {s_Art.Count - s_ContextDrawn} section(s) of {s_Shared.Count} shader(s) worn by more than one material " +
                $"draw with their OWN material's art{(s_Failed > 0 ? $" ({s_Failed} could not be registered)" : "")}.");

        // The rest of the character, said per mesh: what it is, and how many of its sections draw with their own art.
        if (s_ContextLists.Count > 0)
            Log($"Camo: {s_ContextDrawn} section(s) of {s_ContextLists.Count} mesh(es) around it drawn as they ship, with their own art " +
                $"({string.Join(", ", s_ContextLists.Keys.Select(p_M => p_M.Split('/')[^1]))})" +
                (s_Failed > 0 ? $"; {s_Failed} could not be registered" : "") +
                (s_ContextUnknown > 0 ? $"; {s_ContextUnknown} have no material id (no dump log covers their mesh)" : "") + ".");
    }

    private async Task RegisterForeignShadersAsync(string? p_Target = null)
    {
        // The target can be named by the caller: in camo mode the box still says the PREVIOUS preset while a
        // weapon is being picked. Sections wearing a preset the target replaces are its own, not foreign.
        var s_Target = (p_Target ?? TargetShaderBox.Text).Trim().Replace('\\', '/');
        var s_Aliases = m_Preview.MeshTargetAliases;
        var s_Foreign = m_Preview.MeshSections
            .Select(p_S => p_S.Shader)
            .Where(p_S => p_S.Length > 0 && !p_S.Equals(s_Target, StringComparison.OrdinalIgnoreCase) &&
                          !s_Aliases.Contains(p_S, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p_S => !m_Preview.HasForeignShader(p_S))
            .ToList();

        if (s_Foreign.Count == 0)
            return;

        var s_GamePath = GamePath;
        var s_Repl = FindRimeRepl();
        if (s_Repl == null || s_GamePath.Length == 0)
        {
            Log("Foreign sections stay neutral: RimeREPL or the BF3 path is missing.");
            return;
        }

        var s_ShaderFolder = Path.Combine(MeshCacheDir, "shaders");
        Directory.CreateDirectory(s_ShaderFolder);
        ShapeMesh.IsEnabled = false;

        try
        {
            for (var i = 0; i < s_Foreign.Count; i++)
            {
                var s_Shader = s_Foreign[i];
                var s_Dxbc = Path.Combine(s_ShaderFolder, $"{Sanitize(s_Shader)}.dxbc");
                var s_MapPath = SlotMapPath(s_Shader);
                var s_NeedsMounts = !File.Exists(s_Dxbc) || !File.Exists(s_MapPath);

                if (s_NeedsMounts)
                {
                    Log($"Fetching section shader {i + 1}/{s_Foreign.Count}: '{s_Shader}' " +
                        "(mounting the game; first time only)…");
                    MeshProgress.Value = 0;
                    MeshProgress.Visibility = Visibility.Visible;
                }

                var s_Timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200),
                };
                s_Timer.Tick += (_, _) => MeshProgress.Value = Math.Min(95.0, MeshProgress.Value + 95.0 / 300.0);
                if (s_NeedsMounts)
                    s_Timer.Start();

                try
                {
                    var s_ShaderDb = ResolveShaderDb(s_Shader);
                    var s_Error = await Task.Run(() => FetchForeignShaderCore(
                        s_Repl, s_GamePath, s_ShaderDb, s_Shader, s_Dxbc, s_MapPath));

                    if (s_Error != null)
                    {
                        Log($"  '{s_Shader}': {s_Error} — its sections stay neutral.");
                        continue;
                    }
                }
                finally
                {
                    s_Timer.Stop();
                    MeshProgress.Visibility = Visibility.Collapsed;
                }

                var s_Register = RegisterForeignFromCache(s_Shader, s_Dxbc, s_MapPath);
                Log(s_Register == null
                    ? $"  '{s_Shader}': real shader + art loaded for its sections."
                    : $"  '{s_Shader}': {s_Register} — its sections stay neutral.");
            }
        }
        finally
        {
            ShapeMesh.IsEnabled = true;
            SchedulePreview();
        }
    }

    /// <summary>The mount-side half: the chosen permutation's bytecode plus the texture cache, idempotent.</summary>
    /// <summary>
    /// Pulls one section shader into the caches AHEAD OF TIME, so picking an object later costs nothing.
    ///
    /// Exists because the caches are not one thing: meshes, textures and SHADERS are cached separately, and
    /// having the first two is no reason to believe the third is there. A tool that dumps meshes and
    /// textures up front and then mounts the game anyway, the first time the user clicks, looks broken.
    /// </summary>
    internal static string? PrefetchSectionShader(string p_Repl, string p_GamePath, string p_ShaderDb,
        string p_Shader, string p_MeshCacheDir, string p_TexCacheDir)
    {
        var s_Folder = Path.Combine(p_MeshCacheDir, "shaders");
        Directory.CreateDirectory(s_Folder);
        Directory.CreateDirectory(p_TexCacheDir);

        return FetchForeignShaderCore(p_Repl, p_GamePath, p_ShaderDb, p_Shader,
            Path.Combine(s_Folder, $"{Sanitize(p_Shader)}.dxbc"),
            Path.Combine(p_TexCacheDir, SlotMapFileName(p_Shader)));
    }

    internal static string? FetchForeignShaderCore(string p_Repl, string p_GamePath, string p_ShaderDb,
        string p_Shader, string p_DxbcPath, string p_MapPath)
    {
        if (!File.Exists(p_DxbcPath))
        {
            var s_WorkDir = Path.Combine(Path.GetDirectoryName(p_DxbcPath)!, Sanitize(p_Shader) + "_extract");
            var s_Extract = Path.Combine(s_WorkDir, "perms");

            // (one mount of its own, or its part of a batch's: MainWindow.Batch)
            RunGameCommands(p_Repl, p_GamePath, Path.Combine(s_WorkDir, "extract.rime"),
                $"extract_shader_dxbc {p_ShaderDb} \"{p_Shader}\" \"{s_Extract}\"\n", () =>
                {
                    Directory.CreateDirectory(s_WorkDir);
                    if (Directory.Exists(s_Extract))
                        try { Directory.Delete(s_Extract, true); } catch { }
                });

            var s_Folder = s_Extract;
            var s_IndexPath = Path.Combine(s_Extract, "index.txt");
            if (File.Exists(s_IndexPath))
            {
                var s_Row = File.ReadAllLines(s_IndexPath)
                    .Select(p_L => p_L.Split('\t'))
                    .Where(p_P => p_P.Length >= 2)
                    .FirstOrDefault(p_P => p_P[1].Equals(p_Shader, StringComparison.OrdinalIgnoreCase));
                if (s_Row == null)
                    return "the game yielded no exact match for this shader";

                s_Folder = Path.Combine(s_Extract, s_Row[0]);
            }

            var s_Candidates = Directory.Exists(s_Folder)
                ? new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").OrderByDescending(p_F => p_F.Length).ToList()
                : new List<FileInfo>();
            if (s_Candidates.Count == 0)
                return "no pixel shader came out of the game";

            // ⛔ the SAME pick the translation makes (TranslateCore), or the contract describes another permutation than the graph
            var s_Chosen = ShaderIndex.ChooseGBufferPermutation(s_Candidates, new List<string>(), p_Shader);
            File.Copy(s_Chosen.FullName, p_DxbcPath, true);
            // (which permutation it is, beside it: RetireStaleTranslation keeps a contract stamped with the one chosen now)
            File.WriteAllText(p_DxbcPath + ".from", s_Chosen.Name);
            try { Directory.Delete(s_WorkDir, true); } catch { }
        }

        if (!File.Exists(p_MapPath))
        {
            Emit.ShaderContract? s_Contract = null;
            try { s_Contract = Emit.ShaderContract.Detect(File.ReadAllBytes(p_DxbcPath)); }
            catch { }

            var s_CacheDir = Path.GetDirectoryName(p_MapPath)!;
            LoadTexturesCore(p_Repl, p_GamePath, p_ShaderDb, p_Shader, s_CacheDir, p_MapPath, s_Contract);
            if (!File.Exists(p_MapPath))
                return "its texture map could not be built";
        }

        return null;
    }

    /// <summary>
    /// The cached art of one shader's slot map, decoded to pixel blocks the preview can take. A SHARED
    /// shader's sets are keyed by (mesh, variation) — the default set belongs to whichever mesh sorted
    /// first, so the set of the MESH BEING PREVIEWED is preferred when one exists (the jet preset's
    /// default set was another helicopter's art entirely).
    /// </summary>
    internal static List<(int Slot, uint[] Pixels, int Width, int Height, bool Srgb)> LoadForeignTexturesFromCache(
        string p_MapPath, string p_TexCacheDir, string? p_PreferMesh = null)
    {
        var s_Textures = new List<(int Slot, uint[] Pixels, int Width, int Height, bool Srgb)>();
        var s_Slots = ReadSlotMap(p_MapPath);

        if (p_PreferMesh is { Length: > 0 } &&
            ReadSlotMapFull(p_MapPath)?.Variations is { } s_Sets)
        {
            var s_MeshSet = s_Sets
                .Where(p_S => p_S.Key.StartsWith(p_PreferMesh + "|", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(p_S.Value.Mesh, p_PreferMesh, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p_S => p_S.Key.EndsWith("|0") || p_S.Value.Name == "(base)" ? 0 : 1)
                .Select(p_S => p_S.Value.Slots)
                .FirstOrDefault(p_S => p_S is { Count: > 0 });

            if (s_MeshSet != null)
                s_Slots = s_MeshSet;
        }

        return DecodeCachedTextures(s_Slots ?? new Dictionary<string, string>(), p_TexCacheDir);
    }

    /// <summary>
    /// Decodes a register -> game texture name set from the thumbnail cache into the blocks a foreign
    /// registration takes, each with the game's own sRGB flag for that texture. A name with no thumbnail is
    /// skipped: that register samples nothing, which is how a missing texture has always shown.
    /// </summary>
    internal static List<(int Slot, uint[] Pixels, int Width, int Height, bool Srgb)> DecodeCachedTextures(
        IReadOnlyDictionary<string, string> p_Slots, string p_TexCacheDir)
    {
        var s_Textures = new List<(int Slot, uint[] Pixels, int Width, int Height, bool Srgb)>();

        foreach (var (s_Register, s_Name) in p_Slots)
        {
            var s_Png = Path.Combine(p_TexCacheDir, $"{Sanitize(s_Name)}.png");
            if (!int.TryParse(s_Register, out var s_Slot) || !File.Exists(s_Png))
                continue;

            var s_Bitmap = new BitmapImage();
            s_Bitmap.BeginInit();
            s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            s_Bitmap.UriSource = new Uri(s_Png);
            s_Bitmap.EndInit();

            var s_Converted = new FormatConvertedBitmap(s_Bitmap, PixelFormats.Bgra32, null, 0);
            var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);

            // The game's flag for that texture, from the .dds beside the thumbnail (see TextureIsSrgb).
            var s_Srgb = DdsImage.SrgbOf(Path.Combine(p_TexCacheDir, $"{Sanitize(s_Name)}.dds"), s_Name).Srgb;
            s_Textures.Add((s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight, s_Srgb));
        }

        return s_Textures;
    }

    /// <summary>
    /// The material-instance VECTOR values (camo tint, tiling, wear…) of the set the mesh being previewed
    /// uses, so a foreign shader's external constants carry the level's own numbers instead of defaults.
    /// Same set preference as <see cref="LoadForeignTexturesFromCache"/>; the map's first set (the default
    /// one) stands in when the mesh has no set of its own.
    /// </summary>
    internal static Dictionary<string, string>? LoadForeignMaterialValues(
        string p_MapPath, string? p_PreferMesh = null)
    {
        if (ReadSlotMapFull(p_MapPath) is not { } s_Map)
            return null;

        // The engine's layering: the shaderdb default for every value parameter, overwritten by whatever the
        // level's material instance names for the set actually being previewed.
        var s_Result = s_Map.ExternalDefaults is { Count: > 0 } s_Defaults
            ? new Dictionary<string, string>(s_Defaults, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (s_Map.Variations is { Count: > 0 } s_Sets)
        {
            var s_Chosen = p_PreferMesh is { Length: > 0 }
                ? s_Sets
                    .Where(p_S => p_S.Key.StartsWith(p_PreferMesh + "|", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(p_S.Value.Mesh, p_PreferMesh, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p_S => p_S.Key.EndsWith("|0") || p_S.Value.Name == "(base)" ? 0 : 1)
                    .Select(p_S => p_S.Value)
                    .FirstOrDefault(p_S => p_S.Values is { Count: > 0 })
                : null;

            foreach (var (s_Param, s_Value) in (s_Chosen ?? s_Sets.Values.First()).Values ??
                                               new Dictionary<string, string>())
                s_Result[s_Param] = s_Value;
        }

        return s_Result.Count > 0 ? s_Result : null;
    }

    /// <summary>The in-process half: decode the cached art and hand shader + textures to the preview.</summary>
    private string? RegisterForeignFromCache(string p_Shader, string p_DxbcPath, string p_MapPath)
    {
        try
        {
            // ⛔⛔ THE MATERIAL SCAN FIRST, THE SLOT MAP ONLY AS FALLBACK — the order the FACTORY path has
            // always used, and the foreign path did not. The slot map is keyed by SHADER and carries a base
            // set belonging to whichever object built it, with its registers paired by usage class; the scan
            // says what THIS mesh binds to THIS shader. On the LAV-25's lamps: right for one second, then
            // tinted pink — the pink is the map's pairing (a normal map
            // landing in a colour slot renders violet, the failure PairStreamables warns about), and the
            // second of correctness is the material's own art, which the edit path briefly puts there.
            var s_Mesh = m_PreviewMesh;

            // ⛔⛔ THE TWO SOURCES ADD UP — they are not alternatives, and choosing one DROPS what only the
            // other has. The slot map carries the SHADER's own defaults (`ExternalDefaults`) and the engine
            // registers nobody's material names; the material scan says what THIS mesh binds to THIS shader.
            // The law was already paid for once, on weapons: a material lists only what it OVERRIDES, so
            // starting from nothing and filling the rest with 1 is how the ACOG's fibre came out white. It is
            // what the game does: defaults first, the material's own on top.
            var s_Textures = LoadForeignTexturesFromCache(p_MapPath, TexCacheDir, s_Mesh);
            var s_Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (s_Name, s_Value) in LoadForeignMaterialValues(p_MapPath, s_Mesh) ??
                                              (IReadOnlyDictionary<string, string>) new Dictionary<string, string>())
                s_Values[s_Name] = s_Value;

            var s_Slots = s_Mesh is { Length: > 0 } ? m_CamoSlotsFor?.Invoke(s_Mesh, p_Shader, null) : null;
            var s_Own = 0;
            if (s_Slots is { Count: > 0 })
                foreach (var s_Texture in DecodeCachedTextures(s_Slots, TexCacheDir))
                {
                    s_Textures.RemoveAll(p_T => p_T.Slot == s_Texture.Slot);
                    s_Textures.Add(s_Texture);
                    s_Own++;
                }

            foreach (var (s_Name, s_Value) in
                     (s_Mesh is { Length: > 0 } ? m_CamoValuesFor?.Invoke(s_Mesh, p_Shader, null) : null) ??
                     (IReadOnlyDictionary<string, string>) new Dictionary<string, string>())
                s_Values[s_Name] = s_Value;

            var s_Error = m_Preview.RegisterForeignShader(p_Shader, File.ReadAllBytes(p_DxbcPath), s_Textures,
                s_Values, SamplersForShader(p_Shader));
            if (s_Error == null)
                Log($"  '{p_Shader.Split('/')[^1]}': {s_Textures.Count} texture(s) ({s_Own} from this mesh's " +
                    $"materials), {s_Values.Count} value(s); interpolators {m_Preview.ForeignInterpolatorsOf(p_Shader)}; " +
                    $"bound [{string.Join(" ", s_Textures.OrderBy(p_T => p_T.Slot).Select(p_T => $"t{p_T.Slot}"))}]" +
                    (s_Slots is { Count: > 0 }
                        ? $" from this mesh: [{string.Join(" ", s_Slots.OrderBy(p_S => p_S.Key, StringComparer.Ordinal).Select(p_S => $"t{p_S.Key}={p_S.Value.Split('/')[^1]}"))}]"
                        : "") + $"; samplers {m_Preview.ForeignSamplersOf(p_Shader)}.");

            return s_Error;
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message;
        }
    }

    /// <summary>
    /// Folds every texture-and-mask pair into the single node that vocabulary uses. Offered only while
    /// authoring in that style, because in this engine's own vocabulary the two nodes are the right shape.
    /// </summary>
    /// <summary>
    /// Rewrites the open graph into the vocabulary being authored in. A shader translated out of the game
    /// arrives as this engine's own nodes, so without this, authoring in the other one means working on a
    /// graph made of the wrong words. Every rewrite is one the emitted shader cannot tell apart; whatever
    /// cannot be rewritten that way is LEFT and reported by name.
    /// </summary>
    private void OnConvertToUdk(object p_Sender, RoutedEventArgs p_Args)
    {
        Canvas.PushUndo();
        var s_Report = Graph.UdkConvert.Run(Canvas.Graph);

        if (s_Report.Converted == 0 && s_Report.Folded == 0 && s_Report.Left == 0)
        {
            Log("Nothing to convert: this graph is already in that vocabulary.");
            return;
        }

        Log($"Converted {s_Report.Converted} node(s) and folded {s_Report.Folded} texture/mask pair(s)" +
            (s_Report.Left > 0 ? $"; left {s_Report.Left} alone." : "."));
        foreach (var s_Note in s_Report.Notes)
            Log($"  {s_Note}");

        TouchGraph();
        BuildProperties();
    }

    /// <summary>Redraw plus a debounced preview recompile — every edit path goes through here.</summary>
    private void TouchGraph()
    {
        Canvas.Refresh();
        SchedulePreview();
    }

    /// <summary>
    /// True while a CLI test mode drives a hidden window. Set by App.RunHeadless before any window exists.
    /// </summary>
    internal static bool Headless;

    private void OnOpenSettings(object p_Sender, RoutedEventArgs p_Args) =>
        new SettingsWindow(this, m_Settings, ApplySettingsChanged).ShowDialog();

    /// <summary>
    /// Everything a Settings change must touch, so the dialog applies LIVE: table-backed state re-derives,
    /// the preview constants re-feed (that is what makes the FLIR toggle visible without reselecting the
    /// target), and the file persists through the same headless-guarded path as the rest.
    /// </summary>
    internal void ApplySettingsChanged()
    {
        View.GridCanvas.ZoomSpeed = m_Settings.CanvasZoomSpeed;
        GraphCanvas.ApplyShortcuts(m_Settings.NodeShortcuts);
        m_Preview.HideForeignSections = m_Settings.PreviewOnlyEditedShader;

        // Simple mode swaps LIVE, like every other setting here. Ticking a box and being told to reopen
        // something is the same as the box not working.
        if (CamoMode)
            SetCamoSimpleMode(m_Settings.CamoSimpleMode);

        // The folders are settable in TWO places now, so the window's own boxes have to follow the dialog —
        // otherwise the next thing that reads a path takes the stale one out of the box and the setting looks
        // like it did nothing.

        // The vocabulary switch takes effect at once: a palette still listing the other style's nodes after
        // the user chose is the setting appearing not to work.
        BuildPalette();
        View.GraphCanvas.UdkStyle = m_Settings.UdkNodeStyle;
        CollapseButton.Visibility = m_Settings.UdkNodeStyle ? Visibility.Visible : Visibility.Collapsed;

        // In camo mode the weapon re-dresses itself (same values, same art); the slot map's base set is
        // another weapon's and must not come back through a Settings change either.
        if (CamoMode)
            RedressCamoWeapon();
        else if (LoadCachedPreviews() == 0)
            PushInstanceValues(null);

        m_Settings.Save();
    }

    /// <summary>
    /// The game install and the cache folder, from the settings and nowhere else.
    ///
    /// ⛔ THEY USED TO BE TWO TEXT BOXES IN THE TOP BAR, and the settings dialog wrote to both places to keep
    /// them agreeing — two stores for one fact, which is the shape every silent divergence in this editor has
    /// had. The boxes are gone; this is the only reader.
    /// </summary>
    internal string GamePath => m_Settings.GamePath.Trim();

    internal string OutputFolder => m_Settings.OutputFolder.Trim();

    /// <summary>
    /// THE settings instance — the one this window saves on Closing and after every Settings change. Anything
    /// that persists a preference while the window is up writes into this one, never into a fresh Load():
    /// a value saved from another instance was overwritten by this one's next save (symptom: the
    /// camo framework folder had to be picked again on every start).
    /// </summary>
    internal EditorSettings Settings => m_Settings;

    private void PersistSettings()
    {
        // The hidden windows the test seams build get their Closing raised at app shutdown like any other
        // window, and saving THEIR boxes overwrote the user's real settings with a test's scratch directory.
        if (Headless)
            return;

        m_Settings.Save();
    }

    // The level's catalogue, held so the filter box can rebuild the tree without going back to Rime.
    private List<ShaderIndex.Entry> m_ShaderIndex = new();

    /// <summary>
    /// The contract of the shader currently targeted, detected from its own bytecode. Drives the PsIn/PsOut the
    /// emitter writes and which interpolators the input nodes reach. Null = the rigid-mesh default.
    /// </summary>
    private ShaderContract? m_Contract;

    /// <summary>The contract the preview compiles the canvas graph against, for a driver that emits the same HLSL.</summary>
    internal ShaderContract? PreviewContract => m_Contract;

    /// <summary>
    /// The level whose shader index is currently in the browser. It decides which shaderdb a browsed shader is
    /// read from: object shaders (Objects/..., XP2/...) are compiled into EVERY level that uses them, so their
    /// name alone cannot say which database to open - the level the user browsed them in can.
    /// </summary>
    private string? m_LoadedLevel;

    /// <summary>Fills the level dropdown from the install, so it is never a list this editor made up.</summary>
    private void BuildLevelList()
    {
        LevelBox.Items.Clear();
        foreach (var s_Level in LevelScanner.Scan(GamePath))
            LevelBox.Items.Add(s_Level);

        // Preselect the level the current target shader belongs to, if it names one.
        var s_Target = TargetShaderBox.Text.Trim();
        var s_Parts = s_Target.Split('/');
        if (s_Parts.Length > 2 && s_Parts[0].Equals("Levels", StringComparison.OrdinalIgnoreCase))
            LevelBox.SelectedItem = LevelBox.Items.Cast<string>()
                .FirstOrDefault(p_L => p_L.Equals(s_Parts[1], StringComparison.OrdinalIgnoreCase));

        if (LevelBox.SelectedItem == null && LevelBox.Items.Count > 0)
            LevelBox.SelectedItem = LevelBox.Items.Cast<string>()
                .FirstOrDefault(p_L => p_L.Equals("MP_017", StringComparison.OrdinalIgnoreCase))
                ?? LevelBox.Items[0];
    }

    private async void OnLoadShaderIndex(object p_Sender, RoutedEventArgs p_Args)
    {
        if (LevelBox.SelectedItem is not string s_Level)
        {
            Log("Pick a level first. If the list is empty, check the BF3 path - it is scanned from the install.");
            return;
        }

        var s_Cache = ShaderIndex.PathFor(OutputFolder, s_Level);
        if (File.Exists(s_Cache))
        {
            LoadIndexFromCache(s_Cache, s_Level);
            return;
        }

        var s_Repl = FindRimeRepl();
        if (s_Repl == null)
        {
            Log("ERROR: RimeREPL.exe not found (it ships inside the studio; a build of the repository finds it under bin\\Release).");
            return;
        }

        var s_GamePath = GamePath;
        if (s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
        {
            Log("ERROR: the BF3 path is empty or does not exist.");
            return;
        }

        LoadIndexButton.IsEnabled = false;
        Log($"Reading {s_Level}'s shader list - this mounts the game, so a few minutes. Cached afterwards.");

        try
        {
            var s_Db = LevelScanner.ShaderDbFor(s_Level);
            var s_Work = Path.Combine(OutputFolder, "shaderindex");
            Directory.CreateDirectory(s_Work);

            var s_Extract = Path.Combine(s_Work, "dxbc_" + s_Level.ToLowerInvariant());

            var s_Entries = await Task.Run(() =>
            {
                // Both steps in ONE mount: the name list and every shader's bytecode. Detecting a contract needs
                // the bytecode, and mounting per double-click would cost minutes each time.
                var s_ScriptPath = Path.Combine(s_Work, $"list_{s_Level.ToLowerInvariant()}.rime");
                File.WriteAllText(s_ScriptPath,
                    $"mount_game \"{s_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
                    $"dump_shader_db {s_Db}\n" +
                    $"extract_shader_dxbc {s_Db} / \"{s_Extract}\"\nexit\n");

                var s_Run = Run(s_Repl, new[] { s_ScriptPath });
                var s_Parsed = ShaderIndex.ParseDump(s_Run.Output.Split('\n'));
                var s_Contracts = ShaderIndex.DetectAll(s_Extract);

                return s_Parsed.Select(p_P =>
                {
                    s_Contracts.TryGetValue(p_P.Name, out var s_Contract);
                    return new ShaderIndex.Entry
                    {
                        Name = p_P.Name,
                        Textures = p_P.Textures,
                        Family = s_Contract?.Family.ToString() ?? "",
                        RenderTargets = s_Contract?.RenderTargets ?? 0,
                        Shape = s_Contract == null ? "" : string.Join(" ", s_Contract.Interpolators
                            .Select(p_I => $"TC{p_I.Index}.{p_I.UsedMaskText.TrimEnd('_')}" +
                                           (p_I.IsInteger ? "(int)" : ""))),
                        Registers = s_Contract == null ? "" : string.Join(",",
                            s_Contract.MaterialTextures.Select(p_T => p_T.Register)),
                    };
                }).ToList();
            });

            // The extracted bytecode was only needed to classify; ~100 MB per level is not worth keeping.
            try { if (Directory.Exists(s_Extract)) Directory.Delete(s_Extract, true); } catch { }

            if (s_Entries.Count == 0)
            {
                Log($"No shaders came back for {s_Level}. Nothing was cached; see shaderindex\\list_*.rime output.");
                return;
            }

            ShaderIndex.Save(s_Cache, s_Entries.Select(p_E => p_E.ToLine()));
            m_ShaderIndex = s_Entries;
            m_LoadedLevel = s_Level;
            ApplyShaderFilter();

            var s_Families = s_Entries.Where(p_E => p_E.HasContract).GroupBy(p_E => p_E.Family)
                .OrderByDescending(p_G => p_G.Count())
                .Select(p_G => $"{p_G.Key} {p_G.Count()}");

            Log($"{s_Entries.Count} shaders in {s_Level} ({s_Entries.Sum(p_E => p_E.Textures)} texture slots). " +
                $"Contracts: {string.Join(", ", s_Families)}");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR reading the shader list: {s_Exception.Message}");
        }
        finally
        {
            LoadIndexButton.IsEnabled = true;
        }
    }

    private void LoadIndexFromCache(string p_Path, string p_Level)
    {
        m_ShaderIndex = ShaderIndex.Load(p_Path).Select(ShaderIndex.Entry.FromLine).ToList();
        m_LoadedLevel = p_Level;
        ApplyShaderFilter();

        var s_WithContract = m_ShaderIndex.Count(p_E => p_E.HasContract);
        Log($"{m_ShaderIndex.Count} shaders in {p_Level} (from cache, {s_WithContract} with a detected contract)." +
            (s_WithContract == 0 ? " Delete the cache file and press Load again to add contracts." : ""));
    }

    /// <summary>
    /// Points this document at another shader, dropping the object it was aimed at when that object does not
    /// wear the new one.
    ///
    /// ⛔ THE AIM OUTLIVES THE TARGET OTHERWISE. The stamped object is saved with the graph (that is the
    /// point: it survives a reopen), but the target can be RETYPED in the box — and then a take-over would be
    /// aimed at a mesh that may not wear the new shader at all. The bake would clone an entry whose solutions
    /// were never compiled for that mesh's vertex declaration, and the object comes up INVISIBLE with nothing
    /// said. Dropped here, at the moment the two stop matching.
    /// </summary>
    private void RetargetGraph(string p_Target)
    {
        var s_Previous = Canvas.Graph.TargetShader;
        Canvas.Graph.TargetShader = p_Target;

        if (string.Equals(s_Previous, p_Target, StringComparison.OrdinalIgnoreCase))
            return;

        if (ClearStaleAim(Canvas.Graph, ReadSlotMapFull(SlotMapPath(p_Target.Trim()))) is { } s_Dropped)
            Log($"'{s_Dropped}' does not wear '{p_Target.Trim()}', so this graph is no longer aimed at it — " +
                "pick the object's set in the preview to aim it again.");
    }

    /// <summary>
    /// Drops the graph's aimed object when the map of the new target does not list it. Returns what it
    /// dropped, or null when there was nothing to drop.
    ///
    /// ⚠ An UNKNOWN map (never loaded for that shader) is not evidence of absence, so nothing is dropped
    /// there: throwing away a good aim because a cache is cold would be worse than keeping it.
    /// </summary>
    internal static string? ClearStaleAim(ShaderGraph p_Graph, SlotMap? p_Map)
    {
        if (p_Graph.BakeMesh is not { Length: > 0 } s_Mesh || p_Map?.Variations == null)
            return null;

        if (p_Map.Variations.Values.Any(p_V =>
                string.Equals(p_V.Mesh, s_Mesh, StringComparison.OrdinalIgnoreCase)))
            return null;

        p_Graph.BakeMesh = null;
        return s_Mesh;
    }

    private void OnShaderFilterChanged(object p_Sender, TextChangedEventArgs p_Args) => ApplyShaderFilter();

    /// <summary>
    /// Renders the whole window off-screen with a real shader list loaded, so the browser can be looked at
    /// without opening it. Catches what a logic test cannot: eaten underscores, clipped columns, a tree that
    /// renders empty. DesiredSize is not used here because the window has a fixed design size.
    /// </summary>
    internal static System.Windows.Media.Imaging.BitmapSource Snapshot(
        List<ShaderIndex.Entry> p_Index, string p_Filter)
    {
        var s_Window = new MainWindow();
        s_Window.m_ShaderIndex = p_Index;
        s_Window.ShaderFilterBox.Text = p_Filter;
        s_Window.ApplyShaderFilter();

        var s_Content = (FrameworkElement) s_Window.Content;
        var s_Size = new Size(s_Window.Width, s_Window.Height);
        s_Content.Measure(s_Size);
        s_Content.Arrange(new Rect(new Point(0, 0), s_Size));
        s_Content.UpdateLayout();

        var s_Target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int) Math.Ceiling(s_Size.Width), (int) Math.Ceiling(s_Size.Height), 96, 96,
            PixelFormats.Pbgra32);

        s_Target.Render(s_Content);
        return s_Target;
    }

    /// <summary>
    /// Renders the window with the variation picker filled from an existing texture cache (no game mount), so
    /// the picker's colours can be LOOKED at - readability is a question only a picture answers.
    /// </summary>
    internal static int VariationShot(string p_WorkDir, string p_Target, string p_OutputPath)
    {
        var s_Window = new MainWindow();
        s_Window.m_Settings.OutputFolder = p_WorkDir;
        s_Window.TargetShaderBox.Text = p_Target;

        var s_Count = s_Window.LoadCachedPreviews();
        Console.Out.WriteLine($"{s_Count} thumbnail(s) from cache; picker holds " +
                              $"{s_Window.VariationBox.Items.Count} item(s), {s_Window.VariationBox.Visibility}");

        var s_Content = (FrameworkElement) s_Window.Content;
        var s_Size = new Size(s_Window.Width, s_Window.Height);
        s_Content.Measure(s_Size);
        s_Content.Arrange(new Rect(new Point(0, 0), s_Size));
        s_Content.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int) Math.Ceiling(s_Size.Width), (int) Math.Ceiling(s_Size.Height), 96, 96, PixelFormats.Pbgra32);
        s_Bitmap.Render(s_Content);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using (var s_Stream = File.Create(p_OutputPath))
            s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"rendered -> {p_OutputPath}");
        return s_Window.VariationBox.Items.Count > 1 ? 0 : 1;
    }

    private void ApplyShaderFilter()
    {
        var s_Query = ShaderFilterBox.Text;
        var s_Matching = m_ShaderIndex
            .Where(p_E => s_Query.Trim().Length == 0 ||
                          p_E.Name.Contains(s_Query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Built defensively rather than with ToDictionary: a duplicate name would throw and take the whole
        // browser down, which is exactly what happened when the dump turned out to list each shader once per
        // render-path database.
        var s_Textures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Entry in m_ShaderIndex)
            s_Textures[s_Entry.Name] = Math.Max(s_Entry.Textures,
                s_Textures.TryGetValue(s_Entry.Name, out var s_Old) ? s_Old : 0);

        var s_Tree = ShaderIndex.Build(s_Matching.Select(p_E => p_E.Name));
        ShaderTree.Items.Clear();

        // Expanded when filtered, collapsed otherwise: a filter is only useful if you can see what it matched.
        var s_Filtered = s_Query.Trim().Length > 0;
        foreach (var s_Child in s_Tree.Children)
            ShaderTree.Items.Add(BuildShaderTreeItem(s_Child, s_Textures, s_Filtered));
    }

    private static TreeViewItem BuildShaderTreeItem(ShaderTreeNode p_Node,
        IReadOnlyDictionary<string, int> p_Textures, bool p_Expand)
    {
        // TextBlock rather than a string: a string Content is parsed for access keys and eats the underscores
        // that almost every shader name has.
        var s_Label = p_Node.IsShader
            ? $"{p_Node.Name}  ({(p_Textures.TryGetValue(p_Node.ShaderName!, out var s_T) ? s_T : 0)} tex)"
            : $"{p_Node.Name}  ({p_Node.ShaderCount})";

        var s_Item = new TreeViewItem
        {
            Header = new TextBlock { Text = s_Label },
            Tag = p_Node.ShaderName,
            IsExpanded = p_Expand && !p_Node.IsShader,
        };

        foreach (var s_Child in p_Node.Children)
            s_Item.Items.Add(BuildShaderTreeItem(s_Child, p_Textures, p_Expand));

        return s_Item;
    }

    private void OnShaderTreeSelected(object p_Sender, RoutedPropertyChangedEventArgs<object> p_Args)
    {
        if (p_Args.NewValue is TreeViewItem { Tag: string s_Shader })
            Log($"Selected {s_Shader} - double-click to make it the target shader.");
    }

    /// <summary>
    /// Makes the clicked shader the target AND shows it: translates its bytecode to a graph and fills its
    /// texture slots, without asking for anything else.
    ///
    /// ⛔ Feedback after using it: selecting the shader should already have done "translate target to graph"
    /// and "load target textures", or we are only adding useless steps. That is right, and the reason is that
    /// there is no other thing you would want here: picking a target IS the request to see it, so making the
    /// user press two more buttons to get the only useful outcome is ceremony, not choice. The buttons stay,
    /// but as a RETRY (after Ctrl+Z, or when a step failed), not as steps.
    ///
    /// It hangs off the double click rather than mere selection on purpose: each of these mounts the game for
    /// about twenty seconds, so browsing the tree must stay free.
    /// </summary>
    private async void OnShaderTreeDoubleClick(object p_Sender, MouseButtonEventArgs p_Args)
    {
        if (ShaderTree.SelectedItem is not TreeViewItem { Tag: string s_Shader })
            return;

        p_Args.Handled = true;
        await SelectTargetAsync(s_Shader);
    }

    /// <summary>Signals the headless seam that a whole selection - both steps - has finished.</summary>
    internal static TaskCompletionSource<int>? SelectFinished;

    /// <summary>
    /// Everything picking a target does, as one awaitable unit so it can be driven headlessly. The seam enters
    /// HERE and not at the individual buttons on purpose: what changed is the CHAINING of the two steps, and a
    /// test that presses each button separately would pass whether or not they are chained at all.
    /// </summary>
    internal async Task SelectTargetAsync(string s_Shader)
    {
      try
      {
        // A shader open in ANOTHER tab just gets focus — a second double click must not pay the mounts again
        // nor silently replace a graph being edited elsewhere. Selecting the ACTIVE tab's own target falls
        // through instead: that is the retry ("Reload from game", or double-clicking what you are looking at).
        var s_AlreadyOpen = FindTabByTarget(s_Shader);
        if (s_AlreadyOpen >= 0 && s_AlreadyOpen != m_ActiveTab)
        {
            SaveActiveTab();
            ActivateTab(s_AlreadyOpen);
            Log($"'{s_Shader}' is already open in a tab — switched to it. Select it again (or press " +
                "'Reload from game') for a fresh read.");
            SelectFinished?.TrySetResult(Canvas.Graph.Nodes.Count);
            return;
        }

        // A NEW shader opens as its own document, leaving the current tab exactly as it was.
        if (s_AlreadyOpen < 0)
            OpenInNewTab(new ShaderGraph { TargetShader = s_Shader }, null);

        TargetShaderBox.Text = s_Shader;
        Canvas.Graph.TargetShader = s_Shader;

        // The contract was detected when the level was catalogued, so this costs nothing here.
        var s_Entry = m_ShaderIndex.FirstOrDefault(
            p_E => p_E.Name.Equals(s_Shader, StringComparison.OrdinalIgnoreCase));

        m_Contract = null;
        if (s_Entry is { HasContract: true })
        {
            m_Contract = ContractFromEntry(s_Entry);
            Log($"Target = {s_Shader}");
            Log($"  contract: {s_Entry.Family}, {s_Entry.RenderTargets} render target(s), " +
                $"interpolators {s_Entry.Shape}");

            if (s_Entry.Family is not ("RigidMesh" or "RigidMeshSubMaterial"))
                Log("  NOTE: only the rigid-mesh interpolator meanings are measured. For this family the " +
                    "editor exposes them raw (Inputs > Interpolator), it does not claim to know what each one is.");
        }
        else
        {
            Log($"Target = {s_Shader} (no contract in the cache; assuming the rigid-mesh layout). " +
                "Delete the level's shaderindex file and press Load again to detect it.");
        }

        // Whatever the previous target taught the vertex shader no longer applies; the translation step will
        // teach it this shader's meanings in a moment. Without the reset, a rigid mesh selected after an
        // exotic one previewed with the exotic inputs.
        m_Preview.SetInterpolatorMeanings(m_Contract?.InterpolatorMeanings);

        // Same for the material constants: the previous target's instance values must not leak, and the new
        // contract's engine-fed parameters get their Settings-decided state from the first frame.
        PushInstanceValues(null);

        // Both steps, in this order and not the other: the translation adopts the contract of the permutation it
        // actually read and drops the stale thumbnails, and the texture step needs that contract to know which
        // register each texture belongs in.
        //
        // ⛔ And the machine has to LOOK busy for the whole thing. The first mount is ~50 silent seconds, and
        // with nothing moving the user concluded the double-click had done nothing and pressed the buttons by hand
        // - the exact ceremony this chain exists to remove. A busy cursor plus both buttons greyed is the
        // difference between "it is working" and "it ignored me".
        Log($"double-click: translating '{s_Shader}' and loading its textures - two game mounts, ~2 min the " +
            "first time. The buttons stay greyed until it is done.");
        ReloadButton.IsEnabled = false;
        ReloadButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.AppStarting;

        var s_Translated = await RunTranslateAsync();

        // The inner step re-enables its own button on the way out; during the texture mount both stay off.
        ReloadButton.IsEnabled = false;

        if (!s_Translated)
        {
            // Only when the real thing could not be produced. The scaffold used to be what you always got here,
            // and it is strictly worse: it is a guess at the inputs, where the translation is the shader.
            // ⚠ The cached slot map behind it predates the contract, so its registers may be the old
            // declaration-order guess - which is exactly why it is the fallback and not the default.
            Canvas.PushUndo();
            var s_Slots = CachedSlots(s_Shader);
            LoadGraph(SampleGraphs.ScaffoldFor(s_Shader, s_Slots));

            var s_Registers = string.Join(", ",
                s_Slots.Select(p_S => $"t{p_S.Register}={p_S.Name.Split('/').Last()}"));

            Log(s_Slots.Count == 0
                ? "  the translation did not run, so the canvas holds an EMPTY scaffold. Ctrl+Z restores the " +
                  "previous graph."
                : $"  the translation did not run, so the canvas holds a SCAFFOLD from the cached slot map " +
                  $"({s_Registers}) - a guess at the inputs, not this shader. Ctrl+Z restores the previous graph.");

            if (m_Contract == null)
                Log("  FIX: press Load on the level (one game mount) so its contract is detected, then " +
                    "double-click the shader again.");

            return;
        }

        await RunLoadTexturesAsync();
      }
      finally
      {
        ReloadButton.IsEnabled = true;
        ReloadButton.IsEnabled = true;
        Mouse.OverrideCursor = null;
        SelectFinished?.TrySetResult(Canvas.Graph.Nodes.Count);
      }
    }

    /// <summary>
    /// The target's textures from the thumbnail cache as (register, name). The map is keyed by the REAL
    /// register, which is what the scaffold has to put on each node.
    /// </summary>
    /// <summary>The slot map on disk, version-checked.</summary>
    /// <remarks>
    /// ⛔ The map's SCHEMA carries a version because the PAIRING RULE is part of the data: the old maps were
    /// built with the identity rule (texture_TextureN = Nth streamable), which put a normal map into a colour
    /// slot on 89 of the shaders measured against their own disassembly. A cached wrong map would keep being
    /// served forever - the loader returns early on cache hits - so old maps must READ as absent, not as valid.
    /// </remarks>
    internal sealed class SlotMap
    {
        public int V { get; set; }
        public Dictionary<string, string> Slots { get; set; } = new();

        /// <summary>
        /// v3: per-variation slot maps, keyed by the variation hash ("0" = the unvaried master). A shader
        /// whose textures are material parameters can carry several complete texture sets - one per painting,
        /// per camo... - all sharing the master's bytecode, so the editor can swap them without a remount.
        /// </summary>
        public Dictionary<string, VariationSlots>? Variations { get; set; }

        /// <summary>
        /// v5: the shaderdb's own DEFAULT for each external VALUE parameter ("name" -> "x,y,z,w"). This is
        /// what the engine feeds when no material instance names the value — a foreign shader's constant
        /// buffer starts from these, and the set's Values overwrite the ones the level does name. A made-up
        /// neutral is somebody's lethal value: 1 in the FLIR gate rendered the whole jet in thermal mode.
        /// </summary>
        public Dictionary<string, string>? ExternalDefaults { get; set; }

        /// <summary>
        /// v6: how many different meshes in the level wear this shader, and a few of their names.
        ///
        /// ⛔ IT IS THE ONE FACT THAT DECIDES HOW A BAKE MUST BE AIMED, and nothing in the editor showed it.
        /// A replacement answers for the shader's NAME: worn by one mesh it changes that object, worn by
        /// seventy-five it repaints the map. Null in maps cached before this existed — the UI then says the
        /// count is unknown rather than inventing a reassuring one, and the bake measures it anyway.
        /// </summary>
        public int? MeshUsers { get; set; }

        public List<string>? MeshUserSample { get; set; }

        /// <summary>
        /// WHICH MAP that count is from, and it has to travel with it.
        ///
        /// ⛔ A shader is not worn by the same objects everywhere: a preset dresses different meshes in every
        /// map, and one that only one object wears here can be shared in the next. The scope also is not the
        /// author's choice — a shader whose name does not carry a level falls back to a default one — so a
        /// bare number reads as "in the game" when it means "in that map". Named here, the label can say so,
        /// and the bake measures the maps actually being built for.
        /// </summary>
        public string? MeshUsersScope { get; set; }

        /// <summary>
        /// v7: the sampler STATE the game binds to each sampler register ("0" -> "Wrap,Border,Border"; U, V, W).
        ///
        /// ⛔ IT IS NOT IN THE BYTECODE — addressing is API state — and it decides what a fetch OUTSIDE [0,1]
        /// returns, which some shaders rely on as an operation: the kit atlas one adds its tile atlas sampled at
        /// (u, v+1), and the game's `v=Border` makes that add NOTHING for every piece whose V is positive. Bound
        /// as Wrap instead, v+1 lands back inside the atlas and the tile band is added over the piece — the
        /// Sprut-SD's reactive armour blocks came out pale grey while the weapons, whose
        /// presets really are Wrap/Wrap/Wrap, were byte-identical either way.
        ///
        /// Null in maps cached before this existed: the preview then says out loud that it is falling back to
        /// Wrap rather than pretending it knows. `--cachesamplers` fills them in one mount.
        /// </summary>
        public Dictionary<string, string>? Samplers { get; set; }
    }

    internal sealed class VariationSlots
    {
        public string Name { get; set; } = "";

        /// <summary>Mesh partition of the database entry this set came from — what a variation bake copies.
        /// Null in maps cached before variation support; the bake asks for a re-load instead of guessing.</summary>
        public string? Mesh { get; set; }

        /// <summary>Full variation asset path ("(base)" for the unvaried master); the short label above is
        /// ambiguous across folders, and new sibling names are derived from this one.</summary>
        public string? AssetName { get; set; }

        public Dictionary<string, string> Slots { get; set; } = new();

        /// <summary>
        /// The material instance's REAL parameter values ("name" -> "x,y,z,w"), where the level's data names
        /// any - the preview feeds them into cb1 so the object shades as ITS instance, not as the probe
        /// pattern. Parameter names come without the bytecode's external_ prefix.
        /// </summary>
        public Dictionary<string, string>? Values { get; set; }
    }

    internal static SlotMap? ReadSlotMapFull(string p_Path)
    {
        try
        {
            if (!File.Exists(p_Path))
                return null;

            var s_Map = System.Text.Json.JsonSerializer.Deserialize<SlotMap>(File.ReadAllText(p_Path));

            // The pairing rule is PART of the data: a map written under an older rule must read as ABSENT or
            // the cache serves the old error forever. v4 = exact-shader scoping + named-register externals +
            // level-wide database scan; v3 maps (substring-polluted slot lists on shared roots) pair wrongly.
            // v5: external slots are the EXTSLOT∪EXTERNAL union and spaced texture names parse whole — v4
            // maps built for vehicles miss their Diffuse/Camo/Specular entirely, so they must refetch.
            return s_Map is { V: >= 5 } ? s_Map : null;
        }
        catch
        {
            // A v1 map is a plain dictionary and fails to bind to SlotMap the same way garbage does: absent.
            return null;
        }
    }

    internal static Dictionary<string, string>? ReadSlotMap(string p_Path) => ReadSlotMapFull(p_Path)?.Slots;

    /// <summary>
    /// The game's own sampler ADDRESSING for a shader ("0" -> "Wrap,Border,Border"), from its cached slot map.
    /// Null when the map predates <see cref="SlotMap.Samplers"/> — the preview then keeps its wrapping sampler
    /// and says so, and `--cachesamplers` fills every cached map in one mount.
    ///
    /// ONE source for every registration: a foreign shader drawn with the wrong addressing is the game's
    /// bytecode over the game's art producing a different picture, which reads as "the material is wrong".
    /// </summary>
    internal Dictionary<string, string>? SamplersForShader(string p_Shader) =>
        p_Shader is { Length: > 0 } ? ReadSlotMapFull(SlotMapPath(p_Shader))?.Samplers : null;

    /// <summary>
    /// Points the preview at the shader being authored — and hands it that shader's OWN sampler addressing.
    ///
    /// ⛔ ONE PLACE, because the two belong together: the authored shader stands in for a game shader, so it
    /// must run with the state the game binds to it. Setting the target in five places and the addressing in
    /// one would leave four paths drawing the canvas through a wrapping sampler, which for the kit atlas
    /// shader means its tile band printed over every piece (measured on the Sprut-SD).
    /// </summary>
    private void AimPreviewAt(string p_Shader)
    {
        m_Preview.MeshTargetShader = p_Shader;
        m_Preview.SetAuthoredSamplers(SamplersForShader(p_Shader));
    }

    private List<(int Register, string Name)> CachedSlots(string p_Target)
    {
        var s_Slots = ReadSlotMap(SlotMapPath(p_Target));
        if (s_Slots == null)
            return new List<(int, string)>();

        return s_Slots
            .Select(p_S => (Register: int.TryParse(p_S.Key, out var s_N) ? s_N : 0, Name: p_S.Value))
            .Where(p_S => p_S.Register > 0)
            .OrderBy(p_S => p_S.Register)
            .ToList();
    }

    /// <summary>
    /// Rebuilds a contract from the cached line. Only the shape is cached, which is all the emitter needs: the
    /// family decides the field names and the interpolator list decides the PsIn.
    /// </summary>
    private static ShaderContract ContractFromEntry(ShaderIndex.Entry p_Entry)
    {
        var s_Inputs = new List<SignatureElement>();
        foreach (var s_Token in p_Entry.Shape.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // "TC3.xyz" or "TC5.x(int)"
            var s_IsInteger = s_Token.EndsWith("(int)", StringComparison.Ordinal);
            var s_Clean = s_Token.Replace("(int)", "");
            var s_Split = s_Clean.Split('.');
            if (s_Split.Length != 2 || !int.TryParse(s_Split[0].TrimStart('T', 'C'), out var s_Index))
                continue;

            byte s_Used = 0;
            foreach (var s_Component in s_Split[1])
                s_Used |= s_Component switch
                {
                    'x' => (byte) 1, 'y' => (byte) 2, 'z' => (byte) 4, 'w' => (byte) 8, _ => (byte) 0,
                };

            s_Inputs.Add(new SignatureElement
            {
                Semantic = "TEXCOORD", Index = s_Index, Register = s_Index + 1,
                Mask = 0xF, UsedMask = s_Used, ComponentType = s_IsInteger ? 1 : 3,
            });
        }

        // The cached registers rebuild just enough of the binding table for RegisterForStreamable: the Nth entry
        // is the Nth material texture in streamable order, which is what the loader and the scaffold need.
        var s_Resources = p_Entry.Registers.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select((p_R, p_I) => new ResourceBinding
            {
                Name = "texture_Texture" + (p_I == 0 ? "" : (p_I + 1).ToString()),
                Register = int.TryParse(p_R, out var s_Reg) ? s_Reg : 0,
                InputType = 2,
            })
            .ToList();

        return new ShaderContract
        {
            Family = Enum.TryParse<ShaderFamily>(p_Entry.Family, out var s_Family) ? s_Family : ShaderFamily.Unknown,
            RenderTargets = p_Entry.RenderTargets,
            Inputs = s_Inputs,
            Resources = s_Resources,
        };
    }

    private void BuildPalette()
    {
        PaletteTree.Items.Clear();

        // The foreign group sorts LAST and starts closed: it is the overflow a conversion can leave behind,
        // not part of the vocabulary being authored in, so it must not be the first thing the eye lands on.
        foreach (var s_Group in Palette.VisibleFor(m_Settings.UdkNodeStyle)
                     .GroupBy(p_D => p_D.CategoryFor(m_Settings.UdkNodeStyle))
                     .OrderBy(p_G => p_G.Key == Palette.c_ForeignCategory ? 1 : 0)
                     .ThenBy(p_G => p_G.Key))
        {
            var s_Foreign = s_Group.Key == Palette.c_ForeignCategory;
            var s_Item = new TreeViewItem { Header = s_Group.Key, IsExpanded = !s_Foreign };
            if (s_Foreign)
                s_Item.ToolTip = "Nodes this engine has and that vocabulary does not. Converting a graph can " +
                                 "leave them behind, so they are placeable here under their real names — " +
                                 "some have no counterpart because that catalog simply has no such node.";

            foreach (var s_Def in s_Group.OrderBy(p_D => p_D.TitleFor(m_Settings.UdkNodeStyle)))
                s_Item.Items.Add(new TreeViewItem
                {
                    Header = s_Def.TitleFor(m_Settings.UdkNodeStyle), Tag = s_Def.Kind,
                });

            PaletteTree.Items.Add(s_Item);
        }
    }

    private void OnPaletteDoubleClick(object p_Sender, RoutedEventArgs p_Args)
    {
        if (PaletteTree.SelectedItem is TreeViewItem { Tag: string s_Kind })
        {
            Canvas.AddNode(s_Kind);
            Log($"Added {Palette.Get(s_Kind).TitleFor(m_Settings.UdkNodeStyle)}.");
        }
    }

    private Point m_PaletteDragOrigin;

    private void OnPaletteMouseDown(object p_Sender, MouseButtonEventArgs p_Args) =>
        m_PaletteDragOrigin = p_Args.GetPosition(null);

    /// <summary>
    /// Starts a drag once the pointer has actually travelled, so a plain click still selects the entry in the
    /// tree instead of being swallowed as a drag.
    /// </summary>
    private void OnPaletteMouseMove(object p_Sender, MouseEventArgs p_Args)
    {
        if (p_Args.LeftButton != MouseButtonState.Pressed)
            return;

        var s_Travelled = p_Args.GetPosition(null) - m_PaletteDragOrigin;
        if (Math.Abs(s_Travelled.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(s_Travelled.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (FindKindUnderMouse(p_Args.OriginalSource as DependencyObject) is not string s_Kind)
            return;

        DragDrop.DoDragDrop(PaletteTree, s_Kind, DragDropEffects.Copy);
    }

    /// <summary>Walks up from whatever was hit to the TreeViewItem that carries a node kind.</summary>
    private static string? FindKindUnderMouse(DependencyObject? p_Source)
    {
        while (p_Source != null)
        {
            if (p_Source is TreeViewItem { Tag: string s_Kind })
                return s_Kind;

            p_Source = System.Windows.Media.VisualTreeHelper.GetParent(p_Source);
        }

        return null;
    }

    private void OnCopy(object p_Sender, ExecutedRoutedEventArgs p_Args)
    {
        if (Canvas.CopySelection())
            Log($"Copied {Canvas.SelectedCount} node(s).");
        else
            Log("Nothing selected to copy.");
    }

    private void OnPaste(object p_Sender, ExecutedRoutedEventArgs p_Args)
    {
        if (!Canvas.PasteSelection())
        {
            Log("Clipboard holds no nodes copied from this editor.");
            return;
        }

        Log($"Pasted {Canvas.SelectedCount} node(s).");
        if (Canvas.SkippedRoots > 0)
            Log($"Skipped {Canvas.SkippedRoots} root node(s): the graph already has one, and two roots would " +
                "make the render targets ambiguous.");
    }

    private void LoadGraph(ShaderGraph p_Graph)
    {
        m_ParkedCamoGraph = null;
        m_ParkedContract = null;
        m_ShownFactoryPreset = null;
        m_ShownFactoryMesh = null;
        SyncReferenceNote();
        m_MaterialParkedCamo = null;
        m_MaterialParkedContract = null;
        m_MaterialParkedTarget = null;
        m_MaterialEditKey = null;
        m_MaterialEditSection = -1;
        m_Preview.TargetSectionOnly = null;
        m_Preview.SectionOverrides = new Dictionary<int, string>();
        Canvas.Graph = p_Graph;
        TargetShaderBox.Text = p_Graph.TargetShader;
        GraphNameBox.Text = p_Graph.Name;
        BuildProperties();
        SchedulePreview();
        RefreshTabBar();
    }

    /// <summary>
    /// One open document. The canvas and the window fields hold the ACTIVE one; a tab is where the others
    /// wait — graph, save path, contract, texture-set choice and undo history all travel together, because
    /// a tab that came back without any one of them would silently wear the previous document's state.
    /// </summary>
    private sealed class EditorTab
    {
        public ShaderGraph Graph = new();
        public string? GraphPath;
        public ShaderContract? Contract;
        public string Variation = "0";

        // What this tab was previewing. The mesh is remembered even while a primitive is showing, so
        // going back to the mesh icon does not ask for the object again.
        public View.PreviewShape Shape = View.PreviewShape.Cube;
        public string? PreviewMesh;

        // ⛔ AND THE SUBJECT IT WAS SHOWN UNDER. An attachment's mesh does not name its weapon (one PSO-1 serves
        // a dozen rifles), so a tab that remembered only the piece came back with the FIRST weapon offering it:
        // the AKS74u's M145 returned under the A91, and every attachment after that was the A91's
        // (--switchtest "…,AKS74u,acc:11,tab:0,tab:1,…"). The same law the saved file follows.
        public string? PreviewWeapon;
        public string[] Undo = Array.Empty<string>();
        public string[] Redo = Array.Empty<string>();

        // ⭐ ONE DOCUMENT PER SUBJECT (an edit made looking at the LAV-25 — a wire cut in its hull, whose graph is the
        // camo's — showed on the M1A2 too; the chosen rule: an edit belongs to that subject only). Graph above is the document of DocumentSubject
        // (the weapon, vehicle or attachment it was last shown on); Common is the tab's document as it was before anything was
        // changed on any subject — what a subject not yet touched starts from —, and Subjects holds every other subject's own.
        public string? DocumentSubject;

        // ⭐ AND ONE PER CAMO OF THE PICKER (a Diffuse cut on Berkut was still cut on ABU — the other previews must stay as they
        // are unless they are changed themselves, so ABU stays original): every camo of the picker (as shipped = "") keeps
        // its own set — the common document of that camo and the subjects changed under it. Common and Subjects below are the set
        // of the camo the tab is showing (NativeKey), so everything written for "one document per subject" reads the right one.
        public string NativeKey = "";
        public readonly Dictionary<string, CamoDocuments> Natives = new(StringComparer.OrdinalIgnoreCase);

        // The tab's document before anything was changed on any subject or camo: what a camo's common document starts from.
        public ShaderGraph? Base;

        // ⭐ A TAB OF THE GAME'S ORIGINALS (any change to an original opens in a new tab): the tab the studio opens a subject in.
        // Nothing is ever changed in it — the first change moves the window into a copy
        // of it beside it (ForkOriginalTab), and this one goes on showing every camo as the game ships it.
        public bool Original;

        // What the tab strip calls it, when not the document's own name (a tab opened by a change is named after what was changed).
        public string? Title;

        // ⭐ …AND, WHERE THE STUDIO SAYS SO, THE CAMO OF EACH SUBJECT (a soldier's stock looks, per part): the camo of the
        // picker each subject was last on — a soldier's torso on the Ninja look, his legs on Urban. Everywhere else the camo is the TAB's,
        // for every subject (a weapon and its attachment share it), and this is never read.
        public readonly Dictionary<string, string> SubjectNatives = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The camo a subject is on when each keeps its own: the one it was last on, else the one the tab was opened on.</summary>
        public string NativeOfSubject(string p_Subject) =>
            SubjectNatives.TryGetValue(p_Subject, out var s_Native) ? s_Native : Base != null ? NativeKeyOf(Base) : NativeKey;

        public CamoDocuments Set(string p_Native)
        {
            if (!Natives.TryGetValue(p_Native, out var s_Set))
            {
                s_Set = new CamoDocuments();
                if (Base != null)
                {
                    s_Set.Common = ShaderGraph.FromJson(Base.ToJson());
                    s_Set.Common.NativeCamo = p_Native.Length == 0 ? null : p_Native;
                }

                Natives[p_Native] = s_Set;
            }

            return s_Set;
        }

        public ShaderGraph? Common
        {
            get => Set(NativeKey).Common;
            set
            {
                // the first document a tab adopts is its base, and the camo it was on is the one it shows
                if (value != null && Base == null)
                {
                    Base = ShaderGraph.FromJson(value.ToJson());
                    NativeKey = NativeKeyOf(value);
                }

                if (value == null)
                {
                    Base = null;
                    Natives.Clear();
                    return;
                }

                Set(NativeKey).Common = value;
            }
        }

        public Dictionary<string, (ShaderGraph Graph, string[] Undo, string[] Redo)> Subjects => Set(NativeKey).Subjects;
    }

    /// <summary>One camo of the picker in a tab: its common document and the subjects changed under it (see EditorTab.Natives).</summary>
    private sealed class CamoDocuments
    {
        public ShaderGraph? Common;
        public readonly Dictionary<string, (ShaderGraph Graph, string[] Undo, string[] Redo)> Subjects = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The key a document's camo is kept under in a tab: the picker's key, "" for as shipped.</summary>
    private static string NativeKeyOf(ShaderGraph p_Graph) => p_Graph.NativeCamo ?? "";

    /// <summary>The subject (weapon, vehicle or attachment mesh) the document on the canvas belongs to — see EditorTab.Subjects.</summary>
    private string? m_DocumentSubject;

    /// <summary>
    /// The key a subject's document is kept under (the studio's answer, else the mesh itself): a soldier's PART — "soldiers/US_Assault/upper"
    /// — rather than the mesh, because two soldiers wear the same legs and his upper body is two meshes (the torso and the first-person
    /// arms) under one document. Every other subject answers its mesh, as it always did.
    /// </summary>
    private Func<string, string>? m_CamoDocumentKeyFor;

    private string DocumentKeyOf(string p_Mesh) =>
        // the entry picked for this mesh can name its own (the first-person model: upper or lower body, by the entry, not the mesh)
        AccessoryList.SelectedItem is CamoWeapon { DocumentKey.Length: > 0 } s_Entry && s_Entry.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)
            ? s_Entry.DocumentKey
            : m_CamoDocumentKeyFor?.Invoke(p_Mesh) ?? p_Mesh;

    /// <summary>The key of the document an entry of the picker shows — its own when it names one (a first-person entry), else its mesh's.</summary>
    private string EntryKeyOf(CamoWeapon p_Entry) =>
        p_Entry.DocumentKey is { Length: > 0 } s_Own ? s_Own : m_CamoDocumentKeyFor?.Invoke(p_Entry.Mesh) ?? p_Entry.Mesh;

    /// <summary>The key of the document on the canvas — what a seam checks after moving between a soldier's parts.</summary>
    internal string? DocumentSubjectKey => m_DocumentSubject;

    /// <summary>The document one MATERIAL of a mesh is edited under (the studio's answer — a soldier's first-person trousers are his lower body's).</summary>
    private Func<string, int, string>? m_CamoDocumentKeyForMaterial;

    /// <summary>What the context dress was last built from, so an unchanged view is not rebuilt.</summary>
    private string? m_ContextDressKey;

    /// <summary>Per registration name of a context part, what it was compiled from (see RegisterContextPart).</summary>
    private readonly Dictionary<string, string> m_ContextPartBuilt = new(StringComparer.Ordinal);

    /// <summary>
    /// ⭐ THE WHOLE SOLDIER WITH THE WHOLE SKIN (the next phase): every CONTEXT section — the rest of the soldier around the
    /// part on the canvas — is drawn with the DOCUMENT OF THE PART IT BELONGS TO when that document says something: an edited one always,
    /// and every one while a camo is picked (as shipped, an untouched part ships). Its part's document is looked up in the tab on screen
    /// (EditorTab.Subjects, by the studio's per-material key — the first-person trousers are the lower body's) and drawn only where it can
    /// be: the section wears the document's own shader. What has none stays as the game ships it.
    /// </summary>
    private void RefreshContextDress()
    {
        var s_Dress = new Dictionary<int, string>();
        if (!CamoMode || m_CamoDocumentKeyForMaterial == null || m_CamoSectionMaterialsOf == null || m_CamoSlotsForMaterial == null ||
            m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count || m_MaterialEditKey != null)
        {
            if (m_MaterialEditKey == null)
            {
                m_Preview.ContextDress = s_Dress;
                m_ContextDressKey = null;
            }

            return;
        }

        var s_Tab = m_Tabs[m_ActiveTab];
        var s_Sections = m_Preview.MeshSections;
        var s_Factory = CamoFactoryLook();
        var s_Lists = new Dictionary<string, IReadOnlyList<(int MaterialId, string Shader)>>(StringComparer.OrdinalIgnoreCase);
        var s_Parts = new List<(int Section, string Mesh, int MaterialId, string Key, ShaderGraph Document)>();
        for (var i = 0; i < s_Sections.Count; i++)
        {
            if (s_Sections[i].Context is not { Length: > 0 } s_Of)
                continue;

            if (!s_Lists.TryGetValue(s_Of, out var s_List))
                s_Lists[s_Of] = s_List = m_CamoSectionMaterialsOf(s_Of);

            var s_At = s_Sections[i].ContextSection;
            if (s_At >= s_List.Count || !s_List[s_At].Shader.Equals(s_Sections[i].Shader, StringComparison.OrdinalIgnoreCase))
                continue;

            var s_Id = s_List[s_At].MaterialId;
            var s_Key = m_CamoDocumentKeyForMaterial(s_Of, s_Id);

            // its part's document: the one on the canvas when it is that part's, else the tab's copy — and none, as shipped, for a part
            // never changed (it ships); under a camo an untouched part wears the camo's common document, as it would on the canvas.
            // ⭐ Where each part keeps its own camo (a soldier's stock looks), the part's copy and common document are
            // those of ITS camo: an untouched torso on the Ninja look is drawn Ninja around the legs being edited.
            var s_PerSubject = NativePerSubject(s_Key);
            var s_PartNative = s_PerSubject ? s_Tab.NativeOfSubject(s_Key) : s_Tab.NativeKey;
            var s_PartSet = s_Tab.Set(s_PartNative);
            var s_Document = string.Equals(s_Key, m_DocumentSubject, StringComparison.OrdinalIgnoreCase) ? DocumentGraph
                : s_PartSet.Subjects.TryGetValue(s_Key, out var s_Own) ? s_Own.Graph
                // ⭐ …or the one of ANOTHER tab of changes (a tab per part — his helmet changed in its own tab is drawn with
                // its change around the torso being edited in another; the same documents a skin takes, ChangedDocumentsUnder)
                : s_PerSubject && PartDocumentElsewhere(s_Key, s_Sections[i].Shader) is { } s_Elsewhere ? s_Elsewhere
                // (a part on its own camo, untouched: that camo's common document; as shipped, none — it ships)
                : s_PerSubject ? (s_PartNative.Length > 0 ? s_PartSet.Common : null)
                : s_Factory ? null
                : s_Tab.Common;
            if (s_Document == null || !s_Document.TargetShader.Equals(s_Sections[i].Shader, StringComparison.OrdinalIgnoreCase) ||
                s_Document.IsMaterialOff(s_Of, s_Id))
                continue;

            // as shipped (the part's own camo where each keeps one, else the canvas's), only a document that draws differently from the
            // game's own translation (an edited one)
            if ((s_PerSubject ? s_PartNative.Length == 0 : s_Factory) && s_Document.NativeCamo == null &&
                PristineOf(s_Document.TargetShader) is { } s_Pristine && DrawsTheSame(s_Document, s_Pristine))
                continue;

            s_Parts.Add((i, s_Of, s_Id, s_Key, s_Document));
        }

        var s_ViewKey = $"{m_PreviewMesh}|{m_Preview.ForeignGeneration}|{s_Factory}|" + string.Join(";", s_Parts.Select(p_P =>
            $"{p_P.Section}:{p_P.Key}:{LogicFingerprint(p_P.Document)}:{p_P.Document.NativeCamo}:{CustomTextureSignature(p_P.Document)}:" +
            string.Join(",", ValuesOfGraph(p_P.Document).OrderBy(p_V => p_V.Key, StringComparer.Ordinal).Select(p_V => $"{p_V.Key}={p_V.Value}"))));
        if (string.Equals(m_ContextDressKey, s_ViewKey, StringComparison.Ordinal))
            return;

        foreach (var (s_Section, s_Mesh, s_Id, s_Key, s_Document) in s_Parts)
            if (RegisterContextPart(s_Mesh, s_Id, s_Document, s_Key) is { } s_Name)
                s_Dress[s_Section] = s_Name;

        m_Preview.ContextDress = s_Dress;
        m_ContextDressKey = s_ViewKey;
        SchedulePreview();

        if (s_Parts.Count > 0)
            Log($"Camo: the rest of him drawn with the skin — {s_Dress.Count} section(s) with the document of their own part " +
                $"({string.Join(", ", s_Parts.Select(p_P => p_P.Key.Split('/')[^1]).Distinct())})" +
                (s_Dress.Count < s_Parts.Count ? $"; {s_Parts.Count - s_Dress.Count} could not be compiled and ship as they are." : "."));
    }

    /// <summary>
    /// A soldier part's document held by a tab of CHANGES other than the one on screen — the one it shows, or the one kept for it under the
    /// camo it is on — drawn with a given shader; the first in tab order (the one a skin ships first), or null. No comparison here: what is
    /// held in a tab of changes is a change, and the caller still leaves out, as shipped, one that draws like the game's.
    /// CAMO_CONTEXT_TABS_OLD=1 = none (the rest of him from the tab on screen only, as before).
    /// </summary>
    private ShaderGraph? PartDocumentElsewhere(string p_Key, string p_Shader)
    {
        if (Environment.GetEnvironmentVariable("CAMO_CONTEXT_TABS_OLD") == "1")
            return null;

        for (var t = 0; t < m_Tabs.Count; t++)
        {
            if (t == m_ActiveTab || m_Tabs[t].Original)
                continue;

            var s_Tab = m_Tabs[t];
            var s_Document = string.Equals(s_Tab.DocumentSubject, p_Key, StringComparison.OrdinalIgnoreCase) ? s_Tab.Graph
                : s_Tab.Set(NativeHeldFor(s_Tab, p_Key)).Subjects.TryGetValue(p_Key, out var s_Own) ? s_Own.Graph
                : null;
            if (s_Document != null && s_Document.TargetShader.Equals(p_Shader, StringComparison.OrdinalIgnoreCase))
                return s_Document;
        }

        return null;
    }

    /// <summary>
    /// A part's document compiled for ONE context material: the document's graph against its shader's contract, with that material's own
    /// art and numbers under the document's camo, its typed values and its pictures on top — what the part itself is drawn with when it is
    /// the one on the canvas. Cached by what it is built from. Null when the document does not emit or compile.
    /// </summary>
    private string? RegisterContextPart(string p_Mesh, int p_MaterialId, ShaderGraph p_Document, string p_Key)
    {
        var s_Shader = p_Document.TargetShader;
        if (!m_MaterialEditContracts.TryGetValue(s_Shader, out var s_Contract))
            m_MaterialEditContracts[s_Shader] = s_Contract = ReadContractFromCache(s_Shader);
        if (s_Contract == null)
            return null;

        var s_Emit = new HlslEmitter { Contract = s_Contract }.Emit(p_Document);
        if (!s_Emit.Ok)
            return null;

        var s_Native = p_Document.NativeCamo;
        var s_Values = new Dictionary<string, string>(
            m_CamoValuesForMaterial?.Invoke(p_Mesh, s_Shader, s_Native, p_MaterialId) ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (s_Constant, s_Text) in ValuesOfGraph(p_Document))
            s_Values[s_Constant] = s_Text;

        var s_Name = $"{s_Shader}#part:{p_Key}#{p_Mesh}#{p_MaterialId}";
        var s_Built = $"{m_Preview.ForeignGeneration}|{s_Emit.Hlsl.Length}:{s_Emit.Hlsl.GetHashCode()}|{s_Native}|{CustomTextureSignature(p_Document)}";
        if (m_ContextPartBuilt.TryGetValue(s_Name, out var s_Was) && s_Was == s_Built && m_Preview.HasForeignShader(s_Name))
        {
            m_Preview.SetForeignValues(s_Name, s_Values);
            return s_Name;
        }

        try
        {
            using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                s_Emit.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);
            if (s_Compiled.Bytecode == null)
                return null;

            var s_Slots = m_CamoSlotsForMaterial?.Invoke(p_Mesh, s_Shader, s_Native, p_MaterialId);
            var s_Textures = s_Slots is { Count: > 0 }
                ? DecodeCachedTextures(s_Slots, TexCacheDir)
                : LoadForeignTexturesFromCache(SlotMapPath(s_Shader), TexCacheDir, p_Mesh);

            // the document's pictures on top, at the registers its nodes read — what that part draws with on the canvas
            foreach (var s_Picture in CustomTextureBlocks(p_Document))
            {
                s_Textures.RemoveAll(p_T => p_T.Slot == s_Picture.Slot);
                s_Textures.Add((s_Picture.Slot, s_Picture.Pixels, s_Picture.Width, s_Picture.Height, s_Picture.Srgb));
            }

            if (m_Preview.RegisterForeignShader(s_Name, s_Compiled.Bytecode.Data, s_Textures, s_Values, SamplersForShader(s_Shader)) != null)
                return null;

            m_ContextPartBuilt[s_Name] = s_Built;
            return s_Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A document as it counts for "was anything changed on this subject": the view of it (which piece it was saved on, node positions)
    /// and the variation drafts left out — moving a node is not an edit.
    /// </summary>
    /// <summary>
    /// What a document carries beside its graph — its materials' own graphs, the materials switched off, its stickers — as one text (empty
    /// collections are none, as in SubjectFingerprint). DrawsTheSame (HLSL, values, pictures) does not see any of it.
    /// </summary>
    private static string ExtrasFingerprint(ShaderGraph p_Graph) => new ShaderGraph
    {
        MaterialGraphs = p_Graph.MaterialGraphs is { Count: > 0 } ? p_Graph.MaterialGraphs : null,
        MaterialsOff = p_Graph.MaterialsOff is { Count: > 0 } ? p_Graph.MaterialsOff : null,
        Stickers = p_Graph.Stickers is { Count: > 0 } ? p_Graph.Stickers : null,
    }.ToJson();

    /// <summary>
    /// Whether a subject's document was changed against what it starts from, as the tabs of a soldier's parts and his skin's bake read it:
    /// its graph drawn otherwise (DrawsTheSame) — ⭐ or anything changed beside the graph (review 2026-09-29, B7: a part changed only on a
    /// material's own graph or with stickers counted as untouched — its button led away from the tab holding it, and his bake never said
    /// that those do not ship in a skin). CAMO_EXTRAS_UNSEEN_OLD=1 = the graph alone, as before.
    /// </summary>
    private bool SubjectChanged(ShaderGraph p_Document, ShaderGraph p_Start) =>
        !DrawsTheSame(p_Document, p_Start) ||
        (Environment.GetEnvironmentVariable("CAMO_EXTRAS_UNSEEN_OLD") != "1" && ExtrasFingerprint(p_Document) != ExtrasFingerprint(p_Start));

    private static string SubjectFingerprint(ShaderGraph p_Graph)
    {
        var s_Copy = ShaderGraph.FromJson(p_Graph.ToJson());
        s_Copy.PreviewMesh = null;
        s_Copy.PreviewWeapon = null;
        s_Copy.Variations = null;

        // ⛔ AN EMPTY COLLECTION IS NONE (measured 2026-09-25: showing the M416's as-shipped graph gives the document an EMPTY sticker
        // list — ShareStickersWithView — and an original tab that had adopted the F2000 first, with none, forked on a plain pick)
        if (s_Copy.Stickers is { Count: 0 })
            s_Copy.Stickers = null;
        if (s_Copy.MaterialGraphs is { Count: 0 })
            s_Copy.MaterialGraphs = null;
        if (s_Copy.MaterialsOff is { Count: 0 })
            s_Copy.MaterialsOff = null;
        if (s_Copy.AccessoryValues is { Count: 0 })
            s_Copy.AccessoryValues = null;
        foreach (var s_Node in s_Copy.Nodes)
        {
            s_Node.X = 0;
            s_Node.Y = 0;
        }

        return s_Copy.ToJson();
    }

    /// <summary>
    /// ⭐ WHAT EVERY SUBJECT OF A TAB SHARES (the simple mode's numbers stay common; only the graph — nodes and wires — and the
    /// materials are per subject): the simple form's camo — the pattern on the camo register's node, the
    /// native camo picked, CamoTiling and WearAmount — is ONE for the tab; what a subject keeps of its own is the rest of its document
    /// (the nodes, the wires, the other values typed on nodes, its materials' graphs). Copied from the document being left into the
    /// tab's common one and every other subject's, so a change of pattern or tiling alone never makes a subject a document of its own.
    /// </summary>
    private static readonly string[] s_SharedCamoConstants = { "CamoTiling", "WearAmount" };

    private void ShareCamoSettings(EditorTab p_Tab, ShaderGraph p_From)
    {
        // an original tab keeps nothing (EditorTab.Original); and the settings are the camo's: shared under the SAME camo of the
        // picker only (under another one, ABU stays original)
        if (p_Tab.Original)
            return;

        var s_Set = p_Tab.Set(NativeKeyOf(p_From));
        foreach (var s_To in new[] { s_Set.Common }.Concat(s_Set.Subjects.Values.Select(p_S => p_S.Graph)))
            if (s_To != null && !ReferenceEquals(s_To, p_From))
                CopyCamoSettings(p_From, s_To);
    }

    /// <summary>
    /// The document a subject STARTS from under a camo of a tab when it has one of its own (m_SubjectPresetFor — the RHIB): that graph with
    /// the camo's shared settings on it (the pattern, the native camo, the simple form's numbers: the tab's, ShareCamoSettings' rule) and
    /// the camo's key. It stands where the camo's common document stands for every other subject: what the subject shows while nothing was
    /// changed on it, what a change is measured against, and what the bake takes. Null when the subject has none, or when its own was
    /// authored on another preset than the one this tab's camo draws with.
    /// </summary>
    private ShaderGraph? SubjectPresetIn(EditorTab p_Tab, string? p_Subject, string p_Native)
    {
        if (p_Subject is not { Length: > 0 })
            return null;

        // ⭐ a soldier part's starting document is for its AS SHIPPED look (the default for as shipped only — its mask
        // carries that look's colours): each of his other looks keeps its own mask and colours. A vehicle's is for every camo (the RHIB's
        // mask cuts where any camo goes).
        if (p_Native.Length > 0 && NativePerSubject(p_Subject))
            return null;

        if (m_SubjectPresetFor?.Invoke(p_Subject) is not { } s_Own)
            return null;

        var s_Common = p_Tab.Set(p_Native).Common;
        if (s_Common != null && !string.Equals(s_Common.TargetShader, s_Own.TargetShader, StringComparison.OrdinalIgnoreCase))
            return null;

        // (only what the tab's common document HAS: a pattern or a number nobody set on the tab left the subject's own starting ones in
        // place — the RHIB's CamoTiling 10 and WearAmount 4.4, the Skidloader's 8.8 were wiped from its original; review 2026-09-29.
        // CAMO_PRESET_SETTINGS_OLD=1 = copied whole, as before)
        if (s_Common != null)
            CopyCamoSettings(s_Common, s_Own, Environment.GetEnvironmentVariable("CAMO_PRESET_SETTINGS_OLD") != "1");
        s_Own.NativeCamo = p_Native.Length == 0 ? null : p_Native;
        return s_Own;
    }

    /// <param name="p_OnlyWhatItHas">Copy only the settings the source carries: a setting it lacks is left as the target has it, instead of
    /// being taken off (a subject's own starting document under the tab's common one).</param>
    private void CopyCamoSettings(ShaderGraph p_From, ShaderGraph p_To, bool p_OnlyWhatItHas = false)
    {
        p_To.NativeCamo = p_From.NativeCamo;

        // the pattern: the custom picture on the node(s) sampling the camo register — each document's OWN camo register: a subject's starting
        // document on another shader keeps its pattern elsewhere (the Rhino's van body at t6, where the mud's t2 is its specular map)
        if (m_CamoRegisterFor?.Invoke(p_From.TargetShader) is { } s_Register)
        {
            var s_ToRegister = string.Equals(p_From.TargetShader, p_To.TargetShader, StringComparison.OrdinalIgnoreCase)
                ? s_Register
                : m_CamoRegisterFor?.Invoke(p_To.TargetShader);
            bool OnCamo(GraphNode p_Node, string? p_At) => TextureNodeKinds.Contains(p_Node.Kind) && p_Node.GetParam("Register") == p_At;
            var s_Pattern = p_From.Nodes.Where(p_N => OnCamo(p_N, s_Register))
                .Select(p_N => p_N.Params.TryGetValue("CustomTexture", out var s_Custom) ? s_Custom : null)
                .FirstOrDefault(p_C => !string.IsNullOrEmpty(p_C));
            foreach (var s_Node in p_To.Nodes.Where(p_N => s_ToRegister != null && OnCamo(p_N, s_ToRegister)))
                if (s_Pattern != null)
                    s_Node.Params["CustomTexture"] = s_Pattern;
                else if (!p_OnlyWhatItHas)
                    s_Node.Params.Remove("CustomTexture");
        }

        // the simple form's numbers, as typed on their nodes
        foreach (var s_Name in s_SharedCamoConstants)
        {
            var s_Value = p_From.Nodes
                .Where(p_N => p_N.Kind == "ExternalConstant" && BareExternalName(p_N).Equals(s_Name, StringComparison.OrdinalIgnoreCase))
                .Select(p_N => p_N.Params.TryGetValue("PreviewValue", out var s_Typed) ? s_Typed : null)
                .FirstOrDefault(p_V => !string.IsNullOrEmpty(p_V));
            foreach (var s_Node in p_To.Nodes.Where(p_N => p_N.Kind == "ExternalConstant" &&
                                                         BareExternalName(p_N).Equals(s_Name, StringComparison.OrdinalIgnoreCase)))
                if (s_Value != null)
                    s_Node.Params["PreviewValue"] = s_Value;
                else if (!p_OnlyWhatItHas)
                    s_Node.Params.Remove("PreviewValue");
        }
    }

    /// <summary>
    /// Moves a STORED tab (not the live window) to another subject: the document it holds is kept as its subject's own when it differs
    /// from the tab's common one (or dropped when it does not), and the tab takes the new subject's own document — or a fresh copy of
    /// the common one. The tab's shared camo settings go along first (ShareCamoSettings). False when the tab already holds that
    /// subject's document or has no subject to leave yet.
    /// </summary>
    private bool MoveTabToSubject(EditorTab p_Tab, string p_Subject)
    {
        if (string.Equals(p_Tab.DocumentSubject, p_Subject, StringComparison.OrdinalIgnoreCase))
            return false;

        if (p_Tab.DocumentSubject is not { Length: > 0 } || p_Tab.Common == null)
        {
            // nothing shown on it yet: the document is the one the subject starts from, and the common one is taken from it
            p_Tab.DocumentSubject = p_Subject;
            p_Tab.Common ??= ShaderGraph.FromJson(p_Tab.Graph.ToJson());
            return false;
        }

        // under the camo the document being left is on (a subject change keeps the camo: it is the tab's, for every subject) — ⭐ unless the
        // studio keeps one per subject (a soldier's parts, each on its own stock look): then the new subject comes back on ITS camo
        ParkTabDocument(p_Tab);
        TakeTabDocument(p_Tab, p_Subject, NativePerSubject(p_Subject) ? p_Tab.NativeOfSubject(p_Subject) : p_Tab.NativeKey);
        return true;
    }

    /// <summary>
    /// Whether a subject keeps its OWN camo of the picker instead of the tab's (the studio's answer — a soldier's parts, per part).
    /// False for everything else, which is every weapon, attachment and vehicle, as it always was.
    /// </summary>
    private Func<string, bool>? m_CamoNativePerSubject;

    private bool NativePerSubject(string? p_Subject) => p_Subject != null && m_CamoNativePerSubject?.Invoke(p_Subject) == true;

    /// <summary>The camo a STORED tab holds a subject's document under: its own where the subject keeps one, else the tab's.</summary>
    private string NativeHeldFor(EditorTab p_Tab, string p_Subject) =>
        NativePerSubject(p_Subject) ? p_Tab.NativeOfSubject(p_Subject) : p_Tab.NativeKey;

    /// <summary>
    /// Keeps a STORED tab's document as its subject's own under ITS camo (the document says which: a camo picked changes it, and so does
    /// "as shipped" moving to the basic camo on a change) when it differs from that camo's common one — never in an original tab, which
    /// keeps nothing — and makes that camo the one the tab is on. The camo's shared settings go along first (ShareCamoSettings).
    /// </summary>
    private void ParkTabDocument(EditorTab p_Tab)
    {
        p_Tab.NativeKey = NativeKeyOf(p_Tab.Graph);
        if (p_Tab.DocumentSubject is not { Length: > 0 } s_Leaving)
            return;

        // ⛔ WHERE EACH SUBJECT KEEPS ITS OWN CAMO (a soldier's parts), THE ONE IT IS PARKED UNDER IS THE ONE IT IS ON FROM NOW (2026-09-29,
        // --soldiersimpletest): a change under "as shipped" moves the document to the basic camo (PromoteToBasicCamo) without telling the
        // tab, which went on reading the part under the camo it was TAKEN on — his torso with a pattern, left for his head, was parked under
        // "basic" and looked for under "as shipped": gone from the bake (ChangedDocumentsUnder), from the rest of him drawn around the head,
        // and back untouched when pressed again. CAMO_PARK_NATIVE_OLD=1 = as before.
        if (NativePerSubject(s_Leaving) && Environment.GetEnvironmentVariable("CAMO_PARK_NATIVE_OLD") != "1")
            p_Tab.SubjectNatives[s_Leaving] = p_Tab.NativeKey;

        var s_Set = p_Tab.Set(p_Tab.NativeKey);
        ShareCamoSettings(p_Tab, p_Tab.Graph);
        // (measured against what the subject STARTS from: its own document when it has one, else the camo's common one)
        var s_Start = SubjectPresetIn(p_Tab, s_Leaving, p_Tab.NativeKey) ?? s_Set.Common;
        if (!p_Tab.Original && s_Set.Common != null && SubjectFingerprint(p_Tab.Graph) != SubjectFingerprint(s_Start!))
            s_Set.Subjects[s_Leaving] = (p_Tab.Graph, p_Tab.Undo, p_Tab.Redo);
        else
            s_Set.Subjects.Remove(s_Leaving);
    }

    /// <summary>
    /// A STORED tab takes a subject's document under a camo of the picker: its own there, else the one it starts from (its own starting
    /// document — SubjectPresetIn — or a fresh copy of that camo's common one).
    /// </summary>
    private void TakeTabDocument(EditorTab p_Tab, string p_Subject, string p_Native)
    {
        p_Tab.NativeKey = p_Native;
        // (the camo this subject is on, for a family where each subject keeps its own — see EditorTab.SubjectNatives)
        p_Tab.SubjectNatives[p_Subject] = p_Native;
        var s_Set = p_Tab.Set(p_Native);
        if (s_Set.Subjects.TryGetValue(p_Subject, out var s_Own))
            (p_Tab.Graph, p_Tab.Undo, p_Tab.Redo) = s_Own;
        else
        {
            p_Tab.Graph = SubjectPresetIn(p_Tab, p_Subject, p_Native) ?? ShaderGraph.FromJson((s_Set.Common ?? p_Tab.Graph).ToJson());
            p_Tab.Graph.NativeCamo = p_Native.Length == 0 ? null : p_Native;
            p_Tab.Undo = Array.Empty<string>();
            p_Tab.Redo = Array.Empty<string>();
        }

        p_Tab.DocumentSubject = p_Subject;
    }

    /// <summary>
    /// Moves a STORED tab to another camo of the picker, on the same subject (ABU stays original — see EditorTab.Natives).
    /// False when the tab has no subject yet or is already on that camo.
    /// </summary>
    private bool MoveTabToNative(EditorTab p_Tab, string p_Native)
    {
        if (p_Tab.Common == null || p_Tab.DocumentSubject is not { Length: > 0 } s_Subject ||
            string.Equals(NativeKeyOf(p_Tab.Graph), p_Native, StringComparison.OrdinalIgnoreCase))
            return false;

        ParkTabDocument(p_Tab);
        TakeTabDocument(p_Tab, s_Subject, p_Native);
        return true;
    }

    /// <summary>
    /// The live window's document follows the camo picked (see EditorTab.Natives): the one on the canvas is parked under its own camo and
    /// the subject's document under the new one (its own, or a fresh copy of that camo's common one) takes the canvas. Whether it did.
    /// </summary>
    private bool FollowNativeDocument(string? p_Native)
    {
        if (!CamoMode || m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count || m_DocumentSubject == null)
            return false;

        var s_Tab = m_Tabs[m_ActiveTab];
        var s_Key = p_Native ?? "";
        if (s_Tab.Common == null || string.Equals(NativeKeyOf(DocumentGraph), s_Key, StringComparison.OrdinalIgnoreCase))
            return false;

        var s_Mesh = m_PreviewMesh;
        SaveActiveTab();
        m_PreviewMesh = s_Mesh;
        if (!MoveTabToNative(s_Tab, s_Key))
            return false;

        LoadGraph(s_Tab.Graph);
        Canvas.RestoreUndo(s_Tab.Undo, s_Tab.Redo);
        m_DocumentSubject = s_Tab.DocumentSubject;
        if (!s_Tab.Original)
            Log($"Camo: {s_Mesh?.Split('/')[^1]} under this camo shows " +
                (s_Tab.Subjects.ContainsKey(m_DocumentSubject ?? "") ? "what was changed on it under this camo earlier." : "the camo as the game ships it — nothing changed on it under this camo yet."));
        return true;
    }

    /// <summary>
    /// The live window's document follows the subject being put on screen (see EditorTab.Subjects): the one on the canvas is parked
    /// with the tab as its subject's, and the new subject's own document (or a fresh copy of the tab's common one) takes the canvas.
    /// Returns whether the canvas changed document.
    /// </summary>
    private bool FollowSubjectDocument(string p_Subject)
    {
        if (!CamoMode || m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count ||
            string.Equals(m_DocumentSubject, p_Subject, StringComparison.OrdinalIgnoreCase))
            return false;

        var s_Tab = m_Tabs[m_ActiveTab];
        if (m_DocumentSubject == null || s_Tab.Common == null)
        {
            // the tab's first subject: its document is the common one's starting point
            m_DocumentSubject = p_Subject;
            s_Tab.DocumentSubject = p_Subject;
            s_Tab.Common ??= ShaderGraph.FromJson(DocumentGraph.ToJson());
            return false;
        }

        var s_Mesh = m_PreviewMesh;
        SaveActiveTab();
        m_PreviewMesh = s_Mesh;
        var s_Leaving = s_Tab.DocumentSubject;
        if (!MoveTabToSubject(s_Tab, p_Subject))
            return false;

        LoadGraph(s_Tab.Graph);
        Canvas.RestoreUndo(s_Tab.Undo, s_Tab.Redo);
        m_DocumentSubject = p_Subject;
        Log($"Camo: {p_Subject.Split('/')[^1]} shows ITS OWN camo document" +
            (s_Tab.Subjects.ContainsKey(p_Subject) ? " (changed on it earlier)"
                : m_SubjectPresetFor?.Invoke(p_Subject) != null ? " (the one it starts from in the studio, untouched on it so far)"
                : " (the tab's common one, untouched on it so far)") +
            (s_Leaving != null && s_Tab.Subjects.ContainsKey(s_Leaving) ? $"; {s_Leaving.Split('/')[^1]}'s changes stay with it." : "."));
        return true;
    }

    /// <summary>Whether a look at the active original tab is already on its way (ScheduleOriginalCheck).</summary>
    private bool m_OriginalCheckPosted;

    /// <summary>
    /// Asks, once whatever is running now has finished, whether the active ORIGINAL tab was changed (CheckOriginalTab). Called from the two
    /// places every change goes through (SchedulePreview, PushExternalValues); the look itself compares documents, so a call that changed
    /// nothing costs a comparison and moves nothing.
    /// </summary>
    private void ScheduleOriginalCheck()
    {
        if (!CamoMode || m_OriginalCheckPosted)
            return;

        m_OriginalCheckPosted = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(CheckOriginalTab));
    }

    /// <summary>The active tab is an original one and what is on screen in it is no longer the game's: the change moves to a tab of its own.</summary>
    private void CheckOriginalTab()
    {
        m_OriginalCheckPosted = false;
        if (!CamoMode || m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count || m_DocumentSubject == null)
            return;

        var s_Tab = m_Tabs[m_ActiveTab];
        // a pick in flight is not a change; it asks again when it is done
        if (!s_Tab.Original || s_Tab.Base == null || LastMeshPick is { IsCompleted: false })
            return;

        if (LiveDiffersFromOriginal(s_Tab) is { } s_What)
            ForkOriginalTab(s_Tab, s_What);
    }

    /// <summary>
    /// What was changed on screen in an original tab, or null while it is all the game's: a material's own graph on the canvas, the
    /// as-shipped graph, or the camo document itself (wires, nodes, values, pattern, numbers, stickers, materials kept off…) against its
    /// camo's common one. Moving a node or picking something is not a change.
    /// </summary>
    private string? LiveDiffersFromOriginal(EditorTab p_Tab)
    {
        if (m_MaterialEditKey != null && m_MaterialOpened != null && !DrawsTheSame(Canvas.Graph, m_MaterialOpened))
            return "a material's graph";

        if (FactoryGraphEdited)
            return "the as-shipped graph";

        var s_Document = DocumentGraph;
        var s_Common = p_Tab.Set(NativeKeyOf(s_Document)).Common;
        // (against what the subject STARTS from: its own document is its original, not a change — the RHIB, SubjectPresetIn)
        var s_Start = SubjectPresetIn(p_Tab, m_DocumentSubject, NativeKeyOf(s_Document)) ?? s_Common;
        return s_Common != null && SubjectFingerprint(s_Document) != SubjectFingerprint(s_Start!) ? "the camo" : null;
    }

    /// <summary>
    /// ⭐ THE FIRST CHANGE IN AN ORIGINAL TAB OPENS A TAB OF ITS OWN (the moment the user changes a value it opens as a new tab, so
    /// opening the same preview again shows the original; any change to an original opens a new tab, as shipped included).
    /// The window as it is — the change, the canvas,
    /// a material open, the undo history — becomes the NEW tab, beside the original; in the original's place goes a fresh copy of it, on
    /// the same subject and the same camo it was showing, with nothing changed and no history.
    /// </summary>
    private void ForkOriginalTab(EditorTab p_Tab, string p_What)
    {
        var s_Index = m_ActiveTab;

        // the camo the original was on: the document's may have moved (as shipped goes to the basic camo when a pattern is loaded)
        var s_Was = p_Tab.NativeKey;
        var s_Original = new EditorTab
        {
            Base = ShaderGraph.FromJson(p_Tab.Base!.ToJson()),
            Original = true,
            NativeKey = s_Was,
            GraphPath = p_Tab.GraphPath,
            Contract = m_MaterialParkedContract ?? m_ParkedContract ?? m_Contract,
            Variation = m_Variation,
            Shape = m_Preview.Shape,
            PreviewMesh = m_PreviewMesh,
            PreviewWeapon = m_PreviewWeapon,
            DocumentSubject = m_DocumentSubject,
        };

        foreach (var (s_Key, s_Set) in p_Tab.Natives)
            if (s_Set.Common != null)
                s_Original.Natives[s_Key] = new CamoDocuments { Common = ShaderGraph.FromJson(s_Set.Common.ToJson()) };

        // (and the camo each subject was on, where each keeps its own: the original goes on showing every part as it was)
        foreach (var (s_Of, s_On) in p_Tab.SubjectNatives)
            s_Original.SubjectNatives[s_Of] = s_On;

        s_Original.Graph = SubjectPresetIn(s_Original, m_DocumentSubject, s_Was) ??
                           ShaderGraph.FromJson((s_Original.Set(s_Was).Common ?? DocumentGraph).ToJson());

        // what the new tab is called: the subject and the camo it was changed on
        var s_Accessory = AccessoryList.SelectedItem as CamoWeapon;
        // ⭐ a soldier's part is named by the soldier and the part (a tab per part, reached by its button — the tab was
        // "us_cap03_mesh · Default basic camo", a mesh no button names)
        // (…and his first-person model on a shader of its own — Aftermath's arms on characterroot, the torso on characterroot_xp4 — is a document
        // of its own and a tab of its own: said, or two tabs read "Upper body"; review 2026-09-29. Where both share the shader it is the part's
        // one document, whichever view it was changed in, and no word is added.)
        var s_OwnFirstPerson = s_Accessory is { Tag: CockpitTag } &&
                               AccessoryList.Items.OfType<CamoWeapon>().FirstOrDefault(p_A => p_A.Part == s_Accessory.Part && p_A.Tag != CockpitTag) is { } s_Outside &&
                               !string.Equals(m_CamoShaderFor?.Invoke(s_Outside.Mesh), DocumentGraph.TargetShader, StringComparison.OrdinalIgnoreCase);
        var s_Subject = NativePerSubject(m_DocumentSubject) && s_Accessory is { Part.Length: > 0 } && MeshList.SelectedItem is CamoWeapon s_Soldier
            ? $"{s_Soldier.Display} · {s_Accessory.Part}{(s_OwnFirstPerson ? " (first person)" : "")}"
            : s_Accessory is { Tag.Length: > 0 }
                ? s_Accessory.Display.Split('·')[^1].Trim()
                : (MeshList.SelectedItem as CamoWeapon)?.Display ?? m_DocumentSubject!.Split('/')[^1];
        var s_Camo = DocumentGraph.NativeCamo is { } s_Native
            ? NativeCamoList.Items.OfType<NativeCamoChoice>().FirstOrDefault(p_C => string.Equals(p_C.Key, s_Native, StringComparison.OrdinalIgnoreCase))?.Display ?? s_Native
            : "As shipped";

        p_Tab.Original = false;
        p_Tab.NativeKey = NativeKeyOf(DocumentGraph);
        p_Tab.Title = $"{s_Subject} · {s_Camo}";

        // ⛔ the tab of changes is ITS part's (a tab per part of a soldier): the looks the original was browsed on for his OTHER parts stay with
        // the original — carried along, each one read as that part "on a look" from this tab too, and his skin took a look only browsed, or
        // took it twice (review 2026-09-29, B1). CAMO_FORK_NATIVES_OLD=1 = every one copied, as before.
        if (NativePerSubject(m_DocumentSubject) && Environment.GetEnvironmentVariable("CAMO_FORK_NATIVES_OLD") != "1")
            foreach (var s_Other in p_Tab.SubjectNatives.Keys.Where(p_K => !string.Equals(p_K, m_DocumentSubject, StringComparison.OrdinalIgnoreCase)).ToList())
                p_Tab.SubjectNatives.Remove(s_Other);

        m_Tabs.Insert(s_Index, s_Original);
        m_ActiveTab = s_Index + 1;
        RefreshTabBar();
        SyncReferenceNote();
        Log($"Camo: {p_What} changed on {s_Subject} under '{s_Camo}' — the change goes on in a new tab, '{p_Tab.Title}'; the original " +
            "tab stays exactly as the game ships it.");
    }

    private readonly List<EditorTab> m_Tabs = new();
    private int m_ActiveTab = -1;

    /// <summary>Parks the window's live state into the active tab before another one takes the canvas.</summary>
    private void SaveActiveTab()
    {
        if (m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count)
            return;

        // The tab keeps the camo DOCUMENT, never a material's graph being edited (that is saved into the
        // document first) nor the weapon's own graph shown in its place.
        if (m_MaterialEditKey != null)
        {
            SaveEditedMaterial();
            LeaveMaterialEdit(false);
        }

        RestoreCamoGraph();

        var s_Tab = m_Tabs[m_ActiveTab];
        s_Tab.Graph = Canvas.Graph;
        (s_Tab.Undo, s_Tab.Redo) = Canvas.SnapshotUndo();
        s_Tab.Contract = m_Contract;
        s_Tab.Variation = m_Variation;
        s_Tab.GraphPath = m_GraphPath;
        s_Tab.Shape = m_Preview.Shape;
        s_Tab.PreviewMesh = m_PreviewMesh;
        s_Tab.PreviewWeapon = m_PreviewWeapon;
        s_Tab.DocumentSubject = m_DocumentSubject;
    }

    /// <summary>
    /// The subject (weapon, vehicle) the mesh on screen was dressed under — the picker's entry at that moment.
    /// Kept apart from the picker because a pick changes the picker BEFORE the tab being left is saved.
    /// </summary>
    private string? m_PreviewWeapon;

    private void ActivateTab(int p_Index)
    {
        m_ActiveTab = p_Index;
        var s_Tab = m_Tabs[p_Index];

        // Whatever was parked belongs to the tab being left (saved by SaveActiveTab); the canvas takes
        // this tab's document, and its own weapon decides again whether the worn graph stands in.
        m_ParkedCamoGraph = null;
        m_ParkedContract = null;
        m_ShownFactoryPreset = null;
        m_ShownFactoryMesh = null;
        SyncReferenceNote();
        m_MaterialParkedCamo = null;
        m_MaterialParkedContract = null;
        m_MaterialParkedTarget = null;
        m_MaterialEditKey = null;
        m_MaterialEditSection = -1;
        m_Preview.TargetSectionOnly = null;
        m_Preview.SectionOverrides = new Dictionary<int, string>();

        Canvas.Graph = s_Tab.Graph;
        Canvas.RestoreUndo(s_Tab.Undo, s_Tab.Redo);
        m_DocumentSubject = s_Tab.DocumentSubject;
        m_Contract = s_Tab.Contract;
        m_Variation = s_Tab.Variation;
        m_GraphPath = s_Tab.GraphPath;

        TargetShaderBox.Text = s_Tab.Graph.TargetShader;
        GraphNameBox.Text = s_Tab.Graph.Name;
        m_Preview.SetInterpolatorMeanings(m_Contract?.InterpolatorMeanings);

        // Thumbnails and instance values re-derive from the disk cache (their single source); a target with
        // no cache yet just gets the Settings-decided engine parameters. Custom textures ride on top either
        // way — a tab with no cache still shows the user's own files.
        m_TexturePreviews.Clear();
        m_TextureSrgb.Clear();
        if (CamoMode)
        {
            // ⛔ IN CAMO MODE THE SLOT MAP'S BASE SET IS NEVER THE ANSWER. It belongs to whichever weapon
            // came first in the map (the M4A1, the M2 Browning), so loading it here dressed the picked
            // weapon in another one's art on every tab switch — the recurring "M4A1 textures" symptom. The
            // weapon in the picker says what goes on it, whichever tab took the window.
            //
            // ⭐ AND THE TAB BRINGS ITS OWN WEAPON BACK (switching tabs must take into account the weapon being
            // modified in that tab). A camo is authored ON a weapon, so the
            // weapon is part of the document: switching tabs puts that tab's weapon back on screen and in the
            // picker. It goes through the picker on purpose — that is the one path that loads a weapon, and
            // the pick dresses it for this tab. Only when the tab remembers no weapon (a fresh one) or the
            // same one does the weapon on screen simply get re-dressed for this tab's preset.
            // The tab's own starting camo shows in the picker before anything is dressed for it.
            // A tab of the OTHER family (a vehicle camo clicked while the weapons are listed) brings its family
            // back first: its subject is only selectable from its own picker, and the family refills the
            // native-camo picker the tab's choice is shown in.
            // ⛔ The piece AND the subject it was shown under: the same attachment on another weapon is another
            // view (EditorTab.PreviewWeapon).
            // (…and the same mesh under ANOTHER PART's entry — a soldier's first-person model is his sleeves and his trousers: a tab of one
            // clicked while the other is picked kept the other's entry, its part button and its box; review 2026-09-29, B5)
            var s_OtherEntry = NativePerSubject(s_Tab.DocumentSubject) && Environment.GetEnvironmentVariable("CAMO_ENTRY_BY_SUBJECT_OLD") != "1" &&
                               AccessoryList.SelectedItem is CamoWeapon s_Selected &&
                               s_Selected.Mesh.Equals(s_Tab.PreviewMesh ?? "", StringComparison.OrdinalIgnoreCase) &&
                               !string.Equals(EntryKeyOf(s_Selected), s_Tab.DocumentSubject, StringComparison.OrdinalIgnoreCase);
            var s_Elsewhere = s_Tab.PreviewMesh is { Length: > 0 } &&
                              (!string.Equals(s_Tab.PreviewMesh, m_PreviewMesh, StringComparison.OrdinalIgnoreCase) || s_OtherEntry ||
                               (s_Tab.PreviewWeapon is { Length: > 0 } s_TabWeapon &&
                                !string.Equals(s_TabWeapon, (MeshList.SelectedItem as CamoWeapon)?.Mesh, StringComparison.OrdinalIgnoreCase)));
            if (s_Elsewhere)
                ShowFamilyListing(s_Tab.PreviewWeapon ?? s_Tab.PreviewMesh!);

            SyncNativeCamoBox();

            if (s_Elsewhere && SelectCamoWeapon(s_Tab.PreviewMesh!, s_Tab.PreviewWeapon, s_Tab.DocumentSubject))
            {
                // The pick in flight dresses it.
            }
            else
                RedressCamoWeapon();

            // The camo on the canvas changed: whoever owns the camo's own settings (the studio's simple
            // form) puts THIS tab's pattern and numbers back on the preview.
            CamoSimpleShown?.Invoke(this, EventArgs.Empty);
        }
        else if (LoadCachedPreviews() == 0)
        {
            PushInstanceValues(null);
            ApplyCustomTextureOverrides();
        }

        BuildProperties();
        RestoreTabPreview(s_Tab);
        SchedulePreview();
        RefreshTabBar();
        SyncReferenceNote();
    }

    /// <summary>
    /// Puts back the shape — and the mesh — this tab was last previewing.
    ///
    /// ⛔ CACHE-ONLY on purpose: switching tabs must never mount the game. A mesh whose dump is missing or
    /// of an older format falls back to the primitive and FORGETS itself, rather than leaving the mesh icon
    /// lit over an object that is not loaded. The name is restored either way, so the list still shows
    /// which object this tab belongs to.
    /// </summary>
    private async void RestoreTabPreview(EditorTab p_Tab)
    {
        // ⛔⛔ IN CAMO MODE THE WEAPON OWNS THE MESH, NOT THE TAB. A tab remembers the mesh that was on
        // screen when it was LEFT — which, on a preset switch, is already the NEW weapon's — and puts it back
        // when it is returned to. So picking the AUG after the ACR activated the AUG's preset tab, which
        // remembered the ACR, and reloaded the ACR over the AUG that had just been loaded: "I click the AUG
        // and nothing happens". The picker decides what is on screen here; tabs only carry graphs.
        if (CamoMode)
            return;

        m_PreviewMesh = p_Tab.PreviewMesh;

        if (p_Tab.Shape == View.PreviewShape.Mesh && p_Tab.PreviewMesh is { Length: > 0 } s_Mesh)
        {
            var s_File = Path.Combine(MeshCacheDir, $"{Sanitize(s_Mesh)}.rsm");
            if (File.Exists(s_File) && IsCurrentRsm(s_File) && m_Preview.LoadMeshSections(s_File) == null)
            {
                // The target of THIS tab, not the one the mesh was first loaded under: the same object can
                // be open in two tabs for two different shaders of it.
                AimPreviewAt(p_Tab.Graph.TargetShader);
                m_Preview.HideForeignSections = m_Settings.PreviewOnlyEditedShader;
                m_Preview.Shape = View.PreviewShape.Mesh;
                SyncShapeIcons(View.PreviewShape.Mesh);

                if (!m_Settings.PreviewOnlyEditedShader)
                    await RegisterForeignShadersAsync();

                return;
            }

            p_Tab.Shape = View.PreviewShape.Cube;
        }

        m_Preview.Shape = p_Tab.Shape;
        SyncShapeIcons(p_Tab.Shape);
    }

    /// <summary>Lights the one icon that matches the shape actually being rendered.</summary>
    /// <summary>
    /// Whether the mesh section is on screen. It is the mesh ICON's state rather than the preview's shape,
    /// because the two differ for one real moment: the icon pressed with nothing loaded yet, where the panel
    /// has to be visible for a mesh to be picked at all while the preview still shows the primitive.
    /// </summary>
    private bool m_MeshMode;

    /// <summary>
    /// The ONE place the shape toolbar and the mesh section are set from. Three copies of "check this icon,
    /// uncheck the rest" had grown across the handlers, which is how a toolbar ends up disagreeing with what
    /// the preview is actually drawing.
    /// </summary>
    private void SyncShapeIcons(View.PreviewShape p_Shape)
    {
        ShapeSphere.IsChecked = p_Shape == View.PreviewShape.Sphere;
        ShapeCube.IsChecked = p_Shape == View.PreviewShape.Cube;
        ShapeCylinder.IsChecked = p_Shape == View.PreviewShape.Cylinder;
        ShapePlane.IsChecked = p_Shape == View.PreviewShape.Plane;
        ShapeMesh.IsChecked = p_Shape == View.PreviewShape.Mesh || m_MeshMode;
        MeshPanel.Visibility = m_MeshMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenInNewTab(ShaderGraph p_Graph, string? p_Path)
    {
        SaveActiveTab();
        m_Tabs.Add(new EditorTab { Graph = p_Graph, GraphPath = p_Path });
        ActivateTab(m_Tabs.Count - 1);
    }

    private int FindTabByTarget(string p_Target)
    {
        for (var i = 0; i < m_Tabs.Count; i++)
        {
            // The active tab's DOCUMENT (the canvas may hold a material's graph or the no-camo view in its place).
            var s_Graph = i == m_ActiveTab ? DocumentGraph : m_Tabs[i].Graph;
            if (s_Graph.TargetShader.Equals(p_Target, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private void CloseTab(int p_Index)
    {
        if (p_Index < 0 || p_Index >= m_Tabs.Count)
            return;

        // Headless never closes tabs interactively, but the guard keeps a seam from hanging on a box.
        var s_Closing = m_Tabs[p_Index];
        if (!Headless && CloseAsks(p_Index) &&
            MessageBox.Show(this, "This tab has edits. Close it and lose them?", "Close tab",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // (the box is modal, the window is not frozen behind it: the tab is found again by itself, not by where it was — review 2026-09-29, B8)
        p_Index = m_Tabs.IndexOf(s_Closing);
        if (p_Index < 0)
            return;

        m_Tabs.RemoveAt(p_Index);

        if (m_Tabs.Count == 0)
        {
            // The window always shows a document; closing the last tab starts a fresh one (the originals', like the first).
            m_Tabs.Add(new EditorTab { Graph = NewGraphWithRoot(), Original = true });
            m_ActiveTab = -1;
            ActivateTab(0);
            return;
        }

        if (p_Index == m_ActiveTab)
        {
            m_ActiveTab = -1;
            ActivateTab(Math.Min(p_Index, m_Tabs.Count - 1));
        }
        else
        {
            if (p_Index < m_ActiveTab)
                m_ActiveTab--;
            RefreshTabBar();
        }
    }

    /// <summary>
    /// Whether closing a tab asks first: edits are what the undo history records, so an untouched tab closes silently and a worked-on one
    /// asks — ⭐ and in camo mode a tab of changes asks whenever it HOLDS a change, undo or not (review 2026-09-29, B3): a picture or a
    /// number of the simple form leaves no undo step, and neither does a subject's document parked in the tab — they closed without a word.
    /// An original tab keeps nothing and never asks for that. CAMO_CLOSE_ASK_OLD=1 = the undo history alone, as before.
    /// </summary>
    internal bool CloseAsks(int p_Index)
    {
        if (p_Index < 0 || p_Index >= m_Tabs.Count)
            return false;

        var s_Tab = m_Tabs[p_Index];
        var (s_Undo, _) = p_Index == m_ActiveTab ? Canvas.SnapshotUndo() : (s_Tab.Undo, s_Tab.Redo);
        if (s_Undo.Length > 0)
            return true;

        if (!CamoMode || s_Tab.Original || Environment.GetEnvironmentVariable("CAMO_CLOSE_ASK_OLD") == "1")
            return false;

        // a subject's own document kept in it, under any camo of the picker…
        if (s_Tab.Natives.Values.Any(p_S => p_S.Subjects.Count > 0))
            return true;

        // …or the document on it, changed against what its subject starts from
        var s_Document = p_Index == m_ActiveTab ? DocumentGraph : s_Tab.Graph;
        var s_Subject = p_Index == m_ActiveTab ? m_DocumentSubject : s_Tab.DocumentSubject;
        var s_Start = (s_Subject != null ? SubjectPresetIn(s_Tab, s_Subject, NativeKeyOf(s_Document)) : null) ?? s_Tab.Set(NativeKeyOf(s_Document)).Common;
        // (no start yet — a New tab before its first subject — says nothing: the undo history above is its answer)
        return s_Start != null && SubjectFingerprint(s_Document) != SubjectFingerprint(s_Start);
    }

    private string TabTitleOf(int p_Index)
    {
        var s_Tab = m_Tabs[p_Index];
        var s_Graph = p_Index == m_ActiveTab ? DocumentGraph : s_Tab.Graph;
        var s_Target = s_Graph.TargetShader;
        var s_Preset = string.IsNullOrWhiteSpace(s_Target) ? "Untitled" : s_Target.Split('/').Last();
        var s_Named = !string.IsNullOrWhiteSpace(s_Graph.Name) && s_Graph.Name != "Untitled" &&
                      !s_Graph.Name.Equals(s_Preset, StringComparison.OrdinalIgnoreCase);

        // in camo mode the originals say so, and a tab opened by a change is called after it — until the camo is given a name
        if (CamoMode && !s_Named)
        {
            if (s_Tab.Title != null)
                return s_Tab.Title;
            if (s_Tab.Original && s_Tab.DocumentSubject != null)
                return $"Original · {s_Preset}";
        }

        return !string.IsNullOrWhiteSpace(s_Graph.Name) && s_Graph.Name != "Untitled" ? s_Graph.Name : s_Preset;
    }

    private void RefreshTabBar()
    {
        if (TabStrip == null)
            return;

        TabStrip.Children.Clear();

        for (var i = 0; i < m_Tabs.Count; i++)
        {
            var s_Index = i;
            var s_Active = i == m_ActiveTab;

            var s_Title = new TextBlock
            {
                Text = TabTitleOf(i),
                Foreground = new SolidColorBrush(s_Active ? Color.FromRgb(240, 240, 240) : Color.FromRgb(160, 160, 160)),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var s_Close = new TextBlock
            {
                Text = "✕",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10,
                ToolTip = "Close tab (middle-click also closes)",
            };
            s_Close.MouseLeftButtonDown += (_, s_Args) =>
            {
                s_Args.Handled = true;
                CloseTab(s_Index);
            };

            var s_Content = new StackPanel { Orientation = Orientation.Horizontal };
            s_Content.Children.Add(s_Title);
            s_Content.Children.Add(s_Close);

            var s_Tab = new Border
            {
                Background = new SolidColorBrush(s_Active ? Color.FromRgb(50, 50, 58) : Color.FromRgb(38, 38, 42)),
                BorderBrush = new SolidColorBrush(s_Active ? Color.FromRgb(150, 210, 150) : Color.FromRgb(60, 60, 64)),
                BorderThickness = new Thickness(1, 1, 1, s_Active ? 0 : 1),
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Padding = new Thickness(10, 4, 8, 4),
                Margin = new Thickness(0, 0, 2, 0),
                Child = s_Content,
                ToolTip = (i == m_ActiveTab ? Canvas.Graph : m_Tabs[i].Graph).TargetShader is { Length: > 0 } s_Target
                    ? s_Target
                    : "No target shader",
            };
            s_Tab.MouseLeftButtonDown += (_, _) => SwitchToTab(s_Index);
            s_Tab.MouseDown += (_, s_Args) =>
            {
                if (s_Args.ChangedButton != MouseButton.Middle)
                    return;

                s_Args.Handled = true;
                CloseTab(s_Index);
            };

            TabStrip.Children.Add(s_Tab);
        }
    }

    /// <summary>A click on a tab: the one entry for it, so a driver can take the same path the mouse does.</summary>
    internal void SwitchToTab(int p_Index)
    {
        if (p_Index == m_ActiveTab || p_Index < 0 || p_Index >= m_Tabs.Count)
            return;

        SaveActiveTab();
        ActivateTab(p_Index);
    }

    private void OnUndo(object p_Sender, ExecutedRoutedEventArgs p_Args) => DoUndo();
    private void OnRedo(object p_Sender, ExecutedRoutedEventArgs p_Args) => DoRedo();
    private void OnUndoClick(object p_Sender, RoutedEventArgs p_Args) => DoUndo();
    private void OnRedoClick(object p_Sender, RoutedEventArgs p_Args) => DoRedo();

    private void DoUndo()
    {
        if (!Canvas.Undo())
            Log("Nothing left to undo.");
    }

    private void DoRedo()
    {
        if (!Canvas.Redo())
            Log("Nothing left to redo.");
    }

    private void BuildProperties()
    {
        PropertiesPanel.Children.Clear();
        var s_Node = Canvas.Selected;

        if (s_Node == null)
        {
            PropertiesHeader.Text = "Properties";
            PropertiesPanel.Children.Add(new TextBlock { Text = "No node selected.", Margin = new Thickness(0, 4, 0, 0) });
            return;
        }

        // The header names the node the way the canvas draws it: a panel headed with the other vocabulary's
        // label reads as if it belonged to some other node than the one that is selected.
        var s_Selected = s_Node.Def.TitleFor(m_Settings.UdkNodeStyle);
        PropertiesHeader.Text = Canvas.SelectedCount > 1
            ? $"Properties — {s_Selected} ({Canvas.SelectedCount} selected)"
            : $"Properties — {s_Selected}";

        if (Canvas.SelectedCount > 1)
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = $"{Canvas.SelectedCount} nodes selected; editing the last one clicked. " +
                       "Delete removes all of them.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10,
                Opacity = 0.75,
                Margin = new Thickness(0, 2, 0, 6),
            });

        if (s_Node.Def.Description.Length > 0)
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_Node.Def.Description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.85,
                Margin = new Thickness(0, 2, 0, 6),
            });

        // Which CLASS of knob this node is - the difference the game enforces: a baked value is compiled into
        // the shader and only reaches the game through a re-bake, an external one is instance data the game
        // feeds at runtime and varies with no rebake at all.
        var s_IsExternal = s_Node.Kind == "ExternalConstant";
        if (s_IsExternal || s_Node.Def.Params.Count > 0 || s_Node.Def.Inputs.Any(p_P => p_P.IsEditable))
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_IsExternal
                    ? "EXTERNAL — the game feeds this value per material instance/variation at runtime; " +
                      "varying it needs no re-bake."
                    : "BAKED — values here are compiled into the shader; the game only sees changes after " +
                      "Bake. The preview updates live.",
                Foreground = new SolidColorBrush(s_IsExternal
                    ? Color.FromRgb(150, 210, 150)
                    : Color.FromRgb(224, 192, 130)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });

        // ⭐ IN CAMO MODE, WHAT THIS CONSTANT IS — right here, beside the value the user is about to type
        // (a brief explanation shown here as a message, like the one for externals): what it does,
        // what one number means on it, and the range the game itself uses. The studio measured it; the
        // window only shows it.
        if (s_IsExternal && CamoMode &&
            m_CamoPanelNoteFor?.Invoke(s_Node, TargetShaderBox.Text.Trim()) is { Length: > 0 } s_PanelNote)
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_PanelNote,
                Foreground = new SolidColorBrush(Color.FromRgb(190, 230, 190)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

        // For an external parameter: what THIS instance actually feeds it, when the level's data says.
        if (s_Node.Kind == "ExternalConstant")
        {
            var s_Bare = s_Node.GetParam("Name") is var s_Full &&
                         s_Full.StartsWith("external_", StringComparison.Ordinal)
                ? s_Full["external_".Length..]
                : s_Node.GetParam("Name");

            // In camo mode the value is the picked weapon's own material (the studio's set, the same one the
            // preview runs on), never the slot map's — whose variations only cover the meshes one level saw.
            var s_Instance = CamoMode && m_CamoWeaponValues != null
                ? m_CamoWeaponValues.GetValueOrDefault(s_Bare)
                : ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim()))?.Variations is { } s_Vars &&
                  s_Vars.TryGetValue(m_Variation, out var s_Var) &&
                  s_Var.Values?.TryGetValue(s_Bare, out var s_Value) == true
                    ? s_Value
                    : null;

            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_Instance != null
                    ? $"The game feeds: {s_Instance}"
                    : "No instance value in this level's data; the game feeds this at runtime.",
                FontSize = 10,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
            });

            // ⭐ AN EXTERNAL CONSTANT IS AN INPUT — the game hands it a value at run time, so the node had
            // nothing to edit, and in Camo Studio that read as "the green nodes cannot be modified" while
            // the simple form's sliders (which feed these very constants behind the scenes) could. The
            // override is the same thing made visible: the value THIS graph feeds the constant in the
            // preview. It travels with the graph, and the studio's form and this box are two views of it.
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Preview value — the game's own value until you change it; clear the box to go back to it.",
                FontSize = 10,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 2),
            });

            // Pre-filled with the value in force (the game's, as one number), so the user edits from what is
            // there rather than from a blank; Params directly, never GetParam, which answers "0" for a key no
            // palette entry defines. Every box closes its editing session as ONE undo step over the STORED
            // values (what TextChanged expanded and fed), never the box's raw text; see CommitExternalPreviewValue.
            void WireCommit(TextBox p_Box)
            {
                var s_TextOriginal = p_Box.Text;
                var s_StoredOriginal = Live(s_Node).Params.GetValueOrDefault("PreviewValue");
                p_Box.GotKeyboardFocus += (_, _) =>
                {
                    s_TextOriginal = p_Box.Text;
                    s_StoredOriginal = Live(s_Node).Params.GetValueOrDefault("PreviewValue");
                };
                p_Box.LostKeyboardFocus += (_, _) =>
                {
                    if (p_Box.Text == s_TextOriginal)
                        return;

                    var s_StoredNew = Live(s_Node).Params.GetValueOrDefault("PreviewValue");
                    CommitExternalPreviewValue(Live(s_Node), s_StoredOriginal, s_StoredNew);
                    s_TextOriginal = p_Box.Text;
                    s_StoredOriginal = s_StoredNew;
                };
            }

            // ⭐ A TILING IS TWO NUMBERS (tiling along either of the 2 coordinates, each its own individual
            // option): one box per axis, each ONE number, pre-filled with the axis's
            // value in force. The studio says which constants are tilings; everything else keeps the one box.
            if (CamoMode && m_CamoComponentsOf?.Invoke(BareExternalName(Live(s_Node))) == 2)
            {
                var s_Axes = ExternalVectorParts(Live(s_Node));
                var s_BoxX = SelectAllOnFocus(new TextBox { Text = s_Axes[0], Margin = new Thickness(0, 0, 0, 2) });
                var s_BoxY = SelectAllOnFocus(new TextBox { Text = s_Axes[1] });
                s_BoxX.TextChanged += (_, _) => SetExternalPreviewComponents(Live(s_Node), s_BoxX.Text, s_BoxY.Text);
                s_BoxY.TextChanged += (_, _) => SetExternalPreviewComponents(Live(s_Node), s_BoxX.Text, s_BoxY.Text);
                WireCommit(s_BoxX);
                WireCommit(s_BoxY);

                var s_Grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                s_Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                s_Grid.ColumnDefinitions.Add(new ColumnDefinition());
                s_Grid.RowDefinitions.Add(new RowDefinition());
                s_Grid.RowDefinitions.Add(new RowDefinition());
                var s_LabelX = new TextBlock { Text = "X", FontSize = 10, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
                var s_LabelY = new TextBlock { Text = "Y", FontSize = 10, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(s_LabelX, 0); Grid.SetColumn(s_LabelX, 0);
                Grid.SetRow(s_BoxX, 0); Grid.SetColumn(s_BoxX, 1);
                Grid.SetRow(s_LabelY, 1); Grid.SetColumn(s_LabelY, 0);
                Grid.SetRow(s_BoxY, 1); Grid.SetColumn(s_BoxY, 1);
                s_Grid.Children.Add(s_LabelX);
                s_Grid.Children.Add(s_BoxX);
                s_Grid.Children.Add(s_LabelY);
                s_Grid.Children.Add(s_BoxY);
                PropertiesPanel.Children.Add(s_Grid);
                PropertiesPanel.Children.Add(new TextBlock
                {
                    Text = "X repeats the pattern along the weapon, Y across it; the same number in both keeps it square. " +
                           "(Simple mode's Tiling slider sets both.)",
                    FontSize = 10,
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 4),
                });
            }
            else
            {
                var s_ValueBox = SelectAllOnFocus(new TextBox { Text = ExternalValueDisplay(Live(s_Node)) });
                s_ValueBox.TextChanged += (_, _) => SetExternalPreviewValue(Live(s_Node), s_ValueBox.Text);
                WireCommit(s_ValueBox);
                PropertiesPanel.Children.Add(s_ValueBox);
            }
        }

        foreach (var s_Param in s_Node.Def.Params)
        {
            PropertiesPanel.Children.Add(new TextBlock { Text = s_Param.Name, Margin = new Thickness(0, 8, 0, 2) });
            PropertiesPanel.Children.Add(BuildParamEditor(s_Node, s_Param));
        }

        BuildInputDefaults(s_Node);
        BuildCustomTexturePicker(s_Node);
        BuildVariationPicker(s_Node);

        var s_Unavailable = s_Node.Def.Inputs
            .Select(p_P => (Port: p_P, Reason: p_P.WhyUnavailable(s_Node)))
            .Where(p_P => p_P.Reason != null)
            .ToList();

        if (s_Unavailable.Count > 0)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "Inputs currently ignored", Margin = new Thickness(0, 14, 0, 2), FontWeight = FontWeights.Bold,
            });

            foreach (var (s_Port, s_Reason) in s_Unavailable)
                PropertiesPanel.Children.Add(new TextBlock
                {
                    Text = $"{s_Port.Name}: {s_Reason}",
                    FontSize = 10,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3),
                });
        }

        if (s_Node.Def.Params.Count == 0 && s_Unavailable.Count == 0)
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "This node has no parameters.", Margin = new Thickness(0, 4, 0, 0),
            });

        BuildDescriptionEditor(s_Node);
    }

    /// <summary>The node kinds that sample a texture register (single source of truth: the palette).</summary>
    internal static string[] TextureNodeKinds => Graph.Palette.TextureKinds;

    /// <summary>
    /// The variation section on the root node: pick an existing variation of the object to overwrite, or name
    /// a brand-new one — the bake then delivers the graph as that variation (nothing vanilla is replaced)
    /// instead of swapping the target shader's bytecode.
    /// </summary>
    private void BuildVariationPicker(GraphNode p_Node)
    {
        // ANY root carries the variation section — gating on specific kinds silently locked variations out
        // of every shader whose translation picked another lighting model (Metallic, Skin, DynamicEnvmap…),
        // which read as "only works for some objects" when the delivery mechanism is the same for all.
        if (!p_Node.Def.IsRoot)
            return;

        var s_Graph = Canvas.Graph;
        var s_Target = TargetShaderBox.Text.Trim();

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Variation", Margin = new Thickness(0, 14, 0, 2), FontWeight = FontWeights.Bold,
        });

        var s_Map = s_Target.Length > 0 ? ReadSlotMapFull(SlotMapPath(s_Target)) : null;
        var s_Existing = s_Map?.Variations?.Values
            .Select(p_V => p_V.AssetName)
            .Where(p_N => p_N is { Length: > 0 } && p_N != "(base)")
            .Cast<string>()
            .ToList() ?? new List<string>();

        // Variations the user has CREATED here accumulate per target, across graphs and sessions — the
        // dropdown is the one place they all live, and deleting one is an explicit act, not a side effect.
        var s_Custom = ReadCustomVariations(s_Target);
        var s_All = s_Existing.Concat(s_Custom)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_N => p_N, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var s_Status = new TextBlock
        {
            FontSize = 10, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
        };

        // Anything that has to follow the chosen variation hangs off here, so a control added later cannot
        // be left behind by one of the several paths that change the selection.
        Action? s_AfterStatus = null;

        void ShowStatus()
        {
            s_Status.Text = s_Graph.BakeVariation == null
                ? "Bake replaces the target shader (no variation)."
                : $"Bake creates/overwrites the variation:\n{s_Graph.BakeVariation}";

            s_AfterStatus?.Invoke();
        }

        const string c_None = "(none — bake replaces the target)";
        const string c_New = "New variation…";

        var s_Box = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
        var s_Filling = false;
        var s_Delete = new Button
        {
            Content = "Delete variation", Margin = new Thickness(0, 2, 0, 2),
            Visibility = Visibility.Collapsed,
            ToolTip = "Removes the selected variation from this list (game-defined variations stay).",
        };

        // The delete button tracks the SELECTED item, not the click path: the programmatic fills (panel
        // build, Enter-commit) skip the SelectionChanged handler on purpose, so they must refresh it here.
        void UpdateDelete() => s_Delete.Visibility =
            s_Box.SelectedItem is string s_Sel && s_Custom.Contains(s_Sel, StringComparer.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        void FillBox(string? p_Select)
        {
            s_Filling = true;
            s_Box.Items.Clear();
            s_Box.Items.Add(c_None);
            foreach (var s_Name in s_All)
                s_Box.Items.Add(s_Name);
            s_Box.Items.Add(c_New);
            s_Box.SelectedItem = p_Select != null && s_Box.Items.Contains(p_Select) ? p_Select : c_None;
            s_Filling = false;
            UpdateDelete();
        }

        string SuggestName()
        {
            var s_Slash = s_Target.LastIndexOf('/');
            var s_Stem = s_Slash >= 0 ? s_Target[(s_Slash + 1)..] : s_Target;
            if (s_Stem.StartsWith("SS_", StringComparison.OrdinalIgnoreCase))
                s_Stem = s_Stem[3..];
            return s_Slash >= 0 ? $"{s_Target[..s_Slash]}/{s_Stem}_NEW" : $"{s_Stem}_NEW";
        }

        var s_NameBox = new TextBox
        {
            Margin = new Thickness(0, 2, 0, 2),
            Visibility = Visibility.Collapsed,
            ToolTip = "Full sibling path for the new variation — press Enter to add it to the list.",
        };

        s_Box.SelectionChanged += (_, _) =>
        {
            if (s_Filling)
                return;

            switch (s_Box.SelectedItem as string)
            {
                case c_None:
                    SwitchVariationDraft(null);
                    s_NameBox.Visibility = Visibility.Collapsed;
                    s_Delete.Visibility = Visibility.Collapsed;
                    break;
                case c_New:
                    s_NameBox.Visibility = Visibility.Visible;
                    s_Delete.Visibility = Visibility.Collapsed;
                    if (s_NameBox.Text.Trim().Length == 0)
                        s_NameBox.Text = SuggestName();
                    s_NameBox.Focus();
                    s_NameBox.SelectAll();
                    break;
                case { } s_Picked:
                    SwitchVariationDraft(s_Picked);
                    s_NameBox.Visibility = Visibility.Collapsed;
                    UpdateDelete();
                    break;
            }

            ShowStatus();
        };

        // Enter commits the typed name: it joins the list, gets selected, and stays for every future graph
        // on this target — "new" is how the list GROWS, not a transient text field.
        s_NameBox.KeyDown += (_, p_Args) =>
        {
            if (p_Args.Key != System.Windows.Input.Key.Enter || s_NameBox.Text.Trim().Length == 0)
                return;

            var s_Name = s_NameBox.Text.Trim();
            if (!s_Custom.Contains(s_Name, StringComparer.OrdinalIgnoreCase))
            {
                s_Custom.Add(s_Name);
                SaveCustomVariations(s_Target, s_Custom);
            }

            if (!s_All.Contains(s_Name, StringComparer.OrdinalIgnoreCase))
                s_All.Add(s_Name);

            s_All.Sort(StringComparer.OrdinalIgnoreCase);
            SwitchVariationDraft(s_Name);
            s_NameBox.Visibility = Visibility.Collapsed;
            FillBox(s_Name);
            ShowStatus();
        };

        s_Delete.Click += (_, _) =>
        {
            if (s_Box.SelectedItem is not string s_Victim || !s_Custom.Remove(s_Victim))
                return;

            SaveCustomVariations(s_Target, s_Custom);
            s_All.RemoveAll(p_N => p_N.Equals(s_Victim, StringComparison.OrdinalIgnoreCase));
            if (s_Graph.BakeVariation?.Equals(s_Victim, StringComparison.OrdinalIgnoreCase) == true)
                s_Graph.BakeVariation = null;

            s_Delete.Visibility = Visibility.Collapsed;
            FillBox(null);
            ShowStatus();
        };

        // Sits under the dropdown because it only means anything once a variation is picked: a variation
        // nothing points at leaves the map looking untouched, and that is the surprise this box removes.
        var s_Force = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "Use this on the objects already in the level",
                TextWrapping = TextWrapping.Wrap,
            },
            Margin = new Thickness(0, 6, 0, 0),
            IsChecked = s_Graph.ApplyVariationToLevel,
            ToolTip = "The mod repoints the entry those copies ALREADY use at this shader, so every one " +
                      "of them in the map renders with it. Nothing vanilla is replaced: the original shader " +
                      "stays in its own database under its own key and the mod only ships its own small one, " +
                      "so removing the mod puts the map back exactly as it was. Untick it for a variation you " +
                      "mean to place by hand instead — then it ships as an extra variation and the map is " +
                      "left alone.",
        };

        var s_Syncing = false;
        s_Force.Checked += (_, _) =>
        {
            if (!s_Syncing) { s_Graph.ApplyVariationToLevel = true; TouchGraph(); }
        };
        s_Force.Unchecked += (_, _) =>
        {
            if (!s_Syncing) { s_Graph.ApplyVariationToLevel = false; TouchGraph(); }
        };

        // The state has to follow the dropdown, so the box is never armed on a bake that has no variation
        // to point anything at — and says why instead of just going grey.
        //
        // ⚠ It also has to come out UNTICKED there: a greyed box still showing a tick reads as "this will
        // happen anyway". The tick is cleared for the eye only — the graph keeps the user's choice, so
        // picking a variation again brings it back instead of silently resetting to the default.
        void RefreshForce()
        {
            var s_Live = s_Graph.BakeVariation != null;
            s_Force.IsEnabled = s_Live;

            s_Syncing = true;
            s_Force.IsChecked = s_Live && s_Graph.ApplyVariationToLevel;
            s_Syncing = false;

            // ⛔ WHETHER THIS BOX IS NEEDED IS NOT A MATTER OF TASTE, AND THE ANSWER WAS NOWHERE ON SCREEN.
            // It comes down to one measured number: how many meshes in the level wear the target shader. Worn
            // by ONE, a plain replacement changes that object and nothing else, and this box is beside the
            // point. Worn by many, a replacement repaints every one of them — the only way to change a single
            // object is a variation, which is keyed by the MESH, with this ticked. The old caption said "a
            // replacement already changes what those objects render", which is true and, on a shared preset,
            // exactly the sentence that leads to repainting half a map.
            var s_Users = s_Map?.MeshUsers;

            // ⛔ THE MAP THE NUMBER CAME FROM IS PART OF THE NUMBER. A shader dresses different objects in
            // every map, and the one measured here is whichever map the target names — or a default when its
            // name carries none. Printed bare it reads as a fact about the game; named, it is a fact about a
            // map, and the bake is what settles it for the maps actually being built.
            var s_Where = s_Map?.MeshUsersScope is { Length: > 0 } s_Scope
                ? s_Scope.Split('/').Last().ToUpperInvariant()
                : "that map";

            var s_Hint = s_Users switch
            {
                null => "  (how many objects share this shader has not been measured yet — press \"reload " +
                        "from game\"; the bake checks it for the maps you build)",
                <= 1 => $"  (in {s_Where} only this object wears the shader, so a plain replacement already " +
                        "changes it — other maps may differ, and the bake checks the ones you build)",
                _ => $"  ({s_Users} different meshes wear this shader in {s_Where} — a replacement would " +
                     "repaint ALL of them, so aiming at one object needs a variation with this ticked)",
            };

            ((TextBlock) s_Force.Content).Text = (s_Live
                ? "Use this on the objects already in the level"
                : "Use on the level's objects — needs a variation") + s_Hint;
        }

        s_AfterStatus = RefreshForce;

        FillBox(s_Graph.BakeVariation);
        PropertiesPanel.Children.Add(s_Box);
        PropertiesPanel.Children.Add(s_NameBox);
        PropertiesPanel.Children.Add(s_Delete);
        ShowStatus();
        PropertiesPanel.Children.Add(s_Status);
        PropertiesPanel.Children.Add(s_Force);
    }

    /// <summary>
    /// Per-variation WORK IN PROGRESS: switching the dropdown must not lose edits, so the canvas is
    /// snapshotted under the variation it was showing and restored when that variation is picked again.
    /// Keyed by target|variation ("" = the no-variation/base document). Save writes every draft out.
    /// </summary>
    private readonly Dictionary<string, string> m_VariationDrafts = new(StringComparer.OrdinalIgnoreCase);

    private string DraftKey(string p_Target, string? p_Variation) => $"{p_Target}|{p_Variation ?? ""}";

    private void SnapshotVariationDraft()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length > 0)
            m_VariationDrafts[DraftKey(s_Target, Canvas.Graph.BakeVariation)] = Canvas.Graph.ToJson();
    }

    /// <summary>Swaps the canvas to the chosen variation's draft; a variation touched for the FIRST time
    /// inherits a copy of what is on screen (the new variation starts from the current look).</summary>
    private void SwitchVariationDraft(string? p_Variation)
    {
        // Same variation = nothing to do. Without this, the combo's PROGRAMMATIC fill re-enters here,
        // and each round-trip through the panel rebuild kept stacking scaffold roots onto the canvas.
        if (string.Equals(Canvas.Graph.BakeVariation ?? "", p_Variation ?? "", StringComparison.OrdinalIgnoreCase))
            return;

        var s_Target = TargetShaderBox.Text.Trim();
        SnapshotVariationDraft();

        if (m_VariationDrafts.TryGetValue(DraftKey(s_Target, p_Variation), out var s_Json))
        {
            // The tab-switch pattern on purpose: swap the canvas document directly. LoadGraph also rewrites
            // the target box, and THAT path can answer with a scaffold graph — the infinite-roots bug.
            var s_Graph = ShaderGraph.FromJson(s_Json);
            s_Graph.BakeVariation = p_Variation;
            Canvas.Graph = s_Graph;
            SchedulePreview();
        }
        else
        {
            Canvas.Graph.BakeVariation = p_Variation;
        }
    }

    /// <summary>Every OTHER draft of the current target as a full graph, ready to embed or to bake.</summary>
    private List<ShaderGraph>? CollectVariationDrafts(bool p_ExcludeActive)
    {
        var s_Target = TargetShaderBox.Text.Trim();
        var s_Result = new List<ShaderGraph>();

        foreach (var (s_Key, s_Json) in m_VariationDrafts)
        {
            var s_Cut = s_Key.LastIndexOf('|');
            var s_KeyVariation = s_Key[(s_Cut + 1)..];

            if (s_KeyVariation.Length == 0 ||
                !s_Key[..s_Cut].Equals(s_Target, StringComparison.OrdinalIgnoreCase) ||
                (p_ExcludeActive &&
                 s_KeyVariation.Equals(Canvas.Graph.BakeVariation ?? "", StringComparison.OrdinalIgnoreCase)))
                continue;

            var s_Draft = ShaderGraph.FromJson(s_Json);
            s_Draft.BakeVariation = s_KeyVariation;
            s_Draft.Variations = null;
            s_Result.Add(s_Draft);
        }

        return s_Result.Count > 0 ? s_Result : null;
    }

    /// <summary>
    /// Moves a master file's embedded variations into working drafts (and their names into the dropdown's
    /// persisted list), returning the graph stripped — the canvas edits one look at a time.
    /// </summary>
    private void AbsorbEmbeddedVariations(ShaderGraph p_Graph)
    {
        if (p_Graph.Variations is not { Count: > 0 } s_Embedded)
            return;

        var s_Target = p_Graph.TargetShader.Trim();
        var s_Custom = ReadCustomVariations(s_Target);
        var s_Added = 0;

        foreach (var s_Variation in s_Embedded)
        {
            if (s_Variation.BakeVariation is not { Length: > 0 } s_Name)
                continue;

            s_Variation.Variations = null;
            m_VariationDrafts[DraftKey(s_Target, s_Name)] = s_Variation.ToJson();
            if (!s_Custom.Contains(s_Name, StringComparer.OrdinalIgnoreCase))
            {
                s_Custom.Add(s_Name);
                s_Added++;
            }
        }

        if (s_Added > 0)
            SaveCustomVariations(s_Target, s_Custom);

        Log($"{s_Embedded.Count} embedded variation(s) unpacked from the file.");
        p_Graph.Variations = null;
    }

    /// <summary>User-created variation names, per target, surviving restarts and shared by every graph.</summary>
    private string CustomVariationsPath(string p_Target) =>
        Path.Combine(TexCacheDir, $"{Sanitize(p_Target)}.variations.json");

    private List<string> ReadCustomVariations(string p_Target)
    {
        try
        {
            if (p_Target.Length > 0 && File.Exists(CustomVariationsPath(p_Target)))
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    File.ReadAllText(CustomVariationsPath(p_Target))) ?? new List<string>();
        }
        catch
        {
            // An unreadable list only costs re-typing a name; it must never take the panel down.
        }

        return new List<string>();
    }

    private void SaveCustomVariations(string p_Target, List<string> p_Names)
    {
        try
        {
            Directory.CreateDirectory(TexCacheDir);
            File.WriteAllText(CustomVariationsPath(p_Target),
                System.Text.Json.JsonSerializer.Serialize(p_Names));
        }
        catch
        {
            // Losing the persisted list is recoverable; failing the click is not.
        }
    }

    /// <summary>
    /// The per-node texture picker (the Unreal-style "choose the texture" affordance). The chosen file shows
    /// LIVE in the preview, and Bake converts it to the engine's format and ships it in the mod's bundle as a
    /// same-name override of whatever game texture this register binds.
    /// </summary>
    private void BuildCustomTexturePicker(GraphNode p_Node)
    {
        if (!TextureNodeKinds.Contains(p_Node.Kind))
            return;

        var s_Current = p_Node.Params.TryGetValue("CustomTexture", out var s_Path) ? s_Path : "";

        // The name BESIDE the header, resolved live: the chosen file's name, else the game texture this
        // register binds in the set being previewed (the slot map names it), else nothing is known yet.
        // GetParam, never the raw Params dict — a fresh node's register is the palette default.
        var s_NameRegister = p_Node.GetParam("Register");
        var s_GameName = GameTextureNameFor(s_NameRegister);

        var s_ShownName = s_Current.Length > 0
            ? Path.GetFileNameWithoutExtension(s_Current)
            : s_GameName?.Split('/').Last();

        // Read-only borderless TextBoxes, not TextBlocks: the name and the path are exactly the strings a
        // user copies (to search the dump, to name an override), and a TextBlock cannot be selected.
        static TextBox SelectableText(string p_Text, double p_FontSize, double p_Opacity, object? p_Tip) => new()
        {
            Text = p_Text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = p_FontSize,
            Opacity = p_Opacity,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = p_Tip,
            Cursor = Cursors.IBeam,
        };

        var s_Header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 2) };
        s_Header.Children.Add(new TextBlock { Text = "Texture", FontWeight = FontWeights.Bold });
        if (s_ShownName != null)
        {
            var s_NameBox = SelectableText(s_ShownName, 11, 0.7, s_Current.Length > 0 ? s_Current : s_GameName);
            s_NameBox.Margin = new Thickness(8, 1, 0, 0);
            s_Header.Children.Add(s_NameBox);
        }
        PropertiesPanel.Children.Add(s_Header);

        // Whether the loaded slot map knows this register at all decides the honest message: an unbound
        // register previews fine, but a same-name override has no name to override at bake time.
        var s_MapLoaded = ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim())) != null;

        // How the game samples what is in the register — the flag the wear mask (and every other mask built
        // from a channel) depends on; shown beside the name so a number typed against it can be understood.
        var s_Sampling = !string.IsNullOrWhiteSpace(s_NameRegister) &&
                         m_TextureSrgb.TryGetValue(s_NameRegister, out var s_SrgbFlag)
            ? s_SrgbFlag
                ? "  · sampled as sRGB (decoded to linear, as in-game)"
                : "  · sampled linear (as stored)"
            : "";

        var s_PathBox = SelectableText(
            s_Current.Length > 0
                ? "custom: " + Path.GetFileName(s_Current) + (File.Exists(s_Current) ? "" : "  (FILE MISSING)") +
                  s_Sampling
                : s_GameName != null
                    ? s_GameName + s_Sampling
                    : s_MapLoaded
                        ? "(new slot — the game material does not bind this register; a custom texture here is " +
                          "shipped as a brand-new texture and the shader's constants gain the slot at bake)"
                        : "(no game texture named for this register yet — press 'Reload from game')",
            10, 0.7, s_Current.Length > 0 ? s_Current : s_GameName);
        s_PathBox.Margin = new Thickness(0, 0, 0, 3);
        PropertiesPanel.Children.Add(s_PathBox);

        // The thumbnail sits BESIDE the buttons and always shows what the register currently wears: the
        // chosen file when there is one, else the game's own art — one glance answers "which texture is this".
        var s_ThumbSource = s_Current.Length > 0
            ? LoadCustomTextureImage(s_Current)
            : !string.IsNullOrWhiteSpace(s_NameRegister) &&
              m_TexturePreviews.TryGetValue(s_NameRegister, out var s_GameThumb)
                ? s_GameThumb
                : null;

        var s_Buttons = new StackPanel { Orientation = Orientation.Horizontal };

        if (s_ThumbSource != null)
            s_Buttons.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(84, 84, 90)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new Image
                {
                    Source = s_ThumbSource,
                    Width = 48,
                    Height = 48,
                    Stretch = Stretch.Uniform,
                },
                ToolTip = s_Current.Length > 0 ? s_Current : "The game texture bound to this register",
            });

        // Where Browse opens: the CURRENT texture's own folder, custom or vanilla alike — the vanilla art
        // lives as the decoded PNG in the texture cache, so "grab the original and tweak it" starts with the
        // file already under the cursor. Only with nothing at all does the dialog fall back to its default.
        var s_BrowseFrom = s_Current.Length > 0 && File.Exists(s_Current)
            ? s_Current
            : (s_ThumbSource as BitmapImage)?.UriSource is { IsFile: true } s_CacheUri &&
              File.Exists(s_CacheUri.LocalPath)
                ? s_CacheUri.LocalPath
                : null;

        var s_Browse = new Button
        {
            Content = "Browse…",
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        s_Browse.Click += (_, _) =>
        {
            var s_Dialog = new OpenFileDialog
            {
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.dds|" +
                         "All files (*.*)|*.*",
            };

            if (s_BrowseFrom != null)
            {
                s_Dialog.InitialDirectory = Path.GetDirectoryName(s_BrowseFrom);
                s_Dialog.FileName = Path.GetFileName(s_BrowseFrom);
            }

            if (s_Dialog.ShowDialog() != true)
                return;

            SetCustomTexture(p_Node, s_Dialog.FileName);
        };
        s_Buttons.Children.Add(s_Browse);

        // Export writes the texture the register currently WEARS wherever the user points — the raw DDS for a
        // vanilla dump, or converted to PNG when Settings say so (the ready-to-edit lane). Grayed with a
        // reason when there is nothing on disk to export yet.
        var s_Export = new Button
        {
            Content = "Export…",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = s_BrowseFrom != null,
            ToolTip = s_BrowseFrom != null
                ? "Save this texture to a folder of your choice" +
                  (m_Settings.ExportTexturesAsPng ? " (as PNG — see Settings > Export)" : " (as-is — see Settings > Export)")
                : "Nothing to export yet — load the target's textures first.",
        };
        s_Export.Click += (_, _) => ExportTexture(s_BrowseFrom!, s_ShownName ?? "texture");
        s_Buttons.Children.Add(s_Export);

        if (s_Current.Length > 0)
        {
            var s_Clear = new Button
            {
                Content = "Clear",
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 0, 0, 0),
            };
            s_Clear.Click += (_, _) =>
            {
                Canvas.PushUndo();
                p_Node.Params.Remove("CustomTexture");

                // The vanilla art comes back from the disk cache; with no cache yet, the register simply
                // returns to the placeholder on the next texture load.
                if (LoadCachedPreviews() == 0 && p_Node.Params.TryGetValue("Register", out var s_Register))
                {
                    m_TexturePreviews.Remove(s_Register);
                    m_TextureSrgb.Remove(s_Register);
                }

                ApplyCustomTextureOverrides();
                BuildProperties();
                // (an edit like any other: see SetCustomTexture)
                SchedulePreview();
            };
            s_Buttons.Children.Add(s_Clear);
        }

        PropertiesPanel.Children.Add(s_Buttons);

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Shows live in the preview. On Bake it is converted to the engine's texture format and " +
                   "shipped in the mod bundle, overriding the game texture of the same name.",
            FontSize = 10,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });
    }

    /// <summary>
    /// Saves the texture a register currently wears wherever the user points. Settings decide the format:
    /// PNG (converted if needed — the ready-to-edit lane) or the file exactly as it is, which for game art
    /// means the raw DDS dump sitting next to the cache's preview PNG.
    /// </summary>
    private void ExportTexture(string p_SourcePath, string p_Name)
    {
        var s_AsPng = m_Settings.ExportTexturesAsPng;

        var s_Source = p_SourcePath;
        if (!s_AsPng && s_Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            // The cache keeps the decoded PNG for previews AND the faithful DDS dump beside it.
            var s_Dds = Path.ChangeExtension(s_Source, ".dds");
            if (File.Exists(s_Dds))
                s_Source = s_Dds;
        }

        var s_Extension = s_AsPng ? ".png" : Path.GetExtension(s_Source);
        var s_Dialog = new SaveFileDialog
        {
            FileName = p_Name + s_Extension,
            Filter = s_AsPng
                ? "PNG image (*.png)|*.png"
                : $"Texture (*{s_Extension})|*{s_Extension}|All files (*.*)|*.*",
        };

        if (s_Dialog.ShowDialog() != true)
            return;

        try
        {
            if (s_AsPng && !s_Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                if (LoadCustomTextureImage(s_Source) is not BitmapSource s_Image)
                {
                    Log($"ERROR: '{Path.GetFileName(s_Source)}' could not be decoded for PNG export.");
                    return;
                }

                var s_Encoder = new PngBitmapEncoder();
                s_Encoder.Frames.Add(BitmapFrame.Create(s_Image));
                using var s_Stream = File.Create(s_Dialog.FileName);
                s_Encoder.Save(s_Stream);
            }
            else
            {
                File.Copy(s_Source, s_Dialog.FileName, true);
            }

            Log($"Exported '{p_Name}' -> {s_Dialog.FileName}");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR exporting texture: {s_Exception.Message}");
        }
    }

    /// <summary>Decodes a user image for the preview: WPF codecs for the common formats, our decoder for DDS.</summary>
    private static ImageSource? LoadCustomTextureImage(string p_Path)
    {
        try
        {
            if (!File.Exists(p_Path))
                return null;

            if (p_Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                return DdsImage.Load(p_Path);

            var s_Bitmap = new BitmapImage();
            s_Bitmap.BeginInit();
            // ⛔ WPF caches decoded bitmaps PER URI for the process lifetime: without IgnoreImageCache, a file
            // the user re-saved (or re-picked) kept showing its FIRST version until the editor restarted.
            s_Bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            s_Bitmap.UriSource = new Uri(p_Path);
            s_Bitmap.EndInit();
            s_Bitmap.Freeze();
            return s_Bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Feeds every texture node's chosen custom file into the thumbnail dictionary and the live preview,
    /// AFTER the cache-derived art so the user's choice wins. Idempotent — every path that repopulates the
    /// thumbnails ends by calling this.
    /// </summary>
    /// <summary>
    /// The user's own images on the canvas, decoded to pixel blocks by sampler slot — the camo being
    /// authored, in the form a shader registration takes.
    ///
    /// ⛔ THEY WIN ON EVERY REGISTRATION, not only on the main one. <see cref="ApplyCustomTextureOverrides"/>
    /// pushes them into the preview's own texture set; a per-material registration carries a set of its own
    /// and has to be given them too, or dressing each material with its shipped art quietly puts the GAME's
    /// camo texture back where the authored pattern was.
    /// </summary>
    /// <summary>
    /// The custom pictures on the texture nodes, named without decoding them — register, file and the file's last write — for
    /// a key that has to be cheap to build (the per-material dress asks on every slider tick). The file's time is in it so a
    /// picture edited on disk under the same name still counts as a new one.
    /// </summary>
    private string CustomTextureSignature(ShaderGraph? p_Graph = null) =>
        string.Join(",", (p_Graph ?? Canvas.Graph).Nodes
            .Where(p_N => TextureNodeKinds.Contains(p_N.Kind) &&
                          p_N.Params.TryGetValue("CustomTexture", out var s_Path) && s_Path.Length > 0)
            .Select(p_N =>
            {
                var s_Path = p_N.Params["CustomTexture"];
                long s_Time = 0;
                try { s_Time = File.GetLastWriteTimeUtc(s_Path).Ticks; }
                catch (Exception) { /* a file that cannot be read counts by its name alone */ }

                return $"{p_N.GetParam("Register")}:{s_Path}:{s_Time}";
            })
            .OrderBy(p_S => p_S, StringComparer.Ordinal));

    private List<(int Slot, uint[] Pixels, int Width, int Height, bool Srgb)> CustomTextureBlocks(ShaderGraph? p_Graph = null)
    {
        var s_Blocks = new List<(int Slot, uint[] Pixels, int Width, int Height, bool Srgb)>();

        // (p_Graph: the camo DOCUMENT when another material's graph is on the canvas)
        foreach (var s_Node in (p_Graph ?? Canvas.Graph).Nodes)
        {
            if (!TextureNodeKinds.Contains(s_Node.Kind) ||
                !s_Node.Params.TryGetValue("CustomTexture", out var s_Path) || s_Path.Length == 0 ||
                !int.TryParse(s_Node.GetParam("Register"), out var s_Slot) ||
                LoadCustomTextureImage(s_Path) is not BitmapSource s_Image)
                continue;

            try
            {
                var s_Converted = new FormatConvertedBitmap(s_Image, PixelFormats.Bgra32, null, 0);
                var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
                s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);
                s_Blocks.Add((s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight,
                    CustomTextureIsSrgb(s_Node, s_Path)));
            }
            catch (Exception)
            {
                // An image that will not decode leaves that slot on the game art, as it does everywhere else.
            }
        }

        return s_Blocks;
    }

    /// <summary>
    /// A picture chosen on a texture node (the Browse button — and a seam, the same way): the node's own thumbnail and, ⭐ LIKE ANY OTHER
    /// EDIT OF THE GRAPH (a custom texture put through a texture node must just work), the preview
    /// re-run — the tab's original checked (a picture forks it), an as-shipped graph or a material's own graph re-drawn with it, an
    /// accessory's stand-in rebuilt. It used to stop at the thumbnail and the main texture set: a picture on a glass or on an as-shipped
    /// graph drew nowhere and never forked the original.
    /// </summary>
    internal void SetCustomTexture(GraphNode p_Node, string? p_File)
    {
        Canvas.PushUndo();
        if (string.IsNullOrEmpty(p_File))
            p_Node.Params.Remove("CustomTexture");
        else
            p_Node.Params["CustomTexture"] = p_File;

        ApplyCustomTextureOverrides();
        BuildProperties();
        SchedulePreview();
    }

    private void ApplyCustomTextureOverrides()
    {
        foreach (var s_Node in Canvas.Graph.Nodes)
        {
            if (!TextureNodeKinds.Contains(s_Node.Kind) ||
                !s_Node.Params.TryGetValue("CustomTexture", out var s_Path) || s_Path.Length == 0)
                continue;

            // ⛔ GetParam, not the raw dictionary: a FRESHLY PLACED node has an EMPTY Params dict and its
            // register lives in the palette DEFAULT — the raw read skipped new nodes in silence, which is
            // exactly "I chose a custom texture and nothing happened".
            var s_Register = s_Node.GetParam("Register");
            if (string.IsNullOrWhiteSpace(s_Register))
                continue;

            if (LoadCustomTextureImage(s_Path) is not BitmapSource s_Image)
            {
                Log($"Custom texture '{Path.GetFileName(s_Path)}' could not be read; register t{s_Register} " +
                    "keeps the game art.");
                continue;
            }

            m_TexturePreviews[s_Register] = s_Image;
            var s_Srgb = CustomTextureIsSrgb(s_Node, s_Path);
            m_TextureSrgb[s_Register] = s_Srgb;

            // (the canvas's own thumbnail only, while another material's graph is on it — see PushPreviewTextures)
            if (m_Preview.Ready && m_MaterialEditKey == null && int.TryParse(s_Register, out var s_Slot))
            {
                var s_Converted = new FormatConvertedBitmap(s_Image, PixelFormats.Bgra32, null, 0);
                var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
                s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);
                m_Preview.SetTexture(s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight, s_Srgb);
            }
        }

        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// The free-text note on a node, shown as a bubble above it on the canvas. Saved with the graph; it never
    /// reaches the emitted shader.
    /// </summary>
    private void BuildDescriptionEditor(GraphNode p_Node)
    {
        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Description",
            Margin = new Thickness(0, 16, 0, 2),
            FontWeight = FontWeights.Bold,
        });

        var s_Box = new TextBox
        {
            Text = p_Node.Comment ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 2,
            Tag = "node:Description",
        };

        static string? AsComment(string p_Text) => p_Text.Trim().Length > 0 ? p_Text : null;

        var s_Original = s_Box.Text;
        s_Box.TextChanged += (_, _) =>
        {
            Live(p_Node).Comment = AsComment(s_Box.Text);
            TouchGraph();
        };

        s_Box.GotKeyboardFocus += (_, _) => s_Original = s_Box.Text;
        s_Box.LostKeyboardFocus += (_, _) =>
        {
            if (s_Box.Text == s_Original)
                return;

            // Same undo dance as the input-default editors: one undo step per edit session, not per keystroke.
            var s_New = s_Box.Text;
            Live(p_Node).Comment = AsComment(s_Original);
            Canvas.PushUndo();
            Live(p_Node).Comment = AsComment(s_New);
            s_Original = s_New;
        };

        PropertiesPanel.Children.Add(s_Box);
    }

    /// <summary>
    /// Editors for the constants that feed unconnected inputs. Without these the literals baked into each node
    /// are unreachable — you would have to wire a Scalar node just to change a multiplier.
    /// </summary>
    private void BuildInputDefaults(GraphNode p_Node)
    {
        var s_Editable = p_Node.Def.Inputs.Where(p_P => p_P.IsEditable).ToList();
        if (s_Editable.Count == 0)
            return;

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Input values (used while unconnected)",
            Margin = new Thickness(0, 16, 0, 2),
            FontWeight = FontWeights.Bold,
        });

        foreach (var s_Port in s_Editable)
        {
            var s_Connected = Canvas.Graph.ConnectionInto(p_Node.Id, s_Port.Name) != null;
            var s_Components = s_Port.Type.ComponentCount();

            var s_Label = s_Connected
                ? $"{s_Port.Name}  (driven by a wire)"
                : s_Port.DefaultIsExpression
                    ? $"{s_Port.Name}  ({s_Components} × float, default {s_Port.Default})"
                    : $"{s_Port.Name}  ({s_Components} × float)";

            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = s_Label,
                Margin = new Thickness(0, 6, 0, 2),
                FontSize = 11,
                Opacity = s_Connected ? 0.5 : 1.0,
            });

            var s_Box = SelectAllOnFocus(new TextBox
            {
                // Expression defaults start blank: prefilling them would feed the expression text back as a
                // value on the first TextChanged. Blank means "keep the default".
                Text = Live(p_Node).GetInputOverride(s_Port.Name)
                       ?? (s_Port.DefaultIsExpression ? "" : s_Port.DefaultAsText()),
                IsEnabled = !s_Connected,
            });

            var s_Original = s_Box.Text;
            s_Box.TextChanged += (_, _) =>
            {
                Live(p_Node).SetInputOverride(s_Port.Name, s_Box.Text);
                TouchGraph();
            };

            s_Box.GotKeyboardFocus += (_, _) => s_Original = s_Box.Text;
            s_Box.LostKeyboardFocus += (_, _) =>
            {
                if (s_Box.Text == s_Original)
                    return;

                var s_New = s_Box.Text;
                Live(p_Node).SetInputOverride(s_Port.Name, s_Original);
                Canvas.PushUndo();
                Live(p_Node).SetInputOverride(s_Port.Name, s_New);
                s_Original = s_New;

                // Surface a bad value straight away rather than at emit time.
                if (PortTypeUtils.FormatLiteral(s_New, s_Port.Type) == null && s_New.Trim().Length > 0)
                    Log($"'{s_Port.Name}' needs 1 or {s_Components} numbers — '{s_New}' will fall back to the default.");
            };

            PropertiesPanel.Children.Add(s_Box);
        }
    }

    /// <summary>
    /// Swatch plus four numeric fields. The dialog is there for picking a colour by eye, the fields because a
    /// shader value is not limited to 0..1 — an emissive colour above 1 is legitimate and the dialog would
    /// clamp it away.
    /// </summary>
    /// <summary>
    /// Resolves a node by id against the LIVE graph. Property editors capture a node object, but undo, redo and
    /// loading a graph rebuild every node from JSON, so a captured reference can end up writing into an orphan
    /// that nothing renders or emits.
    /// </summary>
    private GraphNode Live(GraphNode p_Node) => Canvas.Graph.FindNode(p_Node.Id) ?? p_Node;

    private FrameworkElement BuildColorEditor(GraphNode p_Captured, ParamDef p_Param)
    {
        var s_Root = new StackPanel();
        var s_Row = new StackPanel { Orientation = Orientation.Horizontal };
        var s_Swatch = new System.Windows.Shapes.Rectangle
        {
            Width = 34, Height = 20, Stroke = Brushes.Black, StrokeThickness = 1,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var s_Fields = new TextBox[4];
        var s_Names = new[] { "R", "G", "B", "A" };

        float[] Read()
        {
            var s_Parts = Live(p_Captured).GetParam(p_Param.Name)
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var s_Values = new[] { 1f, 1f, 1f, 1f };
            for (var i = 0; i < 4 && i < s_Parts.Length; i++)
                if (float.TryParse(s_Parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V))
                    s_Values[i] = s_V;

            return s_Values;
        }

        void Paint()
        {
            var s_Values = Read();
            byte Channel(float p_V) => (byte) Math.Clamp(p_V * 255f, 0f, 255f);
            s_Swatch.Fill = new SolidColorBrush(Color.FromRgb(
                Channel(s_Values[0]), Channel(s_Values[1]), Channel(s_Values[2])));
        }

        var s_Suppress = false;

        void Write(float[] p_Values, bool p_Undo)
        {
            if (p_Undo)
                Canvas.PushUndo();

            Live(p_Captured).Params[p_Param.Name] = string.Join(",",
                p_Values.Select(p_V => p_V.ToString("0.0######", CultureInfo.InvariantCulture)));

            // The fields are written back too, and that must not re-enter through their own change handler.
            s_Suppress = true;
            for (var i = 0; i < 4; i++)
                if (s_Fields[i] != null)
                    s_Fields[i].Text = p_Values[i].ToString("0.0######", CultureInfo.InvariantCulture);

            s_Suppress = false;

            Paint();
            TouchGraph();
        }

        // Tagged so the self-test can find this exact button in the panel instead of guessing at an index.
        var s_Pick = new Button
        {
            Content = "Pick…", Padding = new Thickness(6, 1, 6, 1), Tag = "pick:" + p_Param.Name,
        };

        s_Pick.Click += (_, _) =>
        {
            var s_Picked = AskForColour(Read());
            if (s_Picked == null)
            {
                Log("Colour picker cancelled; nothing changed.");
                return;
            }

            Write(s_Picked, true);

            // Reported so a value that does not land is visible instead of being guessed at.
            Log($"Colour picked -> {p_Param.Name} = {Live(p_Captured).GetParam(p_Param.Name)}");
        };

        s_Row.Children.Add(s_Swatch);
        s_Row.Children.Add(s_Pick);
        s_Root.Children.Add(s_Row);

        var s_Grid = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < 4; i++)
        {
            var s_Index = i;
            var s_Box = SelectAllOnFocus(new TextBox { Width = 46, Margin = new Thickness(0, 0, 4, 0) });
            s_Fields[i] = s_Box;

            // Commit as it is typed, so both paths into the value behave the same. Committing only on focus
            // loss made the two routes asymmetric, which is what made the dialog look broken.
            s_Box.TextChanged += (_, _) =>
            {
                if (s_Suppress)
                    return;

                if (!float.TryParse(s_Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V))
                    return;

                var s_Values = Read();
                s_Values[s_Index] = s_V;

                // No undo snapshot per keystroke; that is taken when the field is entered.
                Write(s_Values, false);
            };

            s_Box.GotKeyboardFocus += (_, _) => Canvas.PushUndo();

            s_Grid.Children.Add(new TextBlock
            {
                Text = s_Names[i], Width = 12, VerticalAlignment = VerticalAlignment.Center, FontSize = 10,
            });
            s_Grid.Children.Add(s_Box);
        }

        s_Root.Children.Add(s_Grid);
        s_Root.Children.Add(new TextBlock
        {
            Text = "Values may exceed 1 (e.g. emissive).", FontSize = 10, Opacity = 0.7,
            Margin = new Thickness(0, 2, 0, 0),
        });

        Write(Read(), false);
        return s_Root;
    }

    /// <summary>
    /// Drives the Description field the way a user does: select a node, type into the panel's real TextBox, and
    /// read the model back. Typed text must land on the node, the graph file must keep it, blank must remove it.
    /// </summary>
    internal static int DescriptionSelfTest()
    {
        var s_Window = new MainWindow();
        s_Window.Canvas.Graph = new ShaderGraph();
        s_Window.Canvas.AddNodeAt("Color", new Point(0, 0));

        var s_Node = s_Window.Canvas.Selected;
        if (s_Node == null)
        {
            Console.Error.WriteLine("DESCTEST: adding a node did not leave it selected.");
            return 1;
        }

        var s_Box = Descendants<TextBox>(s_Window.PropertiesPanel)
            .FirstOrDefault(p_B => p_B.Tag as string == "node:Description");

        if (s_Box == null)
        {
            Console.Error.WriteLine("DESCTEST: no Description box in the properties panel.");
            return 1;
        }

        s_Box.Text = "Comment Test";
        var s_Typed = s_Window.Canvas.Graph.FindNode(s_Node.Id)?.Comment;
        Console.Out.WriteLine($"after typing: Comment = '{s_Typed}'");

        var s_Reloaded = ShaderGraph.FromJson(s_Window.Canvas.Graph.ToJson()).FindNode(s_Node.Id)?.Comment;
        Console.Out.WriteLine($"round-trip:   Comment = '{s_Reloaded}'");

        s_Box.Text = "  ";
        var s_Cleared = s_Window.Canvas.Graph.FindNode(s_Node.Id)?.Comment;
        Console.Out.WriteLine($"after clear:  Comment = {(s_Cleared == null ? "null" : $"'{s_Cleared}'")}");

        // The knob-class line: a Color's values are BAKED, an ExternalConstant is runtime instance data.
        var s_Baked = Descendants<TextBlock>(s_Window.PropertiesPanel)
            .Any(p_T => p_T.Text.StartsWith("BAKED", StringComparison.Ordinal));

        s_Window.Canvas.AddNodeAt("ExternalConstant", new Point(220, 0));
        var s_External = Descendants<TextBlock>(s_Window.PropertiesPanel)
            .Any(p_T => p_T.Text.StartsWith("EXTERNAL", StringComparison.Ordinal));

        Console.Out.WriteLine($"knob class lines (BAKED on Color, EXTERNAL on ExternalConstant): " +
                              $"{(s_Baked && s_External ? "OK" : "FAIL")}");

        var s_Ok = s_Typed == "Comment Test" && s_Reloaded == "Comment Test" && s_Cleared == null &&
                   s_Baked && s_External;
        Console.Out.WriteLine(s_Ok
            ? "DESCTEST: PASS - typed text reached the model, the file keeps it, blank removes it."
            : "DESCTEST: FAIL");
        return s_Ok ? 0 : 1;
    }

    /// <summary>
    /// The modal picker is the only step of the Pick path that cannot be driven from the command line, so it sits
    /// behind this hook. In a normal run the hook is null and the real picker opens; the self-test swaps it and
    /// everything else — Read, PushUndo, the write, the field write-back and TouchGraph — runs for real.
    /// Returns null for "cancelled".
    /// </summary>
    internal static Func<float[], float[]?>? ColourPickerHook;

    private float[]? AskForColour(float[] p_Seed) =>
        ColourPickerHook != null ? ColourPickerHook(p_Seed) : ColourPicker.Pick(this, p_Seed);

    /// <summary>
    /// Drives the whole "Pick a colour" path on a node created exactly the way the palette creates one, with no
    /// window shown. The dialog was reported not applying on a FRESH node while working after one manual edit;
    /// the only way to separate "the write never happens" from "the write happens and is not shown" is to run the
    /// real path and read the model AND the fields back.
    /// </summary>
    internal static int ColourPickSelfTest(bool p_EditFirst)
    {
        var s_Window = new MainWindow();
        s_Window.Canvas.Graph = new ShaderGraph();

        // The same entry point the palette drag, the search popup and the letter shortcuts all use.
        s_Window.Canvas.AddNodeAt("Color", new Point(0, 0));

        var s_Node = s_Window.Canvas.Selected;
        if (s_Node == null || s_Node.Kind != "Color")
        {
            Console.Error.WriteLine("COLORTEST: adding a Color node did not leave it selected.");
            return 1;
        }

        Console.Out.WriteLine($"after add:    Value = {s_Node.GetParam("Value")}");

        var s_Fields = Descendants<TextBox>(s_Window.PropertiesPanel).ToList();
        if (s_Fields.Count < 4)
        {
            Console.Error.WriteLine($"COLORTEST: expected 4 RGBA fields in the panel, found {s_Fields.Count}.");
            return 1;
        }

        if (p_EditFirst)
        {
            // The manual edit reported to "unlock" the dialog.
            s_Fields[0].Text = "0.25";
            Console.Out.WriteLine($"after typing: Value = {s_Window.Canvas.Selected!.GetParam("Value")}");
        }

        var s_Button = Descendants<Button>(s_Window.PropertiesPanel)
            .FirstOrDefault(p_B => p_B.Tag as string == "pick:Value");

        if (s_Button == null)
        {
            Console.Error.WriteLine("COLORTEST: no Pick button found in the properties panel.");
            return 1;
        }

        var s_Seeded = new[] { 0f, 0f, 0f, 0f };
        ColourPickerHook = p_Seed =>
        {
            s_Seeded = p_Seed;

            // Pure red at half alpha: unmistakable against the 1,1,1,1 default, and asymmetric enough that a
            // swizzle or a dropped alpha would show up rather than passing.
            return new[] { 1f, 0f, 0f, 0.5f };
        };

        try
        {
            s_Button.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        catch (Exception s_Exception)
        {
            Console.Error.WriteLine($"COLORTEST: the Pick handler THREW: {s_Exception}");
            return 1;
        }
        finally
        {
            ColourPickerHook = null;
        }

        var s_Value = s_Window.Canvas.Graph.FindNode(s_Node.Id)?.GetParam("Value") ?? "(node not in graph)";
        var s_Shown = string.Join(",", Descendants<TextBox>(s_Window.PropertiesPanel)
            .Take(4).Select(p_F => p_F.Text));

        Console.Out.WriteLine("picker seeded: " + string.Join(",",
            s_Seeded.Select(p_V => p_V.ToString("0.0###", CultureInfo.InvariantCulture))));
        Console.Out.WriteLine($"after pick:   Value = {s_Value}");
        Console.Out.WriteLine($"fields show:  {s_Shown}");

        // Landing in the dictionary is not the same as the shader getting it, so wire the node into a root and
        // check the literal that actually reaches the HLSL.
        var s_Root = new GraphNode { Kind = "StandardRoot", X = 300, Y = 0 };
        s_Window.Canvas.Graph.Nodes.Add(s_Root);
        s_Window.Canvas.Graph.Connect(s_Node.Id, "Out", s_Root.Id, "Diffuse");

        var s_Emitted = new HlslEmitter().Emit(s_Window.Canvas.Graph);
        var s_Wanted = "float4(1.0, 0.0, 0.0, 0.5)";
        var s_Literal = s_Emitted.Hlsl.Split('\n')
            .FirstOrDefault(p_L => p_L.Contains(s_Wanted, StringComparison.Ordinal));

        Console.Out.WriteLine($"emitted:      {s_Literal?.Trim() ?? $"NOT FOUND - no '{s_Wanted}' in the HLSL"}");

        // Full equality, alpha included: a picker that drops the fourth component would otherwise pass.
        var s_Ok = s_Value == "1.0,0.0,0.0,0.5" && s_Shown == "1.0,0.0,0.0,0.5" && s_Literal != null;
        Console.Out.WriteLine(s_Ok
            ? "COLORTEST: PASS - the picked colour reached the model, the fields and the HLSL."
            : "COLORTEST: FAIL - expected 1.0,0.0,0.0,0.5 in the model, the fields and the HLSL.");

        return s_Ok ? 0 : 1;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject p_Root) where T : DependencyObject
    {
        var s_Count = VisualTreeHelper.GetChildrenCount(p_Root);
        for (var i = 0; i < s_Count; i++)
        {
            var s_Child = VisualTreeHelper.GetChild(p_Root, i);
            if (s_Child is T s_Match)
                yield return s_Match;

            foreach (var s_Deep in Descendants<T>(s_Child))
                yield return s_Deep;
        }
    }

    private FrameworkElement BuildParamEditor(GraphNode p_Node, ParamDef p_Param)
    {
        if (p_Param.Kind == ParamKind.Color)
            return BuildColorEditor(p_Node, p_Param);

        if (p_Param.Kind == ParamKind.Choice)
        {
            var s_Choice = new ComboBox();
            foreach (var s_Option in p_Param.Choices)
                s_Choice.Items.Add(s_Option);

            s_Choice.SelectedItem = Live(p_Node).GetParam(p_Param.Name);
            s_Choice.SelectionChanged += (_, _) =>
            {
                if (s_Choice.SelectedItem is not string s_Value || s_Value == Live(p_Node).GetParam(p_Param.Name))
                    return;

                Canvas.PushUndo();
                Live(p_Node).Params[p_Param.Name] = s_Value;
                TouchGraph();

                // A discrete value can decide which PINS are live (a blend mode turning the cutout input on),
                // and that state is drawn when the panel is built — so it has to be rebuilt. Only for
                // discrete values: doing it while someone types in a text box would steal the caret.
                BuildProperties();
            };

            return s_Choice;
        }

        if (p_Param.Kind == ParamKind.Bool)
        {
            var s_Check = new CheckBox
            {
                IsChecked = Live(p_Node).GetParam(p_Param.Name).Equals("true", StringComparison.OrdinalIgnoreCase),
            };

            s_Check.Click += (_, _) =>
            {
                Canvas.PushUndo();
                Live(p_Node).Params[p_Param.Name] = s_Check.IsChecked == true ? "true" : "false";
                TouchGraph();
                BuildProperties();
            };

            return s_Check;
        }

        if (p_Param.Kind == ParamKind.Enum && p_Param.EnumType != null)
        {
            var s_Combo = new ComboBox();
            foreach (var s_Name in Enum.GetNames(p_Param.EnumType))
                s_Combo.Items.Add(s_Name);

            s_Combo.SelectedItem = Live(p_Node).GetParam(p_Param.Name);
            s_Combo.SelectionChanged += (_, _) =>
            {
                if (s_Combo.SelectedItem is not string s_Value || s_Value == Live(p_Node).GetParam(p_Param.Name))
                    return;

                Canvas.PushUndo();
                Live(p_Node).Params[p_Param.Name] = s_Value;
                TouchGraph();

                // Availability can depend on this value (Opacity follows SurfaceShaderType), so redraw the panel.
                BuildProperties();
            };

            return s_Combo;
        }

        var s_Box = SelectAllOnFocus(new TextBox { Text = Live(p_Node).GetParam(p_Param.Name) });
        var s_Original = s_Box.Text;

        s_Box.TextChanged += (_, _) =>
        {
            Live(p_Node).Params[p_Param.Name] = s_Box.Text;
            TouchGraph();
        };

        // One undo step per editing session rather than per keystroke.
        s_Box.GotKeyboardFocus += (_, _) => s_Original = s_Box.Text;
        s_Box.LostKeyboardFocus += (_, _) =>
        {
            if (s_Box.Text == s_Original)
                return;

            var s_New = s_Box.Text;
            Live(p_Node).Params[p_Param.Name] = s_Original;
            Canvas.PushUndo();
            Live(p_Node).Params[p_Param.Name] = s_New;
            s_Original = s_New;
        };

        return s_Box;
    }

    private void OnNew(object p_Sender, RoutedEventArgs p_Args) => NewGraphTab();

    /// <summary>The New button: a fresh graph in its own tab. One entry, so a driver takes the same path.</summary>
    internal void NewGraphTab()
    {
        // ⭐ IN CAMO MODE A NEW TAB IS A NEW CAMO ON THE WEAPON BEING LOOKED AT: it opens already aimed at
        // that weapon's preset, so the weapon stays on screen and the editing starts at once — an empty
        // graph would draw it flat and leave the user to find the preset again. With no weapon picked
        // yet the tab stays fresh, and the first pick aims it.
        if (CamoMode && m_PreviewMesh is { Length: > 0 } s_Mesh &&
            m_CamoShaderFor?.Invoke(s_Mesh) is { } s_Preset && File.Exists(CamoGraphCachePath(s_Preset)))
            try
            {
                var s_Camo = LoadCamoGraph(s_Preset);
                s_Camo.Name = "Untitled";
                OpenInNewTab(s_Camo, null);
                Log($"New camo on '{s_Preset.Split('/')[^1]}', in its own tab — name it under Graph name.");
                return;
            }
            catch (Exception s_Exception)
            {
                Log($"Camo: cached graph unusable ({s_Exception.Message}) — opening an empty tab instead.");
            }

        var s_Graph = NewGraphWithRoot();
        OpenInNewTab(s_Graph, null);
        Log($"New graph with a {s_Graph.Nodes[0].Def.TitleFor(m_Settings.UdkNodeStyle)}, in its own tab.");
    }

    /// <summary>
    /// Makes a properties field select its whole value on the FIRST click, so typing replaces it.
    ///
    /// These boxes hold one short value that people come to overwrite, not to edit in place — having to
    /// sweep the old number first is friction on the most repeated action in the panel. The mouse handler
    /// is needed as well as the focus one: WPF places the caret on click, which would undo the selection.
    /// </summary>
    private static TextBox SelectAllOnFocus(TextBox p_Box)
    {
        p_Box.GotKeyboardFocus += (_, _) => p_Box.SelectAll();
        p_Box.PreviewMouseLeftButtonDown += (_, p_Args) =>
        {
            if (p_Box.IsKeyboardFocusWithin)
                return;

            // Focus it ourselves and swallow the click, or the caret lands and clears the selection.
            p_Box.Focus();
            p_Args.Handled = true;
        };

        return p_Box;
    }

    /// <summary>
    /// A fresh graph already carrying its output node — every shader needs one, and starting empty just
    /// made it the first thing anybody had to go and find. The root is the one of the vocabulary being
    /// authored in: Material while in UDK style, StandardRoot otherwise.
    /// </summary>
    private ShaderGraph NewGraphWithRoot()
    {
        var s_Graph = new ShaderGraph { Name = "Untitled" };
        s_Graph.Nodes.Add(new GraphNode
        {
            Kind = m_Settings.UdkNodeStyle ? "UdkMaterial" : "StandardRoot",
            X = 120,
            Y = -80,
        });

        return s_Graph;
    }

    private void OnOpen(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Dialog = new OpenFileDialog { Filter = "Shader graph (*.json)|*.json|All files (*.*)|*.*" };
        if (s_Dialog.ShowDialog() != true)
            return;

        OpenDocumentFrom(s_Dialog.FileName);
    }

    /// <summary>Opening a file, with the file already chosen — what the button does once the dialog is past.</summary>
    internal void OpenDocumentFrom(string p_Path)
    {
        try
        {
            var s_Opened = ShaderGraph.FromJson(File.ReadAllText(p_Path));
            AbsorbEmbeddedVariations(s_Opened);
            OpenInNewTab(s_Opened, p_Path);
            AdoptOpenedPart(s_Opened);
            Log($"Opened {p_Path} in its own tab");
            LastPreviewRestore = RestoreDocumentPreviewAsync(s_Opened);
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR opening graph: {s_Exception.Message}");
        }
    }

    /// <summary>
    /// ⛔ A FILE OF A SOLDIER'S PART OPENS AS THAT PART'S TAB (review 2026-09-29, D2). Each part keeps its document in a tab of its own, and a
    /// tab is a part's by its subject: an opened tab had none, so the restore's first pick — the soldier, landing on his upper body — was
    /// routed away to another tab, and his legs' pick then found no tab holding the legs' changes: they came up untouched elsewhere and the
    /// file's document was left in a tab no part leads to (and that no bake reads as his). Now the tab is the part's from the start: the
    /// game's translation of its shader the common document (what his other parts start from), the file's the part's own.
    /// CAMO_OPENED_TAB_OLD=1 = as before.
    /// </summary>
    private void AdoptOpenedPart(ShaderGraph p_Document)
    {
        if (!CamoMode || Environment.GetEnvironmentVariable("CAMO_OPENED_TAB_OLD") == "1" || m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count ||
            p_Document.PreviewMesh is not { Length: > 0 } s_Mesh || m_CamoDocumentKeyFor?.Invoke(s_Mesh) is not { } s_Key || !NativePerSubject(s_Key) ||
            PristineOf(p_Document.TargetShader) is not { } s_Pristine)
            return;

        // ⛔ the soldier is the one the file was saved on — his own mesh, PreviewWeapon, the one the restore picks — and only the PART is read off
        // the part's mesh: with nobody on screen yet, a mesh answers with the first soldier who wears it, and Aftermath's RU assault wears the US
        // assault's legs (the tab became the American's, and the Russian's legs came up untouched — measured, --soldiersimpletest RU_Assault_XP4)
        if (p_Document.PreviewWeapon is { Length: > 0 } s_Weapon && m_CamoDocumentKeyFor?.Invoke(s_Weapon) is { } s_WeaponKey && NativePerSubject(s_WeaponKey) &&
            s_WeaponKey.LastIndexOf('/') is > 0 and var s_SoldierEnd && s_Key.LastIndexOf('/') is > 0 and var s_PartStart)
            s_Key = s_WeaponKey[..s_SoldierEnd] + s_Key[s_PartStart..];

        var s_Common = ShaderGraph.FromJson(s_Pristine.ToJson());
        s_Common.NativeCamo = p_Document.NativeCamo;
        var s_Tab = m_Tabs[m_ActiveTab];
        s_Tab.Common = s_Common;
        s_Tab.DocumentSubject = s_Key;
        s_Tab.SubjectNatives[s_Key] = NativeKeyOf(p_Document);
        m_DocumentSubject = s_Key;
        Log($"Camo: the file is {s_Key.Split('/')[^1]}'s document of this soldier ({s_Key}) — this tab is that part's.");
    }

    /// <summary>
    /// Puts an opened camo back ON WHAT IT WAS SAVED ON: the weapon, and then the attachment when the file
    /// was saved on one — the same two picks the user would make, in the same order, because an attachment
    /// only exists in the picker UNDER its weapon. A file from before this (or one whose weapon is not in
    /// the catalogue) opens as it always did, and says so rather than looking like the pick failed.
    /// </summary>
    /// <summary>The restore an Open kicked off, so a driver can wait for it instead of guessing a delay.</summary>
    internal System.Threading.Tasks.Task? LastPreviewRestore { get; private set; }

    /// <param name="p_Document">
    /// The document that was just opened — taken as an argument on purpose: reading it back off the canvas
    /// assumes activating a tab leaves that same object there, and this restore silently did nothing because
    /// of it (measured 2026-09-20). The caller has the file it loaded; it does not have to be asked twice.
    /// </param>
    internal async System.Threading.Tasks.Task RestoreDocumentPreviewAsync(ShaderGraph p_Document)
    {
        if (!CamoMode || p_Document.PreviewMesh is not { Length: > 0 } s_Saved)
            return;

        // The weapon the file names; only a file saved before that existed has to fall back to "whose mesh is
        // this", which for a shared attachment answers with the FIRST weapon offering it, not necessarily the
        // one it was authored on — so it is a fallback, not the rule.
        var s_WeaponMesh = p_Document.PreviewWeapon is { Length: > 0 } s_Named
            ? s_Named
            : m_CamoWeaponOfAccessory?.Invoke(s_Saved) ?? s_Saved;

        // ⛔ the picker of the FAMILY the file was saved on: a vehicle's file opened while the weapons are listed (a studio just
        // started lists them) found no such subject and opened the graph on nothing — measured 2026-09-24, --sessionsavetest
        ShowFamilyListing(s_WeaponMesh);

        var s_Weapon = MeshList.Items.OfType<CamoWeapon>()
            .FirstOrDefault(p_W => p_W.Mesh.Equals(s_WeaponMesh, StringComparison.OrdinalIgnoreCase));

        if (s_Weapon == null)
        {
            Log($"Camo: this file was saved on '{s_Saved.Split('/')[^1]}', which is not in the weapon picker — " +
                "the graph opens on its own.");
            return;
        }

        MeshList.SelectedItem = s_Weapon;
        if (LastMeshPick != null)
            await LastMeshPick;

        if (s_WeaponMesh.Equals(s_Saved, StringComparison.OrdinalIgnoreCase))
            return;

        var s_Accessory = AccessoryList.Items.OfType<CamoWeapon>()
            .FirstOrDefault(p_A => p_A.Mesh.Equals(s_Saved, StringComparison.OrdinalIgnoreCase));

        if (s_Accessory == null)
        {
            Log($"Camo: the attachment this file was saved on is not among {s_Weapon.Mesh.Split('/')[^1]}'s — " +
                "the weapon is on screen instead.");
            return;
        }

        AccessoryList.SelectedItem = s_Accessory;
        if (LastMeshPick != null)
            await LastMeshPick;
    }

    private void OnSave(object p_Sender, RoutedEventArgs p_Args)
    {
        // ⛔ THE FILE IS THE CAMO, not whatever stands on the canvas: a material's graph being edited is
        // written into the document first (the save must carry the material edits), and the
        // camo comes back on screen — saving while a glass material was up used to save THAT as the document.
        if (m_MaterialEditKey != null)
        {
            SaveEditedMaterial();
            LeaveMaterialEdit(true);
        }

        var s_Dialog = new SaveFileDialog
        {
            Filter = "Shader graph (*.json)|*.json",
            FileName = m_GraphPath ?? $"{Canvas.Graph.Name}.json",
        };

        if (s_Dialog.ShowDialog() != true)
            return;

        SaveDocumentTo(s_Dialog.FileName);
    }

    /// <summary>
    /// ⛔ THE NODES ON SCREEN ARE THAT ATTACHMENT'S WORK, AND THE FILE HAS TO SAY SO.
    ///
    /// Picking an attachment in a FRESH tab aims the tab itself at the attachment's preset, so what the user
    /// edits is the document — while the studio keeps per-attachment work in <see cref="ShaderGraph.MaterialGraphs"/>,
    /// which is where picking that attachment goes looking. Saved only as a document, reopening the file put
    /// the attachment back and then drew the STOCK preset over the user's nodes (a specific node setup was
    /// saved, and opening it brought back the bare weapon preset 3p).
    /// Storing the graph where the pick reads it makes the file reopen on those nodes — and the bake ship them.
    ///
    /// Does nothing when the graph is already the attachment's material graph being edited (that path saves
    /// itself), or when what is on screen is a weapon.
    /// </summary>
    private void StoreTabGraphAsAttachmentWork(string p_Mesh)
    {
        // Each reason NAMED: a silent skip here is what made the last round look fixed when it was not.
        var s_Preset = m_CamoShaderFor?.Invoke(p_Mesh);
        var s_MaterialId = AccessoryMaterialId(p_Mesh);
        // ⛔ "A material is being edited" only saves itself when there is a CAMO PARKED behind it. Picking an
        // attachment re-enters material edit with nothing parked (the tab IS its graph), and re-dressing on
        // the way out re-enters it again — so bailing on the edit key alone skipped exactly the case this
        // exists for, and the round looked fixed while the nodes still came back stock.
        var s_Why =
            m_MaterialEditKey != null && m_MaterialParkedCamo != null
                ? "a material is being edited over a camo (that path saves itself)"
            : m_CamoWeaponOfAccessory?.Invoke(p_Mesh) == null ? "what is on screen is a weapon, not an attachment"
            : s_Preset == null ? "the attachment has no preset to take a camo with"
            : !s_Preset.Equals(TargetShaderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                ? $"this tab is aimed at '{TargetShaderBox.Text.Trim().Split('/')[^1]}', not at the attachment's '{s_Preset.Split('/')[^1]}'"
            : s_MaterialId == null ? "the material list is not this attachment's yet"
            : null;

        if (s_Why != null || s_MaterialId is not { } s_Id)
        {
            Log($"Camo: the nodes are saved as the document, not as this attachment's material — {s_Why}.");
            return;
        }

        // A copy, and WITHOUT the document-level lists: a material graph nested inside itself would grow the
        // file on every save and mean nothing to the bake.
        var s_Copy = ShaderGraph.FromJson(Canvas.Graph.ToJson());
        s_Copy.MaterialGraphs = null;
        s_Copy.Variations = null;
        s_Copy.PreviewMesh = null;
        s_Copy.PreviewWeapon = null;

        DocumentGraph.SetMaterialGraph(p_Mesh, s_Id, s_Copy);
        Log($"Camo: these nodes are saved as {p_Mesh.Split('/')[^1]}'s material #{s_Id} — reopening " +
            "the file brings them back on it, and the bake ships them for that attachment.");
    }

    /// <summary>Saving, with the file already chosen — what the button does once the dialog is past.</summary>
    internal void SaveDocumentTo(string p_Path)
    {
        // The material being edited belongs to the document, not the other way round (see OnSave).
        //
        // ⛔ AND IT LEAVES WITHOUT RE-DRESSING, measured 2026-09-20: dressing an ATTACHMENT re-enters its
        // material edit, so the flush undid itself and the canvas was the attachment's graph again by the
        // time the file was written — the file came out as a bare preset and the camo (with the user's nodes
        // inside it) never reached the disk. What is on screen is not touched by a save; the document is.
        if (m_MaterialEditKey != null)
        {
            SaveEditedMaterial();
            LeaveMaterialEdit(false);
        }

        // and the as-shipped graph on the canvas, edited, is the document's too (ShippedGraph)
        SyncShippedGraph();

        // ⛔⛔ THE FILE IS THE DOCUMENT, AND THE DOCUMENT IS NOT ALWAYS WHAT IS ON THE CANVAS. After a material
        // edit the camo is PARKED behind the material's graph, and while the weapon is shown as shipped the
        // canvas holds the weapon's own preset — both perfectly normal states to press Save in. Writing
        // Canvas.Graph wrote THOSE: a saved file came out as a bare `weaponpreset3p` and the attachment's
        // nodes were nowhere in it. `DocumentGraph` is the studio's own answer to "which one is
        // the camo"; the save has to ask it like everything else does.
        var s_Document = DocumentGraph;

        // Retargeting is about the graph the user is aiming, so it only applies when that IS the document.
        if (ReferenceEquals(Canvas.Graph, s_Document))
            RetargetGraph(TargetShaderBox.Text);

        // …and WHAT IT IS ON: in camo mode the file remembers the piece on screen AND the weapon it hangs
        // from, so opening it puts the graph back where it was authored instead of on nothing. Both, because
        // an attachment's mesh is the same on dozens of weapons and does not name one.
        if (CamoMode && m_PreviewMesh is { Length: > 0 } s_OnScreen)
        {
            s_Document.PreviewMesh = s_OnScreen;
            s_Document.PreviewWeapon = (MeshList.SelectedItem as CamoWeapon)?.Mesh;
            StoreTabGraphAsAttachmentWork(s_OnScreen);
        }

        // ⭐ THE FILE CARRIES THE SUBJECT ON SCREEN (saving must store only the shader + materials of the weapon/vehicle/accessory
        // being looked at). A tab keeps, for the whole session, the material work of every subject shown in
        // it (each vehicle's, each weapon's, each attachment's, by "mesh|id"); the save writes the camo's graph and, of that work,
        // only the piece being looked at: its edited material graphs, its materials kept off the camo and — an attachment — its own
        // numbers. The rest stays in the session, untouched: the file is a filtered copy, never a trim of the document.
        var s_AllMaterialGraphs = s_Document.MaterialGraphs;
        var s_AllMaterialsOff = s_Document.MaterialsOff;
        var s_AllAccessoryValues = s_Document.AccessoryValues;
        var s_LeftInSession = 0;
        if (CamoMode && m_PreviewMesh is { Length: > 0 } s_Subject)
        {
            bool Mine(string p_Mesh) => p_Mesh.Equals(s_Subject, StringComparison.OrdinalIgnoreCase);

            var s_Graphs = s_AllMaterialGraphs?
                .Where(p_G => ShaderGraph.TryParseMaterialKey(p_G.Key, out var s_Mesh, out _) && Mine(s_Mesh))
                .ToDictionary(p_G => p_G.Key, p_G => p_G.Value);
            s_LeftInSession = (s_AllMaterialGraphs?.Count ?? 0) - (s_Graphs?.Count ?? 0);
            s_Document.MaterialGraphs = s_Graphs is { Count: > 0 } ? s_Graphs : null;

            var s_Off = s_AllMaterialsOff?.Where(p_M => Mine(p_M.Key)).ToDictionary(p_M => p_M.Key, p_M => p_M.Value);
            s_Document.MaterialsOff = s_Off is { Count: > 0 } ? s_Off : null;

            // an attachment's own numbers are kept by its short name (the same mesh on every weapon that offers it)
            var s_Piece = m_CamoWeaponOfAccessory?.Invoke(s_Subject) != null ? AccessoryList.SelectedItem as CamoWeapon : null;
            var s_PieceKey = s_Piece == null ? null : ShaderGraph.AccessoryKey(s_Piece.Weapon, s_Piece.Tag);
            var s_Numbers = s_PieceKey == null
                ? null
                : s_AllAccessoryValues?.Where(p_A => p_A.Key.Equals(s_PieceKey, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(p_A => p_A.Key, p_A => p_A.Value, StringComparer.OrdinalIgnoreCase);
            s_Document.AccessoryValues = s_Numbers is { Count: > 0 } ? s_Numbers : null;
        }

        try
        {
            // ONE master file: the on-screen document plus every other variation draft of this target
            // embedded inside it. Open unpacks them back into drafts; the bake ships all of them.
            SnapshotVariationDraft();
            s_Document.Variations = CollectVariationDrafts(true);
            File.WriteAllText(p_Path, s_Document.ToJson());
            s_Document.Variations = null;
            m_GraphPath = p_Path;
            Log($"Saved {p_Path}" +
                (s_Document.PreviewMesh is { Length: > 0 } s_On ? $" (on {s_On.Split('/')[^1]})" : "") +
                $" [{s_Document.MaterialGraphs?.Count ?? 0} material graph(s)" +
                (s_LeftInSession > 0 ? $"; {s_LeftInSession} of other subjects stay in this session, not in the file" : "") + "]" +
                (m_VariationDrafts.Count > 0 ? " (variations embedded in the one file)" : ""));

            // A save into the instance library IS publication: the palette entry (and every host graph
            // that places it) follows this file from now on.
            if (Graph.InstanceLibrary.Contains(p_Path))
            {
                var s_Count = Graph.InstanceLibrary.Refresh(Log);
                BuildPalette();
                Log($"Instance library refreshed: {s_Count} fragment(s) available under 'Instances'.");
            }
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR saving graph: {s_Exception.Message}");
        }
        finally
        {
            // the session keeps every subject's work (see above)
            s_Document.MaterialGraphs = s_AllMaterialGraphs;
            s_Document.MaterialsOff = s_AllMaterialsOff;
            s_Document.AccessoryValues = s_AllAccessoryValues;
        }
    }

    private async void OnReloadFromGame(object p_Sender, RoutedEventArgs p_Args)
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
        {
            Log("Set the target shader first (double-click one in the browser, or type its name).");
            return;
        }

        await SelectTargetAsync(s_Target);
    }

    private string? EmitToDisk()
    {
        RetargetGraph(TargetShaderBox.Text);

        // The detected contract shapes PsIn/PsOut; null falls back to the rigid-mesh layout.
        var s_Result = new HlslEmitter { Contract = m_Contract }.Emit(Canvas.Graph);
        foreach (var s_Error in s_Result.Errors)
            Log($"ERROR: {s_Error}");

        if (!s_Result.Ok)
            return null;

        try
        {
            Directory.CreateDirectory(OutputFolder);
            var s_Path = Path.Combine(OutputFolder, $"{SafeName()}.hlsl");
            File.WriteAllText(s_Path, s_Result.Hlsl);
            Log($"Wrote {s_Path}");
            return s_Path;
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR writing HLSL: {s_Exception.Message}");
            return null;
        }
    }

    // CompileToDxbc emits the HLSL itself first, so one button covers the old Emit + Compile pair.
    private void OnExportHlsl(object p_Sender, RoutedEventArgs p_Args) => CompileToDxbc();

    /// <summary>Emits and compiles the graph. Returns the .dxbc path, or null with the reason already logged.</summary>
    private string? CompileToDxbc()
    {
        var s_HlslPath = EmitToDisk();
        if (s_HlslPath == null)
            return null;

        // (the built-in compiler: fxc's /T ps_5_0 /E main /O3 bytes — Emit.ShaderCompiler)
        var s_DxbcPath = Path.ChangeExtension(s_HlslPath, ".dxbc");
        if (File.Exists(s_DxbcPath))
            File.Delete(s_DxbcPath);
        var s_Error = ShaderCompiler.CompilePixelShader(s_HlslPath, s_DxbcPath);
        if (s_Error != null || !File.Exists(s_DxbcPath))
        {
            foreach (var s_Line in (s_Error ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Log($"  compiler: {s_Line.TrimEnd()}");
            Log("The shader did not compile.");
            return null;
        }

        Log($"Compiled -> {s_DxbcPath} ({new FileInfo(s_DxbcPath).Length} bytes)");
        return s_DxbcPath;
    }

    /// <summary>
    /// Bakes the graph into an installable mod: pick the maps, name the mod and bundle, choose where it goes.
    /// Everything the pipeline can get wrong is checked by the baker itself and reported per map.
    /// </summary>
    /// <summary>Set in camo mode: the bake produces a camo mod, and the shader bake below never runs.</summary>
    private Action? m_CamoBake;

    /// <summary>In camo mode: what "Delete camo…" runs (the studio's), or null when the button is hidden.</summary>
    private Action? m_CamoDelete;

    private void OnDeleteCamo(object p_Sender, RoutedEventArgs p_Args) => m_CamoDelete?.Invoke();

    /// <summary>In camo mode: what "Save camo…" and "Open camo…" run (the studio's), or null when the buttons are hidden.</summary>
    private Action? m_CamoSaveCamo;
    private Action? m_CamoOpenCamo;

    private void OnSaveCamo(object p_Sender, RoutedEventArgs p_Args) => m_CamoSaveCamo?.Invoke();
    private void OnOpenCamo(object p_Sender, RoutedEventArgs p_Args) => m_CamoOpenCamo?.Invoke();

    /// <summary>
    /// ⭐ THE CAMO READY TO BAKE, AS ONE THING (the request: save the camo ready to bake, nodes and all, so the user picks a camo, puts
    /// a sticker on it, and the bake bakes the sticker together with that camo): the
    /// tab on screen whole — its common document under every camo of the picker, the document of every subject changed in it, the subject on
    /// screen as it stands —, which is what the bake reads of the session (DocumentOfSubject, EditedMaterialGraphsOfSession…). A soldier's
    /// skin is every tab of his parts (ChangedDocumentsUnder reads them all), so his other parts' tabs of changes go too. The undo history
    /// stays behind: the file is the camo, not the session. Save… (SaveDocumentTo) is untouched — the document of the subject on screen
    /// alone. Each graph is its own JSON object; the studio adds the bake's answers and moves the pictures (CamoSession.SaveCamoTo).
    /// </summary>
    internal (System.Text.Json.Nodes.JsonArray Tabs, int Active) CamoTabsSnapshot()
    {
        // the document as the bake reads it: a material being edited written into it, the as-shipped graph on the canvas its own
        if (m_MaterialEditKey != null)
            SaveEditedMaterial();
        SyncShippedGraph();

        var s_Tabs = new System.Text.Json.Nodes.JsonArray();
        if (!CamoMode || m_ActiveTab < 0 || m_ActiveTab >= m_Tabs.Count)
            return (s_Tabs, 0);

        // (every subject's document with the pattern and the numbers the form shows now, as DocumentOfSubject hands them to the bake)
        ShareCamoSettings(m_Tabs[m_ActiveTab], DocumentGraph);

        var s_Indices = new List<int> { m_ActiveTab };
        if (NativePerSubject(m_DocumentSubject) && m_DocumentSubject!.LastIndexOf('/') is > 0 and var s_End)
        {
            var s_Soldier = m_DocumentSubject[..(s_End + 1)];
            bool Holds(EditorTab p_Tab) =>
                (p_Tab.DocumentSubject?.StartsWith(s_Soldier, StringComparison.OrdinalIgnoreCase) ?? false) ||
                p_Tab.Natives.Values.Any(p_S => p_S.Subjects.Keys.Any(p_K => p_K.StartsWith(s_Soldier, StringComparison.OrdinalIgnoreCase)));
            for (var t = 0; t < m_Tabs.Count; t++)
                if (t != m_ActiveTab && !m_Tabs[t].Original && Holds(m_Tabs[t]))
                    s_Indices.Add(t);
            s_Indices.Sort();
        }

        static System.Text.Json.Nodes.JsonNode? Json(ShaderGraph? p_Graph) =>
            p_Graph == null ? null : System.Text.Json.Nodes.JsonNode.Parse(p_Graph.ToJson());

        var s_Active = 0;
        foreach (var t in s_Indices)
        {
            var s_Tab = m_Tabs[t];
            var s_Live = t == m_ActiveTab;
            var s_Graph = s_Live ? DocumentGraph : s_Tab.Graph;
            var s_Subject = s_Live ? m_DocumentSubject : s_Tab.DocumentSubject;
            var s_NativeKey = s_Live ? NativeKeyOf(DocumentGraph) : s_Tab.NativeKey;

            var s_Natives = new System.Text.Json.Nodes.JsonObject();
            foreach (var (s_Key, s_Set) in s_Tab.Natives)
            {
                // (the subject on screen is the tab's Graph: a copy of it kept under its camo would be the one it was taken with)
                var s_Subjects = new System.Text.Json.Nodes.JsonObject();
                foreach (var (s_Of, s_Own) in s_Set.Subjects)
                    if (!string.Equals(s_Key, s_NativeKey, StringComparison.OrdinalIgnoreCase) || !string.Equals(s_Of, s_Subject, StringComparison.OrdinalIgnoreCase))
                        s_Subjects[s_Of] = Json(s_Own.Graph);
                s_Natives[s_Key] = new System.Text.Json.Nodes.JsonObject { ["Common"] = Json(s_Set.Common), ["Subjects"] = s_Subjects };
            }

            var s_SubjectNatives = new System.Text.Json.Nodes.JsonObject();
            foreach (var (s_Of, s_On) in s_Tab.SubjectNatives)
                s_SubjectNatives[s_Of] = s_On;
            if (s_Live && s_Subject != null && NativePerSubject(s_Subject))
                s_SubjectNatives[s_Subject] = s_NativeKey;

            if (s_Live)
                s_Active = s_Tabs.Count;
            s_Tabs.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["Title"] = s_Tab.Title,
                ["DocumentSubject"] = s_Subject,
                ["NativeKey"] = s_NativeKey,
                ["PreviewMesh"] = s_Live ? m_PreviewMesh : s_Tab.PreviewMesh,
                ["PreviewWeapon"] = s_Live ? m_PreviewWeapon : s_Tab.PreviewWeapon,
                ["Variation"] = s_Live ? m_Variation : s_Tab.Variation,
                ["Shape"] = (s_Live ? m_Preview.Shape : s_Tab.Shape).ToString(),
                ["Graph"] = Json(s_Graph),
                ["Base"] = Json(s_Tab.Base),
                ["Natives"] = s_Natives,
                ["SubjectNatives"] = s_SubjectNatives,
            });
        }

        return (s_Tabs, s_Active);
    }

    /// <summary>
    /// The other half of <see cref="CamoTabsSnapshot"/>: each saved tab back as a tab of changes of its own (never an original — a camo opened
    /// is the user's), in its order, with no undo history; the one that was on screen takes the window, and ActivateTab picks its subject
    /// again — the weapon, under its family, dressed with its pattern and numbers. Returns how many tabs were opened.
    /// </summary>
    internal int OpenCamoTabs(System.Text.Json.Nodes.JsonArray p_Tabs, int p_Active)
    {
        static ShaderGraph? Graph(System.Text.Json.Nodes.JsonNode? p_Node) =>
            p_Node is System.Text.Json.Nodes.JsonObject ? ShaderGraph.FromJson(p_Node.ToJsonString()) : null;
        static string? Text(System.Text.Json.Nodes.JsonNode? p_Node) =>
            p_Node is System.Text.Json.Nodes.JsonValue s_Value && s_Value.TryGetValue<string>(out var s_Text) ? s_Text : null;

        var s_Opened = new List<EditorTab>();
        foreach (var s_Saved in p_Tabs.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (Graph(s_Saved["Graph"]) is not { } s_Graph)
                continue;

            var s_Tab = new EditorTab
            {
                Graph = s_Graph,
                Title = Text(s_Saved["Title"]),
                DocumentSubject = Text(s_Saved["DocumentSubject"]),
                NativeKey = Text(s_Saved["NativeKey"]) ?? "",
                PreviewMesh = Text(s_Saved["PreviewMesh"]),
                PreviewWeapon = Text(s_Saved["PreviewWeapon"]),
                Variation = Text(s_Saved["Variation"]) ?? "0",
                Base = Graph(s_Saved["Base"]),
            };
            if (Enum.TryParse<View.PreviewShape>(Text(s_Saved["Shape"]), out var s_Shape))
                s_Tab.Shape = s_Shape;

            if (s_Saved["Natives"] is System.Text.Json.Nodes.JsonObject s_Natives)
                foreach (var (s_Key, s_Value) in s_Natives)
                {
                    var s_Set = new CamoDocuments { Common = Graph(s_Value?["Common"]) };
                    if (s_Value?["Subjects"] is System.Text.Json.Nodes.JsonObject s_Subjects)
                        foreach (var (s_Of, s_Document) in s_Subjects)
                            if (Graph(s_Document) is { } s_Own)
                                s_Set.Subjects[s_Of] = (s_Own, Array.Empty<string>(), Array.Empty<string>());
                    s_Tab.Natives[s_Key] = s_Set;
                }

            if (s_Saved["SubjectNatives"] is System.Text.Json.Nodes.JsonObject s_PerSubject)
                foreach (var (s_Of, s_On) in s_PerSubject)
                    s_Tab.SubjectNatives[s_Of] = Text(s_On) ?? "";

            s_Opened.Add(s_Tab);
        }

        if (s_Opened.Count == 0)
            return 0;

        SaveActiveTab();
        var s_First = m_Tabs.Count;
        m_Tabs.AddRange(s_Opened);
        ActivateTab(s_First + Math.Clamp(p_Active, 0, s_Opened.Count - 1));
        return s_Opened.Count;
    }

    /// <summary>Lets the studio write into the editor's own output panel instead of inventing another one.</summary>
    internal void LogCamo(string p_Message) => Log(p_Message);

    /// <summary>
    /// Loads an EXPLICIT set of textures, given as register -> asset, and shows them.
    ///
    /// The normal path reads the shader's slot map, which only covers the meshes one level scan saw. The
    /// studio knows the right set for every weapon from the material scan, so in camo mode it says which
    /// textures to use instead of letting the map guess.
    /// </summary>
    internal int LoadCamoTextures(IReadOnlyDictionary<string, string> p_Slots,
        IReadOnlyDictionary<string, string>? p_Values = null)
    {
        // ⛔⛔ THE TEXTURES ARE HALF OF WHAT A MATERIAL IS. The rest are its INSTANCE VALUES — the numbers the
        // game feeds the shader per material (DiffuseDarkening, CamoTiling, the smoothness set). This graph
        // builds its albedo from them BEFORE it samples anything: `Target1 = sqrt(Lerp(MixOut…))`, computed
        // from external constants. Leave them unset and the weapon draws a flat, untextured GREEN however
        // many textures were loaded — which is exactly what was on screen while the art was demonstrably
        // correct on disk.
        // The weapon's own numbers, with whatever the simple form set laid on top: the form's values belong
        // to the CAMO, so they outlive the weapon they were first set on.
        m_CamoWeaponValues = p_Values == null ? null : new Dictionary<string, string>(p_Values, StringComparer.OrdinalIgnoreCase);
        var s_Values = MergedCamoValues();
        if (s_Values != null)
            PushInstanceValues(new Dictionary<string, string>(s_Values));

        // ⛔ THE NAMES TRAVEL WITH THE ART. The properties panel used to name a register's texture from the
        // slot map, whose base set is another weapon's (the M4A1's) — so the thumbnail showed the M416's
        // diffuse and the label beside it said "m4a1_d". What dresses the weapon is what names its textures.
        m_CamoSlotNames = new Dictionary<string, string>(p_Slots);
        m_CamoValueNames = ApplyNodeOverrides(s_Values);

        m_TexturePreviews.Clear();
        m_TextureSrgb.Clear();
        var s_Loaded = 0;

        foreach (var (s_Register, s_Name) in p_Slots)
        {
            var s_Png = Path.Combine(TexCacheDir, $"{Sanitize(s_Name)}.png");
            if (!File.Exists(s_Png))
                continue;

            try
            {
                var s_Bitmap = new BitmapImage();
                s_Bitmap.BeginInit();
                s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
                s_Bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                s_Bitmap.UriSource = new Uri(s_Png);
                s_Bitmap.EndInit();

                m_TexturePreviews[s_Register] = s_Bitmap;
                m_TextureSrgb[s_Register] = TextureIsSrgb(s_Name);
                s_Loaded++;
            }
            catch (Exception)
            {
                // A thumbnail that will not decode leaves that slot empty; the rest still show.
            }
        }

        Canvas.Refresh();
        PushPreviewTextures();

        // The user's own files win over the game art on this path too: a pattern chosen on the node must
        // survive picking another weapon, or the camo being authored vanishes with every click.
        ApplyCustomTextureOverrides();
        return s_Loaded;
    }

    private async void OnBakeShader(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_CamoBake != null)
        {
            m_CamoBake();
            return;
        }

        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
        {
            Log("ERROR: set the target shader first - the bake replaces that shader's bytecode.");
            return;
        }

        var s_GamePath = GamePath;
        if (s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
        {
            Log("ERROR: the game folder is empty or does not exist - set it in Settings ▸ Folders.");
            return;
        }

        var s_Repl = FindRimeRepl();
        if (s_Repl == null)
        {
            Log("ERROR: RimeREPL.exe not found (it ships inside the studio; a build of the repository finds it under bin\\Release).");
            return;
        }

        // Compiled before asking anything: a graph that does not compile has nothing to bake, and finding that
        // out after the user has filled in the dialog wastes their time.
        var s_Dxbc = CompileToDxbc();
        if (s_Dxbc == null)
            return;

        var s_Levels = LevelScanner.Scan(s_GamePath);
        Log($"Found {s_Levels.Count} level(s) in the install.");

        var s_Request = BakeDialog.Ask(this, s_Levels, SafeName(), OutputFolder, s_Target,
            Canvas.Graph.BakeVariation);
        if (s_Request == null)
            return;

        BakeButton.IsEnabled = false;
        var s_WorkDir = Path.Combine(OutputFolder, "bake");

        // Custom textures convert BEFORE the mounts start: a bad file or a missing slot name should cost
        // seconds, not surface after minutes of bundle building. The names come from the texture set the
        // user is LOOKING at (their active variation), falling back to the base set.
        var s_Map = ReadSlotMapFull(SlotMapPath(s_Target));
        var s_BakeSlots = s_Map?.Variations != null && s_Map.Variations.TryGetValue(m_Variation, out var s_Set)
            ? s_Set.Slots
            : s_Map?.Slots;

        // ⛔ EVERY open document goes down the saved-graph lane, variation or not — it is the only one with
        // PER-VARIANT compilation, and that is not a refinement: a shader name owns several pixel flavours
        // with different constant/texture contracts, so the direct lane's single compilation written into
        // all of them renders the object BLACK. Measured: the map's barriers came out black through the
        // direct lane and correct through this one, from the same graph.
        //
        // This used to delegate only variations, which is why the difference stayed hidden — replacements
        // were the case nobody could deliver to the level's own objects until the database order was fixed,
        // so nothing had ever exercised the direct lane on them.
        var s_DelegatePrimary = true;
        var s_CustomTextures = new List<CustomBakeTexture>();

        if (s_DelegatePrimary)
        {
            Directory.CreateDirectory(s_WorkDir);
            var s_Self = ShaderGraph.FromJson(Canvas.Graph.ToJson());
            s_Self.Variations = null;

            // The variation rides the mesh of the (mesh, variation) set SELECTED in the preview — the same
            // picker the user already chose their object with. Baking "whichever mesh the database lists
            // first" put a glass variation on a destruction mesh once, which is not resident when the level
            // bundle loads and crashed the end of the loading screen registering against a null mesh.
            if (s_Map?.Variations != null && s_Map.Variations.TryGetValue(m_Variation, out var s_Chosen) &&
                s_Chosen.Mesh is { Length: > 0 })
                s_Self.BakeMesh = s_Chosen.Mesh;

            var s_SelfPath = Path.Combine(s_WorkDir, "current_variation.json");
            File.WriteAllText(s_SelfPath, s_Self.ToJson());
            s_Request.ExtraGraphs.Insert(0, s_SelfPath);
            Log(Canvas.Graph.BakeVariation is { Length: > 0 } s_Named
                ? $"Variation bake: '{s_Named}' — the open document joins the per-variant lane together " +
                  "with its drafts."
                : "The open document joins the per-variant lane: every pixel flavour of this shader gets " +
                  "the graph compiled against ITS OWN contract.");
        }
        else
        {
            var (s_Prepared, s_TextureError) = PrepareCustomTextures(
                Canvas.Graph, s_BakeSlots, Path.Combine(s_WorkDir, "textures"), Log);

            if (s_TextureError != null)
            {
                Log($"BAKE ABORTED: {s_TextureError}");
                BakeButton.IsEnabled = true;
                return;
            }

            s_CustomTextures = s_Prepared;
        }

        // Every OTHER variation draft of this target joins the bake by itself — nobody lists files by
        // hand for looks that already live in this document.
        SnapshotVariationDraft();
        var s_AutoDrafts = CollectVariationDrafts(true) ?? new List<ShaderGraph>();
        if (s_AutoDrafts.Count > 0)
        {
            var s_AutoDir = Path.Combine(s_WorkDir, "auto_variations");
            Directory.CreateDirectory(s_AutoDir);
            foreach (var s_Draft in s_AutoDrafts)
            {
                var s_AutoPath = Path.Combine(s_AutoDir, $"{Sanitize(s_Draft.BakeVariation!.Split('/')[^1])}.json");
                File.WriteAllText(s_AutoPath, s_Draft.ToJson());
                if (!s_Request.ExtraGraphs.Contains(s_AutoPath, StringComparer.OrdinalIgnoreCase))
                    s_Request.ExtraGraphs.Add(s_AutoPath);
            }

            Log($"{s_AutoDrafts.Count} variation draft(s) of this object join the bake automatically.");
        }

        // Saved graphs join the bake as full documents: each compiles against ITS OWN target's contract and
        // brings its own custom textures. Their preparation mounts the game once, so it runs off the UI thread.
        var s_Shaders = new List<ShaderBaker.BakeShader>();
        if (!s_DelegatePrimary)
            s_Shaders.Add(new(s_Target, s_Dxbc, null));
        if (s_Request.ExtraGraphs.Count > 0)
        {
            var s_Fxc = FindFxc();
            if (s_Fxc == null)
            {
                Log("BAKE ABORTED: fxc.exe not found, and the saved graphs need it to compile.");
                BakeButton.IsEnabled = true;
                return;
            }

            var s_ExtraProgress = new Progress<string>(Log);
            var s_TexCache = TexCacheDir;
            var (s_Extra, s_ExtraTextures, s_ExtraError) = await Task.Run(() => PrepareAdditionalGraphs(
                s_Request.ExtraGraphs, s_Repl, s_Fxc, s_GamePath, s_Request.Levels[0], s_TexCache,
                Path.Combine(s_WorkDir, "extra"), p_Line => ((IProgress<string>) s_ExtraProgress).Report(p_Line)));

            if (s_ExtraError != null)
            {
                Log($"BAKE ABORTED: {s_ExtraError}");
                BakeButton.IsEnabled = true;
                return;
            }

            s_Shaders.AddRange(s_Extra);

            // Two graphs overriding the same texture NAME with different files is a fight nobody notices
            // in the build output — refused here instead.
            foreach (var s_Extra2 in s_ExtraTextures)
            {
                var s_Clash = s_CustomTextures.FirstOrDefault(p_T =>
                    p_T.Name.Equals(s_Extra2.Name, StringComparison.OrdinalIgnoreCase) &&
                    !p_T.DdsPath.Equals(s_Extra2.DdsPath, StringComparison.OrdinalIgnoreCase));

                if (s_Clash != null)
                {
                    Log($"BAKE ABORTED: two graphs override the game texture '{s_Extra2.Name}' with " +
                        "different files.");
                    BakeButton.IsEnabled = true;
                    return;
                }

                if (s_CustomTextures.All(p_T => !p_T.Name.Equals(s_Extra2.Name, StringComparison.OrdinalIgnoreCase)))
                    s_CustomTextures.Add(s_Extra2);
            }
        }

        if (s_CustomTextures.Count > 0)
            Log($"{s_CustomTextures.Count} custom texture(s) will ride in the bundle as same-name overrides.");

        try
        {
            var s_Progress = new Progress<string>(Log);
            var s_Result = await Task.Run(() => ShaderBaker.Bake(s_Request, s_Shaders, s_GamePath,
                s_Repl, s_WorkDir, p_Line => ((IProgress<string>) s_Progress).Report(p_Line), s_CustomTextures));

            var s_Good = s_Result.Levels.Count(p_L => p_L.Ok);
            var s_Bad = s_Result.Levels.Count - s_Good;

            if (s_Good == 0)
            {
                Log("BAKE FAILED: no map produced a usable superbundle. Nothing was written.");
            }
            else
            {
                Log($"BAKE DONE: {s_Good} map(s) baked{(s_Bad > 0 ? $", {s_Bad} skipped or failed" : "")} -> {s_Result.ModFolder}");
                Log($"Install: copy '{s_Request.ModName}' into your VU Mods folder and add a line " +
                    $"'{s_Request.ModName}' to ModList.txt (no '#', or it stays disabled).");
            }

            foreach (var s_Level in s_Result.Levels.Where(p_L => !p_L.Ok))
                Log($"  {s_Level.Level}: {s_Level.Failure}");
        }
        catch (Exception s_Exception)
        {
            Log($"BAKE ERROR: {s_Exception.Message}");
        }
        finally
        {
            BakeButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Rewrites a bake's custom textures for a VARIATION: instead of overriding the vanilla texture by name
    /// (which would retexture every vanilla instance on the map), each ships under a sibling name and is
    /// bound in the CLONE's own constants at the register the vanilla art occupied. The vanilla texture
    /// becomes the group donor — a fresh name has no mounted original to copy the texture group from.
    /// </summary>
    internal static string? RetargetTexturesForVariation(List<CustomBakeTexture> p_Textures,
        VariationSpec p_Spec, Dictionary<string, string>? p_Slots)
    {
        for (var i = 0; i < p_Textures.Count; i++)
        {
            var s_Texture = p_Textures[i];

            if (s_Texture.SlotTarget == null)
            {
                // Same-name override lane: the register comes from the slot map row the name was taken from.
                var s_Register = p_Slots?.FirstOrDefault(p_S =>
                    p_S.Value.Equals(s_Texture.Name, StringComparison.OrdinalIgnoreCase)).Key;

                if (s_Register == null)
                    return $"no register maps to '{s_Texture.Name}' — reload the target textures and retry.";

                p_Textures[i] = s_Texture with
                {
                    Name = Emit.VariationPlan.SiblingTextureName(p_Spec.VariationAsset, s_Texture.Name),
                    SlotTarget = p_Spec.CloneShader,
                    SlotRegister = s_Register,
                    GroupDonor = s_Texture.Name,
                };
            }
            else
            {
                // New-slot lane: the slot surgery moves onto the clone; the name becomes a sibling.
                p_Textures[i] = s_Texture with
                {
                    Name = $"{p_Spec.VariationAsset}_t{s_Texture.SlotRegister}",
                    SlotTarget = p_Spec.CloneShader,
                };
            }
        }

        return null;
    }

    private string TexCacheDir => Path.Combine(OutputFolder, "texcache");

    internal static string SlotMapFileName(string p_Target) => $"{Sanitize(p_Target)}.slots.json";

    private string SlotMapPath(string p_Target) => Path.Combine(TexCacheDir, SlotMapFileName(p_Target));

    /// <summary>
    /// Repopulates the thumbnails from the on-disk cache. Returns how many were restored, so the caller can
    /// tell the difference between "nothing cached yet" and "cache served everything".
    /// </summary>
    /// <summary>
    /// Feeds the current variation's REAL parameter values into the preview's cb1 (pattern slots stay for
    /// everything unnamed), so the object shades as its instance. Null resets to the pure pattern - which
    /// must happen on target switches, or the previous shader's values would leak into the next preview.
    /// </summary>
    /// <summary>
    /// The engine-fed thermal parameters. In the game they are zero unless thermal optics are active; the
    /// preview's probe pattern is non-zero by design, which showed up as a magenta tint on character shaders.
    /// The Settings toggle decides which of the two the preview gets — the BAKE is untouched either way, since
    /// these live in the runtime constant buffer, never in the compiled shader.
    /// </summary>
    private static readonly string[] s_ThermalParameters = { "FLIRData", "FLIRScale" };

    private void PushInstanceValues(Dictionary<string, string>? p_Values, bool p_Log = true)
    {
        // ⛔ Not while one of the object's other materials is on the canvas: the numbers would be that
        // material's, resolved through ITS contract into the constants the camo draws with. Its own numbers
        // reach its section through RefreshMaterialEdits.
        if (m_MaterialEditKey != null)
            return;

        // Whatever the source of the constants — the slot map's variation, the studio's weapon set — the
        // graph's own preview values win: they are what the user typed on the nodes.
        p_Values = ApplyNodeOverrides(p_Values);

        var s_Resolved = new List<(int Element, float[] Value)>();

        // ⭐ a constant the target's bytecode has NO field for that the GRAPH declares (an external constant node with a slot of its own — the
        // Rhino's van body: its camo document adds CamoTiling, WearAmount… at c2-c6 of a block whose shader holds FLIRData/FLIRScale alone):
        // fed at the node's slot, the one the emitter declares it at (packoffset). Only where the contract has none: a field it names wins
        (int Register, int Element) GraphFieldOf(string p_Name)
        {
            foreach (var s_Node in Canvas.Graph.Nodes)
                if (s_Node.Kind == "ExternalConstant" && BareExternalName(s_Node).Equals(p_Name, StringComparison.OrdinalIgnoreCase) &&
                    s_Node.GetParam("Buffer").Equals("externalConstants", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(s_Node.GetParam("Register"), out var s_Register) && int.TryParse(s_Node.GetParam("Element"), out var s_Element))
                    return (s_Register, s_Element);

            return (-1, -1);
        }

        if (m_Contract != null && p_Values != null)
            foreach (var (s_Name, s_Text) in p_Values)
            {
                var (s_Register, s_Element) = m_Contract.ExternalFieldOf(s_Name);
                if (s_Register < 0)
                    (s_Register, s_Element) = GraphFieldOf(s_Name);
                if (s_Register != 1 || s_Element < 0)
                    continue;

                var s_Parts = s_Text.Split(',');
                var s_Value = new float[4];
                for (var i = 0; i < 4 && i < s_Parts.Length; i++)
                    float.TryParse(s_Parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out s_Value[i]);

                s_Resolved.Add((s_Element, s_Value));
            }

        var s_InstanceCount = s_Resolved.Count;

        if (m_Contract != null && !m_Settings.FlirPreview)
            foreach (var s_Name in s_ThermalParameters)
            {
                var (s_Register, s_Element) = m_Contract.ExternalFieldOf(s_Name);
                if (s_Register == 1 && s_Element >= 0 && s_Resolved.All(p_R => p_R.Element != s_Element))
                    s_Resolved.Add((s_Element, new float[4]));
            }

        // The emblem slot's layers: the engine feeds each weapon its owner's; the preview draws a sample (EmblemSlot.PreviewValues), and
        // every layer constant is fed — one left to the preview's pattern would draw a layer of garbage.
        if (Canvas.Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "EmblemLayers") is { } s_EmblemNode)
        {
            var s_Fields = Palette.EmblemFields(s_EmblemNode);
            var s_Sample = EmblemSlot.PreviewValues();
            for (var i = 0; i < s_Fields.Count; i++)
                if (s_Fields[i].Buffer == 1)
                    s_Resolved.Add((s_Fields[i].Element, s_Sample != null && i < s_Sample.Length ? s_Sample[i] : new float[4]));
        }

        // …and a projected slot's frames: the weapon on screen's (in the game each weapon's variation carries its own)
        if (Canvas.Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == "EmblemProjection") is { } s_ProjectionNode)
        {
            var s_Fields = Palette.EmblemFrameFields(s_ProjectionNode);
            var s_Frames = EmblemSlot.FramesOf(EmblemSlot.PreviewSlots, s_Fields.Count / EmblemSlot.FrameRegisters);
            for (var i = 0; i < s_Fields.Count; i++)
                if (s_Fields[i].Buffer == 1)
                    s_Resolved.Add((s_Fields[i].Element, s_Frames[i]));
        }

        m_Preview.SetExternalValues(s_Resolved.Count > 0 ? s_Resolved : null);

        // Quiet for a slider being dragged: a line per tick would bury the Output.
        if (!p_Log)
            return;

        if (s_InstanceCount > 0)
            Log($"{s_InstanceCount} instance value(s) wired into the preview's material parameters.");

        if (s_Resolved.Count > s_InstanceCount)
            Log("Thermal parameters (FLIR) read zero in the preview — enable them in Settings if you want " +
                "to see the thermal tint path.");
    }

    /// <summary>The variation whose texture set is being previewed; "0" is the unvaried master.</summary>
    private string m_Variation = "0";

    private bool m_VariationBoxFilling;

    private sealed class VariationChoice
    {
        public required string Hash { get; init; }
        public required string Name { get; init; }
        public override string ToString() => Name;
    }

    private void OnVariationChanged(object p_Sender, SelectionChangedEventArgs p_Args)
    {
        if (m_VariationBoxFilling || VariationBox.SelectedItem is not VariationChoice s_Choice)
            return;

        m_Variation = s_Choice.Hash;

        // ⛔ THE PICKED SET IS THE OBJECT THIS GRAPH IS AIMED AT, SO IT BELONGS IN THE DOCUMENT. It used to be
        // stamped only onto the throwaway copy the bake makes, which meant a SAVED graph could never carry it:
        // the file went out with no object, and a bake that reads it either aims at whatever mesh the cached
        // map lists first or — once that guess became a refusal — cannot be baked at all. Written here, the
        // choice survives a save, travels with the file and is what "use this on the objects already in the
        // level" is aimed by.
        if (ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim()))?.Variations is { } s_Sets &&
            s_Sets.TryGetValue(s_Choice.Hash, out var s_Set) && s_Set.Mesh is { Length: > 0 } &&
            !string.Equals(Canvas.Graph.BakeMesh, s_Set.Mesh, StringComparison.OrdinalIgnoreCase))
        {
            Canvas.Graph.BakeMesh = s_Set.Mesh;
            TouchGraph();
            Log($"This graph is now aimed at '{s_Set.Mesh}' (save it to keep that).");
        }

        m_TexturePreviews.Clear();
        m_TextureSrgb.Clear();
        if (LoadCachedPreviews() == 0)
            Log("No cached thumbnails for that variation; press 'Reload from game'.");
    }

    /// <summary>
    /// Shows the variation picker when the cached map carries more than one texture set. Filling the combo
    /// fires SelectionChanged, so a guard flag keeps that from re-entering the preview load.
    /// </summary>
    private void FillVariationBox(SlotMap p_Map)
    {
        // ⛔ In camo mode the picker stays gone. This runs on every texture load, so hiding it once in
        // EnterCamoMode would last exactly until the first weapon was picked and it reappeared.
        var s_Show = p_Map.Variations is { Count: > 1 } && !m_CamoHidesVariation;
        VariationLabel.Visibility = s_Show ? Visibility.Visible : Visibility.Collapsed;
        VariationBox.Visibility = s_Show ? Visibility.Visible : Visibility.Collapsed;

        m_VariationBoxFilling = true;
        VariationBox.Items.Clear();

        if (s_Show)
        {
            foreach (var (s_Hash, s_Variation) in p_Map.Variations!
                         .OrderBy(p_V => p_V.Key == "0" ? "" : p_V.Value.Name, StringComparer.OrdinalIgnoreCase))
                VariationBox.Items.Add(new VariationChoice
                {
                    Hash = s_Hash,

                    // Variation assets carry long partition paths; the last segment is the readable part.
                    Name = s_Variation.Name.Contains('/')
                        ? s_Variation.Name.Split('/').Last()
                        : s_Variation.Name,
                });

            // ⛔ THE DOCUMENT'S OWN OBJECT COMES BACK FIRST. A graph aimed at one mesh (the set picked here,
            // saved with it) reopened on "whatever this map lists first" shows another object's textures and
            // silently asks to be aimed again, every single time — the manual step this stamp exists to
            // remove. Matched by MESH, not by the set key, because the key of a shared shader carries the
            // mesh path and a re-scan can renumber it while the mesh stays the same.
            var s_Aimed = Canvas.Graph.BakeMesh is { Length: > 0 } s_Mesh
                ? p_Map.Variations!.FirstOrDefault(p_V =>
                    string.Equals(p_V.Value.Mesh, s_Mesh, StringComparison.OrdinalIgnoreCase)).Key
                : null;

            if (s_Aimed is { Length: > 0 })
                m_Variation = s_Aimed;

            // A shared shader's sets are keyed by (mesh, variation), so "0" may not exist: fall back to the
            // first listed set rather than to a key nobody wrote.
            else if (p_Map.Variations!.ContainsKey(m_Variation) == false)
                m_Variation = VariationBox.Items.Cast<VariationChoice>().FirstOrDefault()?.Hash ?? "0";

            VariationBox.SelectedItem = VariationBox.Items.Cast<VariationChoice>()
                .FirstOrDefault(p_C => p_C.Hash == m_Variation);
        }
        else
        {
            m_Variation = "0";
        }

        m_VariationBoxFilling = false;
    }

    private int LoadCachedPreviews()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
            return 0;

        try
        {
            var s_Map = ReadSlotMapFull(SlotMapPath(s_Target));
            if (s_Map == null)
                return 0;

            FillVariationBox(s_Map);
            var s_Chosen = s_Map.Variations != null &&
                           s_Map.Variations.TryGetValue(m_Variation, out var s_Variation)
                ? s_Variation
                : null;
            var s_Slots = s_Chosen?.Slots ?? s_Map.Slots;
            PushInstanceValues(s_Chosen?.Values);

            var s_Count = 0;
            foreach (var (s_Register, s_Name) in s_Slots)
            {
                var s_Png = Path.Combine(TexCacheDir, $"{Sanitize(s_Name)}.png");
                if (!File.Exists(s_Png))
                    continue;

                var s_Bitmap = new BitmapImage();
                s_Bitmap.BeginInit();
                // Same URI-cache trap as the custom loader: the cache PNGs get REWRITTEN on a re-dump, and
                // without this the thumbnails kept the pre-rewrite pixels until the editor restarted.
                s_Bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
                s_Bitmap.UriSource = new Uri(s_Png);
                s_Bitmap.EndInit();
                s_Bitmap.Freeze();

                m_TexturePreviews[s_Register] = s_Bitmap;
                m_TextureSrgb[s_Register] = TextureIsSrgb(s_Name);
                s_Count++;
            }

            if (s_Count > 0)
            {
                TouchGraph();
                PushPreviewTextures();
                Log($"Loaded {s_Count} cached thumbnail(s) from {TexCacheDir}");
            }

            // The user's chosen files ALWAYS win over the cache-derived art, on every repopulation path.
            ApplyCustomTextureOverrides();

            return s_Count;
        }
        catch (Exception s_Exception)
        {
            Log($"Could not read the thumbnail cache: {s_Exception.Message}");
            return 0;
        }
    }

    private async void OnLoadTextures(object p_Sender, RoutedEventArgs p_Args) => await RunLoadTexturesAsync();

    /// <summary>The texture step itself, awaitable so picking a target can chain it after the translation.</summary>
    private async Task RunLoadTexturesAsync()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        var s_GamePath = GamePath;

        if (s_Target.Length == 0)
        {
            Log("ERROR: set the target shader first.");
            return;
        }

        if (LoadCachedPreviews() > 0)
        {
            Log($"Served from cache. Delete {TexCacheDir} and press again to re-read from the game.");
            return;
        }

        if (s_GamePath.Length == 0)
        {
            Log("ERROR: the game folder is empty and could not be auto-detected — set it in Settings ▸ Folders.");
            return;
        }

        if (!Directory.Exists(s_GamePath))
        {
            Log($"ERROR: '{s_GamePath}' does not exist.");
            return;
        }

        var s_Repl = FindRimeRepl();
        if (s_Repl == null)
        {
            Log("ERROR: RimeREPL.exe not found (it ships inside the studio; a build of the repository finds it under bin\\Release).");
            return;
        }

        var s_ShaderDb = ResolveShaderDb(s_Target);
        var s_CacheDir = TexCacheDir;
        var s_MapPath = SlotMapPath(s_Target);
        ReloadButton.IsEnabled = false;
        Log($"Mounting BF3 to read '{s_Target}' from {s_ShaderDb} — this takes ~20 s (two mounts), please wait…");

        try
        {
            var s_Contract = m_Contract;
            var s_Log = await Task.Run(() =>
                LoadTexturesCore(s_Repl, s_GamePath, s_ShaderDb, s_Target, s_CacheDir, s_MapPath, s_Contract));

            foreach (var s_Line in s_Log)
                Log(s_Line);

            // The decoded PNGs are the single source for thumbnails, so reload through the cache path.
            if (LoadCachedPreviews() == 0)
                Log("No thumbnail could be produced. The lines above say which step failed.");
        }
        catch (Exception s_Exception)
        {
            Log($"ERROR loading textures: {s_Exception.Message}");
        }
        finally
        {
            ReloadButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Keeps only the SHTEX-SHADER blocks whose header names exactly the given shader (any render path),
    /// plus whatever lies outside any block. A substring-matched dump prints one block per SIBLING shader,
    /// and slot lists merged across siblings invent externals the target does not have.
    /// </summary>
    internal static string ScopeToShaderBlocks(string p_Output, string p_Target)
    {
        var s_Kept = new StringBuilder();
        bool? s_InTarget = null;

        foreach (var s_Line in p_Output.Split('\n'))
        {
            var s_Header = Regex.Match(s_Line, @"SHTEX-SHADER:\s*\[[^\]]*\]\s*(\S+)");
            if (s_Header.Success)
            {
                var s_Name = s_Header.Groups[1].Value;
                s_InTarget = string.Equals(s_Name, p_Target, StringComparison.OrdinalIgnoreCase) ||
                             s_Name.EndsWith("/" + p_Target, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (s_InTarget != false)
                s_Kept.Append(s_Line).Append('\n');
        }

        return s_Kept.ToString();
    }

    internal static List<string> LoadTexturesCore(string p_Repl, string p_GamePath, string p_ShaderDb,
        string p_Target, string p_CacheDir, string p_MapPath, ShaderContract? p_Contract = null)
    {
        var s_Result = new List<string>();
        Directory.CreateDirectory(p_CacheDir);

        // Besides the shaderdb's own lists, ask the mesh variation databases for material-bound textures: a
        // shader can take part (or all) of its textures per MATERIAL INSTANCE, and those names exist nowhere
        // in the shaderdb entry. The scope is the LEVEL ROOT (levels/<name>), not the shaderdb's own sublevel:
        // the base sublevel database only knows static props, while soldier gear (CharacterRoot and friends)
        // is recorded per GAMEMODE sublevel — a shader can have zero materials in one database and its whole
        // wardrobe in a sibling. The mesh filter (the shader's folder) keeps that from scanning every mesh of
        // the level. Same mount for everything.
        string? s_MvdbScope = null;
        if (p_ShaderDb.EndsWith("/shaderdb", StringComparison.OrdinalIgnoreCase))
        {
            var s_Sublevel = p_ShaderDb[..^"/shaderdb".Length];
            var s_LevelCut = s_Sublevel.LastIndexOf('/');
            s_MvdbScope = s_LevelCut > 0 ? s_Sublevel[..s_LevelCut] : s_Sublevel;
        }

        var s_FolderCut = p_Target.LastIndexOf('/');
        var s_MeshFilter = s_FolderCut > 0 ? p_Target[..s_FolderCut] : p_Target;

        var s_ListScript = Path.Combine(p_CacheDir, "list.rime");
        // (one mount of its own, or its part of a batch's: MainWindow.Batch)
        var s_List = RunGameCommands(p_Repl, p_GamePath, s_ListScript,
            $"dump_shader_textures {p_ShaderDb} {p_Target}\n" +
            (s_MvdbScope == null ? "" : $"dump_shader_material_textures {s_MvdbScope} {p_Target} {s_MeshFilter}\n") +
            // ⛔ AND THE SAME SCAN WITHOUT THE FOLDER FILTER, BECAUSE THE COUNT IS A DECISION. How many meshes
            // wear this shader is what says whether replacing it changes one object or repaints the map, and
            // the author cannot see it from inside the editor. The filtered pass above cannot answer it: it
            // only looks inside the shader's own folder, so a shader worn from elsewhere comes back as "one
            // mesh" — an undercount, which is the dangerous direction. Measured cost on a mounted game:
            // seconds, once, and the answer is cached with the slot map.
            (s_MvdbScope == null ? "" : $"dump_shader_material_textures {s_MvdbScope} {p_Target}\n"));

        var s_Slots = new Dictionary<string, string>();

        // The shaderdb dump matches by SUBSTRING too, so a shared-root family prints one SHTEX-SHADER block
        // per SIBLING ("Root/CharacterRoot" also prints _skin, _1p, _lod…) and an unscoped parse merges their
        // slot lists — the editor then reports externals this shader does not even have. Every SHTEX* regex
        // below reads only the blocks whose header names the exact target.
        var s_ShaderScope = ScopeToShaderBlocks(s_List.Output, p_Target);

        // Textures bound as shader CONSTANTS carry their real sampler register.
        foreach (Match s_Match in Regex.Matches(s_ShaderScope, @"SHTEX:\s*register=t(\d+)\s+name=(\S+)"))
            s_Slots[s_Match.Groups[1].Value] = s_Match.Groups[2].Value;

        // Mesh shaders normally bind STREAMABLE textures instead, and the shaderdb does not record their
        // register. The register comes from the compiled shader's own binding table (RDEF), passed in as the
        // contract. ⛔ It is NOT declaration order: that held for the oil drum by luck, but a lightmapped shader
        // has the engine in t1..t4 and its material from t5, and the parachute's are fully shuffled
        // (texture_Texture7 sits in t1). Without a contract the slots are left unassigned rather than guessed.
        // Three places a material texture can be named, merged into ONE list for the pairing:
        //   1. SHTEX-SLOT - the solution-level slot list. COMPLETE and ordered: it includes shared assets
        //      (e.g. a generic default normal map) that the per-shader streamable list omits entirely.
        //   2. SHTEX-STREAMABLE - the per-shader list; carries the tiling factor, and is the fallback when a
        //      permutation has no slot list.
        //   3. SHTEX-EXTSLOT + SHMATTEX - external parameter slots, whose VALUES live per material instance in
        //      the mesh variation database (base variation preferred).
        // ⛔ Texture asset names can contain SPACES ("…/ah-1z viper_exterior_d"), so no name/texture field is
        // ever matched with \S+ — each one is delimited by the NEXT literal key (or end of line) instead. A
        // \S+ match here silently truncated every ah1z texture to "…/ah-1z" and the dump then found nothing.
        var s_StreamableRows = Regex
            .Matches(s_ShaderScope, @"SHTEX-STREAMABLE:\s*index=(\d+)\s+name=(.+?)\s+coord=.*?factor=([\d.,\-]+)")
            .Select(p_M => (Index: int.Parse(p_M.Groups[1].Value), Name: p_M.Groups[2].Value,
                Factor: float.TryParse(p_M.Groups[3].Value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s_Factor)
                    ? s_Factor
                    : 0f))
            .GroupBy(p_S => p_S.Index)
            .Select(p_G => p_G.First())
            .OrderBy(p_S => p_S.Index)
            .ToList();

        // GBuffer solutions first - that is the permutation the editor translates and previews; a shader
        // without one (sky, forward) falls back to whatever mode it does have.
        List<(int Slot, string Name)> SlotRows(string p_Tag)
        {
            var s_All = Regex
                .Matches(s_ShaderScope, p_Tag + @":\s*mode=(\S+)\s+slot=(\d+)\s+name=(.+?)\r?$",
                    RegexOptions.Multiline)
                .Select(p_M => (Mode: p_M.Groups[1].Value, Slot: int.Parse(p_M.Groups[2].Value),
                    Name: p_M.Groups[3].Value))
                .ToList();

            var s_Preferred = s_All.Where(p_R => p_R.Mode.Contains("GBuffer")).ToList();
            return (s_Preferred.Count > 0 ? s_Preferred : s_All)
                .GroupBy(p_R => p_R.Slot)
                .Select(p_G => (p_G.Key, p_G.First().Name))
                .OrderBy(p_R => p_R.Key)
                .ToList();
        }

        var s_SlotList = SlotRows("SHTEX-SLOT");
        var s_ExternalSlots = SlotRows("SHTEX-EXTSLOT");

        // The per-shader external list: parameter names in declaration order, with their tiling factor
        // (CamoTile repeats ×10 across a soldier, and that lives here, not in the slot list).
        var s_ExternalRows = Regex
            .Matches(s_ShaderScope, @"SHTEX-EXTERNAL:\s*index=(\d+)\s+param=(\S+).*?factor=([\d.,\-]+)")
            .Select(p_M => (Index: int.Parse(p_M.Groups[1].Value), Param: p_M.Groups[2].Value,
                Factor: float.TryParse(p_M.Groups[3].Value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s_Factor)
                    ? s_Factor
                    : 1f))
            .GroupBy(p_R => p_R.Index)
            .Select(p_G => p_G.First())
            .OrderBy(p_R => p_R.Index)
            .ToList();

        // The slot list and the per-shader external list are BOTH partial, in different ways: a shared root
        // (CharacterRoot and family) carries no solution-level slot lists at all, and a vehicle preset's
        // chosen permutation lists ONE of its six externals (the sibling render path's solutions carry the
        // rest). Preferring whichever list is non-empty therefore dropped a vehicle's Diffuse/Camo/Specular
        // on the floor — the sound rule is the UNION by parameter name, slot rows first (their slot number
        // is the solution's own), per-shader rows filling in what no slot row named.
        foreach (var s_Row in s_ExternalRows)
            if (!s_ExternalSlots.Any(p_S => p_S.Name.Equals(s_Row.Param, StringComparison.OrdinalIgnoreCase)))
                s_ExternalSlots.Add((s_Row.Index, s_Row.Param));

        var s_ExternalFactors = s_ExternalRows
            .GroupBy(p_R => p_R.Param, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Factor, StringComparer.OrdinalIgnoreCase);

        // The shaderdb's own defaults for the external VALUE parameters (invariant-culture floats), kept in
        // the map so a foreign shader's cb starts from what the engine would feed, not from a guess.
        var s_ExternalDefaults = Regex
            .Matches(s_ShaderScope, @"SHTEX-EXTVAL:\s*mode=\S+\s+param=(\S+)\s+default=(\S+)")
            .GroupBy(p_M => p_M.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Groups[2].Value, StringComparer.OrdinalIgnoreCase);

        // The ADDRESS MODE of each sampler register, GBuffer permutation preferred (that is the one the preview
        // draws with). It is API state, absent from the bytecode, and some shaders USE it as an operation: the
        // kit atlas shader adds its tile atlas sampled at (u, v+1), and `v=Border` is what makes that add zero
        // for pieces with positive V. See SlotMap.Samplers.
        var s_SamplerRows = Regex
            .Matches(s_ShaderScope,
                @"SHTEX-SAMPLER:\s*mode=(\S+)\s+register=s(\d+)\s+u=(\S+)\s+v=(\S+)\s+w=(\S+)")
            .Select(p_M => (Mode: p_M.Groups[1].Value, Register: p_M.Groups[2].Value,
                Modes: $"{p_M.Groups[3].Value},{p_M.Groups[4].Value},{p_M.Groups[5].Value}"))
            .ToList();

        var s_PreferredSamplers = s_SamplerRows.Where(p_R => p_R.Mode.Contains("GBuffer")).ToList();
        var s_Samplers = (s_PreferredSamplers.Count > 0 ? s_PreferredSamplers : s_SamplerRows)
            .GroupBy(p_R => p_R.Register)
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Modes);

        // Every material texture row, kept PER TEXTURE SET. The set key is (mesh, variation): a shader owned
        // by one mesh varies by variation only (each painting, each camo), but a SHARED shader is used by
        // MANY meshes - every character head feeds its own art into shaders/Root/CharacterRoot - and pooling
        // those would shuffle heads together. All sets are collected in this one mount, swappable from cache.
        // The dump command matches the shader by SUBSTRING, which drags in siblings ("Root/CharacterRoot"
        // also matches characterroot_skin, _lod, _eye…) whose parameters share names but carry different
        // art. Rows are therefore filtered to the exact target here — merging a sibling's Diffuse into this
        // shader's sets would silently paint the wrong texture.
        // Exact name, or the row's tail when the target was given without its folder prefix — a tail match
        // still cannot admit a sibling, because "…/characterroot_skin" does not END in "/characterroot".
        static bool IsTargetRow(string p_RowShader, string p_TargetName) =>
            string.Equals(p_RowShader, p_TargetName, StringComparison.OrdinalIgnoreCase) ||
            p_RowShader.EndsWith("/" + p_TargetName, StringComparison.OrdinalIgnoreCase);

        var s_TextureRows = Regex
            .Matches(s_List.Output,
                @"SHMATTEX:\s*shader=(\S+)\s+mesh=(\S+)\s+variation=(\d+)\s+variationName=(.+?)\s+source=\S+\s+param=(\S+)\s+texture=(.+?)\r?$",
                RegexOptions.Multiline)
            .Select(p_M => (Shader: p_M.Groups[1].Value, Mesh: p_M.Groups[2].Value, Hash: p_M.Groups[3].Value,
                Name: p_M.Groups[4].Value, Param: p_M.Groups[5].Value, Texture: p_M.Groups[6].Value))
            .Where(p_R => p_R.Texture != "(unresolved)" && IsTargetRow(p_R.Shader, p_Target))
            .ToList();

        // SHMATMESH rows name the (mesh, variation) pairs THEMSELVES, one per database entry material —
        // emitted even when the material binds no texture parameters, which is exactly what a
        // streamable-textured prop looks like. They seed the set list so a variation bake knows the mesh
        // and the existing variation names for ANY target, not just externally-textured ones.
        var s_MeshRows = Regex
            .Matches(s_List.Output,
                @"SHMATMESH:\s*shader=(\S+)\s+mesh=(\S+)\s+variation=(\d+)\s+variationName=(.+?)\r?$",
                RegexOptions.Multiline)
            .Select(p_M => (Shader: p_M.Groups[1].Value, Mesh: p_M.Groups[2].Value, Hash: p_M.Groups[3].Value,
                Name: p_M.Groups[4].Value))
            .Where(p_R => IsTargetRow(p_R.Shader, p_Target))
            .ToList();

        // Every mesh in the level that wears THIS shader (exact name — the scan matches by substring, and a
        // preset's siblings, _alphatest/_metal/_nospec…, would otherwise be counted as users of it).
        var s_MeshUsers = s_TextureRows.Select(p_R => p_R.Mesh)
            .Concat(s_MeshRows.Select(p_R => p_R.Mesh))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_M => p_M, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var s_MultiMesh = s_MeshUsers.Count > 1;

        static string ShortName(string p_Path)
        {
            var s_Last = p_Path.Split('/').Last();
            return s_Last.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase) ? s_Last[..^5] : s_Last;
        }

        string SetKeyOf(string p_Mesh, string p_Hash) => s_MultiMesh ? $"{p_Mesh}|{p_Hash}" : p_Hash;

        string SetLabelOf(string p_Mesh, string p_Hash, string p_VariationName) => s_MultiMesh
            ? p_Hash == "0" ? ShortName(p_Mesh) : $"{ShortName(p_Mesh)} · {ShortName(p_VariationName)}"
            : p_VariationName == "(base)" ? "(base)" : ShortName(p_VariationName);

        var s_VariationSets = s_TextureRows
            .GroupBy(p_R => SetKeyOf(p_R.Mesh, p_R.Hash))
            .ToDictionary(p_G => p_G.Key, p_G => (
                Name: SetLabelOf(p_G.First().Mesh, p_G.First().Hash, p_G.First().Name),
                Mesh: p_G.First().Mesh,
                Hash: p_G.First().Hash,
                Asset: p_G.First().Name,
                Values: p_G.GroupBy(p_R => p_R.Param)
                    .ToDictionary(p_P => p_P.Key, p_P => p_P.First().Texture)));

        // Pairs that carried no texture rows still become sets (empty texture dict): a streamable prop's
        // base entry is one, and it is the seed a "create new variation" bake starts from.
        foreach (var s_MeshRow in s_MeshRows)
        {
            var s_SeedKey = SetKeyOf(s_MeshRow.Mesh, s_MeshRow.Hash);
            if (!s_VariationSets.ContainsKey(s_SeedKey))
                s_VariationSets[s_SeedKey] = (
                    Name: SetLabelOf(s_MeshRow.Mesh, s_MeshRow.Hash, s_MeshRow.Name),
                    Mesh: s_MeshRow.Mesh,
                    Hash: s_MeshRow.Hash,
                    Asset: s_MeshRow.Name,
                    Values: new Dictionary<string, string>());
        }

        // The default set: the first base (hash 0) one, else simply the first.
        var s_DefaultSet = s_VariationSets.Keys
            .OrderBy(p_K => s_VariationSets[p_K].Hash == "0" ? 0 : 1)
            .ThenBy(p_K => p_K, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        // The material instance's VECTOR parameters (cb1 values), per set: a mesh's material-level rows are
        // its base, its variation ASSET's rows override them for that variation.
        var s_VectorRows = Regex
            .Matches(s_List.Output,
                @"SHMATVEC:\s*shader=(\S+)\s+mesh=(\S+)\s+variation=(\d+)\s+variationName=.+?\s+source=(\S+)\s+param=(\S+)\s+value=(\S+)")
            .Select(p_M => (Shader: p_M.Groups[1].Value, Mesh: p_M.Groups[2].Value, Hash: p_M.Groups[3].Value,
                Source: p_M.Groups[4].Value, Param: p_M.Groups[5].Value, Value: p_M.Groups[6].Value))
            .Where(p_R => IsTargetRow(p_R.Shader, p_Target))
            .ToList();

        Dictionary<string, string>? VectorValuesFor(string p_SetKey)
        {
            if (!s_VariationSets.TryGetValue(p_SetKey, out var s_Set))
                return null;

            var s_Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_Row in s_VectorRows.Where(p_R => p_R.Mesh == s_Set.Mesh &&
                                                            p_R.Source == "material"))
                s_Result[s_Row.Param] = s_Row.Value;

            foreach (var s_Row in s_VectorRows.Where(p_R => p_R.Mesh == s_Set.Mesh &&
                                                            p_R.Hash == s_Set.Hash &&
                                                            p_R.Source == "variationAsset"))
                s_Result[s_Row.Param] = s_Row.Value;

            return s_Result.Count > 0 ? s_Result : null;
        }

        // The DEFAULT set's textures feed the base pairing; any other set resolves what it leaves unset.
        var s_MaterialValues = s_DefaultSet != null
            ? new Dictionary<string, string>(s_VariationSets[s_DefaultSet].Values)
            : new Dictionary<string, string>();

        foreach (var s_Set in s_VariationSets.Values)
            foreach (var (s_Param, s_Texture) in s_Set.Values)
                if (!s_MaterialValues.ContainsKey(s_Param))
                    s_MaterialValues[s_Param] = s_Texture;

        var s_Merged = new List<(int Index, string Name, float Factor)>();

        // The slot list supersedes the streamable list when present (it is the same textures plus the shared
        // ones); the factor still comes from the streamable row of the same name.
        if (s_SlotList.Count > 0)
            foreach (var (s_Slot, s_Name) in s_SlotList)
                s_Merged.Add((s_Slot, s_Name,
                    s_StreamableRows.FirstOrDefault(p_S => p_S.Name == s_Name).Factor is var s_F && s_F != 0f
                        ? s_F
                        : 1f));
        else
            s_Merged.AddRange(s_StreamableRows);

        // The stream part is shared by every variation; only the external values change between them.
        var s_StreamPart = s_Merged.ToList();

        // An external whose register the bytecode NAMES (texture_<param>) is bound directly — the class
        // pairing below only competes for the registers that stayed anonymous (texture_TextureN).
        var s_DirectRegisters = new List<int>();

        foreach (var (s_Slot, s_Param) in s_ExternalSlots)
        {
            // ⛔⛔ THE REGISTER THE BYTECODE NAMES FOR AN EXTERNAL IS THAT EXTERNAL'S, WHETHER OR NOT THIS SCAN FOUND ITS VALUE (2026-09-28,
            // the soldiers). It used to be reserved only once a material named a texture for it; with none — every character shader:
            // this scan's mesh filter is the SHADER's folder, shaders/root, which holds no mesh (MeshUsers 0) — the register stayed in the
            // pairing below and the fixed textures slid onto it one by one: characterroot_alphablend's glass scratches on t2
            // (texture_alpha), its flat normal on t3 (the scratches' texture_Texture3), its environment on t4 (the normal's
            // texture_Texture) and nothing on t5 (texture_Texture2, a cube) — the goggles and the gas mask drawn WHITE; every
            // CharacterRoot's ClothDetails on t1 (texture_Diffuse) instead of t5 (texture_Texture11).
            var s_Named = p_Contract?.RegisterForExternalTexture(s_Param) ?? -1;
            if (s_Named >= 0)
                s_DirectRegisters.Add(s_Named);

            if (!s_MaterialValues.TryGetValue(s_Param, out var s_Texture))
            {
                s_Result.Add($"  external '{s_Param}': no material in the mesh variation database names a " +
                             "texture for it; that register stays on the placeholder.");
                continue;
            }

            if (s_Named >= 0)
            {
                s_Slots[s_Named.ToString()] = s_Texture;
            }
            else
            {
                s_Merged.Add((1000 + s_Slot, s_Texture,
                    s_ExternalFactors.TryGetValue(s_Param, out var s_ExtFactor) ? s_ExtFactor : 1f));
            }
        }

        // …and the externals of the per-shader list too (one it names that the solution's slot list leaves out is still not a slot
        // for a fixed texture)
        foreach (var s_External in s_ExternalRows)
            if ((p_Contract?.RegisterForExternalTexture(s_External.Param) ?? -1) is var s_Own and >= 0 && !s_DirectRegisters.Contains(s_Own))
                s_DirectRegisters.Add(s_Own);

        if (s_Merged.Count > 0)
        {
            if (p_Contract == null)
            {
                s_Result.Add($"{s_Merged.Count} texture(s) named for this shader, but no contract for it yet, " +
                             "so their registers are unknown. Press Load on the level to detect contracts, then " +
                             "load the textures again.");
            }
            else
            {
                s_Result.Add($"{s_Merged.Count} texture(s) named for this shader " +
                             $"({s_SlotList.Count} slot, {s_ExternalSlots.Count} external, " +
                             $"{s_DirectRegisters.Count} bound by the register's own name); the rest paired " +
                             "by how the shader USES each slot (normal-map decode vs colour), in list order " +
                             "within each class.");

                foreach (var (s_Register, s_Name) in p_Contract.PairStreamables(s_Merged, s_DirectRegisters))
                    s_Slots[s_Register.ToString()] = s_Name;

                foreach (var s_Entry in s_Merged.Where(p_S => !s_Slots.ContainsValue(p_S.Name)))
                    s_Result.Add($"  [{s_Entry.Index}] {s_Entry.Name}: more textures than material slots in " +
                                 "this permutation; not loaded.");
            }
        }

        if (s_Slots.Count == 0)
        {
            // ⛔ ZERO MATERIAL TEXTURES IS A VALID SHADER, NOT A FAILED DUMP. The Mi-28's canopy glass
            // (`mi28_canopyglass`) reads only the engine's sky cubemap and the outdoor light constants — the
            // game names no texture for it and never will. Leaving no map on disk made "cached" false for
            // ever ("cached = both files"), so every pick of that cockpit would have mounted the game again
            // (2026-09-22). When the dump DID find the shader, an EMPTY map is the truthful answer and is
            // written; only a shader the game did not report at all is left without one.
            var s_Found = Regex.Matches(s_List.Output, @"SHTEX-SHADER:\s*\[[^\]]*\]\s*(\S+)")
                .Any(p_M => IsTargetRow(p_M.Groups[1].Value, p_Target));
            if (s_Found)
            {
                File.WriteAllText(p_MapPath, System.Text.Json.JsonSerializer.Serialize(
                    new SlotMap
                    {
                        V = 5, Slots = new Dictionary<string, string>(),
                        Variations = new Dictionary<string, VariationSlots>
                        {
                            ["0"] = new() { Name = "(base)", Slots = new Dictionary<string, string>() },
                        },
                        ExternalDefaults = s_ExternalDefaults.Count > 0 ? s_ExternalDefaults : null,
                        Samplers = s_Samplers.Count > 0 ? s_Samplers : null,
                        MeshUsers = s_MeshUsers.Count,
                        MeshUserSample = s_MeshUsers.Take(3).ToList(),
                        MeshUsersScope = s_MvdbScope,
                    }));
                s_Result.Add($"'{p_Target}' names no material texture at all (engine textures only): an empty map is cached, nothing to dump.");
                return s_Result;
            }

            s_Result.Add($"No texture slots reported for '{p_Target}'. Check the shader name and the shaderdb path.");
            s_Result.Add($"  (shaderdb used: {p_ShaderDb})");
            s_Result.Add("  RimeREPL said:");
            foreach (var s_Line in s_List.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6))
                s_Result.Add($"    {s_Line.TrimEnd()}");

            return s_Result;
        }

        s_Result.Add($"Found {s_Slots.Count} texture slot(s): " +
                     string.Join(", ", s_Slots.Select(p_S => $"t{p_S.Key}={p_S.Value}")));

        // Pair each remaining texture set the same way the default was paired: shared stream part + that
        // set's external values (default values fill what a set does not override).
        var s_VariationMaps = new Dictionary<string, VariationSlots>();
        if (p_Contract != null)
            foreach (var (s_Key, s_Set) in s_VariationSets)
            {
                if (s_Key == s_DefaultSet)
                    continue;

                var s_VariationSlots = new VariationSlots
                {
                    Name = s_Set.Name, Mesh = s_Set.Mesh, AssetName = s_Set.Asset,
                    Values = VectorValuesFor(s_Key),
                };
                var s_VariationMerged = s_StreamPart.ToList();
                var s_VariationDirect = new List<int>();

                foreach (var (s_Slot, s_Param) in s_ExternalSlots)
                {
                    if (!s_Set.Values.TryGetValue(s_Param, out var s_Texture) &&
                        !s_MaterialValues.TryGetValue(s_Param, out s_Texture))
                        continue;

                    var s_Named = p_Contract.RegisterForExternalTexture(s_Param);
                    if (s_Named >= 0)
                    {
                        s_VariationSlots.Slots[s_Named.ToString()] = s_Texture;
                        s_VariationDirect.Add(s_Named);
                    }
                    else
                    {
                        s_VariationMerged.Add((1000 + s_Slot, s_Texture,
                            s_ExternalFactors.TryGetValue(s_Param, out var s_ExtFactor) ? s_ExtFactor : 1f));
                    }
                }

                foreach (var (s_Register, s_Name) in p_Contract.PairStreamables(s_VariationMerged, s_VariationDirect))
                    s_VariationSlots.Slots[s_Register.ToString()] = s_Name;

                s_VariationMaps[s_Key] = s_VariationSlots;
            }

        if (s_VariationMaps.Count > 0)
            s_Result.Add($"{s_VariationMaps.Count + 1} texture set(s) for this shader: " +
                         $"{(s_DefaultSet != null ? s_VariationSets[s_DefaultSet].Name : "(base)")}, " +
                         string.Join(", ", s_VariationMaps.Values.Select(p_V => p_V.Name)));

        // One dump per distinct texture NAME across every set, all inside this same mount - switching
        // variation later is then a pure cache read, no remount.
        var s_AllTextures = new HashSet<string>(s_Slots.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var s_Variation in s_VariationMaps.Values)
            s_AllTextures.UnionWith(s_Variation.Slots.Values);

        var s_DumpScript = new StringBuilder();
        var s_Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Name in s_AllTextures)
        {
            var s_File = Path.Combine(p_CacheDir, $"{Sanitize(s_Name)}.dds");
            s_Files[s_Name] = s_File;
            s_DumpScript.Append($"dump_texture \"{s_Name}\" \"{s_File}\"\n");
        }

        // (one mount of its own, or its part of a batch's: MainWindow.Batch)
        RunGameCommands(p_Repl, p_GamePath, Path.Combine(p_CacheDir, "dump.rime"), s_DumpScript.ToString());

        var s_Decoded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (s_Name, s_File) in s_Files)
        {
            if (!File.Exists(s_File))
            {
                s_Result.Add($"  {s_Name}: no DDS produced (texture may be a streaming/residency case).");
                continue;
            }

            var s_Image = DdsImage.Load(s_File);
            if (s_Image == null)
            {
                s_Result.Add($"  {s_Name}: DDS not decodable for preview ({new FileInfo(s_File).Length} B).");
                continue;
            }

            var s_Png = Path.Combine(p_CacheDir, $"{Sanitize(s_Name)}.png");
            var s_Encoder = new PngBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(s_Image));
            using (var s_Stream = File.Create(s_Png))
                s_Encoder.Save(s_Stream);

            s_Decoded.Add(s_Name);
        }

        var s_Saved = new Dictionary<string, string>();
        foreach (var (s_Register, s_Name) in s_Slots)
        {
            if (!s_Decoded.Contains(s_Name))
                continue;

            s_Saved[s_Register] = s_Name;
            s_Result.Add($"  t{s_Register}: thumbnail -> {Sanitize(s_Name)}.png");
        }

        var s_BaseValues = s_DefaultSet != null ? VectorValuesFor(s_DefaultSet) : null;
        if (s_BaseValues != null)
            s_Result.Add($"{s_BaseValues.Count} instance value(s) from the level's material data: " +
                         string.Join(", ", s_BaseValues.Select(p_V => $"{p_V.Key}={p_V.Value}")));

        var s_VariationsOut = new Dictionary<string, VariationSlots>
        {
            [s_DefaultSet ?? "0"] = new()
            {
                Name = s_DefaultSet != null ? s_VariationSets[s_DefaultSet].Name : "(base)",
                Mesh = s_DefaultSet != null ? s_VariationSets[s_DefaultSet].Mesh : null,
                AssetName = s_DefaultSet != null ? s_VariationSets[s_DefaultSet].Asset : null,
                Slots = s_Saved,
                Values = s_BaseValues,
            },
        };

        foreach (var (s_Hash, s_Variation) in s_VariationMaps)
            s_VariationsOut[s_Hash] = s_Variation;

        File.WriteAllText(p_MapPath, System.Text.Json.JsonSerializer.Serialize(
            new SlotMap
            {
                V = 5, Slots = s_Saved, Variations = s_VariationsOut,
                ExternalDefaults = s_ExternalDefaults.Count > 0 ? s_ExternalDefaults : null,
                Samplers = s_Samplers.Count > 0 ? s_Samplers : null,
                MeshUsers = s_MeshUsers.Count,
                MeshUserSample = s_MeshUsers.Take(3).ToList(),
                MeshUsersScope = s_MvdbScope,
            }));
        return s_Result;
    }

    /// <summary>What one press of "Translate target to graph" produced, so the caller can report all of it.</summary>
    internal sealed class TranslateResult
    {
        public ShaderGraph? Graph { get; set; }

        /// <summary>The exact bytecode that was translated, kept so --shaderdiff can be run against THIS file.</summary>
        public string? OriginalDxbc { get; set; }

        public string? SourceFile { get; set; }

        /// <summary>
        /// The contract of the permutation that was ACTUALLY translated. It has to travel with the graph: the
        /// texture registers a shader uses differ between its own permutations (the Barrack's plain pass holds
        /// its material at t1..t4, its lightmapped one at t5..t8), so a slot map built from a different
        /// permutation puts the art in registers the graph never samples - which renders as a blank object.
        /// </summary>
        public ShaderContract? Contract { get; set; }

        public int Instructions { get; set; }
        public List<string> Warnings { get; } = new();

        /// <summary>Opcodes with no node, and cbuffer fields with no node. Either one makes the graph inexact.</summary>
        public List<string> Untranslated { get; } = new();

        public List<string> Unmodelled { get; } = new();
        public List<string> Log { get; } = new();
    }

    /// <summary>What TranslateCore asks the game for — one place, so a batch that reads it ahead asks the very same thing.</summary>
    private static string TranslateExtractCommands(string p_ShaderDb, string p_Target, string p_Extract) =>
        $"extract_shader_dxbc {p_ShaderDb} \"{p_Target}\" \"{p_Extract}\"\n";

    /// <summary>
    /// The whole "see this shader as a graph" cycle: pull the target's compiled bytecode out of the game,
    /// disassemble it with fxc, and translate it into nodes.
    ///
    /// It does NOT recover the graph DICE authored - that is destroyed at bake time, and many graphs compile to
    /// the same bytecode, so there is no unique original to recover. It builds ONE graph that computes the same
    /// thing, which is a claim --shaderdiff can actually prove.
    ///
    /// Static and synchronous so the headless seam runs the identical code the button runs.
    /// </summary>
    internal static TranslateResult TranslateCore(string p_Repl, string p_Fxc, string p_GamePath,
        string p_ShaderDb, string p_Target, string p_WorkDir, ShaderContract? p_Contract)
    {
        var s_Result = new TranslateResult();
        Directory.CreateDirectory(p_WorkDir);

        var s_Extract = Path.Combine(p_WorkDir, Sanitize(p_Target));

        // (one mount of its own, or its part of a batch's: MainWindow.Batch — PretranslateCamo reads every shader it translates in one)
        var s_Run = RunGameCommands(p_Repl, p_GamePath, Path.Combine(p_WorkDir, "extract.rime"), TranslateExtractCommands(p_ShaderDb, p_Target, s_Extract),
            () =>
            {
                if (Directory.Exists(s_Extract))
                    try { Directory.Delete(s_Extract, true); } catch { }
            });

        // The extractor writes straight into the output folder for a single match, and numbered subfolders plus
        // an index.txt when the name matched several shaders. Both shapes have to be handled: picking the wrong
        // folder would silently translate a different shader.
        var s_Folder = s_Extract;
        var s_IndexPath = Path.Combine(s_Extract, "index.txt");
        if (File.Exists(s_IndexPath))
        {
            var s_Rows = File.ReadAllLines(s_IndexPath)
                .Select(p_L => p_L.Split('\t'))
                .Where(p_P => p_P.Length >= 2)
                .ToList();

            var s_Row = s_Rows.FirstOrDefault(
                            p_P => p_P[1].Equals(p_Target, StringComparison.OrdinalIgnoreCase))
                        ?? s_Rows.FirstOrDefault();

            if (s_Row == null)
            {
                s_Result.Log.Add($"'{p_Target}' matched several shaders but none of them is that exact name.");
                return s_Result;
            }

            s_Folder = Path.Combine(s_Extract, s_Row[0]);
            if (s_Rows.Count > 1)
                s_Result.Log.Add($"The name matched {s_Rows.Count} shaders; translating the exact match '{s_Row[1]}'.");
        }

        var s_Candidates = Directory.Exists(s_Folder)
            ? new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").OrderByDescending(p_F => p_F.Length).ToList()
            : new List<FileInfo>();

        if (s_Candidates.Count == 0)
        {
            s_Result.Log.Add($"No pixel shader came out of the game for '{p_Target}'.");
            s_Result.Log.Add($"  (shaderdb used: {p_ShaderDb})");
            s_Result.Log.Add("  RimeREPL said:");
            foreach (var s_Line in s_Run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6))
                s_Result.Log.Add($"    {s_Line.TrimEnd()}");

            return s_Result;
        }

        // (the permutation of the declaration its subjects draw with when the plainest serves none of them — ShaderIndex.SubjectDeclarations)
        var s_Chosen = ShaderIndex.ChooseGBufferPermutation(s_Candidates, s_Result.Log, p_Target);
        s_Result.SourceFile = s_Chosen.Name;

        // ⚠ Copied to a short name first: fxc /dumpbin fails on long paths with nothing but "compilation failed;
        // no code produced", and the extracted names are long.
        var s_Short = Path.Combine(p_WorkDir, "target.dxbc");
        File.Copy(s_Chosen.FullName, s_Short, true);
        s_Result.OriginalDxbc = s_Short;

        // Detected from the very bytes about to be translated, so the registers the caller loads textures into
        // are by construction the registers the graph samples.
        try { s_Result.Contract = ShaderContract.Detect(File.ReadAllBytes(s_Short)); }
        catch { }

        var s_Asm = Path.Combine(p_WorkDir, "target.asm");
        if (File.Exists(s_Asm))
            File.Delete(s_Asm);

        // (the built-in disassembler: the very text `fxc /dumpbin /Fc` writes — Emit.ShaderCompiler, proven line for line by --compilertest)
        var s_DumpError = ShaderCompiler.Disassemble(s_Short, s_Asm);
        if (!File.Exists(s_Asm))
        {
            s_Result.Log.Add($"the shader could not be disassembled: {s_DumpError}");
            return s_Result;
        }

        var s_Instructions = Translate.DxbcAsm.Parse(File.ReadAllLines(s_Asm));

        // Classified BEFORE the build: the builder folds UV interpolators into TexCoord nodes, and the
        // classification is what says which interpolators ARE UVs. The sweep runs in this order already.
        s_Result.Contract?.ClassifyInterpolators(s_Instructions);

        // ⛔ The builder reads cbuffer FIELD NAMES through the contract, so it must get the one detected from
        // the very bytes being translated - the caller's contract belongs to whatever the browser showed LAST
        // (or is null in a fresh window), and with it externalConstants fields resolve to nothing and every
        // external silently becomes a literal 0. Caught on XP2: spec/smoothness alphas off while RGB was exact.
        var s_Builder = new Translate.GraphBuilder(s_Result.Contract ?? p_Contract);
        var s_Name = p_Target.Split('/').Last();

        s_Result.Graph = s_Builder.Build(s_Instructions, p_Target, s_Name);
        s_Result.Instructions = s_Instructions.Count;

        // The authored surface facts the bytecode cannot carry, stamped from the shaderdb's own record
        // (the extractor writes them next to the permutations): the real SurfaceShaderType — a bush is
        // OpaqueAlphaTest, not the Opaque default — and whether its solutions render double-sided.
        var s_InfoPath = Path.Combine(s_Folder, "shaderinfo.txt");
        if (s_Result.Graph?.Root is { } s_TranslatedRoot && File.Exists(s_InfoPath))
            foreach (var s_InfoLine in File.ReadAllLines(s_InfoPath))
            {
                var s_Pair = s_InfoLine.Split('=', 2);
                if (s_Pair.Length != 2)
                    continue;

                if (s_Pair[0] == "type" && s_TranslatedRoot.Def.Params.Any(p_P => p_P.Name == "SurfaceShaderType"))
                    s_TranslatedRoot.Params["SurfaceShaderType"] = s_Pair[1].Trim();
                if (s_Pair[0] == "doubleSided" && s_TranslatedRoot.Def.Params.Any(p_P => p_P.Name == "DoubleSided"))
                    s_TranslatedRoot.Params["DoubleSided"] = s_Pair[1].Trim() == "1" ? "true" : "false";
            }

        // Which registers this permutation decodes as normal maps, read from the graph the translation just
        // built (its NormalMap nodes carry the register). The texture loader pairs streamables BY that usage,
        // which is the rule that survived measurement - see ShaderContract.PairStreamables.
        if (s_Result.Contract != null && s_Result.Graph != null)
            s_Result.Contract.NormalTextureRegisters = s_Result.Graph.Nodes
                .Where(p_N => p_N.Kind == "NormalMap")
                .Select(p_N => int.TryParse(p_N.GetParam("Register"), out var s_Register) ? s_Register : -1)
                .Where(p_R => p_R > 0)
                .Distinct()
                .ToList();

        // And what each texture scale IS, by the same principle: the shader's own instructions name them.
        // (Interpolators were classified before the build - the builder needs them.)
        s_Result.Contract?.ClassifyTextureScales(s_Instructions);

        // The AS-TRANSLATED fingerprint: what this graph's HLSL hashes to before the user touches anything.
        // A later variation bake re-emits and compares — equal means "only textures changed" (the translated
        // graph is equivalence-verified, so same emission = vanilla logic by construction); different means
        // the shader was reworked and needs its own cloned entry. Errs SAFE: a spurious mismatch only costs
        // an unnecessary clone, never a missing one.
        if (s_Result.Graph != null)
        {
            s_Result.Graph.TranslatedHlslHash = HlslFingerprint(s_Result.Graph, s_Result.Contract);
            // (and which permutation it is: RetireStaleTranslation keeps a cached translation stamped with the one chosen now)
            s_Result.Graph.TranslatedFrom = s_Chosen.Name;
        }

        // Whether the graph came out as the artist's handful of nodes or as the literal instruction-by-instruction
        // translation - and if the latter, WHY. Without this the only thing a long graph tells you is that it is
        // long, and there is no way to tell "this shader is genuinely complicated" from "the matcher gave up".
        s_Result.Log.Add(s_Builder.FrameRecognised
            ? "Recognised as a standard surface shader: the g-buffer frame is the root node, so what you see is " +
              "the material itself."
            : $"NOT the standard frame ({s_Builder.FrameRejection}), so this is the literal translation - one " +
              "node per operation. Every node is needed for it to compute the same thing; there is just no " +
              "shorter way to say it yet.");
        s_Result.Warnings.AddRange(s_Builder.Warnings.Distinct());
        s_Result.Untranslated.AddRange(s_Builder.UntranslatedOpcodes.OrderBy(p_O => p_O, StringComparer.Ordinal));
        s_Result.Unmodelled.AddRange(s_Builder.UnmodelledConstants.OrderBy(p_C => p_C, StringComparer.Ordinal));
        return s_Result;
    }

    /// <summary>Completed by the click handler so the headless seam can wait for the async work to finish.</summary>
    internal static TaskCompletionSource<int>? TranslateFinished;

    private async void OnTranslateShader(object p_Sender, RoutedEventArgs p_Args) => await RunTranslateAsync();

    /// <summary>
    /// The translate step itself. Split out from the button handler so picking a target can run it directly:
    /// an `async void` handler cannot be awaited, and chaining these two steps needs to know when the first
    /// one finished before the second reads the contract it produced.
    /// </summary>
    /// <returns>True when a graph actually landed on the canvas.</returns>
    private async Task<bool> RunTranslateAsync()
    {
        var s_Target = TargetShaderBox.Text.Trim();
        if (s_Target.Length == 0)
        {
            Log("ERROR: pick a target shader first - the translation reads THAT shader's bytecode.");
            TranslateFinished?.TrySetResult(0);
            return false;
        }

        var s_GamePath = GamePath;
        if (s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
        {
            Log("ERROR: the game folder is empty or does not exist - set it in Settings ▸ Folders.");
            TranslateFinished?.TrySetResult(0);
            return false;
        }

        var s_Repl = FindRimeRepl();
        var s_Fxc = FindFxc();
        if (s_Repl == null || s_Fxc == null)
        {
            Log(s_Repl == null
                ? "ERROR: RimeREPL.exe not found (it ships inside the studio; a build of the repository finds it under bin\\Release)."
                : "ERROR: fxc.exe not found. Install the Windows SDK or put fxc.exe on PATH.");

            TranslateFinished?.TrySetResult(0);
            return false;
        }

        ReloadButton.IsEnabled = false;
        Log($"Reading '{s_Target}' out of the game and translating it - one mount, so ~20 s…");

        try
        {
            var s_Db = ResolveShaderDb(s_Target);
            var s_Work = Path.Combine(OutputFolder, "translate");
            var s_Contract = m_Contract;

            var s_Result = await Task.Run(() =>
                TranslateCore(s_Repl, s_Fxc, s_GamePath, s_Db, s_Target, s_Work, s_Contract));

            foreach (var s_Line in s_Result.Log)
                Log(s_Line);

            if (s_Result.Graph == null)
            {
                Log("Nothing was translated; the graph on the canvas is untouched.");
                return false;
            }

            // Retargeting replaces the canvas, so it goes on the undo stack first: leaving the previous graph up
            // is what made the editor look like it had loaded something when it had not.
            Canvas.PushUndo();
            LoadGraph(s_Result.Graph);

            // The generated graph lands wherever the layout put it, which is rarely where the view is looking.
            Canvas.FrameGraph();

            // ⛔ Adopt the translated permutation's contract, and DROP the stale thumbnails: the cached slot map
            // was built for whatever permutation the level catalogue had picked, and keeping it is how the art
            // ends up in registers this graph never samples - which draws as a blank object with no error.
            if (s_Result.Contract != null)
            {
                m_Contract = s_Result.Contract;
                m_TexturePreviews.Clear();
                m_TextureSrgb.Clear();

                // The preview's vertex shader now speaks THIS shader's interpolator language: without it an
                // "Unknown" family got worldpos/tangent-rows where its own bytecode says normal/uv, and drew a
                // flat grey cube while every differential number stayed green.
                m_Preview.SetInterpolatorMeanings(s_Result.Contract.InterpolatorMeanings);
                PushInstanceValues(null);

                var s_Slots = string.Join(", ", s_Result.Contract.MaterialTextures.Select(p_T => "t" + p_T.Register));
                Log($"  contract taken from the translated permutation; its material sits in {s_Slots}.");
            }

            Log($"{s_Result.Instructions} instruction(s) -> {s_Result.Graph.Nodes.Count} nodes, " +
                $"{s_Result.Graph.Connections.Count} connections. Ctrl+Z restores the previous graph.");

            foreach (var s_Warning in s_Result.Warnings)
                Log("  note: " + s_Warning);

            // ⚠ Two different ways to be inexact, and both have to be said out loud. A missing opcode leaves a
            // register at its old value; an unmodelled cbuffer field shades with a zero where the game has a real
            // constant. Neither raises an error anywhere: the graph compiles and renders either way.
            if (s_Result.Untranslated.Count > 0)
                Log($"  ⚠ NOT EQUIVALENT: no node yet for {string.Join(", ", s_Result.Untranslated)}, so those " +
                    "instructions were skipped and their results are wrong. Everything else is faithful.");

            if (s_Result.Unmodelled.Count > 0)
                Log($"  ⚠ NOT EQUIVALENT: {string.Join(", ", s_Result.Unmodelled)} came through as 0 - the " +
                    "editor has no node for those constants yet. The shape of the graph is right, those values " +
                    "are not.");

            if (s_Result.Untranslated.Count == 0 && s_Result.Unmodelled.Count == 0)
                Log("  every instruction and every constant had a node. Prove it: --shaderdiff <saved graph> " +
                    $"\"{s_Result.OriginalDxbc}\" \"{TexCacheDir}\"");

            Log("  the graph is GENERATED from the bytecode, not the original authored one - that is stripped " +
                "at bake time. It computes the same thing, which is the part that can be proven.");

            return true;
        }
        catch (Exception s_Exception)
        {
            Log($"TRANSLATE ERROR: {s_Exception.Message}");
            return false;
        }
        finally
        {
            ReloadButton.IsEnabled = true;
            TranslateFinished?.TrySetResult(Canvas.Graph.Nodes.Count);
        }
    }

    /// <summary>
    /// Presses the Translate button on a window that is never shown, and waits for the async handler to finish.
    /// The point is that it goes in where the user goes in - the real click, the real handler, the real game mount -
    /// rather than calling TranslateCore directly, which would skip exactly the wiring that tends to break.
    /// </summary>
    /// <summary>
    /// Drives a whole target SELECTION headlessly and checks the two things the user expects to be true afterwards
    /// without touching anything else: a graph on the canvas, and its textures in their slots.
    ///
    /// ⚠ What it does not cover, said plainly: pulling the shader name out of the clicked TreeViewItem. That is
    /// one line and it is the part that was already working; everything downstream of it is exercised here.
    /// </summary>
    /// <summary>
    /// Translates each preset ONCE and keeps the graph, so the studio never reads them out of the game while
    /// the user is working.
    ///
    /// ⛔ THE TRANSLATE STEP HAS NO CACHE OF ITS OWN — it means "read it from the game", and always mounts.
    /// That is right for the editor's "Reload from game" and wrong for camo work, where the same handful of
    /// presets would be re-read for every weapon. Paying for them here is the difference between a tool that
    /// opens instantly and one that mounts the game on a click.
    /// </summary>
    /// <summary>
    /// How many of the root's pins a graph actually FEEDS — the one test that tells a translation from the
    /// stub a failed one leaves behind (root plus a few loose textures). Shared by every caching path:
    /// having it in only one of them is how a stub reached the cache and drew a vehicle flat grey.
    /// </summary>
    internal static int RootPinsFed(Graph.ShaderGraph p_Graph) =>
        p_Graph.Root is { } s_Root ? p_Graph.Connections.Count(p_C => p_C.ToNode == s_Root.Id) : 0;

    /// <summary>
    /// Whether a freshly translated graph is big enough to BE the shader it claims — measured against the
    /// shader's own bytecode, which is the only honest yardstick.
    ///
    /// ⛔⛔ THE PIN TEST WAS NOT ENOUGH, and that is the whole reason this exists. A stub carries a root with
    /// Diffuse AND Normal already wired (measured on the BTR-90's preset: 8 nodes, 3 connections, TWO of them
    /// on the root), so "the root feeds at least two pins" passed it and the vehicle drew flat grey from the
    /// cache. What no stub can fake is SIZE: the translator yields around 1.5-1.8 nodes per instruction
    /// (66 → 119, 40 → 61), and the stub was 8 nodes for 53. Half a node per instruction is a floor no real
    /// translation comes near and no stub reaches.
    ///
    /// Returns true when there is nothing to compare against (no cached bytecode): this must never REJECT a
    /// good graph for lack of a yardstick.
    /// </summary>
    internal bool TranslationLooksComplete(Graph.ShaderGraph p_Graph, string p_Shader, out string p_Why) =>
        TranslationLooksComplete(p_Graph, p_Shader, out p_Why, out _);

    /// <param name="p_Measured">
    /// True when the bytecode was there to measure against. ⛔ It decides how many ROOT PINS the caller may
    /// demand: measured, ONE fed pin is enough — a forward shader (glass, lights, a cage decal) has a single
    /// output and its whole translation hangs off one pin (the HIMARS's glass: 23 instructions, 25 nodes,
    /// 1 pin, refused twice as "failed", 2026-09-22); unmeasured, the old "at least two" stays, because the
    /// BTR's stub came with two pins fed and only the size told it apart.
    /// </param>
    internal bool TranslationLooksComplete(Graph.ShaderGraph p_Graph, string p_Shader, out string p_Why, out bool p_Measured)
    {
        p_Why = "";
        p_Measured = false;

        // ⛔ THE STUB NAMES ITSELF. A failed translation leaves the SCAFFOLD on the canvas, and the scaffold
        // is stamped as such — no size or pin guess needed to refuse it (see ShaderGraph.IsScaffold).
        if (p_Graph.IsScaffold)
        {
            p_Why = "a scaffold, not a translation (the translation did not run)";
            return false;
        }

        var s_Dxbc = Path.Combine(MeshCacheDir, "shaders", $"{Sanitize(p_Shader)}.dxbc");
        if (!File.Exists(s_Dxbc))
            return true;

        int s_Instructions;
        try
        {
            var s_Text = new SharpDX.D3DCompiler.ShaderBytecode(File.ReadAllBytes(s_Dxbc)).Disassemble();
            s_Instructions = s_Text.Split('\n')
                .Select(p_L => p_L.Trim())
                .Count(p_L => p_L.Length > 0 && !p_L.StartsWith("//") && !p_L.StartsWith("dcl_") &&
                              !p_L.StartsWith("ps_") && !p_L.StartsWith("vs_") && p_L != "ret");
        }
        catch (Exception)
        {
            return true;
        }

        if (s_Instructions <= 0)
            return true;

        p_Measured = true;

        // ⛔ A QUARTER, NOT A HALF, AND WIRES TOO. Half a node per instruction refused a COMPLETE translation
        // — the F/A-18F's cockpit shader: 33 instructions, 15 nodes, frame recognised, six root pins fed
        // (2026-09-22) — while the stubs it was written against (8 nodes for 53 and 65 instructions) sit far
        // below a quarter. And a stub has at most three wires whatever the shader (the root's Diffuse/Normal
        // by convention), where a translation wires roughly one per instruction, so the wire count is the
        // second floor a stub cannot reach.
        var s_Floor = s_Instructions / 4;
        if (p_Graph.Nodes.Count >= s_Floor && p_Graph.Connections.Count >= s_Floor)
            return true;

        p_Why = $"{p_Graph.Nodes.Count} node(s), {p_Graph.Connections.Count} wire(s) for a shader of {s_Instructions} instruction(s)";
        return false;
    }

    internal static int PretranslateCamo(IReadOnlyList<string> p_Shaders, string p_Level)
    {
        var s_Window = new MainWindow { m_LoadedLevel = p_Level };
        var s_Missing = new List<string>();

        // ⭐ every shader still to translate read out of the game AHEAD, in one mount (MainWindow.Batch): each translation below replays
        // its own read instead of mounting the game for it (the batch closes when this returns)
        using var s_Batch = ReadTranslationsAhead(s_Window, p_Shaders);

        foreach (var s_Shader in p_Shaders)
        {
            var s_Path = s_Window.CamoGraphCachePath(s_Shader);
            // (a cache made from another permutation than the one chosen now is not "cached")
            s_Window.RetireStaleTranslation(s_Shader);
            if (File.Exists(s_Path))
            {
                Console.Out.WriteLine($"  cached: {s_Shader}");
                continue;
            }

            Console.Out.WriteLine($"  translating: {s_Shader} …");

            var s_Finished = new TaskCompletionSource<int>();
            SelectFinished = s_Finished;
            _ = s_Window.SelectTargetAsync(s_Shader);

            // The handler is async, so without a message loop its continuation would never run.
            var s_Frame = new System.Windows.Threading.DispatcherFrame();
            Task.WhenAny(s_Finished.Task, Task.Delay(TimeSpan.FromMinutes(10)))
                .ContinueWith(_ => s_Frame.Continue = false);

            System.Windows.Threading.Dispatcher.PushFrame(s_Frame);
            SelectFinished = null;

            // ⛔⛔ THE SAME GUARD AS THE WINDOW'S, AND IT WAS MISSING HERE — which is how a stub got cached
            // anyway (the BTR-90's preset: 8 nodes, 0 wires, against 119/152 for the real one; measured
            // 2026-09-22, and it drew the vehicle FLAT GREY from then on). "More than zero nodes" is not a
            // translation: a graph whose ROOT reaches nothing is a failure, usually because the game was
            // busy being mounted by something else.
            var s_Fed = RootPinsFed(s_Window.Canvas.Graph);
            var s_Complete = s_Window.TranslationLooksComplete(s_Window.Canvas.Graph, s_Shader, out var s_TooSmall, out var s_Measured);
            // One fed pin is enough once the size was measured (a forward shader has one output); two
            // without a yardstick — see TranslationLooksComplete.
            if (s_Window.Canvas.Graph.Nodes.Count > 0 && s_Fed >= (s_Measured ? 1 : 2) && s_Complete &&
                s_Window.Canvas.Graph.TargetShader.Equals(s_Shader, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_Path)!);
                File.WriteAllText(s_Path, s_Window.Canvas.Graph.ToJson());
                Console.Out.WriteLine($"    {s_Window.Canvas.Graph.Nodes.Count} node(s) cached.");
            }
            else
            {
                s_Missing.Add(s_Shader);
                Console.Out.WriteLine(s_Window.Canvas.Graph.Nodes.Count > 0
                    ? $"    FAILED - {(s_Complete ? $"{s_Window.Canvas.Graph.Nodes.Count} node(s) and {s_Fed} pin(s) on the root" : s_TooSmall)}: " +
                      "a failed translation, NOT cached (was something else mounting the game?)."
                    : "    FAILED - nothing landed on the canvas.");
            }
        }

        Console.Out.WriteLine(s_Missing.Count == 0
            ? "PRETRANSLATE: every preset is cached."
            : $"PRETRANSLATE: {s_Missing.Count} preset(s) failed.");

        return s_Missing.Count == 0 ? 0 : 1;
    }

    internal static int SelectSelfTest(string p_Target, string p_WorkDir, string? p_GamePath,
        string? p_Level = null)
    {
        var s_Window = new MainWindow();
        s_Window.m_Settings.OutputFolder = p_WorkDir;

        // Simulates "the browser has this level loaded", which routes a non-Levels target to that level's
        // shaderdb - the double click this test mirrors always happens with a level loaded.
        if (!string.IsNullOrWhiteSpace(p_Level))
            s_Window.m_LoadedLevel = p_Level;

        if (!string.IsNullOrWhiteSpace(p_GamePath))
            s_Window.m_Settings.GamePath = p_GamePath;

        var s_Finished = new TaskCompletionSource<int>();
        SelectFinished = s_Finished;

        // Fire and forget exactly as the double click does, then pump: the handler is async, so without a
        // message loop its continuation after the first await would never run.
        _ = s_Window.SelectTargetAsync(p_Target);

        var s_Frame = new System.Windows.Threading.DispatcherFrame();
        var s_Timeout = Task.Delay(TimeSpan.FromMinutes(10));
        Task.WhenAny(s_Finished.Task, s_Timeout).ContinueWith(_ => s_Frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(s_Frame);
        SelectFinished = null;

        Console.Out.WriteLine(s_Window.LogBox.Text.TrimEnd());

        if (!s_Finished.Task.IsCompleted)
        {
            Console.Error.WriteLine("SELECTTEST: FAIL - the selection never finished (10 min timeout).");
            return 1;
        }

        var s_Nodes = s_Finished.Task.Result;
        var s_Textures = s_Window.m_TexturePreviews.Count;
        Console.Out.WriteLine($"after ONE selection: {s_Nodes} node(s) on the canvas, " +
                              $"{s_Textures} texture(s) loaded, target '{s_Window.Canvas.Graph.TargetShader}'");

        if (s_Nodes == 0 || s_Textures == 0)
        {
            Console.Error.WriteLine(s_Nodes == 0
                ? "SELECTTEST: FAIL - selecting the shader left the canvas empty."
                : "SELECTTEST: FAIL - selecting the shader translated it but loaded no textures.");

            return 1;
        }

        // When the map carries several texture sets, drive the picker the way a user does and prove the
        // thumbnails actually swap - from cache, no remount.
        var s_Full = ReadSlotMapFull(s_Window.SlotMapPath(p_Target));
        if (s_Full?.Variations is { Count: > 1 } s_Variations)
        {
            Console.Out.WriteLine($"{s_Variations.Count} texture set(s): " +
                                  string.Join(", ", s_Variations.Values.Select(p_V => p_V.Name)));

            var s_PreviousSources = s_Window.m_TexturePreviews
                .ToDictionary(p_P => p_P.Key, p_P => (p_P.Value as BitmapImage)?.UriSource?.ToString() ?? "");

            // The set to switch to must carry DIFFERENT art than the one showing: on a shared shader the
            // keys are (mesh, variation), so "any other key" can land on a sibling base set with identical
            // textures and the swap check would fail against a working picker.
            var s_Showing = s_Variations.TryGetValue(s_Window.m_Variation, out var s_Selected)
                ? s_Selected.Slots
                : s_Full.Slots;

            var s_Other = s_Window.VariationBox.Items.Cast<VariationChoice>()
                .FirstOrDefault(p_C => p_C.Hash != s_Window.m_Variation &&
                                       s_Variations.TryGetValue(p_C.Hash, out var s_Candidate) &&
                                       s_Candidate.Slots.Any(p_S =>
                                           !s_Showing.TryGetValue(p_S.Key, out var s_Old) ||
                                           !string.Equals(s_Old, p_S.Value, StringComparison.OrdinalIgnoreCase)));

            if (s_Other == null)
            {
                Console.Error.WriteLine("SELECTTEST: FAIL - variations in the map but none carries different art.");
                return 1;
            }

            s_Window.VariationBox.SelectedItem = s_Other;

            var s_Swapped = s_Window.m_TexturePreviews.Count > 0 && s_Window.m_TexturePreviews.Any(p_P =>
                !s_PreviousSources.TryGetValue(p_P.Key, out var s_Old) ||
                s_Old != ((p_P.Value as BitmapImage)?.UriSource?.ToString() ?? ""));

            Console.Out.WriteLine($"picking '{s_Other.Name}' swapped the thumbnails from cache: " +
                                  $"{(s_Swapped ? "OK" : "FAIL")}");

            if (!s_Swapped)
            {
                Console.Error.WriteLine("SELECTTEST: FAIL - switching variation did not change any thumbnail.");
                return 1;
            }
        }

        Console.Out.WriteLine("SELECTTEST: PASS - one selection translated the shader AND filled its textures.");
        return 0;
    }

    /// <summary>
    /// ⛔ THE SURFACE SWEEP. An authoring style is not a palette filter: it is every surface a node name comes
    /// out of. Six separate rounds of "the mode is showing through" were each found by hand, one surface at a
    /// time — palette, search box, shortcuts, drawn caption, the settings list, the startup log — so this walks
    /// them ALL and fails on any that speaks the other vocabulary. It runs in BOTH styles, and the Frostbite
    /// pass is the negative control: there the native names MUST appear, which is what makes a pass mean
    /// something rather than the checks quietly matching nothing.
    /// </summary>
    internal static int UdkSurfaceSelfTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);
        var s_Failures = new List<string>();

        // Every node the two catalogs name differently: in UDK style our own label is the forbidden word, and
        // in Frostbite style it is the one that has to be there.
        var s_Renamed = Palette.All
            .Where(p_D => p_D.TitleFor(true) != p_D.Title)
            .ToDictionary(p_D => p_D.Kind, p_D => p_D.Title);

        // The alias table is applied by NAME, so an entry for a node that is not registered yet vanishes
        // without a word — which is how TextureSample lost its search names. Nothing may be missed.
        foreach (var s_Miss in Palette.UdkAliasMisses)
            s_Failures.Add($"the UDK alias table names '{s_Miss}', which the palette does not have");

        foreach (var s_Miss in Palette.UdkReachableMisses)
            s_Failures.Add($"the reachable-node list names '{s_Miss}', which the palette does not have");

        try
        {
            foreach (var s_Udk in new[] { true, false })
            {
                var s_Mode = s_Udk ? "udk" : "frostbite";
                EditorSettings.PathOverride = Path.Combine(p_WorkDir, $"settings_{s_Mode}.json");
                new EditorSettings { UdkNodeStyle = s_Udk }.Save();

                var s_Window = new MainWindow();

                // 1) The canvas draws captions from this flag, so it has to agree with the setting before any
                // of the checks below mean anything.
                if (GraphCanvas.UdkStyle != s_Udk)
                    s_Failures.Add($"[{s_Mode}] the canvas vocabulary flag did not follow the setting");

                // 2) The palette: nothing in it may be labelled with the other catalog's word, and no two
                // entries may read the same (a duplicate name is a node the user cannot tell apart).
                var s_Listed = Palette.VisibleFor(s_Udk).Select(p_D => p_D.TitleFor(s_Udk)).ToList();
                foreach (var s_Duplicate in s_Listed.GroupBy(p_T => p_T).Where(p_G => p_G.Count() > 1))
                    s_Failures.Add($"[{s_Mode}] the palette lists '{s_Duplicate.Key}' {s_Duplicate.Count()} times");

                if (s_Udk)
                    foreach (var s_Bad in s_Listed.Where(p_T => s_Renamed.ContainsValue(p_T)))
                        s_Failures.Add($"[{s_Mode}] the palette still lists '{s_Bad}'");

                // 2b) The foreign group is an OVERFLOW, not part of the vocabulary: in the UDK style it must
                // be the LAST heading in the tree and start closed, and in the other style it must not exist
                // at all (there, those nodes are simply the palette).
                var s_Headings = s_Window.PaletteTree.Items.OfType<TreeViewItem>().ToList();
                var s_Foreign = s_Headings.FindIndex(p_I => (p_I.Header as string) == Palette.c_ForeignCategory);

                if (s_Udk)
                {
                    if (s_Foreign < 0)
                        s_Failures.Add($"[{s_Mode}] the palette has no '{Palette.c_ForeignCategory}' heading");
                    else if (s_Foreign != s_Headings.Count - 1)
                        s_Failures.Add($"[{s_Mode}] '{Palette.c_ForeignCategory}' is heading " +
                                       $"{s_Foreign + 1} of {s_Headings.Count}, not the last one");
                    else if (s_Headings[s_Foreign].IsExpanded)
                        s_Failures.Add($"[{s_Mode}] '{Palette.c_ForeignCategory}' starts open");
                }
                else if (s_Foreign >= 0)
                {
                    s_Failures.Add($"[{s_Mode}] '{Palette.c_ForeignCategory}' should not exist in this style");
                }

                // 3) The shortcuts, on both of the surfaces that ADVERTISE them — the startup log and the
                // settings list — checked against what the canvas would actually place.
                var s_Log = s_Window.LogBox.Text;
                var s_DialogText = new List<string>();
                var s_Dialog = new SettingsWindow(s_Window, s_Window.m_Settings, () => { });
                CollectText(s_Dialog.Content, s_DialogText);

                foreach (var (s_Key, s_Kind) in GraphCanvas.Shortcuts.Where(p_S => p_S.Key is >= Key.A and <= Key.Z))
                {
                    var s_Placed = Palette.PlacedTitle(s_Kind, s_Udk);

                    if (!s_Log.Contains($"{s_Key}={s_Placed}"))
                        s_Failures.Add($"[{s_Mode}] the startup log does not announce {s_Key} as '{s_Placed}'");

                    if (!s_DialogText.Contains(s_Placed))
                        s_Failures.Add($"[{s_Mode}] the settings shortcut list has no row reading '{s_Placed}'");

                    // The row must not carry the OTHER vocabulary's word for the very node it binds.
                    if (s_Udk && s_Renamed.TryGetValue(s_Kind, out var s_Native) && s_Placed != s_Native &&
                        s_DialogText.Contains(s_Native))
                        s_Failures.Add($"[{s_Mode}] the settings shortcut list still reads '{s_Native}'");
                }

                s_Dialog.Close();

                // 4) The properties header, for every node the palette offers: place it (which selects it and
                // runs the real BuildProperties) and read the heading the user would see.
                foreach (var s_Def in Palette.VisibleFor(s_Udk))
                {
                    s_Window.Canvas.AddNode(s_Def.Kind);

                    var s_Expected = $"Properties — {s_Def.TitleFor(s_Udk)}";
                    if (s_Window.PropertiesHeader.Text != s_Expected)
                        s_Failures.Add($"[{s_Mode}] properties for '{s_Def.Kind}' headed " +
                                       $"'{s_Window.PropertiesHeader.Text}', expected '{s_Expected}'");
                }

                Console.Out.WriteLine($"  {s_Mode,-9} palette {s_Listed.Count,3} node(s), " +
                                      $"{GraphCanvas.Shortcuts.Count(p_S => p_S.Key is >= Key.A and <= Key.Z)} shortcut(s), " +
                                      $"properties heading checked on every one");
            }
        }
        finally
        {
            EditorSettings.PathOverride = null;
            GraphCanvas.UdkStyle = false;
        }

        foreach (var s_Failure in s_Failures)
            Console.Error.WriteLine("  " + s_Failure);

        if (s_Failures.Count > 0)
        {
            Console.Error.WriteLine($"UDKSURFACETEST: FAIL - {s_Failures.Count} surface(s) speak the wrong vocabulary.");
            return 1;
        }

        Console.Out.WriteLine("UDKSURFACETEST: PASS - every surface that names a node speaks the style in use.");
        return 0;
    }

    /// <summary>Every piece of text a dialog would show, walked through its logical tree.</summary>
    private static void CollectText(object? p_Node, List<string> p_Into)
    {
        switch (p_Node)
        {
            case TextBlock s_Text:
                p_Into.Add(s_Text.Text ?? "");
                break;

            case ContentControl { Content: string s_Content }:
                p_Into.Add(s_Content);
                break;
        }

        if (p_Node is DependencyObject s_Object)
            foreach (var s_Child in LogicalTreeHelper.GetChildren(s_Object))
                CollectText(s_Child, p_Into);
    }

    /// <summary>
    /// Drives the Settings machinery the way the dialog does: file round-trip (against its own scratch file,
    /// never the user's), corrupt-value clamping, shortcut remap reaching the live canvas table, and the FLIR
    /// toggle reaching the preview constants through the real PushInstanceValues path.
    /// </summary>
    internal static int SettingsSelfTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);
        EditorSettings.PathOverride = Path.Combine(p_WorkDir, "settings_test.json");

        try
        {
            // 1) Round-trip: every new field survives save/load.
            var s_Saved = new EditorSettings
            {
                CanvasZoomSpeed = 2.5,
                PreviewZoomSpeed = 0.5,
                PreviewOrbitSpeed = 1.75,
                FlirPreview = true,
                ExportTexturesAsPng = false,
                NodeShortcuts = { ["Multiply"] = "Q" },
            };
            s_Saved.Save();

            var s_Loaded = EditorSettings.Load();
            if (Math.Abs(s_Loaded.CanvasZoomSpeed - 2.5) > 0.001 ||
                Math.Abs(s_Loaded.PreviewZoomSpeed - 0.5) > 0.001 ||
                Math.Abs(s_Loaded.PreviewOrbitSpeed - 1.75) > 0.001 ||
                !s_Loaded.FlirPreview ||
                s_Loaded.ExportTexturesAsPng ||
                !s_Loaded.NodeShortcuts.TryGetValue("Multiply", out var s_Q) || s_Q != "Q")
            {
                Console.Error.WriteLine("SETTINGSTEST: FAIL - the file round-trip lost a value.");
                return 1;
            }

            // 2) A corrupt value must clamp, not freeze the canvas.
            File.WriteAllText(EditorSettings.PathOverride,
                "{\"CanvasZoomSpeed\": 99, \"PreviewZoomSpeed\": 0}");
            var s_Clamped = EditorSettings.Load();
            if (Math.Abs(s_Clamped.CanvasZoomSpeed - 4.0) > 0.001 ||
                Math.Abs(s_Clamped.PreviewZoomSpeed - 0.25) > 0.001)
            {
                Console.Error.WriteLine("SETTINGSTEST: FAIL - out-of-range values did not clamp.");
                return 1;
            }

            // 3) A remap reaches the LIVE placement table: Q places Multiply, M no longer does.
            GraphCanvas.ApplyShortcuts(new Dictionary<string, string> { ["Multiply"] = "Q" });
            var s_Table = GraphCanvas.Shortcuts.ToList();
            var s_Remapped = s_Table.Any(p_S => p_S.Key == Key.Q && p_S.Kind == "Multiply") &&
                             s_Table.All(p_S => p_S.Key != Key.M || p_S.Kind != "Multiply");
            GraphCanvas.ApplyShortcuts(null);
            if (!s_Remapped)
            {
                Console.Error.WriteLine("SETTINGSTEST: FAIL - the shortcut remap did not reach the canvas table.");
                return 1;
            }

            // 4) The FLIR toggle, through the real preview-constant path. A fabricated contract carries the
            // two thermal fields; toggling the setting must change what PushInstanceValues feeds.
            var s_Window = new MainWindow();
            s_Window.m_Contract = new ShaderContract
            {
                ConstantFields = new List<Emit.DxbcSignature.ConstantField>
                {
                    new() { Name = "external_FLIRData", Register = 1, Offset = 0 },
                    new() { Name = "external_FLIRScale", Register = 1, Offset = 16 },
                },
            };

            s_Window.m_Settings.FlirPreview = false;
            s_Window.PushInstanceValues(null);
            var s_ZeroLogged = s_Window.LogBox.Text.Contains("Thermal parameters (FLIR) read zero");

            var s_Before = s_Window.LogBox.Text.Length;
            s_Window.m_Settings.FlirPreview = true;
            s_Window.PushInstanceValues(null);
            var s_PatternSilent = !s_Window.LogBox.Text[s_Before..].Contains("Thermal parameters");

            if (!s_ZeroLogged || !s_PatternSilent)
            {
                Console.Error.WriteLine("SETTINGSTEST: FAIL - the FLIR toggle did not change the preview feed " +
                                        $"(zeroLogged={s_ZeroLogged}, patternSilent={s_PatternSilent}).");
                return 1;
            }

            Console.Out.WriteLine("SETTINGSTEST: PASS - round-trip, clamping, live remap and the FLIR toggle " +
                                  "all reach their targets.");
            return 0;
        }
        finally
        {
            EditorSettings.PathOverride = null;
        }
    }

    /// <summary>
    /// Drives the tab machinery end to end: a second document opens in its own tab, switching restores the
    /// other document's graph/target/undo history untouched, re-selecting an open target focuses instead of
    /// re-reading, closing falls back to the neighbour — and the strip itself is photographed and probed,
    /// because a tab bar is judged by what it draws.
    /// </summary>
    /// <summary>
    /// Drives the custom-texture machinery: a chosen file overrides the thumbnail/preview for its register,
    /// and PrepareCustomTextures converts through the REAL texconv into the in-game-proven DDS lanes (DXT1
    /// opaque, DXT5 alpha, DXT1+flag normal maps, never DX10) with the right game-name mapping — including
    /// the refusals (unnamed register, missing file), which are what keep a half-shipped bake from existing.
    /// </summary>
    internal static int CustomTextureSelfTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);

        // Two source images rendered here so the test owns its inputs: one opaque, one with real alpha.
        var s_OpaquePng = Path.Combine(p_WorkDir, "custom_opaque.png");
        var s_AlphaPng = Path.Combine(p_WorkDir, "custom_alpha.png");
        WriteTestPng(s_OpaquePng, 64, 64, p_WithAlpha: false);
        WriteTestPng(s_AlphaPng, 64, 64, p_WithAlpha: true);

        // 1) The preview override: the register's thumbnail becomes OUR file. The startup canvas is empty
        // by design, so the test seeds the node it needs (Register living in Params, the touched shape).
        var s_Window = new MainWindow();
        var s_TextureNode = new GraphNode { Kind = "Texture", X = 100, Y = 100 };
        s_TextureNode.Params["Register"] = "1";
        s_Window.Canvas.Graph.Nodes.Add(s_TextureNode);

        var s_Register = s_TextureNode.Params["Register"];
        s_TextureNode.Params["CustomTexture"] = s_OpaquePng;
        s_Window.ApplyCustomTextureOverrides();

        var s_Overridden = s_Window.m_TexturePreviews.TryGetValue(s_Register, out var s_Thumb) &&
                           (s_Thumb as BitmapImage)?.UriSource?.LocalPath.EndsWith("custom_opaque.png",
                               StringComparison.OrdinalIgnoreCase) == true;

        if (!s_Overridden)
        {
            Console.Error.WriteLine($"CUSTOMTEXTEST: FAIL - t{s_Register} did not take the custom thumbnail.");
            return 1;
        }

        // A FRESHLY PLACED node: empty Params dict, register living only in the palette default. This is the
        // exact shape that silently skipped the override (a node placed, a file chosen, nothing happened).
        var s_FreshNode = new GraphNode { Kind = "Texture", X = 500, Y = 500 };
        s_FreshNode.Params["CustomTexture"] = s_AlphaPng;
        s_Window.Canvas.Graph.Nodes.Add(s_FreshNode);
        s_Window.ApplyCustomTextureOverrides();

        var s_FreshRegister = s_FreshNode.GetParam("Register");
        var s_FreshTaken = !string.IsNullOrWhiteSpace(s_FreshRegister) &&
                           s_Window.m_TexturePreviews.TryGetValue(s_FreshRegister, out var s_FreshThumb) &&
                           (s_FreshThumb as BitmapImage)?.UriSource?.LocalPath.EndsWith("custom_alpha.png",
                               StringComparison.OrdinalIgnoreCase) == true;

        if (!s_FreshTaken)
        {
            Console.Error.WriteLine("CUSTOMTEXTEST: FAIL - a freshly placed node (default register " +
                                    $"'{s_FreshRegister}') did not take its custom texture.");
            return 1;
        }

        // Independence: nodes placed through the real placement path get their own FREE register each —
        // sharing register 1 with the existing node was how a new node's texture replaced the original's.
        s_Window.Canvas.AddNodeAt("Texture", new Point(600, 600));
        var s_PlacedA = s_Window.Canvas.Graph.Nodes[^1].GetParam("Register");
        s_Window.Canvas.AddNodeAt("Texture", new Point(700, 600));
        var s_PlacedB = s_Window.Canvas.Graph.Nodes[^1].GetParam("Register");

        var s_UsedBefore = s_Window.Canvas.Graph.Nodes[..^2]
            .Where(p_N => TextureNodeKinds.Contains(p_N.Kind))
            .Select(p_N => p_N.GetParam("Register"))
            .ToHashSet();

        if (s_PlacedA == s_PlacedB || s_UsedBefore.Contains(s_PlacedA) || s_UsedBefore.Contains(s_PlacedB))
        {
            Console.Error.WriteLine($"CUSTOMTEXTEST: FAIL - placed nodes were not independent " +
                                    $"(registers '{s_PlacedA}' and '{s_PlacedB}', used: {string.Join(",", s_UsedBefore)}).");
            return 1;
        }

        // 2) The conversion, through the real texconv. One graph with the three lanes.
        var s_Graph = new ShaderGraph();
        s_Graph.Nodes.Add(new GraphNode
        {
            Kind = "Texture",
            Params = { ["Register"] = "1", ["CustomTexture"] = s_OpaquePng },
        });
        s_Graph.Nodes.Add(new GraphNode
        {
            Kind = "Texture",
            Params = { ["Register"] = "2", ["CustomTexture"] = s_AlphaPng },
        });
        s_Graph.Nodes.Add(new GraphNode
        {
            Kind = "NormalMap",
            Params = { ["Register"] = "3", ["CustomTexture"] = s_OpaquePng },
        });

        var s_Slots = new Dictionary<string, string>
        {
            ["1"] = "test/diffuse_name",
            ["2"] = "test/decal_name",
            ["3"] = "test/normal_name",
        };

        var (s_Textures, s_Error) = PrepareCustomTextures(s_Graph, s_Slots,
            Path.Combine(p_WorkDir, "conv"), Console.Out.WriteLine);

        if (s_Error != null)
        {
            Console.Error.WriteLine($"CUSTOMTEXTEST: FAIL - conversion refused: {s_Error}");
            return 1;
        }

        static string FourCcOf(string p_Dds)
        {
            var s_Header = new byte[96];
            using var s_Stream = File.OpenRead(p_Dds);
            _ = s_Stream.Read(s_Header, 0, s_Header.Length);
            return System.Text.Encoding.ASCII.GetString(s_Header, 84, 4);
        }

        var s_ByName = s_Textures.ToDictionary(p_T => p_T.Name);
        var s_Lanes =
            s_Textures.Count == 3 &&
            s_ByName["test/diffuse_name"] is { Srgb: true, Normal: false } s_Diffuse &&
            FourCcOf(s_Diffuse.DdsPath) == "DXT1" &&
            s_ByName["test/decal_name"] is { Srgb: true, Normal: false } s_Decal &&
            FourCcOf(s_Decal.DdsPath) == "DXT5" &&
            s_ByName["test/normal_name"] is { Srgb: false, Normal: true } s_Normal &&
            FourCcOf(s_Normal.DdsPath) == "DXT1";

        Console.Out.WriteLine($"lanes: {string.Join("; ", s_Textures.Select(p_T => $"{p_T.Name} -> " +
            $"{FourCcOf(p_T.DdsPath)} srgb={p_T.Srgb} normal={p_T.Normal}"))}");

        if (!s_Lanes)
        {
            Console.Error.WriteLine("CUSTOMTEXTEST: FAIL - a converted texture landed in the wrong lane.");
            return 1;
        }

        // 3a) A register the material does NOT bind is the NEW-SLOT lane: a fresh name under custom/, the
        // slot surgery marked with the graph's target, and the pool group borrowed from a bound sibling.
        var (s_SlotTextures, s_SlotError) = PrepareCustomTextures(s_Graph,
            new Dictionary<string, string> { ["1"] = "test/diffuse_name" },
            Path.Combine(p_WorkDir, "conv2"), Console.Out.WriteLine);

        var s_NewSlot = s_SlotTextures.FirstOrDefault(p_T => p_T.SlotRegister == "2");
        var s_SlotLane = s_SlotError == null && s_NewSlot != null &&
                         s_NewSlot.SlotTarget == s_Graph.TargetShader &&
                         s_NewSlot.Name.StartsWith("custom/", StringComparison.Ordinal) &&
                         s_NewSlot.GroupDonor == "test/diffuse_name" &&
                         s_SlotTextures.Any(p_T => p_T.SlotTarget == null && p_T.Name == "test/diffuse_name");

        if (!s_SlotLane)
        {
            Console.Error.WriteLine($"CUSTOMTEXTEST: FAIL - the new-slot lane misfired (error={s_SlotError}, " +
                                    $"slot={s_NewSlot?.Name}/{s_NewSlot?.SlotTarget}/{s_NewSlot?.GroupDonor}).");
            return 1;
        }

        // 3b) A vanished file must still ABORT — never half-ship.
        s_Graph.Nodes[0].Params["CustomTexture"] = Path.Combine(p_WorkDir, "gone.png");
        var (_, s_NoFile) = PrepareCustomTextures(s_Graph, s_Slots,
            Path.Combine(p_WorkDir, "conv3"), Console.Out.WriteLine);

        if (s_NoFile == null)
        {
            Console.Error.WriteLine("CUSTOMTEXTEST: FAIL - a vanished file was accepted instead of refused.");
            return 1;
        }

        Console.Out.WriteLine("CUSTOMTEXTEST: PASS - preview override, all three DDS lanes, the new-slot lane " +
                              "and the missing-file refusal.");
        return 0;
    }

    private static void WriteTestPng(string p_Path, int p_Width, int p_Height, bool p_WithAlpha)
    {
        var s_Pixels = new byte[p_Width * p_Height * 4];
        for (var s_Y = 0; s_Y < p_Height; s_Y++)
            for (var s_X = 0; s_X < p_Width; s_X++)
            {
                var i = (s_Y * p_Width + s_X) * 4;
                s_Pixels[i + 0] = (byte) (s_X * 255 / p_Width);
                s_Pixels[i + 1] = (byte) (s_Y * 255 / p_Height);
                s_Pixels[i + 2] = 200;
                s_Pixels[i + 3] = p_WithAlpha ? (byte) (s_X * 255 / p_Width) : (byte) 255;
            }

        var s_Bitmap = BitmapSource.Create(p_Width, p_Height, 96, 96, PixelFormats.Bgra32, null,
            s_Pixels, p_Width * 4);
        var s_Encoder = new PngBitmapEncoder();
        s_Encoder.Frames.Add(BitmapFrame.Create(s_Bitmap));
        using var s_Stream = File.Create(p_Path);
        s_Encoder.Save(s_Stream);
    }

    internal static int TabSelfTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);
        var s_Window = new MainWindow();

        if (s_Window.m_Tabs.Count != 1)
        {
            Console.Error.WriteLine($"TABTEST: FAIL - expected 1 startup tab, found {s_Window.m_Tabs.Count}.");
            return 1;
        }

        var s_SampleNodes = s_Window.Canvas.Graph.Nodes.Count;
        var s_SampleTarget = s_Window.Canvas.Graph.TargetShader;

        // A second document in its own tab, then an EDIT in it (the edit is what must not leak or vanish).
        var s_Second = new ShaderGraph { Name = "second", TargetShader = "shaders/Test/Second" };
        s_Second.Nodes.Add(new GraphNode { Kind = "StandardRoot", X = 0, Y = 0 });
        s_Window.OpenInNewTab(s_Second, null);

        s_Window.Canvas.PushUndo();
        s_Window.Canvas.Graph.Nodes.Add(new GraphNode { Kind = "Scalar", X = 10, Y = 10 });

        if (s_Window.m_Tabs.Count != 2 || s_Window.m_ActiveTab != 1 ||
            s_Window.TargetShaderBox.Text != "shaders/Test/Second" || !s_Window.Canvas.CanUndo)
        {
            Console.Error.WriteLine("TABTEST: FAIL - opening a second tab did not take the window over.");
            return 1;
        }

        // Switch away: the first document comes back exactly as it was, including an EMPTY undo history.
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);
        if (s_Window.Canvas.Graph.Nodes.Count != s_SampleNodes || s_Window.Canvas.CanUndo ||
            s_Window.TargetShaderBox.Text != s_SampleTarget)
        {
            Console.Error.WriteLine("TABTEST: FAIL - switching back did not restore the first document.");
            return 1;
        }

        // Switch forward: the edit AND its undo history are still there.
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(1);
        if (s_Window.Canvas.Graph.Nodes.Count != 2 || !s_Window.Canvas.CanUndo)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the second tab lost its edit or its undo history.");
            return 1;
        }

        // Re-selecting an OPEN target focuses its tab — no new tab, no re-read.
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);
        var s_Finished = new TaskCompletionSource<int>();
        SelectFinished = s_Finished;
        _ = s_Window.SelectTargetAsync("shaders/Test/Second");
        SelectFinished = null;

        if (!s_Finished.Task.IsCompleted || s_Window.m_Tabs.Count != 2 || s_Window.m_ActiveTab != 1)
        {
            Console.Error.WriteLine("TABTEST: FAIL - selecting an already-open target did not focus its tab.");
            return 1;
        }

        // ── Per-tab preview: each document keeps the shape it was last showing ───────────────────────
        // Tab 1 is active here. Give the two tabs DIFFERENT shapes and check each comes back with its own.
        s_Window.m_Preview.Shape = View.PreviewShape.Cylinder;
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);
        s_Window.m_Preview.Shape = View.PreviewShape.Sphere;
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(1);

        if (s_Window.m_Preview.Shape != View.PreviewShape.Cylinder || s_Window.ShapeCylinder.IsChecked != true)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the tab did not come back with its own preview shape.");
            return 1;
        }

        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);
        if (s_Window.m_Preview.Shape != View.PreviewShape.Sphere || s_Window.ShapeSphere.IsChecked != true)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the other tab's shape leaked across the switch.");
            return 1;
        }

        // A mesh whose dump is NOT cached must not leave the mesh icon lit over an object that never
        // loaded: the shape falls back, while the NAME is kept so the list still shows the object.
        s_Window.m_PreviewMesh = "props/test/never_dumped_mesh";
        s_Window.m_Preview.Shape = View.PreviewShape.Mesh;
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(1);
        s_Window.SaveActiveTab();
        s_Window.ActivateTab(0);

        if (s_Window.m_Preview.Shape == View.PreviewShape.Mesh || s_Window.ShapeMesh.IsChecked == true ||
            s_Window.m_PreviewMesh != "props/test/never_dumped_mesh")
        {
            Console.Error.WriteLine("TABTEST: FAIL - an uncached mesh was restored as if it had loaded.");
            return 1;
        }

        // The photograph, before closing anything: two tabs, the active one wearing the accent border.
        var s_Strip = s_Window.TabStrip;
        s_Strip.Measure(new Size(1200, 40));
        s_Strip.Arrange(new Rect(0, 0, 1200, s_Strip.DesiredSize.Height));
        s_Strip.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            1200, Math.Max(1, (int) Math.Ceiling(s_Strip.DesiredSize.Height)), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        s_Bitmap.Render(s_Strip);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        var s_ShotPath = Path.Combine(p_WorkDir, "tabstrip.png");
        using (var s_Stream = File.Create(s_ShotPath))
            s_Encoder.Save(s_Stream);

        // The probe wants the EXPECTED colour, not "something differs": the active tab's accent border
        // (150,210,150) must exist somewhere in the strip's pixels.
        var s_Pixels = new byte[s_Bitmap.PixelWidth * s_Bitmap.PixelHeight * 4];
        s_Bitmap.CopyPixels(s_Pixels, s_Bitmap.PixelWidth * 4, 0);
        var s_AccentSeen = false;
        for (var i = 0; i < s_Pixels.Length; i += 4)
            if (s_Pixels[i + 2] == 150 && s_Pixels[i + 1] == 210 && s_Pixels[i] == 150)
            {
                s_AccentSeen = true;
                break;
            }

        Console.Out.WriteLine($"tab strip rendered -> {s_ShotPath}; active-tab accent present: " +
                              $"{(s_AccentSeen ? "OK" : "FAIL")}");
        if (!s_AccentSeen)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the strip drew, but no pixel wears the active-tab accent.");
            return 1;
        }

        // Closing the active tab (edits present, headless skips the confirm box) falls back to the sample.
        s_Window.CloseTab(1);
        if (s_Window.m_Tabs.Count != 1 || s_Window.Canvas.Graph.Nodes.Count != s_SampleNodes)
        {
            Console.Error.WriteLine("TABTEST: FAIL - closing the second tab did not fall back to the first.");
            return 1;
        }

        // ⛔ THE OBJECT A GRAPH IS AIMED AT HAS TO COME BACK WITH IT. Saved on the document but not restored,
        // reopening lands on whatever set the map lists first — another object's textures in the preview and
        // the aim silently asking to be redone every time, which is the manual step the stamp exists to
        // remove. The fixture names the aimed set LAST alphabetically on purpose: the old behaviour (first
        // listed) picks the other one, so this cannot pass by accident.
        s_Window.Canvas.Graph.BakeMesh = "props/test/aimed_mesh";
        s_Window.FillVariationBox(new SlotMap
        {
            V = 5,
            Variations = new Dictionary<string, VariationSlots>
            {
                ["aa|0"] = new() { Name = "aa_other", Mesh = "props/test/other_mesh" },
                ["zz|0"] = new() { Name = "zz_aimed", Mesh = "props/test/aimed_mesh" },
            },
        });

        // ⛔ THE AIM MUST NOT OUTLIVE THE TARGET. The stamped object is saved with the graph, but the target
        // can be retyped in the box — and a take-over aimed at a mesh that does not wear the new shader is a
        // clone whose solutions were never compiled for that mesh's declaration: invisible, silently. Kept
        // when the new target's map lists it, dropped when it does not, and LEFT ALONE when the map is
        // unknown, because a cold cache is not evidence of absence.
        var s_Aimed = new ShaderGraph { BakeMesh = "props/test/aimed_mesh" };
        var s_Wearing = new SlotMap
        {
            V = 6,
            Variations = new Dictionary<string, VariationSlots>
            {
                ["a|0"] = new() { Name = "aimed", Mesh = "props/test/aimed_mesh" },
            },
        };

        var s_NotWearing = new SlotMap
        {
            V = 6,
            Variations = new Dictionary<string, VariationSlots>
            {
                ["b|0"] = new() { Name = "other", Mesh = "props/test/other_mesh" },
            },
        };

        if (ClearStaleAim(s_Aimed, s_Wearing) != null || s_Aimed.BakeMesh == null)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the aim was dropped for a shader the object DOES wear.");
            return 1;
        }

        if (ClearStaleAim(s_Aimed, null) != null || s_Aimed.BakeMesh == null)
        {
            Console.Error.WriteLine("TABTEST: FAIL - the aim was dropped on an unknown map; a cold cache is " +
                                    "not evidence that the object does not wear the shader.");
            return 1;
        }

        if (ClearStaleAim(s_Aimed, s_NotWearing) != "props/test/aimed_mesh" || s_Aimed.BakeMesh != null)
        {
            Console.Error.WriteLine("TABTEST: FAIL - a graph retargeted at a shader its object does not wear " +
                                    "kept the aim; the bake would clone an entry that cannot draw that mesh.");
            return 1;
        }

        // ⛔ THE MESH SECTION BELONGS TO MESH MODE, AND COMING BACK TO IT MUST NOT COST A RELOAD. Both are
        // one state: the panel shows while the mesh icon is the chosen one, and switching to a primitive and
        // back is a shape change — the geometry never left the preview. Asserted through the single place
        // that sets the toolbar, because it used to be set from three.
        s_Window.m_MeshMode = true;
        s_Window.SyncShapeIcons(View.PreviewShape.Mesh);

        if (s_Window.MeshPanel.Visibility != Visibility.Visible || s_Window.ShapeMesh.IsChecked != true)
        {
            Console.Error.WriteLine("TABTEST: FAIL - mesh mode does not show the mesh section.");
            return 1;
        }

        s_Window.m_MeshMode = false;
        s_Window.SyncShapeIcons(View.PreviewShape.Cube);

        if (s_Window.MeshPanel.Visibility != Visibility.Collapsed || s_Window.ShapeCube.IsChecked != true ||
            s_Window.ShapeMesh.IsChecked != false)
        {
            Console.Error.WriteLine("TABTEST: FAIL - leaving mesh mode leaves the mesh section on screen, or " +
                                    "the toolbar disagreeing with the shape being drawn.");
            return 1;
        }

        if (s_Window.m_Variation != "zz|0")
        {
            Console.Error.WriteLine("TABTEST: FAIL - a graph aimed at an object reopened on set " +
                                    $"'{s_Window.m_Variation}' instead of the one carrying that mesh; the " +
                                    "user would have to aim it again on every open.");
            return 1;
        }

        Console.Out.WriteLine("TABTEST: PASS - open/switch/focus/close all keep each document intact, and the " +
                              "strip draws its state.");
        return 0;
    }

    internal static int TranslateSelfTest(string p_Target, string p_WorkDir, string? p_GamePath,
        string? p_Level = null)
    {
        var s_Window = new MainWindow();
        s_Window.m_Settings.OutputFolder = p_WorkDir;
        s_Window.TargetShaderBox.Text = p_Target;

        // Simulates "the browser has this level loaded", which is what routes a non-Levels target to the right
        // shaderdb. Without it the test runs the old typed-by-hand path (mp_017 fallback).
        if (!string.IsNullOrWhiteSpace(p_Level))
            s_Window.m_LoadedLevel = p_Level;

        if (!string.IsNullOrWhiteSpace(p_GamePath))
            s_Window.m_Settings.GamePath = p_GamePath;

        var s_Button = Descendants<Button>((DependencyObject) s_Window.Content)
            .FirstOrDefault(p_B => p_B.Tag as string == "translate");

        if (s_Button == null)
        {
            Console.Error.WriteLine("TRANSLATETEST: no button tagged 'translate' in the window.");
            return 1;
        }

        var s_Finished = new TaskCompletionSource<int>();
        TranslateFinished = s_Finished;

        s_Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        // The handler is async, so the click returns at its first await. Pump the dispatcher until it completes,
        // which is also what keeps the continuation running - without a message loop it would never resume.
        var s_Frame = new System.Windows.Threading.DispatcherFrame();
        var s_Timeout = Task.Delay(TimeSpan.FromMinutes(10));
        Task.WhenAny(s_Finished.Task, s_Timeout).ContinueWith(_ => s_Frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(s_Frame);
        TranslateFinished = null;

        Console.Out.WriteLine(s_Window.LogBox.Text.TrimEnd());

        if (!s_Finished.Task.IsCompleted)
        {
            Console.Error.WriteLine("TRANSLATETEST: FAIL - the click never finished (10 min timeout).");
            return 1;
        }

        var s_Nodes = s_Finished.Task.Result;
        var s_Graph = s_Window.Canvas.Graph;
        Console.Out.WriteLine($"canvas now holds {s_Nodes} node(s), {s_Graph.Connections.Count} connection(s), " +
                              $"target '{s_Graph.TargetShader}'");

        // Written out so --shaderdiff can be pointed at the graph THIS CLICK produced, against the bytecode THIS
        // CLICK extracted. Reading the node count only proves something was built, not that it is the same shader.
        try
        {
            var s_Saved = Path.Combine(p_WorkDir, "translated.json");
            File.WriteAllText(s_Saved, s_Graph.ToJson());
            Console.Out.WriteLine($"graph written to {s_Saved}");
        }
        catch (Exception s_Exception)
        {
            Console.Error.WriteLine($"could not save the graph: {s_Exception.Message}");
        }

        // A canvas with a couple of nodes is what a FAILED translation leaves behind (the scaffold, or an empty
        // graph), so the bar is a real graph with wiring - not merely "the handler did not throw".
        var s_Ok = s_Nodes > 8 && s_Graph.Connections.Count > 8 &&
                   s_Graph.TargetShader.Equals(p_Target, StringComparison.OrdinalIgnoreCase);

        Console.Out.WriteLine(s_Ok
            ? "TRANSLATETEST: PASS - the button pulled the shader out of the game and put a graph on the canvas."
            : "TRANSLATETEST: FAIL - the click left no usable graph on the canvas.");

        return s_Ok ? 0 : 1;
    }

    /// <summary>
    /// The shaderdb a target is read from. A `Levels/&lt;name&gt;/...` target names its own level; every other
    /// shader (Objects/..., XP2/..., Vehicles/...) is compiled into every level that uses it, so the level whose
    /// index is loaded in the browser decides - that is where the user found the name. Only with no browser
    /// context at all does the old mp_017 fallback remain, for targets typed by hand.
    /// </summary>
    internal string ResolveShaderDb(string p_Target)
    {
        var s_ByName = ShaderDbNamedBy(p_Target);
        if (s_ByName != null)
            return s_ByName;

        if (m_LoadedLevel != null)
            return LevelScanner.ShaderDbFor(m_LoadedLevel);

        return "levels/mp_017/mp_017/shaderdb";
    }

    private static string? ShaderDbNamedBy(string p_Target)
    {
        var s_Parts = p_Target.Split('/');
        if (s_Parts.Length > 2 && s_Parts[0].Equals("Levels", StringComparison.OrdinalIgnoreCase))
            return $"levels/{s_Parts[1].ToLowerInvariant()}/{s_Parts[1].ToLowerInvariant()}/shaderdb";

        return null;
    }

    /// <summary>The CLI has no browser, so name-or-mp_017 is all it can do; the window itself uses ResolveShaderDb.</summary>
    internal static string ShaderDbForTarget(string p_Target) =>
        ShaderDbNamedBy(p_Target) ?? "levels/mp_017/mp_017/shaderdb";

    private string SafeName() => Sanitize(Canvas.Graph.Name) is { Length: > 0 } s_Name ? s_Name : "graph";

    private static string Sanitize(string p_Text)
    {
        var s_Builder = new StringBuilder();
        foreach (var s_Char in p_Text)
            s_Builder.Append(char.IsLetterOrDigit(s_Char) ? s_Char : '_');

        return s_Builder.ToString().Trim('_');
    }

    private sealed class ProcessResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
    }

    private static ProcessResult Run(string p_Executable, IEnumerable<string> p_Arguments)
    {
        var s_Info = new ProcessStartInfo(p_Executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(p_Executable) ?? Environment.CurrentDirectory,
        };

        foreach (var s_Argument in p_Arguments)
            s_Info.ArgumentList.Add(s_Argument);

        using var s_Process = Process.Start(s_Info);
        if (s_Process == null)
            return new ProcessResult { ExitCode = -1, Output = "could not start process" };

        var s_StdOut = s_Process.StandardOutput.ReadToEnd();
        var s_StdErr = s_Process.StandardError.ReadToEnd();
        s_Process.WaitForExit();

        return new ProcessResult { ExitCode = s_Process.ExitCode, Output = s_StdOut + s_StdErr };
    }

    /// <summary>Walks up from the editor's own folder looking for the release RimeREPL next to the repo root.</summary>
    /// <summary>
    /// One custom texture ready for the bundle. Two delivery lanes: with <see cref="SlotTarget"/> null the
    /// texture OVERRIDES the vanilla game texture of the same Name; with it set, the texture is BRAND-NEW —
    /// the bake adds a texture slot (register &lt;- Name) to the target shader's constants so the engine binds
    /// it natively, and <see cref="GroupDonor"/> lends the pool TextureGroup a fresh name cannot look up.
    /// </summary>
    internal sealed record CustomBakeTexture(string Name, string DdsPath, bool Srgb, bool Normal,
        string? SlotTarget = null, string SlotRegister = "", string? GroupDonor = null);

    /// <summary>
    /// Prepares SAVED graphs for a multi-shader bake: each one is compiled against the contract detected from
    /// ITS OWN target's bytecode — emitting a saved CharacterRoot graph under the rigid default would produce
    /// a silently wrong shader, so the targets' reference permutations are extracted first (one mount for all
    /// of them) and each graph's HLSL is emitted with what its own bytes say.
    /// </summary>
    internal static (List<Emit.ShaderBaker.BakeShader> Shaders,
        List<CustomBakeTexture> Textures, string? Error) PrepareAdditionalGraphs(
            IReadOnlyList<string> p_GraphPaths, string p_Repl, string p_Fxc, string p_GamePath,
            string p_FirstLevel, string p_TexCacheDir, string p_WorkDir, Action<string> p_Log)
    {
        var s_Shaders = new List<Emit.ShaderBaker.BakeShader>();
        var s_Textures = new List<CustomBakeTexture>();
        if (p_GraphPaths.Count == 0)
            return (s_Shaders, s_Textures, null);

        Directory.CreateDirectory(p_WorkDir);

        var s_Graphs = new List<(string Path, ShaderGraph Graph)>();
        foreach (var s_GraphPath in p_GraphPaths)
        {
            if (!File.Exists(s_GraphPath))
                return (s_Shaders, s_Textures, $"saved graph not found: {s_GraphPath}");

            ShaderGraph s_Graph;
            try { s_Graph = ShaderGraph.FromJson(File.ReadAllText(s_GraphPath)); }
            catch (Exception s_Exception)
            {
                return (s_Shaders, s_Textures, $"'{Path.GetFileName(s_GraphPath)}' is not a graph: {s_Exception.Message}");
            }

            if (string.IsNullOrWhiteSpace(s_Graph.TargetShader))
                return (s_Shaders, s_Textures,
                    $"'{Path.GetFileName(s_GraphPath)}' has no target shader — open it and set one first.");

            s_Graphs.Add((s_GraphPath, s_Graph));

            // A MASTER file carries its variations inside; each embedded one is a bake lane of its own.
            if (s_Graph.Variations is { Count: > 0 } s_EmbeddedLanes)
            {
                foreach (var s_Lane in s_EmbeddedLanes)
                {
                    if (s_Lane.BakeVariation is not { Length: > 0 })
                        continue;

                    if (string.IsNullOrWhiteSpace(s_Lane.TargetShader))
                        s_Lane.TargetShader = s_Graph.TargetShader;

                    s_Lane.Variations = null;
                    s_Graphs.Add(($"{s_GraphPath}#{s_Lane.BakeVariation.Split('/')[^1]}", s_Lane));
                }

                s_Graph.Variations = null;
                p_Log($"  '{Path.GetFileName(s_GraphPath)}': {s_EmbeddedLanes.Count} embedded variation(s) join the bake.");
            }
        }

        // One mount extracts every target's reference permutation.
        var s_Db = Emit.LevelScanner.ShaderDbFor(p_FirstLevel);
        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        for (var i = 0; i < s_Graphs.Count; i++)
        {
            // ⛔⛔ THE FOLDER IS EMPTIED FIRST, AND SKIPPING THAT ABORTED A BAKE. The extractor writes numbered
            // subfolders plus an index.txt when the name matches several shaders, and the loose files straight
            // into the folder when it matches ONE — so a single-match shader extracted into a folder left over
            // from a MULTI-match one lands next to that one's index.txt, and the reader below, finding an
            // index, looks for its own name among ANOTHER shader's rows and gives up. Slot number i means a
            // different shader from one bake to the next; only clearing makes it mean this one.
            var s_Out = Path.Combine(p_WorkDir, $"extract_{i}");
            if (Directory.Exists(s_Out))
                try { Directory.Delete(s_Out, true); } catch { /* a locked leftover fails loudly below */ }

            s_Script.AppendLine(
                $"extract_shader_dxbc {s_Db} \"{s_Graphs[i].Graph.TargetShader}\" \"{s_Out}\"");
        }

        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(p_WorkDir, "contracts.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString());
        p_Log($"Reading {s_Graphs.Count} saved shader(s) out of {p_FirstLevel} for their contracts (one mount)…");
        Run(p_Repl, new[] { s_ScriptPath });

        for (var i = 0; i < s_Graphs.Count; i++)
        {
            var (s_GraphPath, s_Graph) = s_Graphs[i];
            var s_Target = s_Graph.TargetShader;
            var s_Extract = Path.Combine(p_WorkDir, $"extract_{i}");

            // The extractor writes numbered subfolders + index.txt when the name matched several shaders.
            var s_Folder = s_Extract;
            var s_IndexPath = Path.Combine(s_Extract, "index.txt");
            if (File.Exists(s_IndexPath))
            {
                var s_Row = File.ReadAllLines(s_IndexPath)
                    .Select(p_L => p_L.Split('\t'))
                    .Where(p_P => p_P.Length >= 2)
                    .FirstOrDefault(p_P => p_P[1].Equals(s_Target, StringComparison.OrdinalIgnoreCase));

                if (s_Row == null)
                    return (s_Shaders, s_Textures,
                        $"'{s_Target}' matched several shaders in {p_FirstLevel} but none exactly.");

                s_Folder = Path.Combine(s_Extract, s_Row[0]);
            }

            var s_Candidates = Directory.Exists(s_Folder)
                ? new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").OrderByDescending(p_F => p_F.Length).ToList()
                : new List<FileInfo>();

            if (s_Candidates.Count == 0)
                return (s_Shaders, s_Textures,
                    $"'{s_Target}' produced no pixel shader from {p_FirstLevel} — is it in that map?");

            var s_Log = new List<string>();
            var s_Chosen = ShaderIndex.ChooseGBufferPermutation(s_Candidates, s_Log, s_Target);

            ShaderContract? s_Contract = null;
            try
            {
                var s_Bytes = File.ReadAllBytes(s_Chosen.FullName);
                s_Contract = ShaderContract.Detect(s_Bytes);
                var s_Disasm = new SharpDX.D3DCompiler.ShaderBytecode(s_Bytes).Disassemble();
                s_Contract.ClassifyInterpolators(Translate.DxbcAsm.Parse(s_Disasm.Split('\n')));
            }
            catch (Exception s_Exception)
            {
                return (s_Shaders, s_Textures, $"could not read '{s_Target}' contract: {s_Exception.Message}");
            }

            var s_EmitResult = new HlslEmitter { Contract = s_Contract }.Emit(s_Graph);
            if (!s_EmitResult.Ok)
                return (s_Shaders, s_Textures,
                    $"'{Path.GetFileName(s_GraphPath)}' does not emit: {string.Join("; ", s_EmitResult.Errors)}");

            var s_Hlsl = Path.Combine(p_WorkDir, $"extra_{i}.hlsl");
            var s_Dxbc = Path.ChangeExtension(s_Hlsl, ".dxbc");
            File.WriteAllText(s_Hlsl, s_EmitResult.Hlsl);
            if (File.Exists(s_Dxbc))
                File.Delete(s_Dxbc);

            // (the built-in compiler: fxc's /T ps_5_0 /E main /O3 bytes — Emit.ShaderCompiler, proven byte for byte by --compilertest)
            var s_CompileError = ShaderCompiler.CompilePixelShader(s_Hlsl, s_Dxbc);

            if (!File.Exists(s_Dxbc))
                return (s_Shaders, s_Textures,
                    $"the shader compiler rejected '{Path.GetFileName(s_GraphPath)}': " +
                    string.Join(" ", (s_CompileError ?? "").Split('\n').TakeLast(3)).Trim());

            var s_FullMap = ReadSlotMapFull(Path.Combine(p_TexCacheDir, SlotMapFileName(s_Target)));
            var s_Slots = s_FullMap?.Slots;
            var (s_GraphTextures, s_TexError) = PrepareCustomTextures(s_Graph, s_Slots,
                Path.Combine(p_WorkDir, $"textures_{i}"), p_Log);

            if (s_TexError != null)
                return (s_Shaders, s_Textures, $"'{Path.GetFileName(s_GraphPath)}': {s_TexError}");

            // A saved graph carrying a variation delivers as one, exactly like the active graph would.
            VariationSpec? s_GraphVariation = null;
            // ⛔ Whether the graph reproduces the vanilla logic decides whether ANY bytecode is
            // patched, and that is true of a replacement as much as of a variation — so it is
            // measured out here, where both can see it.
            var s_SameLogic = s_Graph.TranslatedHlslHash != null &&
                              HlslFingerprint(s_Graph, s_Contract) == s_Graph.TranslatedHlslHash;

            string? s_ManifestForBake = null;

            if (s_Graph.BakeVariation is { Length: > 0 } s_VariationName)
            {
                // On a shared shader "any mesh that uses it" is not good enough: the first one listed can
                // be a destruction mesh, which is not resident when the level bundle loads and crashes the
                // end of the loading screen registering the variation against a null mesh. The graph's own
                // stamp (the preview's selected set) wins; without one, prefer a mesh that does not look
                // like destruction debris.
                static string? PickMesh(IEnumerable<string?>? p_Meshes) => p_Meshes?
                    .Where(p_M => p_M is { Length: > 0 })
                    .OrderBy(p_M => p_M!.Contains("destruction", StringComparison.OrdinalIgnoreCase) ||
                                    p_M.Contains("_wreck", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .FirstOrDefault();

                var s_Mesh = s_Graph.BakeMesh is { Length: > 0 } s_Stamped
                    ? s_Stamped
                    : PickMesh(s_FullMap?.Variations?.Values.Select(p_V => p_V.Mesh));

                // A pre-variation cached map lacks the mesh name; refresh it here rather than bounce the
                // user to a refresh affordance that no longer exists.
                if (s_Mesh == null)
                {
                    p_Log($"  '{s_Target}': cached texture map predates variation support — refreshing...");
                    var s_MapPath = Path.Combine(p_TexCacheDir, SlotMapFileName(s_Target));
                    LoadTexturesCore(p_Repl, p_GamePath, ShaderDbForTarget(s_Target), s_Target,
                        p_TexCacheDir, s_MapPath, s_Contract);
                    s_Mesh = PickMesh(ReadSlotMapFull(s_MapPath)?.Variations?.Values.Select(p_V => p_V.Mesh));
                }

                if (s_Mesh == null)
                    return (s_Shaders, s_Textures,
                        $"'{Path.GetFileName(s_GraphPath)}': no database entry names a mesh for this " +
                        "target even after a refresh — a variation cannot be delivered without one.");

                // ⛔ TAKING OVER WHAT IS ALREADY IN THE MAP AIMS AT ONE MESH, SO THE MESH CANNOT BE A GUESS.
                // A preset shader is worn by dozens of different meshes in a single map, and the fallback
                // above picks whichever one the cached map happens to list first. Aimed at the wrong one, the
                // bake installs, reports success and changes nothing — the failure mode that only shows up
                // after a boot.
                //
                // ⚠ BUT ONLY WHEN THERE IS SOMETHING TO GET WRONG. A shader worn by ONE mesh has no ambiguity
                // to resolve and no picker to choose from (the editor hides it), so demanding a stamp there
                // would refuse the very case that has always worked — a guard that fires where no mistake is
                // possible is just a wall.
                var s_MeshChoices = s_FullMap?.Variations?.Values
                    .Select(p_V => p_V.Mesh)
                    .Where(p_M => p_M is { Length: > 0 })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() ?? 0;

                if (s_Graph.ApplyVariationToLevel && s_Graph.BakeMesh is not { Length: > 0 } &&
                    s_MeshChoices > 1)
                    return (s_Shaders, s_Textures,
                        $"'{Path.GetFileName(s_GraphPath)}': this bake is meant to change the copies already " +
                        $"in the level, but no object is stamped on this graph — '{s_Target}' is worn by more " +
                        "than one mesh and the take-over would be aimed at a guess. Open it, pick the " +
                        "object's set in the preview (that aims the graph) and SAVE it, then bake. A graph " +
                        "saved before this editor recorded the choice carries no object even if it was baked " +
                        "correctly at the time.");

                p_Log($"  variation rides mesh '{s_Mesh}'" +
                      (s_Graph.BakeMesh is { Length: > 0 } ? " (the preview's selected set)." : "."));


                s_GraphVariation = Emit.VariationPlan.Build(s_Target, s_VariationName, s_Mesh,
                    Path.Combine(p_WorkDir, $"variation_{i}"), s_SameLogic ? null : s_Dxbc,
                    s_Graph.ApplyVariationToLevel);

                p_Log(s_Graph.ApplyVariationToLevel
                    ? "  the level's own copies of this object will render with this shader."
                    : "  ships as an extra variation; the level's own copies are left alone.");

                var s_RetargetError = RetargetTexturesForVariation(s_GraphTextures, s_GraphVariation,
                    s_Slots != null ? new Dictionary<string, string>(s_Slots) : null);

                if (s_RetargetError != null)
                    return (s_Shaders, s_Textures, $"'{Path.GetFileName(s_GraphPath)}': {s_RetargetError}");

                p_Log($"  variation: '{s_VariationName}' (hash {s_GraphVariation.Hash}) -> clone " +
                      $"'{s_GraphVariation.CloneShader}', " +
                      $"{(s_SameLogic ? "vanilla bytecode" : "authored bytecode")}");

            }

            // Per-VARIANT compilation: the mode ships several pixel flavours (base, probe-lit via
            // interpolators, probe-lit via cbuffer...) and one compilation stuffed into all of them is
            // how probe-lit objects lost their ambient and instanced batches shaded garbage. Each
            // distinct vanilla flavour gets the graph compiled against ITS OWN detected contract; a
            // flavour the emitter cannot honour keeps its vanilla bytes, which renders correct (just
            // not customised) instead of broken.
            if (!s_SameLogic)
            {
                var s_Manifest = new List<string>();
                var s_ChosenBytes = File.ReadAllBytes(s_Chosen.FullName);
                var s_Distinct = new List<byte[]>();
                var s_VariantIndex = 0;

                // Whether this bake targets the GBuffer or the forward (Default) pass — the variants
                // worth compiling are the ones from the SAME pass as the chosen reference. A forward
                // entry has only single-target flavours; gating on ">= 3 targets" here would silently
                // skip every one of them. ⛔ And "forward" is decided by the measured discriminator
                // (the outdoor-light constants ride ONLY the forward solutions), not by target count:
                // the vegetation-alpha preset writes a REDUCED two-target GBuffer that a count-based
                // rule misfiled as forward, which sent its variants down the keeps-vanilla branch.
                var s_ChosenContract = ShaderContract.Detect(s_ChosenBytes);
                var s_ChosenIsForward = s_ChosenContract.ConstantFields.Any(p_F =>
                    p_F.Name.Equals("outdoorLightHemisphereDir", StringComparison.OrdinalIgnoreCase));

                // Same pass as the chosen reference: the full-GBuffer and forward cases keep the
                // ">= 3 on both sides" split they were verified with; a REDUCED GBuffer reference
                // (fewer than three targets, not forward) matches by exact target count, which is
                // what keeps its alpha-tested ZOnly flavours (one target) out of the patch set.
                bool SamePass(Emit.ShaderContract p_Variant) =>
                    s_ChosenIsForward || s_ChosenContract.RenderTargets >= 3
                        ? (p_Variant.RenderTargets >= 3) == (s_ChosenContract.RenderTargets >= 3)
                        : p_Variant.RenderTargets == s_ChosenContract.RenderTargets;

                foreach (var s_PermFile in new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc"))
                {
                    var s_PermBytes = File.ReadAllBytes(s_PermFile.FullName);
                    if (s_Distinct.Any(p_D => p_D.AsSpan().SequenceEqual(s_PermBytes)))
                        continue;

                    s_Distinct.Add(s_PermBytes);

                    if (s_PermBytes.AsSpan().SequenceEqual(s_ChosenBytes))
                    {
                        s_Manifest.Add($"{s_PermFile.FullName}|{s_Dxbc}");
                        continue;
                    }

                    try
                    {
                        var s_VarContract = ShaderContract.Detect(s_PermBytes);
                        if (!SamePass(s_VarContract))
                            continue;

                        // A flavour whose interpolator layout no family explains cannot be patched
                        // safely — the emitted code would read positions blind. It keeps vanilla
                        // bytes, the same fallback as a flavour the emitter declines.
                        if (s_VarContract.Family == Emit.ShaderFamily.Unknown)
                        {
                            p_Log($"    variant {s_PermFile.Name}: unrecognized interpolator layout " +
                                  "— keeps vanilla bytes.");
                            continue;
                        }

                        // Forward flavours carry their light terms IN THE BODY (probe SH rows, the
                        // lightmap set) — a GBuffer root re-emits those blocks per flavour, but a
                        // forward graph's body IS the authored code and has no such terms. The
                        // probe-CBUFFER twin's delta is measured and mechanical, so it is SYNTHESIZED
                        // onto a clone of the graph (spawned single objects are lit exactly this way);
                        // the interpolator-probe and lightmap flavours still keep vanilla bytes —
                        // correct, just not customised — rather than losing their light.
                        var s_VariantGraph = s_Graph;
                        var s_HasLightmap = s_VarContract.ConstantFields.Any(
                            p_F => p_F.Name.StartsWith("lightMap", StringComparison.OrdinalIgnoreCase));

                        if (s_ChosenIsForward &&
                            (s_VarContract.Probes != Emit.ProbeTransport.None || s_HasLightmap))
                        {
                            if (s_VarContract.Probes == Emit.ProbeTransport.Cbuffer && !s_HasLightmap)
                            {
                                var s_Probeized = Graph.ShaderGraph.FromJson(s_Graph.ToJson());
                                if (Emit.ForwardProbe.TryProbeize(s_Probeized, out var s_Why))
                                {
                                    s_VariantGraph = s_Probeized;
                                    p_Log($"    variant {s_PermFile.Name}: probe-lit twin synthesized " +
                                          "(ambient x ShO + SH, reflection x ShO).");
                                }
                                else
                                {
                                    p_Log($"    variant {s_PermFile.Name}: probe twin not derivable " +
                                          $"({s_Why}) — keeps vanilla bytes.");
                                    continue;
                                }
                            }
                            else
                            {
                                p_Log($"    variant {s_PermFile.Name}: adds its own light terms " +
                                      "(probe-interpolator/lightmap) — keeps vanilla bytes.");
                                continue;
                            }
                        }

                        var s_VarDisasm = new SharpDX.D3DCompiler.ShaderBytecode(s_PermBytes).Disassemble();
                        var s_VarInstructions = Translate.DxbcAsm.Parse(s_VarDisasm.Split('\n'));
                        s_VarContract.ClassifyInterpolators(s_VarInstructions);

                        // The alpha-coverage tap count is a fact of EACH flavour's own bytecode: the
                        // plain vegetation-alpha solutions dither two taps, the instanced ones four.
                        // A graph translated from one flavour carries that flavour's count, so the
                        // node is re-stamped with what THIS variant actually does — measured from its
                        // instructions, not generalised from the family.
                        var s_VanillaTaps = CountAlphaCoverageTaps(s_VarInstructions);
                        if (s_VanillaTaps is 2 or 4 &&
                            s_VariantGraph.Nodes.Any(p_N => p_N.Kind == "AlphaCoverage" &&
                                p_N.GetParam("Taps") != s_VanillaTaps.ToString()))
                        {
                            var s_Retapped = Graph.ShaderGraph.FromJson(s_VariantGraph.ToJson());
                            foreach (var s_CoverageNode in s_Retapped.Nodes
                                         .Where(p_N => p_N.Kind == "AlphaCoverage"))
                                s_CoverageNode.Params["Taps"] = s_VanillaTaps.ToString();

                            s_VariantGraph = s_Retapped;
                            p_Log($"    variant {s_PermFile.Name}: alpha-coverage re-stamped to " +
                                  $"{s_VanillaTaps} taps (this flavour's own pattern).");
                        }

                        // ⛔ THE GRAPH'S TEXTURE REGISTERS BELONG TO THE FLAVOUR IT WAS TRANSLATED FROM.
                        // Emitting it verbatim against another one samples whatever that flavour keeps at
                        // those registers — on a lightmapped variant t1..t4 are the engine's lightmap, so
                        // the albedo would be read out of the irradiance atlas. Matched by material slot.
                        int NewRegisterFor(Graph.GraphNode p_TexNode) =>
                            int.TryParse(p_TexNode.GetParam("Register"), out var s_Old)
                                ? s_VarContract.RemapRegisterFrom(s_ChosenContract, s_Old) is var s_New &&
                                  s_New >= 0 && s_New != s_Old
                                    ? s_New
                                    : -1
                                : -1;

                        var s_Remapped = new List<string>();
                        if (s_VariantGraph.Nodes.Any(p_N => NewRegisterFor(p_N) >= 0))
                        {
                            // Never in place: the graph may still be the user's own document.
                            if (ReferenceEquals(s_VariantGraph, s_Graph))
                                s_VariantGraph = Graph.ShaderGraph.FromJson(s_Graph.ToJson());

                            foreach (var s_TexNode in s_VariantGraph.Nodes)
                            {
                                var s_NewReg = NewRegisterFor(s_TexNode);
                                if (s_NewReg < 0)
                                    continue;

                                s_Remapped.Add($"t{s_TexNode.GetParam("Register")}->t{s_NewReg}");
                                s_TexNode.Params["Register"] = s_NewReg.ToString(CultureInfo.InvariantCulture);
                            }
                        }

                        if (s_Remapped.Count > 0)
                            p_Log($"    variant {s_PermFile.Name}: texture registers remapped for this " +
                                  $"flavour ({string.Join(", ", s_Remapped)}).");

                        var s_VarEmit = new HlslEmitter { Contract = s_VarContract }.Emit(s_VariantGraph);
                        if (!s_VarEmit.Ok)
                        {
                            p_Log($"    variant {s_PermFile.Name}: not emittable " +
                                  $"({string.Join("; ", s_VarEmit.Errors.Take(1))}) — keeps vanilla bytes.");
                            continue;
                        }

                        var s_VarHlsl = Path.Combine(p_WorkDir, $"variant_{i}_{s_VariantIndex}.hlsl");
                        var s_VarDxbc = Path.ChangeExtension(s_VarHlsl, ".dxbc");
                        File.WriteAllText(s_VarHlsl, s_VarEmit.Hlsl);
                        if (File.Exists(s_VarDxbc))
                            File.Delete(s_VarDxbc);

                        ShaderCompiler.CompilePixelShader(s_VarHlsl, s_VarDxbc);

                        if (File.Exists(s_VarDxbc))
                        {
                            s_Manifest.Add($"{s_PermFile.FullName}|{s_VarDxbc}");
                            p_Log($"    variant {s_PermFile.Name}: {s_VarContract.Pass} -> compiled " +
                                  $"({new FileInfo(s_VarDxbc).Length} B).");
                        }
                        else
                        {
                            p_Log($"    variant {s_PermFile.Name}: the shader compiler rejected it — keeps vanilla bytes.");
                        }
                    }
                    catch (Exception s_VariantException)
                    {
                        p_Log($"    variant {s_PermFile.Name}: {s_VariantException.Message} — keeps vanilla bytes.");
                    }

                    s_VariantIndex++;
                }

                if (s_Manifest.Count > 1)
                {
                    var s_ManifestPath = Path.Combine(p_WorkDir, $"variation_{i}", "patch.manifest");
                    Directory.CreateDirectory(Path.GetDirectoryName(s_ManifestPath)!);
                    File.WriteAllLines(s_ManifestPath, s_Manifest);
                    s_ManifestForBake = s_ManifestPath;
                    if (s_GraphVariation != null)
                        s_GraphVariation.ManifestPath = s_ManifestPath;
                    p_Log($"    {s_Manifest.Count} variant(s) patched via manifest.");
                }
            }

            s_Shaders.Add(new Emit.ShaderBaker.BakeShader(s_Target, s_Dxbc, s_GraphVariation,
                s_ManifestForBake));
            s_Textures.AddRange(s_GraphTextures);
            p_Log($"  saved shader ready: '{s_Target}' ({s_EmitResult.Hlsl.Length} chars HLSL, " +
                  $"{new FileInfo(s_Dxbc).Length} B DXBC, {s_GraphTextures.Count} custom texture(s))");
        }

        return (s_Shaders, s_Textures, null);
    }

    /// <summary>
    /// Converts every texture node's chosen file into an engine-ready DDS and names the game texture each one
    /// overrides. All-or-nothing on purpose: a bake that silently ships HALF the chosen textures looks like a
    /// working mod with wrong art, which is the worst failure to diagnose in-game.
    ///
    /// Format lanes are the ones proven in-game: classic-header DXT1/DXT5 only (a DX10-header DDS builds fine
    /// and then hangs the client reading the chunk), normal maps as DXT1 with the normal-map flag, sRGB as a
    /// resource FLAG (never in the DDS header, which would force DX10).
    /// </summary>
    /// <summary>Decodes an image the way the preview does and writes it as a BMP: pixels only. Null on success.</summary>
    internal static string? WriteBmpCopy(string p_Source, string p_Bmp)
    {
        try
        {
            var s_Bitmap = new BitmapImage();
            s_Bitmap.BeginInit();
            s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            s_Bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            s_Bitmap.UriSource = new Uri(p_Source);
            s_Bitmap.EndInit();
            s_Bitmap.Freeze();

            var s_Encoder = new BmpBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(s_Bitmap));
            using var s_Stream = File.Create(p_Bmp);
            s_Encoder.Save(s_Stream);
            return null;
        }
        catch (Exception s_Exception)
        {
            return $"could not be decoded ({s_Exception.Message})";
        }
    }

    internal static (List<CustomBakeTexture> Textures, string? Error) PrepareCustomTextures(
        ShaderGraph p_Graph, IReadOnlyDictionary<string, string>? p_Slots, string p_WorkDir,
        Action<string> p_Log)
    {
        // GetParam for the register: a node whose register was never TOUCHED carries it as a palette default,
        // not as a dictionary entry — the raw read shipped "" and the bake refused a perfectly good node.
        var s_Chosen = p_Graph.Nodes
            .Where(p_N => TextureNodeKinds.Contains(p_N.Kind) &&
                          p_N.Params.TryGetValue("CustomTexture", out var s_File) && s_File.Length > 0)
            .Select(p_N => (Node: p_N,
                File: p_N.Params["CustomTexture"],
                Register: p_N.GetParam("Register")))
            .ToList();

        var s_Result = new List<CustomBakeTexture>();
        if (s_Chosen.Count == 0)
            return (s_Result, null);

        var s_Texconv = FindTexconv();
        if (s_Texconv == null)
            return (s_Result, "texconv.exe not found (expected next to RimeREPL.exe or the editor). It is " +
                              "needed to convert custom textures to the engine's format.");

        if (p_Slots == null || p_Slots.Count == 0)
            return (s_Result, "no texture slot map for this shader — press 'Reload from game' first so the " +
                              "editor knows WHICH game texture each register binds (that name is what the " +
                              "custom texture overrides).");

        Directory.CreateDirectory(p_WorkDir);

        foreach (var (s_Node, s_File, s_Register) in s_Chosen)
        {
            if (!File.Exists(s_File))
                return (s_Result, $"custom texture '{s_File}' (register t{s_Register}) no longer exists.");

            // Two lanes. A register the game material BINDS -> override that texture by name. A register it
            // does NOT bind (an independent node the user added) -> a BRAND-NEW texture under a name of our
            // own, plus a shader-constant slot added at bake so the engine binds it there natively — the
            // mechanism the water-palette work proved in-game. The new name needs a GROUP DONOR: a fresh
            // name has no original to copy the pool group from, and 'Default' draws black.
            string s_GameName;
            string? s_SlotTarget = null;
            string? s_GroupDonor = null;

            if (p_Slots.TryGetValue(s_Register, out var s_BoundName))
            {
                s_GameName = s_BoundName;
            }
            else
            {
                s_GameName = $"custom/{Sanitize(p_Graph.TargetShader).ToLowerInvariant()}/t{s_Register}";
                s_SlotTarget = p_Graph.TargetShader;
                s_GroupDonor = p_Slots.Values.FirstOrDefault();

                if (s_GroupDonor == null)
                    return (s_Result, $"register t{s_Register} is a new slot, but the material binds no " +
                                      "texture at all to donate a pool group from.");
            }

            var s_Normal = s_Node.Kind == "NormalMap";
            var s_Srgb = !s_Normal;
            var s_Format = s_Normal ? "BC1_UNORM" : ImageHasAlpha(s_File) ? "BC3_UNORM" : "BC1_UNORM";

            var s_OutName = Sanitize(s_GameName) + ".dds";
            var s_OutPath = Path.Combine(p_WorkDir, s_OutName);
            if (File.Exists(s_OutPath))
                File.Delete(s_OutPath);

            // texconv names its output after the input; convert into a scratch folder then claim the file.
            var s_ConvDir = Path.Combine(p_WorkDir, "conv_" + Sanitize(s_Register));
            Directory.CreateDirectory(s_ConvDir);

            // ⛔ Never the user's PNG/JPG itself. A PNG that carries sRGB/gAMA chunks (Photoshop's, WPF's) makes
            // texconv convert the pixels sRGB -> linear while compressing to a non-sRGB DXT format, and the
            // resource is then flagged sRGB for the game, which decodes it AGAIN: darker and more saturated
            // than the preview (measured 2026-09-11 on the camo thumbnails: mean 170 -> 115). A BMP carries no
            // colour-space metadata, so the pixels go through untouched. A DDS is handed over as it is.
            var s_Input = s_File;
            if (!s_File.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                s_Input = Path.Combine(s_ConvDir, Path.GetFileNameWithoutExtension(s_File) + ".bmp");
                if (WriteBmpCopy(s_File, s_Input) is { } s_BmpError)
                    return (s_Result, $"custom texture '{Path.GetFileName(s_File)}' (register t{s_Register}): {s_BmpError}");
            }

            var s_Process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s_Texconv)
            {
                Arguments = $"-nologo -y -m 0 -pow2 -f {s_Format} -o \"{s_ConvDir}\" \"{s_Input}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            var s_ToolOutput = s_Process == null
                ? ""
                : s_Process.StandardOutput.ReadToEnd() + s_Process.StandardError.ReadToEnd();
            s_Process?.WaitForExit();

            var s_Produced = Path.Combine(s_ConvDir,
                Path.GetFileNameWithoutExtension(s_File) + ".dds");

            if (!File.Exists(s_Produced))
                return (s_Result, $"texconv produced nothing for '{Path.GetFileName(s_File)}': " +
                                  $"{s_ToolOutput.Trim()}");

            File.Move(s_Produced, s_OutPath, true);

            // The in-game-proven lane is a CLASSIC DDS header. A DX10-header one builds a bundle that hangs
            // the client reading the chunk — caught here, where it costs seconds instead of a game boot.
            var s_Header = new byte[96];
            using (var s_Stream = File.OpenRead(s_OutPath))
                _ = s_Stream.Read(s_Header, 0, s_Header.Length);

            var s_FourCc = System.Text.Encoding.ASCII.GetString(s_Header, 84, 4);
            if (s_FourCc == "DX10")
                return (s_Result, $"the converted '{Path.GetFileName(s_File)}' came out with a DX10 header, " +
                                  "which the game cannot read from a mod bundle. Re-save the source as PNG " +
                                  "and try again.");

            s_Result.Add(new CustomBakeTexture(s_GameName, s_OutPath, s_Srgb, s_Normal,
                s_SlotTarget, s_SlotTarget != null ? s_Register : "", s_GroupDonor));
            p_Log($"  custom texture: t{s_Register} '{Path.GetFileName(s_File)}' -> {s_FourCc} " +
                  (s_SlotTarget != null
                      ? $"NEW slot '{s_GameName}' (group from '{s_GroupDonor}')"
                      : $"overriding '{s_GameName}'") +
                  (s_Normal ? " (normal map)" : ""));
        }

        return (s_Result, null);
    }

    /// <summary>True when any pixel is meaningfully transparent — that decides DXT5 over DXT1.</summary>
    private static bool ImageHasAlpha(string p_Path)
    {
        try
        {
            if (LoadCustomTextureImage(p_Path) is not BitmapSource s_Image)
                return false;

            var s_Converted = new FormatConvertedBitmap(s_Image, PixelFormats.Bgra32, null, 0);
            var s_Pixels = new byte[s_Converted.PixelWidth * s_Converted.PixelHeight * 4];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);

            for (var i = 3; i < s_Pixels.Length; i += 4)
                if (s_Pixels[i] < 250)
                    return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// How many alpha-coverage taps a pixel shader's own instructions perform: the single-lane samples
    /// whose resource swizzle hands over the texture's ALPHA (the .w channel at the written lane). The
    /// plain vegetation-alpha solutions take two, the instanced ones four; anything else returns whatever
    /// it counts and the caller ignores counts that are not a measured tap pattern.
    /// </summary>
    private static int CountAlphaCoverageTaps(List<Translate.AsmInstruction> p_Instructions) =>
        p_Instructions.Count(p_I =>
            p_I.Opcode.StartsWith("sample", StringComparison.Ordinal) &&
            p_I.Destination is { Kind: Translate.OperandKind.Temp, WriteMask.Length: 1 } s_Dest &&
            p_I.Sources.FirstOrDefault(p_S => p_S.Kind == Translate.OperandKind.Resource) is { } s_Resource &&
            s_Resource.Swizzle.ElementAtOrDefault(s_Dest.WriteMask[0]) == 3);

    /// <summary>
    /// FNV-1a of the graph's emitted HLSL under the given contract, or null when it does not emit. The same
    /// deterministic emitter both stamps (at translate) and compares (at bake), so the hash answers exactly
    /// one question: did the shader's LOGIC change since it came out of the game?
    /// </summary>
    internal static string? HlslFingerprint(ShaderGraph p_Graph, ShaderContract? p_Contract)
    {
        try
        {
            var s_Emit = new HlslEmitter { Contract = p_Contract }.Emit(p_Graph);
            if (!s_Emit.Ok)
                return null;

            var s_Hash = 2166136261u;
            foreach (var s_Byte in System.Text.Encoding.UTF8.GetBytes(s_Emit.Hlsl))
            {
                s_Hash ^= s_Byte;
                s_Hash *= 16777619u;
            }

            return s_Hash.ToString("x8");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Where the tools an application ships INSIDE its executable were unpacked (RimeREPL under "rime\", texconv beside it) — set once at
    /// start-up by an application that carries them, before anything looks for a tool; null for a build of the repository, which finds
    /// them in the repository's own folders.
    /// </summary>
    internal static string? ToolsFolder { get; set; }

    internal static string? FindTexconv()
    {
        var s_Candidates = new List<string>();

        // the shipped one first, then the one beside the executable, then a build of the repository's
        if (ToolsFolder != null)
            s_Candidates.Add(Path.Combine(ToolsFolder, "texconv.exe"));

        if (FindRimeRepl() is { } s_Repl)
            s_Candidates.Add(Path.Combine(Path.GetDirectoryName(s_Repl)!, "texconv.exe"));

        s_Candidates.Add(Path.Combine(AppContext.BaseDirectory, "texconv.exe"));

        var s_Directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && s_Directory != null; i++, s_Directory = s_Directory.Parent)
            s_Candidates.Add(Path.Combine(s_Directory.FullName, "texconv.exe"));

        return s_Candidates.FirstOrDefault(File.Exists);
    }

    internal static string? FindRimeRepl()
    {
        // the shipped one first (ToolsFolder), then one beside the executable, then a build of the repository (bin\Release up the tree)
        var s_Candidates = new List<string>();
        if (ToolsFolder != null)
            s_Candidates.Add(Path.Combine(ToolsFolder, "rime", "RimeREPL.exe"));
        s_Candidates.Add(Path.Combine(AppContext.BaseDirectory, "rime", "RimeREPL.exe"));

        var s_Directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && s_Directory != null; i++, s_Directory = s_Directory.Parent)
            s_Candidates.Add(Path.Combine(s_Directory.FullName, "bin", "Release", "RimeREPL.exe"));

        return s_Candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The shader compiler the bake and the translation hand around: always the built-in one (Emit.ShaderCompiler — the redistributable
    /// Direct3D compiler shipped beside the executable, byte-identical to the SDK's fxc), so no Windows SDK is needed.
    /// </summary>
    internal static string? FindFxc() => ShaderCompiler.BuiltIn;

    /// <summary>The Windows SDK's own fxc.exe (the newest installed), or null — a seam's oracle for the built-in compiler (Emit.ShaderCompiler).</summary>
    internal static string? FindSdkFxc()
    {
        var s_Roots = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\bin",
            @"C:\Program Files\Windows Kits\10\bin",
        };

        var s_Candidates = new List<string>();
        foreach (var s_Root in s_Roots)
        {
            if (!Directory.Exists(s_Root))
                continue;

            foreach (var s_Version in Directory.GetDirectories(s_Root))
            {
                var s_Path = Path.Combine(s_Version, "x64", "fxc.exe");
                if (File.Exists(s_Path))
                    s_Candidates.Add(s_Path);
            }
        }

        s_Candidates.Sort(StringComparer.OrdinalIgnoreCase);
        return s_Candidates.Count > 0 ? s_Candidates[^1] : null;
    }

    /// <summary>
    /// Turns this window into the CAMO authoring view: the shader-browsing chrome goes away and the mesh
    /// list becomes the weapons a camo can be previewed on.
    ///
    /// ⛔ OPT-IN, AND THE EDITOR IS UNTOUCHED UNLESS IT IS CALLED. Nothing here runs on the normal path —
    /// the shader editor opens exactly as it always did, which is the only way a shared window can serve
    /// two tools without either of them growing modes it has to reason about.
    ///
    /// The weapons arrive as MESH names, not weapon names, on purpose: the mesh picker already resolves a
    /// name against the cache and only mounts the game when a dump is missing, so feeding it mesh names
    /// reuses all of that instead of adding a second path that loads meshes.
    /// </summary>
    /// <summary>In camo mode: the shader a given weapon mesh wears, so picking the weapon loads its graph.</summary>
    private Func<string, string?>? m_CamoShaderFor;

    /// <summary>Whether this window is serving the camo studio. Settings uses it to show camo-only options.</summary>
    internal bool CamoMode { get; private set; }

    /// <summary>Where a preset's translated graph is kept, so it is read out of the game only once.</summary>
    /// <summary>The HLSL the preview last compiled for the canvas (a seam reads it: what the authored shader samples, and from where).</summary>
    internal string? LastPreviewHlsl { get; private set; }

    internal string CamoGraphCachePath(string p_Shader) =>
        Path.Combine(OutputFolder, "camographs", $"{Sanitize(p_Shader)}.json");

    /// <summary>
    /// ⭐ (2026-09-28) Retires a shader's cached TRANSLATION (camographs) and re-makes its cached CONTRACT (meshcache/shaders) when they were
    /// made from another permutation than the one chosen now (ShaderIndex.ChooseGBufferPermutation with its subjects' declaration —
    /// characterroot_xp4: sol0, the faces', before; sol19, the cloth, now): the graph is translated again by the next pick (one mount), the
    /// contract is copied from the extraction on disk. ⛔ Both caches are only ever made when MISSING, with no version, so without this every machine that cached a shader before
    /// keeps its old graph for ever. Only a shader the rule moves off its plainest permutation is looked at, and a cache stamped with the
    /// chosen one (ShaderGraph.TranslatedFrom; "&lt;contract&gt;.from") is kept. Without the extraction on disk nothing is known and nothing retired.
    /// </summary>
    internal void RetireStaleTranslation(string p_Shader)
    {
        var s_Folder = Path.Combine(OutputFolder, "translate", Sanitize(p_Shader));
        var s_Index = Path.Combine(s_Folder, "index.txt");
        if (File.Exists(s_Index) && File.ReadAllLines(s_Index).Select(p_L => p_L.Split('\t'))
                .FirstOrDefault(p_P => p_P.Length >= 2 && p_P[1].Trim().Equals(p_Shader, StringComparison.OrdinalIgnoreCase)) is { } s_Row)
            s_Folder = Path.Combine(s_Folder, s_Row[0].Trim());
        if (!Directory.Exists(s_Folder))
            return;

        var s_Candidates = new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").OrderByDescending(p_F => p_F.Length).ToList();
        if (s_Candidates.Count == 0)
            return;

        var s_Chosen = ShaderIndex.ChooseGBufferPermutation(s_Candidates, new List<string>(), p_Shader);
        var s_Now = s_Chosen.Name;
        if (s_Now == ShaderIndex.ChooseGBufferPermutation(s_Candidates, new List<string>()).Name)
            return;

        var s_Graph = CamoGraphCachePath(p_Shader);
        if (File.Exists(s_Graph))
        {
            string? s_From = null;
            try { s_From = ShaderGraph.FromJson(File.ReadAllText(s_Graph)).TranslatedFrom; }
            catch { }

            if (s_From != s_Now)
            {
                File.Delete(s_Graph);
                Log($"Camo: '{p_Shader}' — its cached graph was translated from {s_From ?? "its plainest permutation"}, not from {s_Now} " +
                    "(the permutation of the declaration its subjects draw with): retired, the next pick translates it again.");
            }
        }

        // the contract is re-made at once from the extraction on disk (the chosen permutation is in it: no mount) — the preview reads it
        // from this cache and nothing re-fetches the tab's own shader when it is missing
        var s_Dxbc = Path.Combine(MeshCacheDir, "shaders", $"{Sanitize(p_Shader)}.dxbc");
        var s_DxbcFrom = File.Exists(s_Dxbc + ".from") ? File.ReadAllText(s_Dxbc + ".from").Trim() : null;
        if (File.Exists(s_Dxbc) && s_DxbcFrom != s_Now)
        {
            File.Copy(s_Chosen.FullName, s_Dxbc, true);
            File.WriteAllText(s_Dxbc + ".from", s_Now);
            Log($"Camo: '{p_Shader}' — its cached contract came from {s_DxbcFrom ?? "its plainest permutation"}: re-made from {s_Now} " +
                "(the extraction on disk).");
        }
    }

    /// <summary>
    /// A graph the user keeps as the STARTING POINT for one attachment's mesh — the same piece is the same
    /// piece on every weapon that carries it (the sound suppressor hangs from dozens), so what was worked out
    /// once for it opens with it everywhere (the suppressor's own graph is what makes the camo show on it).
    ///
    /// Keyed by MESH and not by unlock on purpose: an unlock is per weapon, the mesh is the piece itself.
    /// </summary>
    internal string AttachmentPresetPath(string p_Mesh) =>
        Path.Combine(OutputFolder, "attachmentpresets", $"{Sanitize(p_Mesh)}.json");

    /// <summary>
    /// The host's starting graph for a piece the user has saved none for (the camo studio ships the suppressor's), a fresh graph per
    /// call; null when there is none.
    /// </summary>
    private Func<string, ShaderGraph?>? m_PiecePresetFor;

    /// <summary>Sets the host's loader of pieces' starting graphs (see m_PiecePresetFor).</summary>
    internal void SetPiecePresets(Func<string, ShaderGraph?>? p_PresetFor) => m_PiecePresetFor = p_PresetFor;

    /// <summary>Pieces whose saved preset has already been announced, so picking one twice does not repeat it.</summary>
    private readonly HashSet<string> m_AttachmentPresetSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ⭐ A SUBJECT'S OWN STARTING DOCUMENT — the piece's saved preset (AttachmentPresetOf) carried over to a whole subject (the
    /// request: the preset used, plus the custom mask made for it, as the RHIB's default in Camo Studio):
    /// the RHIB's hull sits in the camo mask the game's preset draws from the uvs, so a camo only shows on it through a graph of its own
    /// (the custom mask on a texture node). In camo mode the studio hands over the document a subject starts from when it has one (its Data,
    /// keyed by mesh), a FRESH graph per call; null for every other subject, which starts from its camo's common document as before.
    /// </summary>
    private Func<string, ShaderGraph?>? m_SubjectPresetFor;

    /// <summary>Sets the studio's loader of subjects' own starting documents (see m_SubjectPresetFor).</summary>
    internal void SetSubjectPresets(Func<string, ShaderGraph?>? p_PresetFor) => m_SubjectPresetFor = p_PresetFor;

    /// <summary>Subjects whose own starting document has already been announced, so picking one twice does not repeat it.</summary>
    private readonly HashSet<string> m_SubjectPresetSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// That starting point — the user's own, else the host's (m_PiecePresetFor) — or null when the piece has none. A broken file is said
    /// out loud, not swallowed.
    /// </summary>
    internal ShaderGraph? AttachmentPresetOf(string p_Mesh)
    {
        var s_Path = AttachmentPresetPath(p_Mesh);
        if (!File.Exists(s_Path))
            return m_PiecePresetFor?.Invoke(p_Mesh);

        try
        {
            return ShaderGraph.FromJson(File.ReadAllText(s_Path));
        }
        catch (Exception s_Exception)
        {
            Log($"Camo: the saved preset for {p_Mesh.Split('/')[^1]} could not be read ({s_Exception.Message}) — " +
                "the stock preset is used instead.");
            return null;
        }
    }

    /// <summary>
    /// The shader's contract, read from the prefetched bytecode instead of from the level index.
    ///
    /// Camo mode never loads a level index — the shader browser is hidden — so the usual source is empty.
    /// The dxbc is already on disk from the prefetch, and it is the same thing the index was derived from.
    /// </summary>
    private ShaderContract? ContractFromCache(string p_Shader)
    {
        var s_Contract = ReadContractFromCache(p_Shader);
        if (s_Contract != null)
            m_Preview.SetInterpolatorMeanings(s_Contract.InterpolatorMeanings);

        return s_Contract;
    }

    /// <summary>
    /// The same contract, WITHOUT handing its interpolator meanings to the authored shader's feed — for a
    /// contract read about ANOTHER shader than the one the 3D draws the camo with. ⛔ ContractFromCache does
    /// both, so reading a material's contract while the camo is on screen re-fed the camo's sections with that
    /// material's interpolators.
    /// </summary>
    private ShaderContract? ReadContractFromCache(string p_Shader)
    {
        var s_Dxbc = Path.Combine(MeshCacheDir, "shaders", $"{Sanitize(p_Shader)}.dxbc");
        if (!File.Exists(s_Dxbc))
        {
            Log($"Camo: no cached bytecode for '{p_Shader}' — material values cannot be wired, so the " +
                "weapon will draw flat. Run the cache step.");

            return null;
        }

        try
        {
            var s_Bytes = File.ReadAllBytes(s_Dxbc);
            var s_Contract = ShaderContract.Detect(s_Bytes);
            s_Contract.ClassifyInterpolators(Translate.DxbcAsm.Parse(
                new SharpDX.D3DCompiler.ShaderBytecode(s_Bytes).Disassemble().Split('\n')));

            return s_Contract;
        }
        catch (Exception s_Exception)
        {
            Log($"Camo: contract unreadable ({s_Exception.Message}).");
            return null;
        }
    }

    /// <summary>The studio's form, shown in place of the canvas when simple mode is on.</summary>
    private UIElement? m_CamoSimplePanel;

    /// <summary>The palette column's own size, remembered so turning simple mode off restores it exactly.</summary>
    private (GridLength Width, double MinWidth)? m_PaletteWidth;

    /// <summary>Camo mode hides the variation picker for good, not just once.</summary>
    private bool m_CamoHidesVariation;

    /// <summary>Which shader the contract in hand belongs to, so a different preset re-reads it.</summary>
    private string? m_CamoContractShader;

    /// <summary>Rises with each mesh selection; an older, still-running one checks it and gives up.</summary>
    private int m_MeshPickTicket;

    /// <summary>
    /// Hands the window the simple-mode panel and shows or hides it.
    ///
    /// It goes in the CANVAS's own grid cell, on top of it: the two are alternatives, and giving the form
    /// its own column would leave a graph nobody is looking at taking up half the window. The node palette
    /// goes with it — in simple mode there are no nodes to place.
    /// </summary>
    internal void SetCamoSimpleMode(bool p_On, UIElement? p_Panel = null)
    {
        if (p_Panel != null && !ReferenceEquals(m_CamoSimplePanel, p_Panel))
        {
            m_CamoSimplePanel = p_Panel;
            Grid.SetRow((FrameworkElement) p_Panel, Grid.GetRow(Canvas));
            Grid.SetColumn((FrameworkElement) p_Panel, Grid.GetColumn(Canvas));
            ((Grid) Canvas.Parent).Children.Add(p_Panel);
        }

        if (m_CamoSimplePanel == null)
            return;

        m_CamoSimplePanel.Visibility = p_On ? Visibility.Visible : Visibility.Collapsed;
        Canvas.Visibility = p_On ? Visibility.Collapsed : Visibility.Visible;
        LeftPanel.Visibility = p_On ? Visibility.Collapsed : Visibility.Visible;

        // ⛔ HIDING THE PANEL IS NOT ENOUGH — its COLUMN keeps its width (and its MinWidth), so the palette's
        // 210 px stayed on screen as a blank white band. Both have to go, and both have to come back.
        var s_Body = (Grid) Canvas.Parent;
        var s_Palette = s_Body.ColumnDefinitions[Grid.GetColumn(LeftPanel)];
        var s_Splitter = s_Body.ColumnDefinitions[Grid.GetColumn(LeftSplitter)];

        m_PaletteWidth ??= (s_Palette.Width, s_Palette.MinWidth);

        s_Palette.MinWidth = p_On ? 0 : m_PaletteWidth.Value.MinWidth;
        s_Palette.Width = p_On ? new GridLength(0) : m_PaletteWidth.Value.Width;
        s_Splitter.Width = p_On ? new GridLength(0) : new GridLength(5);
        LeftSplitter.Visibility = p_On ? Visibility.Collapsed : Visibility.Visible;

        if (p_On)
            CamoSimpleShown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the simple panel comes on screen AND whenever the camo on the canvas changes (a tab
    /// switch), so the studio fills the form with that camo and puts its settings back on the preview.
    /// </summary>
    internal event EventHandler? CamoSimpleShown;

    /// <summary>In camo mode: anything the studio wants said about a weapon the moment it is picked.</summary>
    private Func<string, string?>? m_CamoNoteFor;

    /// <summary>
    /// Camo Studio only: the entries of the accessory picker under a weapon (the weapon itself first, then
    /// its optics / underbarrel / accessory unlocks in the game's order). An entry with an empty mesh is an
    /// unlock with nothing visible to paint; it is listed so the user sees it is there, not hidden.
    /// </summary>
    private Func<string, IReadOnlyList<CamoWeapon>>? m_CamoAccessoriesOf;

    /// <summary>Camo Studio only: the weapon (its preview mesh) an accessory mesh hangs from; null for a weapon body.</summary>
    private Func<string, string?>? m_CamoWeaponOfAccessory;

    /// <summary>Filling the accessory list raises SelectionChanged; without this every fill would re-pick a mesh.</summary>
    private bool m_AccessoryListFilling;

    /// <summary>
    /// The accessory picker's only entry until a subject is picked — it explains itself rather than open
    /// empty. It names the FAMILY's subject: on the Vehicles tab it must not say "weapon".
    /// </summary>
    private string AccessoryPlaceholder =>
        $"(pick a {m_CamoFamily?.SubjectWord ?? "weapon"} above first)";

    /// <summary>
    /// Camo Studio only: the dumped sections of a mesh with the game's own material IDs, in dump order (the
    /// order the preview holds them), read from the cache logs. Empty for a mesh no log covers.
    /// </summary>
    private Func<string, IReadOnlyList<(int MaterialId, string Shader)>>? m_CamoSectionMaterialsOf;

    /// <summary>Filling the material list raises SelectionChanged and the boxes' Checked; without this a fill would write the document.</summary>
    private bool m_MaterialListFilling;

    /// <summary>One material of the object on screen, as the material picker lists it.</summary>
    internal sealed class MaterialEntry
    {
        /// <summary>Index into the preview's section list.</summary>
        public int Section { get; init; }

        /// <summary>The game's material ID: its index in the mesh's material list (the dump order when no log names it).</summary>
        public int MaterialId { get; init; }

        public string Name { get; init; } = "";
        public string Shader { get; init; } = "";

        /// <summary>Whether the material wears a weapon preset, i.e. the camo CAN go on it.</summary>
        public bool TakesCamo { get; init; }

        /// <summary>Whether the material ID came from the dump log (else it is the section's order, and says so).</summary>
        public bool IdKnown { get; init; }

        /// <summary>Whether this camo carries an edited graph for the material — it ships that shader at the bake.</summary>
        public bool Edited { get; set; }

        public override string ToString() =>
            $"#{MaterialId}{(IdKnown ? "" : "?")}  {(Name.Length > 0 ? Name : "(unnamed)")} - {Shader.Split('/')[^1]}" +
            (TakesCamo ? "  [camo]" : "  [keeps its own shader]") + (Edited ? "  [edited]" : "");
    }

    /// <summary>
    /// Refills the material picker from the sections of the loaded mesh: one entry per section with the
    /// game's material ID (the cache log's, else the order), what it wears and whether the camo can go on
    /// it. Silent; the boxes follow the document for the first entry.
    /// </summary>
    private void FillMaterialList()
    {
        if (!CamoMode || m_CamoSectionMaterialsOf == null)
            return;

        // Another object (or another tab) on screen: a material's graph left on the canvas was the previous
        // object's — it is saved and the camo's graph comes back before anything is dressed.
        if (m_MaterialEditKey != null)
        {
            SaveEditedMaterial();
            LeaveMaterialEdit(false);
        }

        var s_Mesh = m_PreviewMesh ?? "";
        var s_Sections = m_Preview.MeshSections;
        var s_Known = s_Mesh.Length > 0 ? m_CamoSectionMaterialsOf(s_Mesh) : Array.Empty<(int, string)>();

        // The log's list is trusted when it is a PREFIX of the sections: one entry per section of the mesh
        // itself, same shaders, in order. ⛔ It used to demand the SAME LENGTH, and the moment the view drew
        // something the mesh does not hold — a tracked vehicle's two belts, appended after its own sections —
        // the lists differed in length and EVERY material id was dropped (the picker fell back to the section
        // order and the panel showed "#0?"), taking the per-material dressing with it.
        var s_Match = s_Known.Count <= s_Sections.Count &&
                      s_Known.Zip(s_Sections).All(p_P => p_P.Second.Shader.Equals(p_P.First.Shader, StringComparison.OrdinalIgnoreCase));

        // A refill for the SAME object (a re-dress, a tab switch) keeps the material the user is on.
        var s_Keep = string.Equals(m_MaterialListMesh, s_Mesh, StringComparison.OrdinalIgnoreCase)
            ? (MaterialList.SelectedItem as MaterialEntry)?.MaterialId
            : null;
        m_MaterialListMesh = s_Mesh;

        m_MaterialListFilling = true;
        try
        {
            MaterialList.Items.Clear();
            MaterialList.ItemTemplate = null;
            for (var i = 0; i < s_Sections.Count; i++)
            {
                // The rest of a soldier drawn around the part being authored is not this subject's to list: its materials belong
                // to the part that owns them, and are picked there.
                if (s_Sections[i].Context.Length > 0)
                    continue;

                // Beyond the mesh's own sections there is no id to know: that is the belt, which the game
                // carries as a mesh of its own and no material list of this mesh describes.
                var s_HasId = s_Match && i < s_Known.Count;
                var s_MaterialId = s_HasId ? s_Known[i].MaterialId : i;
                MaterialList.Items.Add(new MaterialEntry
                {
                    Section = i,
                    MaterialId = s_MaterialId,
                    IdKnown = s_HasId,
                    Name = s_Sections[i].Material,
                    Shader = s_Sections[i].Shader,
                    TakesCamo = m_Preview.IsTargetSection(s_Sections[i]),
                    Edited = DocumentGraph.MaterialGraphOf(s_Mesh, s_MaterialId) != null,
                });
            }

            MaterialPanel.Visibility = MaterialList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var s_Kept = s_Keep == null
                ? null
                : MaterialList.Items.OfType<MaterialEntry>().FirstOrDefault(p_M => p_M.MaterialId == s_Keep.Value);
            if (s_Kept != null)
                MaterialList.SelectedItem = s_Kept;
            else
            {
                MaterialIsolate.IsChecked = false;
                m_Preview.IsolatedSection = null;

                // ⭐ …on the first material of the DOCUMENT's own preset when the first section is a PAINTED material of another one
                // (2026-09-26, the DPV: its section 0 is the CROWS turret on the base mud, a sibling the document draws; opening on it
                // made the turret's art and numbers the object's — the body drawn with the turret's textures, the form's wear at its
                // 150). Every other catalogued object opens on section 0 as before (measured: the DPV is the only one; the Su-25's
                // section 0, its cockpit, is not painted and stays).
                var s_First = MaterialList.Items.OfType<MaterialEntry>().FirstOrDefault();
                var s_OwnPreset = s_First is { TakesCamo: true } && !s_First.Shader.Equals(CamoTarget, StringComparison.OrdinalIgnoreCase)
                    ? MaterialList.Items.OfType<MaterialEntry>()
                        .FirstOrDefault(p_M => p_M.TakesCamo && p_M.Shader.Equals(CamoTarget, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (s_OwnPreset != null)
                    MaterialList.SelectedItem = s_OwnPreset;
                else
                    MaterialList.SelectedIndex = MaterialList.Items.Count > 0 ? 0 : -1;
            }

            SyncMaterialBoxes();
        }
        finally
        {
            m_MaterialListFilling = false;
        }

        ApplyMaterialChoices();

        // ⛔ AND THE PANEL'S THUMBNAILS FOR THE ENTRY THAT IS ALREADY SELECTED. Picking a material loads its
        // own textures (OnMaterialPicked), but the FIRST entry is selected by this fill, which raises no
        // SelectionChanged — so the material a vehicle OPENS on kept the object's single set. Measured: the
        // LAV-25's hull showed the ATGM launchers' art until another material was clicked and back.
        if (MaterialList.SelectedItem is MaterialEntry s_Opened)
            LoadPanelTexturesFor(s_Opened);

        // ⭐ AN ACCESSORY OPENS ON ITS OWN GRAPH: the camo's graph is the weapon's
        // first-person preset and an accessory does not draw with it, so picking an accessory and finding
        // that graph on the canvas offers constants that reach nothing. The list's first painted material is
        // the accessory's body; its graph is the one to edit.
        // ⛔⛔ …EXCEPT WHILE THE PICKER SAYS "AS SHIPPED" (reported twice: as shipped was still not fixed).
        // Fixing FactoryPresetOf was not enough: the factory graph went up and
        // THIS line put the accessory's camo preset straight back on the canvas — the log says it in order
        // ("the canvas shows 'weaponpresetfp'" and then "editing material #1 … the graph of 'weaponpreset3p'")
        // — so the view kept drawing the camo's preset and the piece looked nothing like the game.
        // "As shipped" means the GAME's object: no material of ours is opened over it, whatever it is.
        if (m_MaterialEditKey == null && !CamoFactoryLook() && m_CamoWeaponOfAccessory?.Invoke(s_Mesh) != null &&
            MaterialList.SelectedItem is MaterialEntry s_Body && s_Body.TakesCamo &&
            !DocumentGraph.IsMaterialOff(s_Mesh, s_Body.MaterialId))
            ShowMaterialGraph(s_Body);

        // ⛔ THE FIRST LOOK AT AN OBJECT, WHICH IS THE ONE THE USER SEES: the camo compiles on a debounce, so it
        // can compile while MeshSections still holds the PREVIOUS object — the dress refuses a section list
        // that is not this mesh's, clears itself, and nothing recompiles afterwards, so the object stays
        // undressed until the user edits something (any edit made it look right). This is
        // the point where the sections ARE this object's, so the dress is asserted from here too.
        RefreshMaterialDress();
    }

    /// <summary>The mesh the material picker was last filled for, so a refill of the same one keeps the selection.</summary>
    private string? m_MaterialListMesh;

    /// <summary>The boxes say what the document says about the selected material.</summary>
    private void SyncMaterialBoxes()
    {
        var s_Was = m_MaterialListFilling;
        m_MaterialListFilling = true;
        try
        {
            if (MaterialList.SelectedItem is not MaterialEntry s_Entry || m_PreviewMesh is not { Length: > 0 } s_Mesh)
            {
                MaterialPaint.IsEnabled = false;
                MaterialPaint.IsChecked = false;
                return;
            }

            MaterialPaint.IsEnabled = s_Entry.TakesCamo;
            MaterialPaint.IsChecked = s_Entry.TakesCamo && !DocumentGraph.IsMaterialOff(s_Mesh, s_Entry.MaterialId);
            MaterialPaint.Content = s_Entry.TakesCamo
                ? "This material takes the camo"
                : "This material never takes the camo (its own shader, as shipped)";
        }
        finally
        {
            m_MaterialListFilling = s_Was;
        }
    }

    /// <summary>
    /// Puts the document's per-material choice on the view: every own section whose material ID the
    /// document keeps off draws with the game's bytecode of its own shader — registered here if it is not
    /// yet — and the rest take the authored shader. Nothing to do outside camo mode.
    /// </summary>
    private void ApplyMaterialChoices()
    {
        if (!CamoMode || m_PreviewMesh is not { Length: > 0 } s_Mesh)
            return;

        var s_Off = new HashSet<int>();
        foreach (var s_Entry in MaterialList.Items.OfType<MaterialEntry>())
            if (s_Entry.TakesCamo && DocumentGraph.IsMaterialOff(s_Mesh, s_Entry.MaterialId))
                s_Off.Add(s_Entry.Section);

        // The shipped shaders of the sections kept off, from the cache (idempotent; nothing mounts).
        if (s_Off.Count > 0)
            RegisterFactoryShaders(null);

        m_Preview.SectionsOff = s_Off;
    }

    private void OnMaterialPicked(object p_Sender, SelectionChangedEventArgs p_Args)
    {
        if (m_MaterialListFilling)
            return;

        SyncMaterialBoxes();
        if (MaterialList.SelectedItem is MaterialEntry s_Entry)
        {
            m_Preview.IsolatedSection = MaterialIsolate.IsChecked == true ? s_Entry.Section : null;
            Log($"Material #{s_Entry.MaterialId} '{s_Entry.Name}' wears '{s_Entry.Shader.Split('/')[^1]}' — " +
                (s_Entry.TakesCamo
                    ? DocumentGraph.IsMaterialOff(m_PreviewMesh ?? "", s_Entry.MaterialId)
                        ? "a weapon preset; this camo is kept OFF it (it ships as is)."
                        : "a weapon preset; this camo goes on it."
                    : "not a weapon preset; it keeps its own shader in the game, whatever the camo."));

            // The canvas follows the picked material: its own graph, or the camo's for a painted one.
            ShowMaterialGraph(s_Entry);

            // ⛔ AND THE THUMBNAILS FOLLOW IT TOO — AFTER that, never before. The panel used to show ONE set
            // for the whole object, so on a vehicle every material displayed the same textures (the LAV-25
            // showed the ATGM launchers' art under all five). The 3D already draws each material with its
            // own; the panel has to agree, or it is an instrument that lies to whoever checks by looking.
            // ⛔ Put BEFORE ShowMaterialGraph it did nothing on the materials that share the tab's target:
            // that call re-dresses with the object's single set and overwrote these. Measured — the CROWS
            // (its own preset, its own path) took it and the hull did not.
            LoadPanelTexturesFor(s_Entry);
        }
    }

    /// <summary>
    /// The texture set (and numbers) the panel and the 3D show for a material of the object on screen.
    ///
    /// ⛔⛔ A PAINTED MATERIAL SHOWS THE CAMO'S SET, NOT THE SET OF THE SHADER IT SHIPS WITH. Its canvas is the
    /// camo document — the tab's preset, with the picked native camo on the camo register — and the set has
    /// to be that preset's (its registers, its camo) for THIS material's own art. Loading the material's
    /// shipped set instead (2026-09-21 to 2026-09-22) put, on every weapon whose body ships a NoCamo preset
    /// (27 of 59), the NoCamo layout over the camo's: t2 stopped being the camo and became the SPECULAR, the
    /// numbers lost CamoTiling/WearAmount — an M416 under ABU drew purple with no camo, and picking a native
    /// camo changed nothing on screen. "As shipped" controls could not see it: that look draws the game's
    /// bytecode. `--nativetest M416` did (8 checks), which nobody had run since.
    /// A material that keeps its own shader (or is kept off) shows its own set — its own graph is what the
    /// canvas holds for it. While a material's graph is being EDITED the edit dressed the canvas already
    /// (DressMaterialGraph, an accessory's layering included) and nothing goes over it from here.
    /// </summary>
    private void LoadPanelTexturesFor(MaterialEntry p_Entry)
    {
        if (m_CamoSlotsForMaterial == null || m_PreviewMesh is not { Length: > 0 } s_Mesh || p_Entry.MaterialId < 0)
            return;

        var s_Painted = p_Entry.TakesCamo && !DocumentGraph.IsMaterialOff(s_Mesh, p_Entry.MaterialId);

        // A material's OWN graph is on the canvas. A painted one there is an accessory's body, dressed with its
        // layering (native camo, host wear, pattern) by DressMaterialGraph: nothing goes over it. An unpainted
        // one was dressed by shader; its own material's art is more exact where two of them share a shader.
        if (m_MaterialEditKey != null)
        {
            if (s_Painted || !string.Equals(m_MaterialEditKey, ShaderGraph.MaterialKey(s_Mesh, p_Entry.MaterialId), StringComparison.OrdinalIgnoreCase))
                return;

            if (m_CamoSlotsForMaterial(s_Mesh, p_Entry.Shader, null, p_Entry.MaterialId) is { Count: > 0 } s_Own)
                LoadCamoTextures(s_Own, m_CamoValuesForMaterial?.Invoke(s_Mesh, p_Entry.Shader, null, p_Entry.MaterialId));

            return;
        }

        // The DOCUMENT is on the canvas. An unpainted material selected there (an object that opens on one,
        // the pilot's cockpit mesh does) changes nothing: the set on the canvas is the camo's, and loading that
        // material's own set as the object's main set would hand the painted sections another shader's art.
        // As shipped, a WEAPON's painted material has the game's no-camo graph on the canvas, dressed by
        // SyncFactoryLook. ⛔ A VEHICLE's has not — it has no twin, the document stays — and asking "as shipped?"
        // here instead of "is the no-camo graph up?" left every painted material of a LAV-25 showing the ATGM
        // launchers' textures, the hull included (the graph did not update).
        if (!s_Painted || FactoryGraphShown)
            return;

        var s_Target = TargetShaderBox.Text.Trim();
        var s_Native = DocumentGraph.NativeCamo;
        if (s_Target.Length == 0)
            return;

        if (m_CamoSlotsForMaterial(s_Mesh, s_Target, s_Native, p_Entry.MaterialId) is { Count: > 0 } s_Slots)
            LoadCamoTextures(s_Slots, m_CamoValuesForMaterial?.Invoke(s_Mesh, s_Target, s_Native, p_Entry.MaterialId));
    }

    private void OnMaterialPaintChanged(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_MaterialListFilling || MaterialList.SelectedItem is not MaterialEntry s_Entry ||
            !s_Entry.TakesCamo || m_PreviewMesh is not { Length: > 0 } s_Mesh)
            return;

        var s_Off = MaterialPaint.IsChecked != true;
        DocumentGraph.SetMaterialOff(s_Mesh, s_Entry.MaterialId, s_Off);
        ApplyMaterialChoices();
        Log(s_Off
            ? $"Camo: kept OFF material #{s_Entry.MaterialId} '{s_Entry.Name}' of {s_Mesh.Split('/')[^1]} — it ships as is; the bake leaves it alone."
            : $"Camo: material #{s_Entry.MaterialId} '{s_Entry.Name}' of {s_Mesh.Split('/')[^1]} takes the camo again.");

        // Kept off, the material shows the shader it ships with; painted again, the camo's graph.
        ShowMaterialGraph(s_Entry);
    }

    private void OnMaterialIsolateChanged(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_MaterialListFilling)
            return;

        m_Preview.IsolatedSection = MaterialIsolate.IsChecked == true && MaterialList.SelectedItem is MaterialEntry s_Entry
            ? s_Entry.Section
            : null;
    }

    /// <summary>What the window shows after a pick, so a driver can hold it against the weapon it asked for.</summary>
    internal sealed class CamoSnapshotView
    {
        public string? Picked { get; init; }
        public string? LoadedMeshFile { get; init; }
        public string Target { get; init; } = "";

        /// <summary>The preset the CAMO document is authored on (CamoTarget) — the same as Target except while one of the object's
        /// other materials is on the canvas, when Target (the box) names that material's shader. What a bake asks.</summary>
        public string DocumentTarget { get; init; } = "";
        public string Variation { get; init; } = "";
        public string? ContractShader { get; init; }
        public int TabCount { get; init; }
        public int ActiveTab { get; init; }

        /// <summary>Sections the authored shader draws — the target's own and the ones it replaces.</summary>
        public int OwnSections { get; init; }

        /// <summary>Whether those sections are being drawn with the game's own shaders instead (the weapon as it ships, no camo).</summary>
        public bool FactoryLook { get; init; }

        /// <summary>Opaque sections the next frame draws with the neutral grey stand-in (no shader found for them).</summary>
        public List<string> NeutralSections { get; init; } = new();

        /// <summary>Under the factory look, the registered shader every own section draws with (the body's preset recompiled with the sticker layer), or null (each section's own bytecode).</summary>
        public string? FactoryShader { get; init; }

        /// <summary>The worn preset whose graph the canvas shows in place of the camo's, or null (the canvas holds the camo).</summary>
        public string? FactoryGraph { get; init; }

        /// <summary>Whether the native-camo picker can be reached: its panel and the column it lives in are both visible.</summary>
        public bool NativePickerVisible { get; init; }

        /// <summary>The target of the graph ON THE CANVAS — the worn preset while as shipped, the camo's preset otherwise.</summary>
        public string CanvasTarget { get; init; } = "";

        /// <summary>The line under the preview: "Preview paused: …" when the graph did not compile.</summary>
        public string PreviewStatus { get; init; } = "";

        /// <summary>Sticker mode: whether it is on, the layer's register (null = the graph samples none), and how many placements the graph holds.</summary>
        public bool StickerMode { get; init; }
        public int? StickerRegister { get; init; }
        public int StickerCount { get; init; }
        public int SelectedSticker { get; init; } = -1;

        /// <summary>The mesh of the weapon selected in the picker, which must follow the tab.</summary>
        public string? SelectedWeapon { get; init; }

        /// <summary>The mesh of the entry selected in the accessory picker (the weapon's own for its first entry), or null with no entry.</summary>
        public string? SelectedAccessory { get; init; }

        /// <summary>
        /// The PAIR that names the piece on screen: the weapon's folder and the attachment's tag, both empty
        /// on the weapon's own entry. ⛔ The mesh above cannot do this job — a suppressor, a rail or a kobra
        /// is the SAME mesh on dozens of weapons (measured 2026-09-20, it reopened an AEK's file on the A91),
        /// and what the bake needs to know is which weapon it is being authored under.
        /// </summary>
        public string SelectedAccessoryWeapon { get; init; } = "";
        public string SelectedAccessoryTag { get; init; } = "";

        /// <summary>The entries the accessory picker offers, in order — the weapon first, then its rows' unlocks.</summary>
        public IReadOnlyList<string> AccessoryEntries { get; init; } = Array.Empty<string>();

        /// <summary>The family on screen ("weapons", "vehicles"), the tabs offered, and the words the two
        /// pickers are wearing — what a seam holds a tab press against.</summary>
        public string? Family { get; init; }

        public IReadOnlyList<string> Families { get; init; } = Array.Empty<string>();

        public string SubjectTitle { get; init; } = "";

        public string AccessoryTitle { get; init; } = "";

        /// <summary>The entries the SUBJECT picker offers, in order.</summary>
        public IReadOnlyList<string> SubjectEntries { get; init; } = Array.Empty<string>();

        /// <summary>The group buttons on screen over the subject picker (the soldiers' "US", "RU"), and the pressed one; empty / null without.</summary>
        public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();

        public string? Group { get; init; }

        /// <summary>The part buttons on screen (a soldier's "Head", "Upper body", "Lower body"), the pressed one, and the key of the document on the canvas.</summary>
        public IReadOnlyList<string> Parts { get; init; } = Array.Empty<string>();

        public string? Part { get; init; }

        public string? DocumentSubject { get; init; }

        /// <summary>Sections drawn as CONTEXT around the subject (the rest of a soldier), and how many of them wear their own material's art.</summary>
        public int ContextSections { get; init; }

        public int ContextSectionsWithArt { get; init; }

        /// <summary>Context sections drawn with the document of their own part (the rest of a soldier wearing the skin).</summary>
        public int ContextSectionsDressed { get; init; }

        /// <summary>The material picker's entries for the object on screen, as shown.</summary>
        public IReadOnlyList<string> Materials { get; init; } = Array.Empty<string>();

        /// <summary>Section indices the view keeps the camo off (the document's choice for this mesh).</summary>
        public IReadOnlyList<int> SectionsOff { get; init; } = Array.Empty<int>();

        /// <summary>The material ("mesh|id") whose own graph is on the canvas, being edited; null on the camo's.</summary>
        public string? MaterialEditing { get; init; }

        /// <summary>Every material of this camo that carries an edited graph ("mesh|id"), in key order.</summary>
        public IReadOnlyList<string> EditedMaterials { get; init; } = Array.Empty<string>();

        /// <summary>Register -> the game texture NAME the properties panel would show beside its thumbnail.</summary>
        public IReadOnlyDictionary<string, string> TextureNames { get; init; } = new Dictionary<string, string>();

        /// <summary>Parameter -> value as the preview runs them (the weapon's material, the camo's live values on top).</summary>
        public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

        /// <summary>Register -> the custom texture file a node of the graph on the canvas samples instead of the game's.</summary>
        public IReadOnlyDictionary<string, string> CustomTextures { get; init; } = new Dictionary<string, string>();

        /// <summary>The notes carried by the nodes of the graph on the canvas, keyed by node kind and name.</summary>
        public IReadOnlyDictionary<string, string> NodeNotes { get; init; } = new Dictionary<string, string>();

        /// <summary>Bare constant name -> the preview value its node carries in the graph on the canvas.</summary>
        public IReadOnlyDictionary<string, string> PreviewValues { get; init; } = new Dictionary<string, string>();

        /// <summary>Bare constant name -> the text the properties box shows for that preview value.</summary>
        public IReadOnlyDictionary<string, string> PreviewValuesShown { get; init; } = new Dictionary<string, string>();

        /// <summary>Every external constant of the graph -> the text its value box shows (the game's value until changed).</summary>
        public IReadOnlyDictionary<string, string> ValueBoxes { get; init; } = new Dictionary<string, string>();

        /// <summary>What the preview draws: "Mesh", "Cube", "Sphere", "Cylinder" or "Plane".</summary>
        public string Shape { get; init; } = "";

        /// <summary>The native camo the graph on the canvas starts from (its key), or null for the weapon as shipped.</summary>
        public string? NativeCamo { get; init; }

        /// <summary>The native camo the picker shows selected (its key), or null for "as shipped".</summary>
        public string? NativeCamoShown { get; init; }

        /// <summary>Whether the mesh section (the weapon list) is on screen.</summary>
        public bool MeshMode { get; init; }

        /// <summary>How many authored shaders the preview has accepted so far.</summary>
        public int ShaderVersion { get; init; }

        /// <summary>Register -> cache file name (no extension) of the thumbnail in it.</summary>
        public IReadOnlyDictionary<string, string> Thumbnails { get; init; } = new Dictionary<string, string>();

        /// <summary>
        /// Register -> whether the preview samples that texture through an sRGB view (the game's own flag,
        /// read from the .dds): the input the wear mask depends on, so a driver can check it reached the view.
        /// </summary>
        public IReadOnlyDictionary<string, bool> TextureSrgb { get; init; } = new Dictionary<string, bool>();

        public string Output { get; init; } = "";
    }

    /// <summary>
    /// The albedo target of the last frame drawn, for a driver to hash: the one surface the authored shader
    /// writes directly, so "the graph changed and the picture did not" is measurable without a screen.
    /// </summary>
    /// <summary>
    /// The preview itself, for a seam that has to look at WHAT IS DRAWN rather than at the model — isolating
    /// composite parts to see which ones a piece is made of, for instance. Seams only; the window's own code
    /// goes through the members above.
    /// </summary>
    internal View.ShaderPreview Preview => m_Preview;

    /// <summary>Asks for a redraw from a seam, the same one an edit schedules.</summary>
    internal void SchedulePreviewNow() => SchedulePreview();

    internal byte[]? CaptureAlbedo() => m_Preview.CaptureTarget(1);

    /// <summary>One g-buffer target of the last frame as the pixel shader wrote it (RGBA) — RT0 is the world normal — for a driver.</summary>
    internal byte[]? CaptureGBuffer(int p_Target) => m_Preview.CaptureTarget(p_Target);

    /// <summary>The preview as the user sees it — one frame rendered and read back — as BGRA bytes.</summary>
    internal byte[]? CaptureShown(out int p_Width, out int p_Height) =>
        m_Preview.CaptureShownFrame(out p_Width, out p_Height);

    /// <summary>Dollies the preview camera by a factor (0.5 = twice as close) — what the wheel does, for a driver's pictures.</summary>
    internal void ZoomPreview(float p_Factor) =>
        m_Preview.Distance = Math.Clamp(m_Preview.Distance * p_Factor, 0.05f, 100f);

    /// <summary>
    /// Gives the hidden preview panel a real size before its device is created. Without a shown window the
    /// panel keeps its default 200×100, which is too small to look at; a driver sets this before pumping.
    /// </summary>
    internal void SetHeadlessPreviewSize(int p_Width, int p_Height)
    {
        if (m_PreviewPanel == null || m_PreviewInitialised)
            return;

        m_PreviewPanel.Width = p_Width;
        m_PreviewPanel.Height = p_Height;
    }

    internal CamoSnapshotView CamoSnapshot() => new()
    {
        Picked = m_PreviewMesh,
        LoadedMeshFile = m_Preview.MeshPath == null ? null : Path.GetFileName(m_Preview.MeshPath),
        Target = TargetShaderBox.Text.Trim(),
        DocumentTarget = CamoTarget,
        Variation = m_Variation,
        ContractShader = m_CamoContractShader,
        TabCount = m_Tabs.Count,
        ActiveTab = m_ActiveTab,
        OwnSections = m_Preview.MeshSections.Count(m_Preview.IsTargetSection),
        FactoryLook = m_Preview.FactoryLook,
        NeutralSections = m_Preview.NeutralSections(),
        FactoryShader = m_Preview.FactoryShader,
        PreviewStatus = PreviewStatus.Text,
        StickerMode = StickerMode,
        StickerRegister = DocumentGraph.StickerRegister,
        StickerCount = DocumentGraph.Stickers?.Count ?? 0,
        FactoryGraph = m_ShownFactoryPreset,
        NativePickerVisible = NativeCamoPanel.Visibility == Visibility.Visible && LeftPanel.Visibility == Visibility.Visible,
        CanvasTarget = Canvas.Graph.TargetShader,
        SelectedSticker = SelectedSticker,
        ShaderVersion = m_Preview.AuthoredShaderVersion,
        SelectedWeapon = (MeshList.SelectedItem as CamoWeapon)?.Mesh,
        SelectedAccessory = (AccessoryList.SelectedItem as CamoWeapon)?.Mesh,
        SelectedAccessoryWeapon = (AccessoryList.SelectedItem as CamoWeapon)?.Weapon ?? "",
        SelectedAccessoryTag = (AccessoryList.SelectedItem as CamoWeapon)?.Tag ?? "",
        AccessoryEntries = AccessoryPanel.Visibility == Visibility.Visible
            ? AccessoryList.Items.OfType<CamoWeapon>().Select(p_A => p_A.Display).ToList()
            : Array.Empty<string>(),
        Family = CamoFamilyKey,
        Families = CamoFamilyKeys,
        SubjectTitle = MeshListTitle.Text,
        AccessoryTitle = AccessoryListTitle.Text,
        SubjectEntries = MeshList.Items.OfType<CamoWeapon>().Select(p_S => p_S.Display).ToList(),
        Groups = CamoGroupPanel.Visibility == Visibility.Visible
            ? CamoGroupPanel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Select(p_T => p_T.Tag as string ?? "").ToList()
            : Array.Empty<string>(),
        Group = CamoGroupKey,
        Parts = CamoPartPanel.Visibility == Visibility.Visible
            ? CamoPartPanel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Select(p_T => p_T.Tag as string ?? "").ToList()
            : Array.Empty<string>(),
        Part = CamoPartPanel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().FirstOrDefault(p_T => p_T.IsChecked == true)?.Tag as string,
        DocumentSubject = m_DocumentSubject,
        ContextSections = m_Preview.MeshSections.Count(p_S => p_S.Context.Length > 0),
        ContextSectionsWithArt = Enumerable.Range(0, m_Preview.MeshSections.Count)
            .Count(p_I => m_Preview.MeshSections[p_I].Context.Length > 0 && m_Preview.SectionArt.ContainsKey(p_I)),
        ContextSectionsDressed = m_Preview.ContextDress.Count,
        Materials = MaterialList.Items.OfType<MaterialEntry>().Select(p_M => p_M.ToString()).ToList(),
        SectionsOff = m_Preview.SectionsOff.OrderBy(p_S => p_S).ToList(),
        MaterialEditing = m_MaterialEditKey,
        EditedMaterials = DocumentGraph.MaterialGraphs?.Keys.OrderBy(p_K => p_K, StringComparer.OrdinalIgnoreCase).ToList()
                          ?? (IReadOnlyList<string>) Array.Empty<string>(),
        TextureNames = m_TexturePreviews.Keys.ToDictionary(p_R => p_R, p_R => GameTextureNameFor(p_R) ?? ""),
        Values = m_CamoValueNames == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(m_CamoValueNames, StringComparer.OrdinalIgnoreCase),
        // The camo DOCUMENT's own facts (what it samples, what it says, what it feeds), whichever graph the
        // canvas is showing; the boxes below are the panel's, i.e. the canvas's.
        CustomTextures = DocumentGraph.Nodes
            .Where(p_N => TextureNodeKinds.Contains(p_N.Kind) &&
                          p_N.Params.TryGetValue("CustomTexture", out var s_Custom) && s_Custom.Length > 0)
            .GroupBy(p_N => p_N.GetParam("Register"))
            .Where(p_G => !string.IsNullOrWhiteSpace(p_G.Key))
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Params["CustomTexture"]),
        NodeNotes = DocumentGraph.Nodes
            .Where(p_N => !string.IsNullOrWhiteSpace(p_N.Comment))
            .GroupBy(p_N => TextureNodeKinds.Contains(p_N.Kind)
                ? $"{p_N.Kind}:t{p_N.GetParam("Register")}"
                : $"{p_N.Kind}:{p_N.GetParam("Name")}")
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Comment!),
        PreviewValues = DocumentGraph.Nodes
            .Where(p_N => p_N.Kind == "ExternalConstant" &&
                          p_N.Params.TryGetValue("PreviewValue", out var s_Preview) && s_Preview.Length > 0)
            .GroupBy(BareExternalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Params["PreviewValue"], StringComparer.OrdinalIgnoreCase),
        PreviewValuesShown = Canvas.Graph.Nodes
            .Where(p_N => p_N.Kind == "ExternalConstant" &&
                          p_N.Params.TryGetValue("PreviewValue", out var s_Shown) && s_Shown.Length > 0)
            .GroupBy(BareExternalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p_G => p_G.Key, p_G => ExternalValueDisplay(p_G.First()), StringComparer.OrdinalIgnoreCase),
        ValueBoxes = Canvas.Graph.Nodes
            .Where(p_N => p_N.Kind == "ExternalConstant")
            .GroupBy(BareExternalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p_G => p_G.Key, p_G => ExternalValueDisplay(p_G.First()), StringComparer.OrdinalIgnoreCase),
        Shape = m_Preview.Shape.ToString(),
        MeshMode = m_MeshMode,
        NativeCamo = DocumentGraph.NativeCamo,
        NativeCamoShown = (NativeCamoList.SelectedItem as NativeCamoChoice)?.Key,
        Thumbnails = m_TexturePreviews.ToDictionary(
            p_T => p_T.Key,
            p_T => (p_T.Value as BitmapImage)?.UriSource is { } s_Uri
                ? Path.GetFileNameWithoutExtension(s_Uri.LocalPath)
                : "(custom)"),
        TextureSrgb = new Dictionary<string, bool>(m_TextureSrgb),
        Output = LogBox.Text,
    };

    /// <summary>
    /// One thing a camo can be looked at on — weapons, vehicles, soldiers — and the words the pickers
    /// wear while it is the one on screen.
    ///
    /// ⛔ THE FAMILY IS NOT A MODE OF THE WINDOW. Everything below the tabs is answered through the same
    /// callbacks, by MESH: the studio knows whether a mesh is a rifle, an attachment, a tank or a tank's
    /// antenna, so the window only has to show the right list and the right words. A second set of
    /// callbacks per family is how the two halves drift apart.
    /// </summary>
    internal sealed class CamoFamily
    {
        /// <summary>Stable name a seam can ask for: "weapons", "vehicles", "soldiers".</summary>
        public string Key { get; init; } = "";

        /// <summary>The word on the tab.</summary>
        public string Display { get; init; } = "";

        public string Tooltip { get; init; } = "";

        /// <summary>What the subject picker is called here: "Preview weapon", "Preview vehicle".</summary>
        public string SubjectTitle { get; init; } = "Preview mesh";

        /// <summary>The one word for a subject — "weapon", "vehicle" — for the sentences that name it.</summary>
        public string SubjectWord { get; init; } = "weapon";

        public string SubjectHelp { get; init; } = "";

        /// <summary>What the second picker is called ("Preview accessory" stays as it is).</summary>
        public string AccessoryTitle { get; init; } = "Preview accessory";

        public string AccessoryHelp { get; init; } = "";

        /// <summary>What the subject picker lists.</summary>
        public IReadOnlyList<CamoWeapon> Subjects { get; init; } = Array.Empty<CamoWeapon>();

        /// <summary>
        /// The sides the subjects are split by — a row of buttons over the picker, which then lists only the pressed one's (the
        /// soldiers: US / RU buttons → the 8 models). Keys match <see cref="CamoWeapon.Group"/>. Null: no row.
        /// </summary>
        public IReadOnlyList<(string Key, string Display)>? Groups { get; init; }

        /// <summary>The order the part buttons read in ("Head, upper body, lower body"); parts it does not name follow in list order.</summary>
        public IReadOnlyList<string>? PartOrder { get; init; }

        /// <summary>
        /// A foreign material's texture the game binds nothing to draws with the shader's NEUTRAL (white, a flat normal), never with the
        /// edited shader's art (ShaderPreview.NeutralEmptySlots). The soldiers' (2026-09-28); weapons and vehicles keep what they had.
        /// </summary>
        public bool NeutralForeignSlots { get; init; }

        /// <summary>What the Cockpit box says here, when not the aircraft's words (the soldiers' "First person …"); null = the XAML's.</summary>
        public string? CockpitLabel { get; init; }

        public string? CockpitTooltip { get; init; }

        /// <summary>
        /// The game's own camos offered as a starting point, or null where the game ships none — which
        /// is the vehicles' case (BF3 itself offers no camos for them, so for now they keep only
        /// "as shipped"). Null hides the picker rather than offering a list of one.
        /// </summary>
        public IReadOnlyList<(string Key, string Display)>? NativeCamos { get; init; }

        /// <summary>
        /// The native camos per GROUP, when each side has its own (the soldiers' stock looks: the US and the RU name their
        /// XP2 looks apart) — the picker lists the pressed group's. Null: <see cref="NativeCamos"/> for every group.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<(string Key, string Display)>>? GroupNativeCamos { get; init; }

        /// <summary>
        /// Set when the family is on the row but not built yet: the tab is disabled and says this. The
        /// Soldiers tab was wanted before its work started, so it must not look broken.
        /// </summary>
        public string? Unavailable { get; init; }
    }

    /// <summary>One weapon in the camo picker: what the user reads, and the mesh it stands for.</summary>
    internal sealed class CamoWeapon
    {
        public string Display { get; init; } = "";
        public string Mesh { get; init; } = "";

        /// <summary>For an accessory entry: the weapon's folder and the attachment's short name, which is what
        /// its own numbers are kept under (ShaderGraph.AccessoryKey). Empty on a weapon's own entry.</summary>
        public string Weapon { get; init; } = "";
        public string Tag { get; init; } = "";

        /// <summary>For a subject of a family with groups (<see cref="CamoFamily.Groups"/>): the group it is listed under ("US", "RU").</summary>
        public string Group { get; init; } = "";

        /// <summary>
        /// For an entry of the accessory picker that is a PART of the subject (a soldier's "Head", "Upper body", "Lower body"): the part
        /// button it stands under — the picker then shows as a row of those buttons. Empty for everything else.
        /// </summary>
        public string Part { get; init; } = "";

        /// <summary>
        /// The document this entry is edited under, when not its mesh's own (the first-person model of a soldier is the SAME mesh under two
        /// parts: its sleeves under the upper body's document, its trousers under the lower body's). Empty = the mesh's key, as always.
        /// </summary>
        public string DocumentKey { get; init; } = "";

        /// <summary>
        /// Material ids of this entry's mesh that are NOT this entry's to edit: drawn around it as they ship, never listed, never the edited
        /// shader's (the trousers of the first-person model while its upper body is edited, and the other way round). Empty = all its own.
        /// </summary>
        public IReadOnlyList<int> ContextMaterials { get; init; } = Array.Empty<int>();

        /// <summary>The combo shows this. No template, no converter — the list holds what it means.</summary>
        public override string ToString() => Display;
    }

    /// <summary>In camo mode: the studio's texture set for a (mesh, shader, native camo or null), or null to use the slot map.</summary>
    /// <summary>
    /// The texture set / values of ONE material of a mesh, by the game's material id — what lets every
    /// material of an object wear the camo over its own art. Null (no studio behind the window) leaves the
    /// old single-set behaviour in place.
    /// </summary>
    private Func<string, string, string?, int, IReadOnlyDictionary<string, string>?>? m_CamoSlotsForMaterial;

    private Func<string, string, string?, int, IReadOnlyDictionary<string, string>?>? m_CamoValuesForMaterial;

    /// <summary>
    /// In camo mode: what the object on screen CARRIES that is not in its own mesh, already placed — a
    /// tracked vehicle's two running belts. Null for everything else, which is every weapon and every
    /// wheeled vehicle (their wheels are inside the body mesh).
    /// ⭐ …AND THE REST OF A SOLDIER: his helmet, head and legs around the torso being authored, each with its game
    /// mesh name in Context — drawn as it ships, with its OWN art, and never the edited shader's (see ShaderPreview.MeshSection.Context).
    /// </summary>
    private Func<string, IReadOnlyList<(string Path, float OffsetX, bool MirrorX, string Context)>?>? m_CamoExtraMeshesFor;

    private Func<string, string, string?, IReadOnlyDictionary<string, string>?>? m_CamoSlotsFor;

    /// <summary>In camo mode: the weapon's own material values for (mesh, shader or null for its preset, native camo or null), which its albedo is built from.</summary>
    private Func<string, string?, string?, IReadOnlyDictionary<string, string>?>? m_CamoValuesFor;


    /// <summary>One row of the native-camo picker: what the user reads, and the key the studio knows it by (null = as shipped).</summary>
    internal sealed class NativeCamoChoice
    {
        public string? Key { get; init; }
        public string Display { get; init; } = "";
        public override string ToString() => Display;
    }

    /// <summary>True while the native-camo picker is being filled or synced, so its handler does not re-dress.</summary>
    private bool m_NativeCamoFilling;

    /// <summary>
    /// Puts the picker on the choice the graph on the canvas carries. The choice is the DOCUMENT's: a tab
    /// switch brings its own back (it must stay in place even when the user switches tabs).
    /// </summary>
    private void SyncNativeCamoBox()
    {
        if (NativeCamoList.Items.Count == 0)
            return;

        m_NativeCamoFilling = true;
        NativeCamoList.SelectedItem = NativeCamoList.Items.OfType<NativeCamoChoice>()
            .FirstOrDefault(p_C => string.Equals(p_C.Key, DocumentGraph.NativeCamo, StringComparison.OrdinalIgnoreCase))
            ?? NativeCamoList.Items.OfType<NativeCamoChoice>().FirstOrDefault();
        m_NativeCamoFilling = false;
    }

    /// <summary>Selects a native camo in the picker by key (null = as shipped) — the user's path, so a driver takes it too.</summary>
    internal bool SelectNativeCamo(string? p_Key)
    {
        var s_Item = NativeCamoList.Items.OfType<NativeCamoChoice>()
            .FirstOrDefault(p_C => string.Equals(p_C.Key, p_Key, StringComparison.OrdinalIgnoreCase));
        if (s_Item == null)
            return false;

        NativeCamoList.SelectedItem = s_Item;
        return true;
    }

    private void OnNativeCamoPicked(object p_Sender, SelectionChangedEventArgs p_Args)
    {
        if (m_NativeCamoFilling || NativeCamoList.SelectedItem is not NativeCamoChoice s_Choice)
            return;

        if (string.Equals(DocumentGraph.NativeCamo, s_Choice.Key, StringComparison.OrdinalIgnoreCase))
            return;

        // ⭐ EVERY CAMO OF THE PICKER KEEPS ITS OWN DOCUMENT (a Diffuse cut on Berkut must not be cut on ABU — see
        // EditorTab.Natives): the subject's document under the camo picked takes the canvas, the one being left stays with its camo.
        var s_MaterialOpen = m_MaterialEditKey != null ? MaterialList.SelectedItem as MaterialEntry : null;
        var s_Followed = FollowNativeDocument(s_Choice.Key);

        // The document remembers it; the weapon on screen is dressed for it — texture and, where this
        // weapon has that camo as one of its own, the numbers the game tuned it with.
        DocumentGraph.NativeCamo = s_Choice.Key;
        RedressCamoWeapon();
        SchedulePreview();

        if (s_Followed)
        {
            // the material that was open stays open, now that camo's (leaving the document closed it)
            if (s_MaterialOpen != null && m_MaterialEditKey == null && MaterialList.Items.Contains(s_MaterialOpen))
            {
                ShowMaterialGraph(s_MaterialOpen);
                LoadPanelTexturesFor(s_MaterialOpen);
            }

            // another document on the canvas: the simple form shows ITS pattern and numbers
            CamoSimpleShown?.Invoke(this, EventArgs.Empty);
        }

        // ⛔ THE PIECE'S WEAR BOXES FOLLOW THE PICKER FROM HERE, NOT FROM SyncFactoryLook (measured with two
        // window shots, 2026-09-20): while an attachment's own material is being edited that function RETURNS
        // EARLY, so a call at its end never ran and the boxes stayed hidden after a camo was picked. This is
        // the user's own action on the picker, which is the one moment the answer always changes.
        ShowAccessoryValues();
        Log(s_Choice.Key == null
            ? "Camo: back to the weapon as shipped."
            : $"Camo: starting from '{s_Choice.Display}' — it is not replaced in the game, this camo begins from it.");

        // what the studio says this camo cannot do on the subject on screen (a soldier's stock look that puts other meshes on this part)
        if (m_PreviewMesh != null && m_CamoNativeNoteFor?.Invoke(m_PreviewMesh, s_Choice.Key) is { } s_Note)
            Log("NOTE: " + s_Note);

        // (the part's option says what the look just picked does: its mask may put the camo nowhere)
        SyncPartOption();
    }

    /// <summary>In camo mode: what a native camo cannot do on a subject (mesh, key) → the note, or null (the studio's answer).</summary>
    private Func<string, string?, string?>? m_CamoNativeNoteFor;

    /// <summary>In camo mode: whether sections wearing a given shader belong to the camo being authored.</summary>
    private Func<string, bool>? m_CamoOwnsSection;

    /// <summary>In camo mode: the note a node of a freshly loaded camo graph should carry (node, preset) -> text.</summary>
    private Func<GraphNode, string, string?>? m_CamoNoteForNode;

    /// <summary>In camo mode: the vector ONE number stands for on a constant, (bare name, number) -> "x,y,z,w".</summary>
    private Func<string, string, string>? m_CamoExpandValue;

    /// <summary>In camo mode: how many components a constant is edited as (2 = a tiling with an X and a Y box).</summary>
    private Func<string, int>? m_CamoComponentsOf;

    /// <summary>In camo mode: what the properties panel says beside a constant's value box, (node, preset) -> text.</summary>
    private Func<GraphNode, string, string?>? m_CamoPanelNoteFor;

    /// <summary>
    /// The cached, translated graph of a preset, ready to be a camo: aimed at the preset, and with the nodes
    /// the simple form edits (pattern, tiling, wear) annotated in the form's own words — so in graph mode
    /// the user can tell which node is which without the form. A node that already carries a note keeps it.
    /// Throws when the cache is missing or unreadable; callers decide what that costs.
    /// </summary>
    private ShaderGraph LoadCamoGraph(string p_Preset)
    {
        var s_Graph = ShaderGraph.FromJson(File.ReadAllText(CamoGraphCachePath(p_Preset)));
        s_Graph.TargetShader = p_Preset;

        // Added to whatever the node already says (the translator's "Samples 'texture_Camo' (t2)." stays),
        // and only once: the same graph is loaded on every New.
        if (m_CamoNoteForNode != null)
            foreach (var s_Node in s_Graph.Nodes)
                if (m_CamoNoteForNode(s_Node, p_Preset) is { } s_Note &&
                    !(s_Node.Comment?.Contains(s_Note, StringComparison.Ordinal) ?? false))
                    s_Node.Comment = string.IsNullOrWhiteSpace(s_Node.Comment) ? s_Note : $"{s_Node.Comment.TrimEnd()}\n{s_Note}";

        return s_Graph;
    }

    /// <summary>In camo mode: register -> game texture of the set that dressed the weapon on screen.</summary>
    private Dictionary<string, string>? m_CamoSlotNames;

    /// <summary>In camo mode: parameter -> value as the preview runs them — the weapon's material with the form's live values on top.</summary>
    private Dictionary<string, string>? m_CamoValueNames;

    /// <summary>In camo mode: the weapon's own material values, before anything of the camo rides on top.</summary>
    private Dictionary<string, string>? m_CamoWeaponValues;

    /// <summary>In camo mode: the simple form's values (CamoTiling, WearAmount), laid over the weapon's on every dress.</summary>
    private Dictionary<string, string>? m_CamoLiveValues;

    /// <summary>In camo mode: the simple form's pattern and the register it goes to, as last applied.</summary>
    private (string? Path, string? Register) m_CamoLivePattern;

    /// <summary>The target of the tab on the canvas — in camo mode, the preset the camo is authored on.</summary>
    internal string ActiveTarget => TargetShaderBox.Text.Trim();

    /// <summary>Raised when the user types a preview value on an external constant: (name without prefix, text).</summary>
    internal event Action<string, string>? ExternalValueEdited;

    /// <summary>The bare parameter name of an external-constant node: "external_CamoTiling" -> "CamoTiling".</summary>
    private static string BareExternalName(GraphNode p_Node)
    {
        var s_Name = p_Node.GetParam("Name");
        return s_Name.StartsWith("external_", StringComparison.Ordinal) ? s_Name["external_".Length..] : s_Name;
    }

    /// <summary>
    /// Sets the value a graph feeds an external constant in the preview — the properties box and the
    /// studio's form both come through here. Empty clears it. The preview is fed at once (a value is not
    /// a recompile), and whoever mirrors the value elsewhere (the form) is told.
    /// </summary>
    internal void SetExternalPreviewValue(GraphNode p_Node, string p_Text)
    {
        if (DocumentNodeFor(p_Node) is not { } s_Node)
            return;

        p_Node = s_Node;
        // (one number with a decimal comma is that number where the box holds one — not a tiling, whose two axes have a box each)
        var s_Text = DecimalComma(p_Text.Trim(), m_CamoComponentsOf?.Invoke(BareExternalName(p_Node)) != 2);
        if (s_Text.Length == 0)
            p_Node.Params.Remove("PreviewValue");
        else
            p_Node.Params["PreviewValue"] = ExpandExternalValue(p_Node, s_Text);

        PushExternalValues();
        Canvas.Refresh();
        ExternalValueEdited?.Invoke(BareExternalName(p_Node), p_Node.Params.GetValueOrDefault("PreviewValue") ?? "");
    }

    /// <summary>
    /// ONE number typed with a decimal COMMA — "0,5", the way many locales write it — is read as that number where the box holds
    /// one number (a scalar, a grey, one axis of a tiling): it was stored as the vector (0, 5) — a wear of 0, the pattern whole, without a
    /// word — and an axis box took it as unreadable and kept its old number. A text with a point, or with more than one comma, is left as
    /// typed (a vector: "4,2,0,0"). Review 2026-09-29. CAMO_DECIMAL_COMMA_OLD=1 = as before.
    /// </summary>
    private static string DecimalComma(string p_Text, bool p_OneNumber)
    {
        if (!p_OneNumber || Environment.GetEnvironmentVariable("CAMO_DECIMAL_COMMA_OLD") == "1" || p_Text.Contains('.') ||
            p_Text.Count(p_C => p_C == ',') != 1)
            return p_Text;

        var s_Dotted = p_Text.Replace(',', '.');
        return float.TryParse(s_Dotted, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? s_Dotted : p_Text;
    }

    /// <summary>
    /// Sets a two-axis constant (a tiling) from its two boxes: each box is ONE number for its axis; a box
    /// left empty or unreadable keeps that axis's value in force (the graph's, else the game's); both empty
    /// clears the override. Stored as the "x,y,0,0" the constant takes and fed at once, like SetExternalPreviewValue.
    /// </summary>
    internal void SetExternalPreviewComponents(GraphNode p_Node, string p_X, string p_Y)
    {
        if (DocumentNodeFor(p_Node) is not { } s_Node)
            return;

        p_Node = s_Node;
        // (each box is one number: "0,5" is 0.5 — DecimalComma)
        var s_X = DecimalComma(p_X.Trim(), true);
        var s_Y = DecimalComma(p_Y.Trim(), true);
        if (s_X.Length == 0 && s_Y.Length == 0)
        {
            p_Node.Params.Remove("PreviewValue");
        }
        else
        {
            var s_Parts = ExternalVectorParts(p_Node);
            var s_ValidX = float.TryParse(s_X, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            var s_ValidY = float.TryParse(s_Y, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            p_Node.Params["PreviewValue"] = $"{(s_ValidX ? s_X : s_Parts[0])},{(s_ValidY ? s_Y : s_Parts[1])},{s_Parts[2]},{s_Parts[3]}";
        }

        PushExternalValues();
        Canvas.Refresh();
        ExternalValueEdited?.Invoke(BareExternalName(p_Node), p_Node.Params.GetValueOrDefault("PreviewValue") ?? "");
    }

    /// <summary>
    /// The four components of the value in force for a constant — the graph's own when set, else the
    /// game's — as text, "0" where unknown; a stored one number is expanded first (a tiling "3" is 3 and 3).
    /// </summary>
    private string[] ExternalVectorParts(GraphNode p_Node)
    {
        var s_Vector = p_Node.Params.TryGetValue("PreviewValue", out var s_Stored) && !string.IsNullOrWhiteSpace(s_Stored)
            ? ExpandExternalValue(p_Node, s_Stored.Trim())
            : GameValueOf(p_Node) ?? "";

        return s_Vector.Split(',').Select(p_P => p_P.Trim()).Concat(Enumerable.Repeat("0", 4)).Take(4)
            .Select(p_P => p_P.Length == 0 ? "0" : p_P).ToArray();
    }

    /// <summary>
    /// Closes an editing session of the preview-value box as ONE undo step: the node is put back to what it
    /// stored before the session, an undo point is taken, and what the session stored is put back.
    ///
    /// ⛔ THE STORED VALUES, NEVER THE BOX'S TEXT (editing the tiling broke and forced a straight UV that
    /// persisted). The box shows a tiling as ONE number ("3") and TextChanged stores its
    /// expansion ("3,3,0,0"); the old handler wrote the raw "3" back on losing focus, and the next dress
    /// (another weapon, another native camo) read it as (3,0,0,0) — v × 0 — which collapses the pattern into
    /// straight stripes on every weapon from then on, and would have shipped that way in a bake.
    /// </summary>
    internal void CommitExternalPreviewValue(GraphNode p_Node, string? p_StoredBefore, string? p_StoredAfter)
    {
        if (DocumentNodeFor(p_Node) is not { } s_Node)
            return;

        p_Node = s_Node;

        static void Store(GraphNode p_N, string? p_Value)
        {
            if (string.IsNullOrWhiteSpace(p_Value))
                p_N.Params.Remove("PreviewValue");
            else
                p_N.Params["PreviewValue"] = p_Value;
        }

        Store(p_Node, p_StoredBefore);
        Canvas.PushUndo();
        Store(p_Node, p_StoredAfter);
        PushExternalValues();
    }

    /// <summary>
    /// What a typed value stands for. ONE number is what people type (without having to write
    /// x,x,x,x): the studio says which components it fills (a tiling repeats in x and y, a wear amount
    /// lives in x); without the studio, x of the game's own value is replaced and the rest kept. A full
    /// "x,y,z,w" is taken as typed.
    /// </summary>
    private string ExpandExternalValue(GraphNode p_Node, string p_Text)
    {
        if (p_Text.Contains(','))
            return p_Text;

        if (!float.TryParse(p_Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return p_Text;

        var s_Name = BareExternalName(p_Node);
        if (m_CamoExpandValue != null)
            return m_CamoExpandValue(s_Name, p_Text);

        var s_Game = m_CamoWeaponValues?.GetValueOrDefault(s_Name);
        var s_Parts = (s_Game ?? "0,0,0,0").Split(',');
        return string.Join(",", new[] { p_Text }.Concat(s_Parts.Skip(1).Take(3)).Concat(Enumerable.Repeat("0", 4)).Take(4));
    }

    /// <summary>
    /// The text the preview-value box shows: the value the preview feeds this constant — the graph's own
    /// when one is set, else the game's — as the one number it stands for when the vector is that number's
    /// expansion, else the vector itself. Never empty when a value is known: the user changes a number
    /// they can see, not one they have to look up (the default value is already written there).
    /// </summary>
    private string ExternalValueDisplay(GraphNode p_Node)
    {
        var s_Vector = p_Node.Params.TryGetValue("PreviewValue", out var s_Stored) && !string.IsNullOrWhiteSpace(s_Stored)
            ? s_Stored
            : GameValueOf(p_Node);

        if (string.IsNullOrWhiteSpace(s_Vector))
            return "";

        var s_First = s_Vector.Split(',')[0].Trim();
        return string.Equals(ExpandExternalValue(p_Node, s_First), s_Vector, StringComparison.Ordinal) ? s_First : s_Vector;
    }

    /// <summary>The value the game feeds a constant for what is on screen: the weapon's material in camo mode, the slot map's variation otherwise.</summary>
    private string? GameValueOf(GraphNode p_Node)
    {
        var s_Name = BareExternalName(p_Node);
        if (CamoMode)
            return m_CamoWeaponValues?.GetValueOrDefault(s_Name);

        return ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim()))?.Variations is { } s_Vars &&
               s_Vars.TryGetValue(m_Variation, out var s_Var) &&
               s_Var.Values?.TryGetValue(s_Name, out var s_Value) == true
            ? s_Value
            : null;
    }

    /// <summary>
    /// The value the camo DOCUMENT holds for a constant (the preview value typed on its node, or set by
    /// the form), as one number when it is one; null when the document holds none — the form shows the
    /// document, never a copy of its own (showing the form must not put anything back).
    /// </summary>
    internal string? CamoDocumentValue(string p_Name)
    {
        var s_Vector = DocumentGraph.Nodes
            .Where(p_N => p_N.Kind == "ExternalConstant" && BareExternalName(p_N).Equals(p_Name, StringComparison.OrdinalIgnoreCase))
            .Select(p_N => p_N.Params.GetValueOrDefault("PreviewValue"))
            .FirstOrDefault(p_V => !string.IsNullOrWhiteSpace(p_V));
        if (s_Vector == null)
            return m_CamoLiveValues?.GetValueOrDefault(p_Name) is { Length: > 0 } s_Live ? s_Live.Split(',')[0].Trim() : null;

        var s_First = s_Vector.Split(',')[0].Trim();
        var s_Expanded = m_CamoExpandValue?.Invoke(p_Name, s_First) ?? $"{s_First},0,0,0";
        return string.Equals(s_Expanded, s_Vector, StringComparison.Ordinal) ? s_First : s_Vector;
    }

    /// <summary>Whether the camo document has an external constant of that name — somewhere a number of the form can live (a soldier's CamoTileFactor).</summary>
    internal bool CamoDocumentHasConstant(string p_Name) =>
        DocumentGraph.Nodes.Any(p_N => p_N.Kind == "ExternalConstant" && BareExternalName(p_N).Equals(p_Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a texture node of the camo document samples a register — somewhere the form's pattern can go.</summary>
    internal bool CamoDocumentSamples(string? p_Register) =>
        p_Register != null && DocumentGraph.Nodes.Any(p_N => TextureNodeKinds.Contains(p_N.Kind) && p_N.GetParam("Register") == p_Register);

    /// <summary>The custom texture (a pattern) the camo document puts on the nodes sampling a register, or null.</summary>
    internal string? CamoDocumentTexture(string? p_Register) =>
        p_Register == null
            ? null
            : DocumentGraph.Nodes
                .Where(p_N => TextureNodeKinds.Contains(p_N.Kind) && p_N.GetParam("Register") == p_Register)
                .Select(p_N => p_N.Params.GetValueOrDefault("CustomTexture"))
                .FirstOrDefault(p_T => !string.IsNullOrWhiteSpace(p_T));

    /// <summary>The game's own value of a constant for the weapon on screen, as one number when it is one — for the studio's form.</summary>
    internal string? CamoGameValue(string p_Name)
    {
        var s_Vector = m_CamoWeaponValues?.GetValueOrDefault(p_Name);
        if (string.IsNullOrWhiteSpace(s_Vector))
            return null;

        var s_First = s_Vector.Split(',')[0].Trim();
        var s_Expanded = m_CamoExpandValue?.Invoke(p_Name, s_First) ?? $"{s_First},0,0,0";
        return string.Equals(s_Expanded, s_Vector, StringComparison.Ordinal) ? s_First : s_Vector;
    }

    /// <summary>
    /// Lays the graph's preview values over a set of constants: every external-constant node of the graph
    /// on the canvas with a preview value wins over the game's / material's own. Null in, null out only when
    /// nothing overrides either.
    /// </summary>
    private Dictionary<string, string>? ApplyNodeOverrides(IReadOnlyDictionary<string, string>? p_Values)
    {
        Dictionary<string, string>? s_Result = p_Values == null
            ? null
            : new Dictionary<string, string>(p_Values, StringComparer.OrdinalIgnoreCase);

        foreach (var s_Node in Canvas.Graph.Nodes)
        {
            if (s_Node.Kind != "ExternalConstant" || !s_Node.Params.TryGetValue("PreviewValue", out var s_Text) ||
                string.IsNullOrWhiteSpace(s_Text))
                continue;

            // A stored ONE number (a graph saved by the box's old focus handler, or edited by hand) is fed
            // as the vector it stands for, exactly as if it had just been typed: "3" on a tiling is (3,3),
            // never (3,0).
            s_Result ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            s_Result[BareExternalName(s_Node)] = ExpandExternalValue(s_Node, s_Text.Trim());
        }

        return s_Result;
    }

    /// <summary>Re-feeds the preview's constants from their current sources, in either mode.</summary>
    private void PushExternalValues()
    {
        // a value typed never recompiles, so it never reaches SchedulePreview: an original tab is looked at again from here too
        ScheduleOriginalCheck();

        if (CamoMode)
        {
            var s_Merged = ApplyNodeOverrides(MergedCamoValues());
            PushInstanceValues(s_Merged == null ? null : new Dictionary<string, string>(s_Merged), false);
            m_CamoValueNames = s_Merged;

            // ⛔ An object with more than one camo material (every vehicle) draws them through registrations of their own that
            // carry their own numbers: the push above never reaches them, so a slider moved and the picture did not.
            // They take the numbers here — re-fed, not rebuilt (ApplyMaterialDress, the numbers-only path).
            RefreshMaterialDress();

            // A value typed over the weapon's own (or cleared again) is one of the things that decide
            // whether there is a camo to show; a value alone never recompiles, so it is re-asked here.
            SyncFactoryLook();

            // A value typed on one of the object's OTHER materials is an edit of that material.
            if (m_MaterialEditKey != null)
                RefreshMaterialEdits();

            return;
        }

        var s_Map = ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim()));
        var s_Chosen = s_Map?.Variations != null && s_Map.Variations.TryGetValue(m_Variation, out var s_Set)
            ? s_Set.Values
            : null;
        PushInstanceValues(s_Chosen, false);
    }

    /// <summary>The weapon's material values with the camo's live values on top; null when neither exists.</summary>
    private Dictionary<string, string>? MergedCamoValues()
    {
        if (m_CamoWeaponValues == null && m_CamoLiveValues == null)
            return null;

        var s_Merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s_Name, s_Value) in m_CamoWeaponValues ?? new Dictionary<string, string>())
            s_Merged[s_Name] = s_Value;

        foreach (var (s_Name, s_Value) in m_CamoLiveValues ?? new Dictionary<string, string>())
            s_Merged[s_Name] = s_Value;

        return s_Merged;
    }

    /// <summary>
    /// The simple form's settings, applied to the preview the way the graph would carry them, LIVE.
    ///
    /// The pattern becomes the custom texture of every node sampling the camo register — the same thing a
    /// user would do by hand on the node, so graph mode shows exactly what simple mode set, and the graph
    /// carries it. The numbers ride over the weapon's material values and survive every re-dress (another
    /// weapon picked, a tab switch). Both go to the preview at once: a slider that only wrote a file would
    /// be the half-built control the form's note used to warn about.
    /// </summary>
    /// <summary>
    /// Every sampler register a shader's own bytecode reads a camo pattern from — its RDEF names them
    /// texture_Camo (weapon presets, one) or texture_CamoA / texture_CamoB (the jet family of vehicle presets,
    /// two: A where the colour uv's V >= 0, B where it is negative). Empty without a cached dump.
    /// </summary>
    private List<string> CamoRegistersOf(string? p_Shader)
    {
        var s_Registers = new List<string>();
        if (string.IsNullOrEmpty(p_Shader) || ContractFromCache(p_Shader) is not { } s_Contract)
            return s_Registers;

        foreach (var s_Resource in s_Contract.Resources)
            if (s_Resource.IsTexture &&
                s_Resource.Name.StartsWith("texture_Camo", StringComparison.OrdinalIgnoreCase) &&
                s_Resource.Name.Length <= "texture_Camo".Length + 1)
                s_Registers.Add(s_Resource.Register.ToString());

        return s_Registers;
    }

    /// <summary>
    /// The registers of ANOTHER shader that read the external texture the target reads at p_Slot — by the
    /// texture's NAME in the two bytecodes (texture_CamoA is t3 in vehiclepreset_jet and t4 in its _decals
    /// sibling, which inserts texture_Decal at t3). A camo texture with no namesake in the other shader lands
    /// on EVERY camo register it has (the jet's CamoA and CamoB are the one Camo of vehiclepreset_nomud, and
    /// the other way round). Empty when the other shader reads no such texture; the slot itself when either
    /// contract is not cached (the by-slot overlay of before).
    /// </summary>
    private int[] SlotsOfSameTexture(string p_Target, string p_Other, int p_Slot, Func<string, ShaderContract?>? p_Read = null)
    {
        // (p_Read: how the two contracts are read — the side-effect-free reader when the 3D is not drawing either)
        var s_Read = p_Read ?? ContractFromCache;
        if (s_Read(p_Target) is not { } s_From || s_Read(p_Other) is not { } s_To)
            return new[] { p_Slot };

        var s_Name = s_From.Resources.FirstOrDefault(p_R => p_R.IsTexture && p_R.Register == p_Slot)?.Name;
        if (s_Name == null || !s_Name.StartsWith("texture_", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<int>();

        var s_Param = s_Name["texture_".Length..];
        var s_Exact = s_To.RegisterForExternalTexture(s_Param);
        if (s_Exact >= 0)
            return new[] { s_Exact };

        var s_IsCamo = s_Param.StartsWith("Camo", StringComparison.OrdinalIgnoreCase) &&
                       (s_Param.Length == 4 || (s_Param.Length == 5 && char.IsLetter(s_Param[4])));
        return s_IsCamo
            ? CamoRegistersOf(p_Other).Select(int.Parse).ToArray()
            : Array.Empty<int>();
    }

    internal void SetCamoOverrides(string? p_PatternPath, string? p_PatternRegister,
        IReadOnlyDictionary<string, string>? p_Values, bool p_UserAction = true)
    {
        var s_Pattern = string.IsNullOrWhiteSpace(p_PatternPath) || !File.Exists(p_PatternPath) ? null : p_PatternPath;
        var s_PatternChanged = !string.Equals(s_Pattern, m_CamoLivePattern.Path, StringComparison.OrdinalIgnoreCase) ||
                               !string.Equals(p_PatternRegister, m_CamoLivePattern.Register, StringComparison.OrdinalIgnoreCase);
        var s_Old = Environment.GetEnvironmentVariable("CAMO_FORM_WRITES_OLD") == "1";

        // ⛔⛔ THE FORM SHOWN AGAIN IS NOT THE USER, AND IT WRITES NOTHING INTO THE DOCUMENT (review 2026-09-29 — the standing rule of 2026-09-12:
        // a mode is transparent to the document, re-showing a panel is no user action). It wrote both numbers on their nodes and the pattern
        // on the camo nodes on every tab or subject switch — in graph mode too, the handler being wired either way: a tiling typed "4,2" came
        // back "3,3" (the vector did not parse), the number never typed (the weapon's own tiling beside a typed wear) was written and shared
        // across the tab's subjects, a soldier's "7,5" became 3, and a picture whose file had moved — or one on the jet's CamoB node — was
        // stripped from its node. Now it only puts the window's own state in step with the document on screen: no live value of another
        // tab's form, the pattern the document carries. CAMO_FORM_WRITES_OLD=1 = as before.
        if (!p_UserAction && !s_Old)
        {
            m_CamoLiveValues = null;
            m_CamoLivePattern = (s_Pattern, p_PatternRegister);
            if (LastMeshPick is { IsCompleted: false })
                return;

            PushExternalValues();
            BuildProperties();
            return;
        }

        // ⛔ A change made while a subject is still loading would land on the document being left (the canvas has not changed yet): refused
        // and said — the form is filled again from the new subject's document when it lands
        if (p_UserAction && !s_Old && LastMeshPick is { IsCompleted: false })
        {
            Log("Camo: the subject is still loading — that change was not applied; make it again once it is on screen.");
            return;
        }

        // The USER writing on the camo (a slider, a pattern) while the weapon is as shipped: the picker moves
        // to basic and the camo's graph comes back first. The form being shown again (leaving sticker mode,
        // a tab switch) re-feeds the same settings and is NOT the user: it moves nothing.
        if (p_UserAction && (s_Pattern != null || p_Values is { Count: > 0 }))
            LeaveFactoryGraph(s_Pattern != null ? "a pattern was loaded" : "a value was set");

        m_CamoLivePattern = (s_Pattern, p_PatternRegister);

        // ⛔ THE CAMO DOCUMENT, not the canvas (review 2026-09-29): with one of the object's materials open on the canvas, the form wrote its
        // pattern and numbers into THAT material's graph (a skin ships no material graph) while it reads the document
        var s_Document = s_Old ? Canvas.Graph : DocumentGraph;

        // ⭐ THE NUMBERS LIVE ON THE NODES. Each value the form sets is written as the preview value of the
        // external-constant node of that name — the very box the user sees in graph mode — so the two
        // views hold ONE value and the graph carries it. A name with no node in this graph stays as a
        // live value of the window, so it still reaches the preview.
        m_CamoLiveValues = null;
        if (p_Values != null)
            foreach (var (s_Name, s_Text) in p_Values)
            {
                var s_Nodes = s_Document.Nodes
                    .Where(p_N => p_N.Kind == "ExternalConstant" &&
                                  BareExternalName(p_N).Equals(s_Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (s_Nodes.Count == 0)
                {
                    m_CamoLiveValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    m_CamoLiveValues[s_Name] = s_Text;
                    continue;
                }

                foreach (var s_Node in s_Nodes)
                    s_Node.Params["PreviewValue"] = s_Text;
            }

        if (s_PatternChanged && p_PatternRegister != null)
        {
            // ⭐ ON EVERY CAMO REGISTER THE TARGET READS, not only the one named. A weapon preset has one
            // (texture_Camo, t2); the jet family reads texture_CamoA where the colour uv's V >= 0 and
            // texture_CamoB where it is negative (46 % of the F/A-18F's hull) — a pattern put on one of them
            // painted half a jet and left the game's grey on the other half (2026-09-22). Removing the
            // pattern clears the same set, so no node keeps a picture the picker no longer names.
            var s_Registers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { p_PatternRegister };
            foreach (var s_Other in CamoRegistersOf(s_Document.TargetShader))
                s_Registers.Add(s_Other);

            var s_Nodes = s_Document.Nodes
                .Where(p_N => TextureNodeKinds.Contains(p_N.Kind) && s_Registers.Contains(p_N.GetParam("Register") ?? ""))
                .ToList();

            foreach (var s_Node in s_Nodes)
                if (s_Pattern == null)
                    s_Node.Params.Remove("CustomTexture");
                else
                    s_Node.Params["CustomTexture"] = s_Pattern;

            if (s_Nodes.Count == 0)
                Log($"Camo: no node samples register t{p_PatternRegister} in this graph — the pattern has nowhere to go.");
            else if (s_Pattern != null && s_Registers.Count > 1)
                Log($"Camo: the pattern goes on t{string.Join(", t", s_Nodes.Select(p_N => p_N.GetParam("Register")).Distinct().OrderBy(p_R => p_R, StringComparer.Ordinal))} — " +
                    "this preset reads its camo from more than one register.");
        }

        // A pattern change re-dresses (the game art has to come back when it is removed); a number only
        // pushes — the sliders fire continuously while dragged, and re-dressing costs a thumbnail reload.
        // Never mid-pick: the pick in flight dresses with these values when it lands.
        if (LastMeshPick is { IsCompleted: false })
            return;

        if (s_PatternChanged)
        {
            RedressCamoWeapon();
            // ⛔ …and a picture is a change like any other: the original tab is looked at again (review 2026-09-29). Every change reaches
            // that look through SchedulePreview or PushExternalValues, and a picture chosen here took neither — a picture on a weapon on one of
            // the game's camos (ABU), or on a soldier's part on one of his looks, stayed in the ORIGINAL tab, which keeps nothing: gone at the
            // next pick. (As shipped it forked only because the picker moved to the basic camo.) CAMO_PATTERN_FORK_OLD=1 = as before.
            if (Environment.GetEnvironmentVariable("CAMO_PATTERN_FORK_OLD") != "1")
                ScheduleOriginalCheck();
        }
        else
            PushExternalValues();

        // The panel may be showing one of those nodes: its box follows the form.
        BuildProperties();
    }

    /// <summary>
    /// The game texture a register wears in the set on screen: the studio's set in camo mode, the slot map's
    /// otherwise. The properties panel and the driver's snapshot both name textures through this one lookup,
    /// so what is shown beside a thumbnail is exactly what is measured.
    /// </summary>
    private string? GameTextureNameFor(string? p_Register)
    {
        if (string.IsNullOrWhiteSpace(p_Register))
            return null;

        if (CamoMode && m_CamoSlotNames != null)
            return m_CamoSlotNames.GetValueOrDefault(p_Register);

        if (ReadSlotMapFull(SlotMapPath(TargetShaderBox.Text.Trim())) is not { } s_Map)
            return null;

        return s_Map.Variations != null &&
               s_Map.Variations.TryGetValue(m_Variation, out var s_Set) &&
               s_Set.Slots.TryGetValue(p_Register, out var s_VariationName)
            ? s_VariationName
            : s_Map.Slots.GetValueOrDefault(p_Register);
    }

    internal void EnterCamoMode(IReadOnlyList<CamoWeapon> p_Weapons, Action? p_Bake = null,
        Func<string, string?>? p_ShaderFor = null, Func<string, string?>? p_NoteFor = null,
        Func<string, string, string?, IReadOnlyDictionary<string, string>?>? p_SlotsFor = null,
        Func<string, string?, string?, IReadOnlyDictionary<string, string>?>? p_ValuesFor = null,
        Func<string, bool>? p_OwnsSection = null,
        Func<GraphNode, string, string?>? p_NoteForNode = null,
        Func<string, string, string>? p_ExpandValue = null,
        Func<GraphNode, string, string?>? p_PanelNoteFor = null,
        IReadOnlyList<(string Key, string Display)>? p_NativeCamos = null,
        Func<string, int>? p_ComponentsOf = null,
        Action? p_Delete = null,
        Action? p_Stickers = null,
        Func<string, int?>? p_DiffuseRegisterFor = null,
        (string Key, string Display)? p_BasicCamo = null,
        Func<string, string>? p_NoCamoPresetFor = null,
        Func<string, IReadOnlyList<CamoWeapon>>? p_AccessoriesOf = null,
        Func<string, string?>? p_WeaponOfAccessory = null,
        Func<string, string?>? p_CamoRegisterFor = null,
        Func<string, IReadOnlyList<(int MaterialId, string Shader)>>? p_SectionMaterialsOf = null,
        IReadOnlyList<CamoFamily>? p_Families = null,
        Func<string, string, string?, int, IReadOnlyDictionary<string, string>?>? p_SlotsForMaterial = null,
        Func<string, string, string?, int, IReadOnlyDictionary<string, string>?>? p_ValuesForMaterial = null,
        Func<string, IReadOnlyList<(string Path, float OffsetX, bool MirrorX, string Context)>?>? p_ExtraMeshesFor = null,
        Func<string, (IReadOnlyList<int> Sections, IReadOnlyDictionary<int, int[]> Parts)?>? p_PieceInMesh = null,
        Func<string, IReadOnlyCollection<int>?>? p_PartsHiddenAtRest = null,
        Func<string, string>? p_DocumentKeyFor = null,
        Func<string, int, string>? p_DocumentKeyForMaterial = null,
        Func<string, bool>? p_NativePerSubject = null,
        Func<string, string?, string?>? p_NativeNoteFor = null,
        Action? p_SaveCamo = null,
        Action? p_OpenCamo = null)
    {
        m_CamoNativePerSubject = p_NativePerSubject;
        m_CamoNativeNoteFor = p_NativeNoteFor;
        m_CamoDocumentKeyFor = p_DocumentKeyFor;
        m_CamoDocumentKeyForMaterial = p_DocumentKeyForMaterial;
        m_CamoPieceInMesh = p_PieceInMesh;
        m_CamoPartsHiddenAtRest = p_PartsHiddenAtRest;
        m_CamoExtraMeshesFor = p_ExtraMeshesFor;
        m_CamoSlotsForMaterial = p_SlotsForMaterial;
        m_CamoValuesForMaterial = p_ValuesForMaterial;

        m_CamoBasicKey = p_BasicCamo?.Key;
        m_CamoBasicDisplay = p_BasicCamo?.Display;
        m_CamoNoCamoPresetFor = p_NoCamoPresetFor;

        // ⭐ MATERIALS BY ID (some assets have 2 or more material IDs — several shaders inside one
        // object): the picker lists the object's materials with the game's own IDs, and
        // the camo can be kept off any preset material by ID — the view and the bake follow that choice.
        m_CamoSectionMaterialsOf = p_SectionMaterialsOf;
        MaterialPanel.Visibility = Visibility.Collapsed;
        MaterialList.Items.Clear();

        // ⭐ ACCESSORIES (every accessory ported, so the user can modify their skins just as with the
        // weapons): a second picker under the weapon's, filled from the
        // weapon's customization rows the moment a weapon is picked. Only where the studio brings the table.
        m_CamoAccessoriesOf = p_AccessoriesOf;
        m_CamoWeaponOfAccessory = p_WeaponOfAccessory;
        m_CamoRegisterFor = p_CamoRegisterFor;
        AccessoryPanel.Visibility = p_AccessoriesOf != null ? Visibility.Visible : Visibility.Collapsed;

        // The entries belong to a weapon, and none is picked yet: the list SAYS so instead of opening empty
        // (it used to open empty, with nothing in it).
        ResetAccessoryList();

        // Deleting a baked camo is the bake's counterpart (just as custom camos are baked, there is an option
        // to delete them), so its button sits beside the bake button
        // and only exists where baking does.
        m_CamoDelete = p_Delete;
        DeleteCamoButton.Visibility = p_Delete != null ? Visibility.Visible : Visibility.Collapsed;

        // …and saving the camo ready to bake, and opening it again, beside them: the studio's, where it offers them
        m_CamoSaveCamo = p_SaveCamo;
        m_CamoOpenCamo = p_OpenCamo;
        SaveCamoButton.Visibility = p_SaveCamo != null ? Visibility.Visible : Visibility.Collapsed;
        OpenCamoButton.Visibility = p_OpenCamo != null ? Visibility.Visible : Visibility.Collapsed;

        // Stickers are the studio's: it brings the library panel and the knowledge of which register the
        // diffuse lives at; the button only exists where the studio offers the mode.
        m_CamoStickers = p_Stickers;
        m_StickerDiffuseRegisterFor = p_DiffuseRegisterFor;
        StickersButton.Visibility = p_Stickers != null ? Visibility.Visible : Visibility.Collapsed;

        m_CamoSlotsFor = p_SlotsFor;
        m_CamoValuesFor = p_ValuesFor;
        m_CamoOwnsSection = p_OwnsSection;
        m_CamoNoteForNode = p_NoteForNode;
        m_CamoExpandValue = p_ExpandValue;
        m_CamoPanelNoteFor = p_PanelNoteFor;
        m_CamoComponentsOf = p_ComponentsOf;

        // The list is of WEAPONS here, and the words above it must say so — the editor's speak of "objects
        // in this map that wear this shader", which is not what a camo author is choosing.
        MeshListTitle.Text = "Preview weapon";
        MeshListHelp.Text = "The weapon the camo is looked at on. Every weapon is cached — picking one is instant.";

        // ⭐ THE GAME'S OWN CAMOS AS A STARTING POINT: a picker beside the weapon's, with "as shipped"
        // first. The choice belongs to the tab's camo, so it comes back with the tab.
        FillNativeCamos(p_NativeCamos);

        m_CamoShaderFor = p_ShaderFor;
        m_CamoNoteFor = p_NoteFor;
        CamoMode = true;

        // The bake button is KEPT and repointed rather than hidden: baking is still what finishes the job,
        // it just produces a camo mod instead of a shader mod. Hiding it and adding another one beside it
        // would leave the window with two buttons that mean the same thing.
        m_CamoBake = p_Bake;
        if (p_Bake != null)
            BakeButton.Content = "Bake camo…";
        else
            BakeButton.Visibility = Visibility.Collapsed;

        ShaderBrowserHead.Visibility = Visibility.Collapsed;
        ShaderTree.Visibility = Visibility.Collapsed;
        LeftSplitter.Visibility = Visibility.Collapsed;
        TargetShaderLabel.Visibility = Visibility.Collapsed;
        TargetShaderBox.Visibility = Visibility.Collapsed;

        // Hiding the tree is not enough: its row keeps its 3* share and leaves a dead band above the nodes.
        LeftPanel.RowDefinitions[1].Height = new GridLength(0);
        LeftPanel.RowDefinitions[2].Height = new GridLength(0);

        m_MeshListFilling = true;
        MeshList.Items.Clear();

        // The template shortens a mesh PATH for display; these items are already the weapon's name, so it
        // would only get in the way.
        MeshList.ItemTemplate = null;
        foreach (var s_Weapon in p_Weapons)
            MeshList.Items.Add(s_Weapon);

        m_MeshListFilling = false;
        MeshListNote.Visibility = Visibility.Collapsed;

        // "Linked objects" picks WHICH TEXTURE SET of a shader to preview — the variations an object already
        // has. For camo work the weapon list above already answers "what am I looking at", and the variation
        // being authored is the one the user is making, so the box only offers a choice that does not apply.
        // Its underlying selection still drives which textures load; what goes away is the control.
        VariationLabel.Visibility = Visibility.Collapsed;
        VariationBox.Visibility = Visibility.Collapsed;
        m_CamoHidesVariation = true;

        // A camo is always looked at on a weapon, so the mesh view is where this opens rather than a
        // primitive the user would have to switch away from.
        m_MeshMode = true;
        SyncShapeIcons(View.PreviewShape.Mesh);

        // ⛔ NOTHING IS PRESELECTED, and the obvious version of this was wrong. Opening on an
        // already-cached weapon looked safe — the dump is on disk — but picking a mesh ALSO fetches the
        // shader of every section it wears, and THAT mounts the game: the window spent a minute mounting
        // the moment it opened, unasked. Guarding on the mesh cache guarded the wrong thing.
        Log($"Camo mode: {p_Weapons.Count} weapon(s) to preview on — pick one under Preview weapon.");

        // ⭐ The tab row last, and it OWNS the list and the labels from here on: the block above filled
        // the weapons because that is what a caller with no families means, and the first family fills
        // them again through the one path every later tab press uses. Two fillers is how the list and
        // the words it is under stop agreeing.
        if (p_Families is { Count: > 0 })
        {
            BuildCamoFamilyTabs(p_Families);
            ShowCamoFamily(p_Families[0].Key);
        }
    }

    /// <summary>The families on the tab row, in the order the studio gave them.</summary>
    private IReadOnlyList<CamoFamily> m_CamoFamilies = Array.Empty<CamoFamily>();

    /// <summary>The one on screen. Null before <see cref="EnterCamoMode"/> is given any.</summary>
    private CamoFamily? m_CamoFamily;

    /// <summary>Guards the toggles while the code is the one pressing them.</summary>
    private bool m_FamilySwitching;

    /// <summary>Which family is on screen, for a seam and for the snapshot.</summary>
    internal string? CamoFamilyKey => m_CamoFamily?.Key;

    /// <summary>The families the window is offering, by key — what a seam can ask it to show.</summary>
    internal IReadOnlyList<string> CamoFamilyKeys => m_CamoFamilies.Select(p_F => p_F.Key).ToList();

    /// <summary>
    /// Builds the tab row from the families the studio brought. One tab per family, in its order, and a
    /// family that is not built yet is shown DISABLED with its reason in the tooltip.
    /// </summary>
    private void BuildCamoFamilyTabs(IReadOnlyList<CamoFamily> p_Families)
    {
        m_CamoFamilies = p_Families;
        CamoFamilyPanel.Children.Clear();

        foreach (var s_Family in p_Families)
        {
            var s_Tab = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = s_Family.Display,
                Style = (Style) FindResource("FamilyTab"),
                Tag = s_Family.Key,
                IsEnabled = s_Family.Unavailable == null,
                ToolTip = s_Family.Unavailable ?? s_Family.Tooltip,
            };

            s_Tab.Click += OnCamoFamilyTab;
            CamoFamilyPanel.Children.Add(s_Tab);
        }

        // One family is not a choice: the row would only take space away from the pickers.
        CamoFamilyPanel.Visibility = p_Families.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCamoFamilyTab(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_FamilySwitching || p_Sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string s_Key })
            return;

        ShowCamoFamily(s_Key);
    }

    /// <summary>
    /// Puts a family on screen: its subjects in the picker, its words on the labels, its native camos (or
    /// none), and the attachment picker back to "pick one above".
    ///
    /// ⛔ NOTHING IS PRESELECTED, for the same reason nothing is when the window opens: picking a subject
    /// fetches the shader of every section it wears, and THAT mounts the game. A tab press must stay free.
    /// </summary>
    internal bool ShowCamoFamily(string p_Key)
    {
        if (m_CamoFamilies.FirstOrDefault(p_F =>
                p_F.Key.Equals(p_Key, StringComparison.OrdinalIgnoreCase)) is not { } s_Family)
            return false;

        if (s_Family.Unavailable != null)
        {
            Log($"{s_Family.Display}: {s_Family.Unavailable}");
            SyncFamilyTabs();
            return false;
        }

        m_CamoFamily = s_Family;
        SyncFamilyTabs();

        MeshListTitle.Text = s_Family.SubjectTitle;
        MeshListHelp.Text = s_Family.SubjectHelp;
        AccessoryListTitle.Text = s_Family.AccessoryTitle;
        AccessoryListHelp.Text = s_Family.AccessoryHelp;

        BuildCamoGroupTabs(s_Family);
        FillCamoSubjects(s_Family);

        ResetAccessoryList();
        FillNativeCamos(NativeCamosOf(s_Family));

        Log($"{s_Family.Display}: {MeshList.Items.Count} to preview on" +
            (CamoGroupKey is { } s_Group ? $" ({s_Group})" : "") + $" — pick one under {s_Family.SubjectTitle}.");
        return true;
    }

    /// <summary>The group pressed in each family that has groups (family key → group key), so coming back to a tab keeps its side.</summary>
    private readonly Dictionary<string, string> m_CamoGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The group on screen, or null when the family on screen has none — what a seam checks after pressing one.</summary>
    internal string? CamoGroupKey =>
        m_CamoFamily?.Groups is { Count: > 0 } ? m_CamoGroups.GetValueOrDefault(m_CamoFamily.Key) : null;

    /// <summary>
    /// The row of group buttons for a family (the soldiers' US / RU) — or no row. The first group is the pressed one until the user
    /// presses another; the choice is kept per family.
    /// </summary>
    private void BuildCamoGroupTabs(CamoFamily p_Family)
    {
        CamoGroupPanel.Children.Clear();
        if (p_Family.Groups is not { Count: > 0 } s_Groups)
        {
            CamoGroupPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!m_CamoGroups.TryGetValue(p_Family.Key, out var s_Pressed) || s_Groups.All(p_G => p_G.Key != s_Pressed))
            m_CamoGroups[p_Family.Key] = s_Groups[0].Key;

        foreach (var (s_Key, s_Display) in s_Groups)
        {
            var s_Tab = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = s_Display,
                Style = (Style) FindResource("FamilyTab"),
                Tag = s_Key,
                ToolTip = $"The {p_Family.SubjectWord}s of {s_Display}.",
            };

            s_Tab.Click += OnCamoGroupTab;
            CamoGroupPanel.Children.Add(s_Tab);
        }

        CamoGroupPanel.Visibility = Visibility.Visible;
        SyncGroupTabs();
    }

    /// <summary>The picker gets the family's subjects — of the pressed group only, when the family has groups. Nothing is preselected.</summary>
    private void FillCamoSubjects(CamoFamily p_Family)
    {
        var s_Group = CamoGroupKey;

        m_MeshListFilling = true;
        MeshList.Items.Clear();

        // The template shortens a mesh PATH for display; these items already read as names.
        MeshList.ItemTemplate = null;
        foreach (var s_Subject in p_Family.Subjects)
            if (s_Group == null || s_Subject.Group.Equals(s_Group, StringComparison.OrdinalIgnoreCase))
                MeshList.Items.Add(s_Subject);

        m_MeshListFilling = false;
        MeshListNote.Visibility = Visibility.Collapsed;
    }

    private void OnCamoGroupTab(object p_Sender, RoutedEventArgs p_Args)
    {
        if (m_FamilySwitching || m_CamoFamily == null ||
            p_Sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string s_Key })
            return;

        m_CamoGroups[m_CamoFamily.Key] = s_Key;
        SyncGroupTabs();
        FillCamoSubjects(m_CamoFamily);
        ResetAccessoryList();
        RefillGroupNatives(m_CamoFamily);
        Log($"{m_CamoFamily.Display} ({s_Key}): {MeshList.Items.Count} to preview on — pick one under {m_CamoFamily.SubjectTitle}.");
    }

    /// <summary>The native camos a family offers on the group on screen (its side's own, where each side has its own).</summary>
    private IReadOnlyList<(string Key, string Display)>? NativeCamosOf(CamoFamily p_Family) =>
        p_Family.GroupNativeCamos != null && CamoGroupKey is { } s_Group && p_Family.GroupNativeCamos.TryGetValue(s_Group, out var s_Own)
            ? s_Own
            : p_Family.NativeCamos;

    /// <summary>
    /// The picker refilled for the group now on screen, where each side has its own camos — and put back on the camo of the document on
    /// the canvas. A no-op for a family whose camos are the same on every side.
    /// </summary>
    private void RefillGroupNatives(CamoFamily p_Family)
    {
        if (p_Family.GroupNativeCamos == null)
            return;

        FillNativeCamos(NativeCamosOf(p_Family));
        SyncNativeCamoBox();
    }

    /// <summary>Presses a group button THE WAY THE USER DOES (its own Click). False when the row has no such button.</summary>
    internal bool PressCamoGroup(string p_Key)
    {
        foreach (var s_Child in CamoGroupPanel.Children)
        {
            if (s_Child is not System.Windows.Controls.Primitives.ToggleButton { Tag: string s_Key } s_Tab ||
                !s_Key.Equals(p_Key, StringComparison.OrdinalIgnoreCase) || !s_Tab.IsEnabled)
                continue;

            s_Tab.IsChecked = true;
            s_Tab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return true;
        }

        return false;
    }

    /// <summary>The pressed group button is the group on screen; the code presses them, so the handler is guarded.</summary>
    private void SyncGroupTabs()
    {
        var s_Group = CamoGroupKey;
        m_FamilySwitching = true;
        foreach (var s_Child in CamoGroupPanel.Children)
            if (s_Child is System.Windows.Controls.Primitives.ToggleButton { Tag: string s_Key } s_Tab)
                s_Tab.IsChecked = s_Group != null && s_Key.Equals(s_Group, StringComparison.OrdinalIgnoreCase);

        m_FamilySwitching = false;
    }

    /// <summary>
    /// Presses a family tab THE WAY THE USER DOES — the button's own Click, not the method under it.
    ///
    /// ⛔ A seam that calls <see cref="ShowCamoFamily"/> directly skips the button, and the button is half
    /// of what can be wrong (a disabled one must do nothing at all). False here means there is no such tab
    /// or it is disabled, which is a measurement, not a failure of the seam.
    /// </summary>
    internal bool PressCamoFamily(string p_Key)
    {
        foreach (var s_Child in CamoFamilyPanel.Children)
        {
            if (s_Child is not System.Windows.Controls.Primitives.ToggleButton { Tag: string s_Key } s_Tab ||
                !s_Key.Equals(p_Key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!s_Tab.IsEnabled)
                return false;

            s_Tab.IsChecked = true;
            s_Tab.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return true;
        }

        return false;
    }

    /// <summary>The pressed tab is the family on screen; the code presses them, so the handler is guarded.</summary>
    private void SyncFamilyTabs()
    {
        m_FamilySwitching = true;
        foreach (var s_Child in CamoFamilyPanel.Children)
            if (s_Child is System.Windows.Controls.Primitives.ToggleButton { Tag: string s_Key } s_Tab)
                s_Tab.IsChecked = m_CamoFamily != null &&
                                  s_Key.Equals(m_CamoFamily.Key, StringComparison.OrdinalIgnoreCase);

        m_FamilySwitching = false;
    }

    /// <summary>
    /// The attachment picker back to its placeholder. The entries belong to a subject and none is picked
    /// yet: the list SAYS so instead of opening empty.
    /// </summary>
    private void ResetAccessoryList()
    {
        m_AccessoryListFilling = true;
        AccessoryList.Items.Clear();
        AccessoryList.ItemTemplate = null;
        AccessoryList.Items.Add(new CamoWeapon { Display = AccessoryPlaceholder, Mesh = "" });
        AccessoryList.SelectedIndex = 0;
        m_AccessoryListFilling = false;
        ShowAccessoryValues();
        SyncCockpitBox();
    }

    /// <summary>
    /// The game's own camos for the family on screen, "as shipped" first — or no picker at all where the
    /// game ships none. A picker offering a single entry is a control that cannot be used.
    /// </summary>
    private void FillNativeCamos(IReadOnlyList<(string Key, string Display)>? p_NativeCamos)
    {
        if (p_NativeCamos is not { Count: > 0 })
        {
            NativeCamoPanel.Visibility = Visibility.Collapsed;
            return;
        }

        m_NativeCamoFilling = true;
        NativeCamoList.Items.Clear();
        NativeCamoList.Items.Add(new NativeCamoChoice { Key = null, Display = "(as shipped — the game's default skin)" });
        foreach (var (s_Key, s_Display) in p_NativeCamos)
            NativeCamoList.Items.Add(new NativeCamoChoice { Key = s_Key, Display = s_Display });

        NativeCamoList.SelectedIndex = 0;
        m_NativeCamoFilling = false;
        NativeCamoPanel.Visibility = Visibility.Visible;
    }

    private void Log(string p_Message)
    {
        var s_Line = $"[{DateTime.Now:HH:mm:ss}] {p_Message}{Environment.NewLine}";
        LogBox.AppendText(s_Line);
        LogBox.ScrollToEnd();
        WriteOutputFile(s_Line);
    }

    /// <summary>
    /// ⭐ THE OUTPUT ON DISK (asked to check the log, there was none to read: the panel lives only in the window, and a long
    /// session cannot be copied out of it). In camo mode every line also goes to camostudio_output.log beside the studio's caches; each
    /// run starts it afresh and keeps the previous run's as camostudio_output.prev.log. Never while a seam drives a hidden window
    /// (Headless): a seam leaves the machine as it found it.
    /// </summary>
    private void WriteOutputFile(string p_Line)
    {
        if (Headless || !CamoMode)
            return;

        try
        {
            var s_Path = Path.Combine(OutputFolder, "camostudio_output.log");
            if (!m_OutputFileStarted)
            {
                m_OutputFileStarted = true;
                Directory.CreateDirectory(OutputFolder);
                if (File.Exists(s_Path))
                    File.Copy(s_Path, Path.Combine(OutputFolder, "camostudio_output.prev.log"), true);
                File.WriteAllText(s_Path, $"Camo Studio output, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                                          LogBox.Text, new System.Text.UTF8Encoding(false));
                return;
            }

            File.AppendAllText(s_Path, p_Line, new System.Text.UTF8Encoding(false));
        }
        catch (Exception)
        {
            // the panel keeps the line either way; a locked file must not break the window
        }
    }

    private bool m_OutputFileStarted;
}
