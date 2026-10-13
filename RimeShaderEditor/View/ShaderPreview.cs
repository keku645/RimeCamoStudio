using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace RimeShaderEditor.View;

/// <summary>The stand-in geometry the preview renders the authored shader on.</summary>
public enum PreviewShape
{
    Cube,
    Sphere,
    Cylinder,
    Plane,

    /// <summary>A real game mesh, loaded with <see cref="ShaderPreview.LoadMeshObj"/>. Falls back to the
    /// cube until one is loaded.</summary>
    Mesh,
}

/// <summary>
/// Renders the authored shader on a cube. Two passes, mirroring the engine: the cube writes the four GBuffer
/// targets through the generated pixel shader unmodified, then a resolve pass lights them. The authored shader
/// is therefore executed for real rather than reinterpreted, which is the only way the preview can tell the
/// truth about things like the smoothness response.
/// </summary>
public sealed class ShaderPreview : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Tangent;

        /// <summary>
        /// The handedness of the texture mapping at the vertex (+1 as laid out, −1 mirrored) — what the
        /// game's per-vertex BinormalSign carries; 0 (a primitive) reads as +1. Computed from the UV
        /// gradients when a mesh is loaded, so the tangent frame's determinant tells a mirrored half
        /// apart, the way it does in the game (the sticker side gate reads it).
        /// </summary>
        public float Handedness;
        public Vector2 TexCoord;

        /// <summary>
        /// The SECOND texture coordinate set, for the meshes that have one (every vehicle body does). The
        /// game's body preset samples its diffuse with one set and its normal map with the other, so a
        /// preview carrying a single set cannot draw it — see the UvPair note in PreviewShaders. A mesh
        /// without a second set carries a copy of the first, so every one-UV subject is untouched.
        /// </summary>
        public Vector2 TexCoord1;

        /// <summary>
        /// The THIRD slot of the dump (RSM7): the set a second uv interpolator carries — the jets'
        /// declaration adds a TexCoord2 after the bone indices and their decal preset samples its normal map
        /// with it. A dump without one repeats the first, like the second does.
        /// </summary>
        public Vector2 TexCoord2;
    }

    private const int c_TargetCount = 4;

    private Device? m_Device;
    private DeviceContext? m_Context;
    private SwapChain? m_SwapChain;
    private RenderTargetView? m_BackBufferView;
    private Texture2D? m_Output;

    private readonly Texture2D?[] m_Targets = new Texture2D?[c_TargetCount];
    private readonly RenderTargetView?[] m_TargetViews = new RenderTargetView?[c_TargetCount];
    private readonly ShaderResourceView?[] m_TargetSrvs = new ShaderResourceView?[c_TargetCount];
    private Texture2D? m_Depth;
    private DepthStencilView? m_DepthView;
    private ShaderResourceView? m_DepthSrv;

    private Buffer? m_CubeVertices;
    private Buffer? m_CubeIndices;
    private Buffer? m_VsConstants;
    private Buffer? m_ViewConstants;

    /// <summary>The target shader's own parameters (cb1). 64 registers covered every mp_017 shader measured; 128 since the emblem slot.</summary>
    private Buffer? m_ParameterConstants;

    /// <summary>Real material-instance values overriding pattern slots of cb1, as (element, xyzw).</summary>
    private List<(int Element, float[] Value)>? m_ExternalOverrides;

    /// <summary>
    /// Overwrites named external-parameter slots of cb1 with a material instance's REAL values, keeping the
    /// distinctive pattern everywhere else. EDITOR PREVIEW ONLY: the differential harnesses never call this -
    /// both of their sides must keep eating the identical pattern.
    /// </summary>
    public int SetExternalValues(IEnumerable<(int Element, float[] Value)>? p_Values)
    {
        m_ExternalOverrides = p_Values?.ToList();
        PushParameterBuffer();
        return m_ExternalOverrides?.Count ?? 0;
    }

    private void PushParameterBuffer()
    {
        if (m_Device == null || m_ParameterConstants == null)
            return;

        var s_Parameters = new RawVector4[c_ParameterSlots];
        for (var i = 0; i < c_ParameterSlots; i++)
            s_Parameters[i] = new RawVector4(0.10f + i * 0.037f, 0.65f - i * 0.021f,
                0.30f + i * 0.013f, 0.50f + i * 0.007f);

        foreach (var (s_Element, s_Value) in m_ExternalOverrides ?? new List<(int, float[])>())
            if (s_Element is >= 0 and < c_ParameterSlots && s_Value.Length >= 4)
                s_Parameters[s_Element] = new RawVector4(s_Value[0], s_Value[1], s_Value[2], s_Value[3]);

        m_Device.ImmediateContext.UpdateSubresource(s_Parameters, m_ParameterConstants);
    }

    /// <summary>DICE's `$Globals` (cb0): the outdoor light terms a forward-lit shader reads.</summary>
    private Buffer? m_GlobalConstants;

    // 128: the emblem slot's forty layers are eighty constants right after a weapon preset's twelve (c12..c91; EmblemSlot).
    private const int c_ParameterSlots = 128;
    private Buffer? m_LightConstants;

    private VertexShader? m_CubeVs;
    private InputLayout? m_CubeLayout;
    private VertexShader? m_ResolveVs;
    private PixelShader? m_ResolvePs;
    private PixelShader? m_ForwardResolvePs;
    private PixelShader? m_NeutralGBufferPs;
    private PixelShader? m_NeutralForwardPs;
    private DepthStencilState? m_DepthReadState;
    private BlendState? m_ForwardBlend;
    private RasterizerState? m_RasterNoCull;
    private PixelShader? m_AuthoredPs;

    /// <summary>True when the authored shader writes a single target (a forward/transparent solution):
    /// its output is finished premultiplied colour and takes the compositing resolve, not the lit one.</summary>
    private bool m_ForwardOutput;
    private SamplerState? m_Sampler;
    private RasterizerState? m_Raster;
    private RasterizerState? m_RasterMirrored;
    private DepthStencilState? m_DepthState;

    /// <summary>
    /// ⛔ THE GAME'S MESHES ARE RIGHT-HANDED AND THIS PIPELINE IS LEFT-HANDED (LookAtLH / PerspectiveFovLH):
    /// drawn as-is they come out as their MIRROR IMAGE — measured on the M4A1 (every weapon showed
    /// mirrored): the ejection port, forward assist and magazine release showed on the
    /// geometric LEFT side, the selector and bolt catch on the RIGHT. A loaded mesh is therefore drawn
    /// through a mirror of its lateral axis (muzzle and up untouched); the primitives, which every
    /// guardian render is pinned to, stay exactly as they were. Everything else lives in MESH space and
    /// goes through this one matrix: picks, projections, the overlay, the tangent frame's handedness.
    /// </summary>
    private static readonly Matrix s_MeshWorld = Matrix.Scaling(-1f, 1f, 1f);

    private Matrix World => m_Shape == PreviewShape.Mesh && m_MeshData != null ? s_MeshWorld : Matrix.Identity;

    private bool Mirrored => World.M11 < 0f;

    /// <summary>Whether the view draws its mesh through the lateral mirror (a loaded game mesh: always; a primitive: never).</summary>
    public bool MeshMirrored => Mirrored;

    /// <summary>A mirror flips the winding, so the mirrored world culls the other face; double-sided draws both.</summary>
    private RasterizerState? RasterFor(bool p_DoubleSided) =>
        p_DoubleSided ? m_RasterNoCull : Mirrored ? m_RasterMirrored : m_Raster;

    // --- the gizmo overlay: coloured line segments over the finished frame, in mesh space -------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayVertex
    {
        public Vector3 Position;
        public Vector4 Colour;
    }

    private VertexShader? m_OverlayVs;
    private PixelShader? m_OverlayPs;
    private InputLayout? m_OverlayLayout;
    private DepthStencilState? m_DepthNone;
    private Buffer? m_OverlayBuffer;
    private int m_OverlayCapacity;
    private OverlayVertex[] m_OverlayVertices = Array.Empty<OverlayVertex>();
    private bool m_OverlayDirty;

    /// <summary>One line segment of the overlay, both ends in the space the mesh is drawn in, ARGB colour.</summary>
    public readonly record struct OverlayLine(Vector3 A, Vector3 B, uint Colour);

    /// <summary>
    /// Replaces the overlay: what the sticker mode draws over the weapon (a sticker's outline projected on
    /// the surface, its handles). Empty clears it. Uploaded on the next frame; nothing here touches the
    /// GBuffer passes, so the differential harnesses — which never set an overlay — see the same frames.
    /// </summary>
    public void SetOverlayLines(IReadOnlyList<OverlayLine> p_Lines)
    {
        var s_Vertices = new OverlayVertex[p_Lines.Count * 2];
        for (var i = 0; i < p_Lines.Count; i++)
        {
            var s_Colour = ColourOf(p_Lines[i].Colour);
            s_Vertices[i * 2] = new OverlayVertex { Position = p_Lines[i].A, Colour = s_Colour };
            s_Vertices[i * 2 + 1] = new OverlayVertex { Position = p_Lines[i].B, Colour = s_Colour };
        }

        m_OverlayVertices = s_Vertices;
        m_OverlayDirty = true;
    }

    public void ClearOverlay()
    {
        SetOverlayLines(Array.Empty<OverlayLine>());
        SetOverlayPictures(null, 0, Array.Empty<PictureVertex>());
    }

    /// <summary>A corner of a picture laid over the view: where it is, in the space the mesh is drawn in, and where in the picture.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct PictureVertex(Vector3 Position, Vector2 Uv);

    private VertexShader? m_PictureVs;
    private PixelShader? m_PicturePs;
    private InputLayout? m_PictureLayout;
    private Buffer? m_PictureBuffer;
    private int m_PictureCapacity;
    private PictureVertex[] m_PictureVertices = Array.Empty<PictureVertex>();
    private bool m_PictureDirty;
    private ShaderResourceView? m_PictureView;
    private byte[]? m_PicturePixels;
    private int m_PictureSide;

    /// <summary>
    /// Pictures laid on the body, over the frame (a vehicle's emblem slot showed no sticker drawn, not even a sample): triangles of
    /// ONE square picture (premultiplied BGRA, <paramref name="p_Side"/> on a side, held by reference — the same
    /// array is uploaded once), tested against the depth of the drawn mesh without writing it, so a square on the far side stays hidden.
    /// Null clears them. Nothing here touches the GBuffer passes.
    /// </summary>
    /// <summary>How many picture corners the overlay draws (three per triangle) — for a driver.</summary>
    public int OverlayPictureVertices => m_PictureVertices.Length;

    public void SetOverlayPictures(byte[]? p_Pixels, int p_Side, IReadOnlyList<PictureVertex> p_Triangles)
    {
        if (!ReferenceEquals(p_Pixels, m_PicturePixels))
        {
            m_PictureView?.Dispose();
            m_PictureView = null;
            m_PicturePixels = p_Pixels;
            m_PictureSide = p_Side;
        }

        m_PictureVertices = p_Pixels == null || p_Side <= 0 ? Array.Empty<PictureVertex>() : p_Triangles.ToArray();
        m_PictureDirty = true;
    }

    private static Vector4 ColourOf(uint p_Argb) => new(
        ((p_Argb >> 16) & 0xFF) / 255f, ((p_Argb >> 8) & 0xFF) / 255f, (p_Argb & 0xFF) / 255f, (p_Argb >> 24) / 255f);

    /// <summary>The view-projection the frame is drawn with — the one thing a pick and a projection must share with Render.</summary>
    private Matrix ViewProjection() => ViewMatrix() * ProjectionMatrix();

    /// <summary>
    /// A camera put by hand instead of the orbit — the first-person eye (to simulate how the soldier would look in first person with
    /// the weapon in hand): the eye, the direction it looks and its up, in the space the mesh is DRAWN in (after its lateral
    /// mirror), and the vertical field of view in radians. Null = the orbit (Yaw, Pitch, Distance, Target).
    /// </summary>
    public (Vector3 Eye, Vector3 Look, Vector3 Up, float FovY)? FixedCamera
    {
        get => m_FixedCamera;
        set
        {
            if (m_FixedCamera == value)
                return;

            m_FixedCamera = value;
            FixedCameraChanged?.Invoke();
        }
    }

    private (Vector3 Eye, Vector3 Look, Vector3 Up, float FovY)? m_FixedCamera;

    /// <summary>Raised when the view goes onto the first-person eye, leaves it, or its field of view changes (the window's FOV bar follows).</summary>
    public event Action? FixedCameraChanged;

    private Vector3 EyePosition() => FixedCamera is { } s_Fixed ? s_Fixed.Eye : CameraPosition();

    private Matrix ViewMatrix() => FixedCamera is { } s_Fixed
        ? Matrix.LookAtLH(s_Fixed.Eye, s_Fixed.Eye + s_Fixed.Look, s_Fixed.Up)
        : Matrix.LookAtLH(CameraPosition(), Target, Vector3.UnitY);

    private Matrix ProjectionMatrix()
    {
        // The near plane follows the dolly so extreme closeups do not clip into the mesh: at 3 metres it sits at the old 0.05, right up
        // close it tightens to millimetres; the first-person eye sits a few centimetres from the weapon.
        var s_Near = FixedCamera != null ? 0.002f : Math.Clamp(Distance * 0.02f, 0.002f, 0.05f);
        return Matrix.PerspectiveFovLH(FixedCamera?.FovY ?? 0.9f, m_Width / (float) Math.Max(1, m_Height), s_Near, 100f);
    }

    /// <summary>Where a ray from a panel pixel meets the mesh: the point, the surface normal there, the texture coordinate, the section, and the body triangle (in <see cref="Surface"/>'s numbering).</summary>
    public readonly record struct SurfaceHit(Vector3 Position, Vector3 Normal, Vector2 Uv, int Section, float Distance, int Triangle = -1);

    private Graph.StickerSurface? m_Surface;
    private (string? Mesh, string Target, int Aliases, bool Every, int Solid) m_SurfaceKey;
    private Vector3 m_MeshCentre;
    private float m_MeshScale = 1f;

    /// <summary>
    /// The body as sticker mode sees it — the sections the authored shader draws, with their positions and
    /// unwrap — rebuilt when the mesh or the target changes. Triangles are numbered in the order
    /// <see cref="PickRanges"/> walks them, which is the order every pick here walks them too. It knows
    /// how the mesh was recentred and rescaled for the camera, so a placement's anchor can be kept in the
    /// mesh's own units and the bake, reading the raw dump, resolves the same point.
    /// </summary>
    public Graph.StickerSurface? Surface
    {
        get
        {
            if (m_MeshData == null)
                return null;

            var s_Key = (MeshPath, MeshTargetShader, MeshTargetAliases.Count, PickEverySection, PickEverySection ? m_SolidVersion : 0);
            if (m_Surface != null && m_SurfaceKey == s_Key)
                return m_Surface;

            var (s_Vertices, s_Indices) = m_MeshData.Value;
            var s_Positions = new System.Numerics.Vector3[s_Vertices.Length];
            var s_Uv = new System.Numerics.Vector2[s_Vertices.Length];
            for (var i = 0; i < s_Vertices.Length; i++)
            {
                s_Positions[i] = new System.Numerics.Vector3(s_Vertices[i].Position.X, s_Vertices[i].Position.Y, s_Vertices[i].Position.Z);
                s_Uv[i] = new System.Numerics.Vector2(s_Vertices[i].TexCoord.X, s_Vertices[i].TexCoord.Y);
            }

            var s_Kept = new List<int>();
            foreach (var (_, _, s_Start, s_Count) in PickRanges())
                for (var i = s_Start; i + 2 < s_Start + s_Count; i += 3)
                {
                    s_Kept.Add(s_Indices[i]);
                    s_Kept.Add(s_Indices[i + 1]);
                    s_Kept.Add(s_Indices[i + 2]);
                }

            m_Surface = s_Kept.Count == 0
                ? null
                : new Graph.StickerSurface(s_Positions, s_Uv, s_Kept.ToArray(),
                    new System.Numerics.Vector3(m_MeshCentre.X, m_MeshCentre.Y, m_MeshCentre.Z), m_MeshScale);
            m_SurfaceKey = s_Key;
            return m_Surface;
        }
    }

    /// <summary>
    /// Casts a ray from a pixel of the panel (0,0 top-left) through the loaded mesh and returns the nearest
    /// hit on a section the authored shader draws — the body, never the bullets or the tape a sticker cannot
    /// live on. Null when nothing is under the pixel or no mesh is loaded.
    /// </summary>
    public SurfaceHit? Raycast(float p_X, float p_Y)
    {
        if (m_MeshData == null || m_Width <= 0 || m_Height <= 0)
            return null;

        // The pixel's ray in WORLD space, then into MESH space through the world matrix's inverse — the
        // triangles are intersected where they are stored (a mirrored world flips the ray, not the mesh).
        var s_Inverse = Matrix.Invert(World * ViewProjection());
        var s_Ndc = new Vector2(p_X / m_Width * 2f - 1f, 1f - p_Y / m_Height * 2f);
        var s_Origin = Vector3.TransformCoordinate(new Vector3(s_Ndc, 0f), s_Inverse);
        var s_Far = Vector3.TransformCoordinate(new Vector3(s_Ndc, 1f), s_Inverse);
        var s_Direction = Vector3.Normalize(s_Far - s_Origin);

        var (s_Vertices, s_Indices) = m_MeshData.Value;
        SurfaceHit? s_Best = null;
        var s_Triangle = -1;

        foreach (var (s_SectionIndex, _, s_RangeStart, s_RangeCount) in PickRanges())
        {
            var s_End = s_RangeStart + s_RangeCount;
            for (var i = s_RangeStart; i + 2 < s_End; i += 3)
            {
                s_Triangle++;
                ref var s_A = ref s_Vertices[s_Indices[i]];
                ref var s_B = ref s_Vertices[s_Indices[i + 1]];
                ref var s_C = ref s_Vertices[s_Indices[i + 2]];

                // Möller–Trumbore, both faces: from outside a closed body the nearest hit is the visible one.
                var s_Edge1 = s_B.Position - s_A.Position;
                var s_Edge2 = s_C.Position - s_A.Position;
                var s_P = Vector3.Cross(s_Direction, s_Edge2);
                var s_Det = Vector3.Dot(s_Edge1, s_P);
                if (Math.Abs(s_Det) < 1e-9f)
                    continue;

                var s_InvDet = 1f / s_Det;
                var s_T = s_Origin - s_A.Position;
                var s_U = Vector3.Dot(s_T, s_P) * s_InvDet;
                if (s_U < 0f || s_U > 1f)
                    continue;

                var s_Q = Vector3.Cross(s_T, s_Edge1);
                var s_V = Vector3.Dot(s_Direction, s_Q) * s_InvDet;
                if (s_V < 0f || s_U + s_V > 1f)
                    continue;

                var s_Distance = Vector3.Dot(s_Edge2, s_Q) * s_InvDet;
                if (s_Distance <= 1e-5f || (s_Best != null && s_Distance >= s_Best.Value.Distance))
                    continue;

                var s_W = 1f - s_U - s_V;
                var s_Normal = Vector3.Normalize(s_A.Normal * s_W + s_B.Normal * s_U + s_C.Normal * s_V);
                var s_Uv = s_A.TexCoord * s_W + s_B.TexCoord * s_U + s_C.TexCoord * s_V;
                s_Best = new SurfaceHit(s_Origin + s_Direction * s_Distance, s_Normal, s_Uv,
                    s_SectionIndex, s_Distance, s_Triangle);
            }
        }

        return s_Best;
    }

    /// <summary>A mesh-space point on the panel, in pixels (0,0 top-left); null when it is behind the camera.</summary>
    public Vector2? ProjectToScreen(Vector3 p_Position)
    {
        if (m_Width <= 0 || m_Height <= 0)
            return null;

        var s_Clip = Vector4.Transform(new Vector4(p_Position, 1f), World * ViewProjection());
        if (s_Clip.W <= 1e-6f)
            return null;

        return new Vector2((s_Clip.X / s_Clip.W + 1f) * 0.5f * m_Width, (1f - s_Clip.Y / s_Clip.W) * 0.5f * m_Height);
    }

    /// <summary>The camera's position in MESH space (through the world matrix's inverse), for facing a surface point.</summary>
    public Vector3 CameraPoint => Vector3.TransformCoordinate(EyePosition(), Matrix.Invert(World));

    /// <summary>Converts a point or a direction of the mesh's own recentred space into the space it is drawn in (through its mirror).</summary>
    public Vector3 ToDrawn(Vector3 p_Mesh) => Vector3.TransformNormal(p_Mesh, World);

    /// <summary>
    /// The sections a sticker can live on: the ones the authored shader draws (target + aliases) — or, with <see cref="PickEverySection"/>,
    /// every section the view DRAWS solid (the last frame's), whatever shader draws it.
    /// </summary>
    private IEnumerable<(int Index, MeshSection Section, int Start, int Count)> PickRanges()
    {
        var s_All = MeshTargetShader.Length == 0 || (PickEverySection &&
                    (Environment.GetEnvironmentVariable("CAMO_SLOT_PICK_ALL") == "1" || m_SolidSections.Count == 0));
        for (var s_Index = 0; s_Index < m_MeshSections.Count; s_Index++)
        {
            var s_Section = m_MeshSections[s_Index];
            if (s_All)
                yield return (s_Index, s_Section, s_Section.StartIndex, s_Section.IndexCount);
            else if (!PickEverySection)
            {
                if (IsTargetSection(s_Section))
                    yield return (s_Index, s_Section, s_Section.StartIndex, s_Section.IndexCount);
            }
            else if (m_SolidSections.Contains(SolidKey(s_Index, -1)))
                yield return (s_Index, s_Section, s_Section.StartIndex, s_Section.IndexCount);
            else
                foreach (var (s_Part, s_Start, s_Count) in s_Section.Parts)
                    if (s_Count > 0 && m_SolidSections.Contains(SolidKey(s_Index, s_Part)))
                        yield return (s_Index, s_Section, s_Start, s_Count);
        }
    }

    /// <summary>A section drawn whole (part −1), or one part of it, as the solid set keeps it.</summary>
    private static long SolidKey(int p_Section, int p_Part) => ((long) p_Section << 32) | (uint) (p_Part + 1);

    // ⛔ 2nd (the 2S25 Sprut-SD: the stickers floating in the air came back): a section is not the unit — the
    // view draws some sections PART BY PART (VisibleParts: the Sprut's add-on panels are parts of its body section, not fitted), and the
    // parts it leaves out took the picture all the same. The set is what DrawSectionRanges drew in pass 1: a section whole, or its parts.
    // ⛔ (the BMP-2, a square over the hull's side: the sticker either floated in the air or came out cut up over several layers):
    // EVERY section was too many — the BMP-2's reactive armour (a kit the game equips apart, a hidden section here), its lights
    // twice over (on and off) and its glass lie over the hull, and the emblem's picture was laid on each of them, floating where the view
    // draws nothing. The sections that count are the ones the user SEES as body: drawn this frame, opaque. Kept from the last frame's pass
    // 1; a change of the set changes the surface (its key carries the version). CAMO_SLOT_PICK_ALL=1 = every section, as before.
    private HashSet<long> m_SolidSections = new();
    private readonly HashSet<long> m_SolidThisFrame = new();
    private int m_SolidVersion;
    private bool m_RecordingSolid;

    /// <summary>Raised from a frame whose solid sections differ from the frame before (see m_SolidSections).</summary>
    public event Action? SolidSectionsChanged;

    /// <summary>The sections the last frame drew solid (whole or in part), by index — for a driver that reports what an emblem square lies on.</summary>
    public IReadOnlyCollection<int> SolidSections => m_SolidSections.Select(p_K => (int) (p_K >> 32)).Distinct().ToList();

    /// <summary>
    /// Every section of the mesh is pickable and part of <see cref="Surface"/>, whatever shader draws it — a vehicle's emblem slot:
    /// the emblem is a decal there, and a decal lands on any part of the body, so a click must not pass through a part the
    /// camo's own shader does not draw. Off = the authored shader's sections, as a sticker needs.
    /// </summary>
    public bool PickEverySection { get; set; }

    /// <summary>
    /// One entry per texture register, indexed BY the register. It used to be two fields, t1 and t2, which was
    /// fine only for the oil drum: a lightmapped shader binds its material from t5, and the shader that hit it
    /// samples t1..t4. Everything above t2 therefore sampled (0,0,0,0), and since albedo and specular are both
    /// products of those samples, the object came out mathematically black - no error, no warning, just a black
    /// cube that reads as "the editor is broken".
    /// </summary>
    private readonly ShaderResourceView?[] m_Textures = new ShaderResourceView?[c_TextureSlots];

    private const int c_TextureSlots = 16;

    /// <summary>Sampler registers bound per draw — the same sixteen the pixel stage exposes.</summary>
    private const int c_SamplerSlots = 16;

    private int m_Width;
    private int m_Height;

    public float Yaw { get; set; } = 0.6f;
    public float Pitch { get; set; } = 0.4f;
    public float Distance { get; set; } = 3.2f;
    public float Exposure { get; set; } = 1.0f;
    public float LightYaw { get; set; } = 0.9f;
    public float LightPitch { get; set; } = 0.7f;

    /// <summary>Point the camera orbits and looks at; panning moves this rather than the camera.</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>Slides the orbit target across the screen plane, so panning feels attached to the view.</summary>
    public void Pan(float p_ScreenX, float p_ScreenY)
    {
        var s_Forward = Vector3.Normalize(Target - CameraPosition());
        var s_Right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, s_Forward));
        var s_Up = Vector3.Cross(s_Forward, s_Right);

        // Scale with distance so the drag tracks the pointer at any zoom.
        var s_Scale = Distance * 0.5f;
        Target += (-s_Right * p_ScreenX + s_Up * p_ScreenY) * s_Scale;
    }

    public void ResetView()
    {
        FixedCamera = null;
        Yaw = 0.6f;
        Pitch = 0.4f;
        Distance = 3.2f;
        Target = Vector3.Zero;
    }

    private Vector3 CameraPosition() => Target + new Vector3(
        (float) (Math.Cos(Pitch) * Math.Sin(Yaw)),
        (float) Math.Sin(Pitch),
        (float) (Math.Cos(Pitch) * Math.Cos(Yaw))) * Distance;

    /// <summary>Seconds fed to the shader's time input, so panners and curves animate.</summary>
    public float Time { get; set; }

    /// <summary>
    /// A cube shows tiling and normal maps best, but its faces are flat, so a tight specular highlight never
    /// lands on one and smoothness looks like it does nothing. The sphere is what makes that readable; the
    /// cylinder reads curvature + tiling together, and the plane is the honest view for decals and terrain.
    /// </summary>
    public PreviewShape Shape
    {
        get => m_Shape;
        set
        {
            if (m_Shape == value)
                return;

            m_Shape = value;
            if (m_Device != null)
                BuildGeometry();
        }
    }

    /// <summary>
    /// The historical two-shape switch, kept as a facade because every harness seam (PREVIEW_SPHERE, the
    /// sweep, shaderdiff) speaks it — the guardian renders must keep their exact geometry.
    /// </summary>
    public bool UseSphere
    {
        get => m_Shape == PreviewShape.Sphere;
        set => Shape = value ? PreviewShape.Sphere : PreviewShape.Cube;
    }

    private PreviewShape m_Shape;
    private int m_IndexCount;
    private Format m_IndexFormat = Format.R16_UInt;
    private (Vertex[] Vertices, int[] Indices)? m_MeshData;

    /// <summary>One material section of a loaded game mesh: an index range plus the shader that wears it.
    /// Category is the game's own pass bucket (0 opaque, 1 transparent, 2 transparent decal) — a foreign
    /// TRANSPARENT section is skipped rather than stood in for, because an opaque grey rotor disc or canopy
    /// occludes the whole object. DoubleSided comes from the shader's own solutions (Flags bit 1) and turns
    /// culling off for that section — a canopy is visible from inside, foliage from both sides.</summary>
    public readonly record struct MeshSection(string Shader, string Material, int Category, bool DoubleSided,
        int StartIndex, int IndexCount)
    {
        /// <summary>
        /// The composite PARTS this section is made of, as contiguous index ranges (the loader groups the
        /// triangles by part so each one can be drawn — or left out — on its own).
        ///
        /// ⛔ A section of a vehicle is ONE draw and several OBJECTS: the kit section carries the stowage AND
        /// the reactive-armour blocks, which the game equips separately. Empty for everything that has no
        /// parts (every weapon, and any dump older than RSM6), and then the section is drawn whole.
        /// </summary>
        public IReadOnlyList<(int Part, int Start, int Count)> Parts { get; init; } =
            Array.Empty<(int, int, int)>();

        /// <summary>
        /// The game mesh this section was read from when it came from a CONTEXT mesh — the rest of a soldier drawn around the part
        /// being authored (the preview is the whole character, head included) — or empty. A context section is never
        /// the edited shader's own, whatever it wears: it is someone else's material, shown as it ships.
        /// </summary>
        public string Context { get; init; } = "";

        /// <summary>For a context section, its index among that mesh's own sections (the order its dump and material list have).</summary>
        public int ContextSection { get; init; }
    }

    private readonly List<MeshSection> m_MeshSections = new();

    /// <summary>The shader the editor is editing, for deciding which mesh sections are "ours".</summary>
    public string MeshTargetShader { get; set; } = "";

    /// <summary>
    /// Other shader names whose sections ALSO count as the edited shader's own — the presets it replaces.
    /// A camo is authored on a preset that substitutes the NoCamo one a weapon's body wears; to a plain
    /// name match the body was foreign, drawn with the game's own bytecode, and the graph being edited
    /// never reached it. Empty outside that use.
    /// </summary>
    public IReadOnlyCollection<string> MeshTargetAliases { get; set; } = Array.Empty<string>();

    /// <summary>Whether a section is drawn with the authored shader: the target's own, or one it replaces.</summary>
    public bool IsTargetSection(MeshSection p_Section)
    {
        // A soldier's helmet and legs wear the same CharacterRoot as the torso being authored, and are still not the torso's.
        if (p_Section.Context.Length > 0)
            return false;

        var s_Target = MeshTargetShader.Replace('\\', '/');
        return p_Section.Shader.Equals(s_Target, StringComparison.OrdinalIgnoreCase) ||
               MeshTargetAliases.Contains(p_Section.Shader, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>When set, mesh sections worn by OTHER shaders are not drawn at all (the Settings option).</summary>
    public bool HideForeignSections { get; set; }

    /// <summary>
    /// Indices (into <see cref="MeshSections"/>) of OWN sections the camo is kept off: each draws with the
    /// game's bytecode of the shader it wears, as it ships, while the rest of the object takes the authored
    /// shader — the user's per-material choice. Empty = every own section takes it.
    /// </summary>
    public ISet<int> SectionsOff { get; set; } = new HashSet<int>();

    /// <summary>When set, only that section (index into <see cref="MeshSections"/>) is drawn — to SEE which material an ID is.</summary>
    public int? IsolatedSection { get; set; }

    /// <summary>
    /// When set, the authored shader draws ONLY that section (index into <see cref="MeshSections"/>) — the
    /// material whose graph is on the canvas — and every other section, the target's siblings included,
    /// draws as foreign. Null = every target section takes the authored shader.
    /// </summary>
    public int? TargetSectionOnly { get; set; }

    /// <summary>
    /// Per section (index into <see cref="MeshSections"/>), the registered foreign shader it draws with
    /// instead of whatever its name or the factory look would pick: an edited material's own compiled
    /// graph, or the camo compiled for the body while another material is being edited.
    /// </summary>
    public IDictionary<int, string> SectionOverrides { get; set; } = new Dictionary<int, string>();

    /// <summary>The override registered for a section, if any.</summary>
    private ForeignShader? OverrideFor(int p_Index) =>
        SectionOverrides.TryGetValue(p_Index, out var s_Name) && m_ForeignShaders.TryGetValue(s_Name, out var s_Found)
            ? s_Found
            : null;

    /// <summary>
    /// Per section, the registered shader that is the AUTHORED one carrying THAT MATERIAL's own art — so
    /// every material of an object wears the same camo over its own textures and numbers.
    ///
    /// ⛔ WHY IT EXISTS: the authored shader holds ONE texture set for the whole mesh, which is right for a
    /// weapon (one body material) and wrong for anything with several. Measured on the LAV-25: its hull and
    /// its ATGM launchers both wear vehiclepreset_mud, so the single set dressed the hull in the launchers'
    /// art and the vehicle drew flat grey under any camo.
    ///
    /// Set by the window, which owns the registrations; empty means "one set for the whole mesh", the
    /// behaviour every weapon had before. A material being EDITED still wins through
    /// <see cref="SectionOverrides"/>, and the factory look is untouched — as shipped, each section already
    /// draws with the game's own shader and the slot map's per-mesh set.
    /// </summary>
    public IDictionary<int, string> MaterialDress { get; set; } = new Dictionary<int, string>();

    /// <summary>The per-material dress of a section while the camo is being shown, if there is one.</summary>
    private ForeignShader? DressFor(int p_Index) =>
        !FactoryLook && MaterialDress.TryGetValue(p_Index, out var s_Name) &&
        m_ForeignShaders.TryGetValue(s_Name, out var s_Found)
            ? s_Found
            : null;

    /// <summary>
    /// When set, the sections the authored shader would draw are drawn with the game's OWN bytecode of the
    /// shader each one wears — registered as foreign under that name — so the object looks as it ships, with
    /// nothing of the graph on it. The target and its aliases still decide which sections are "own" for
    /// everything else (sticker surfaces, the section count); only the DRAW is handed to the game's shader.
    /// A section whose shader is not registered falls back to the neutral stand-in, as any foreign one does.
    /// </summary>
    public bool FactoryLook { get; set; }

    /// <summary>
    /// Under the factory look, the registered foreign shader every OWN section draws with — the game's
    /// no-camo preset — instead of each section's own; null draws each with the shader it wears.
    /// </summary>
    public string? FactoryShader { get; set; }

    /// <summary>
    /// The opaque sections the NEXT frame would draw with the neutral grey stand-in, by name — the same
    /// resolution the draw makes (own and authored, a dress, the factory look, a foreign registration), read
    /// only. A seam's measure of "the object came out grey" (switching between vehicles or weapons sometimes
    /// left the shaders unapplied and the object grey): a frame hash cannot say WHICH section
    /// lost its shader; this can.
    /// </summary>
    internal List<string> NeutralSections()
    {
        var s_Out = new List<string>();
        if (m_Shape != PreviewShape.Mesh || m_MeshSections.Count == 0 || MeshTargetShader.Length == 0)
            return s_Out;

        for (var s_Index = 0; s_Index < m_MeshSections.Count; s_Index++)
        {
            var s_Section = m_MeshSections[s_Index];
            if (IsolatedSection is { } s_Only && s_Only != s_Index)
                continue;
            if (HiddenSections.Contains(s_Index) && IsolatedSection != s_Index && TargetSectionOnly != s_Index)
                continue;

            var s_Own = IsTargetSection(s_Section);
            var s_Off = s_Own && SectionsOff.Contains(s_Index);
            var s_Dress = s_Own && !s_Off && TargetSectionOnly == null ? DressFor(s_Index) : null;
            var s_Mine = s_Own && !FactoryLook && !s_Off && s_Dress == null && OverrideFor(s_Index) == null &&
                         (TargetSectionOnly == null || TargetSectionOnly == s_Index);
            var s_Foreign = s_Mine
                ? null
                : OverrideFor(s_Index) ?? s_Dress ??
                  (s_Own && !s_Off
                      ? FactoryShaderFor(s_Index, s_Section)
                      : SectionArtFor(s_Index) ??
                        (m_ForeignShaders.TryGetValue(s_Section.Shader, out var s_Found) ? s_Found : null));

            if (!s_Own && HideForeignSections && OverrideFor(s_Index) == null)
                continue;

            // A section whose material names no shader at all (the Su-35's PlaneSkeleton_M, a cockpit's
            // Main_LOD) is grey on every visit, by the dump: said apart from one whose shader was not applied.
            if (!s_Mine && s_Foreign == null && s_Section.Category == 0)
                s_Out.Add(s_Section.Shader.Length == 0
                    ? $"#{s_Index} (no shader in the dump)"
                    : $"#{s_Index} {s_Section.Shader.Split('/')[^1]}{(s_Own ? " (own)" : "")}");
        }

        return s_Out;
    }

    /// <summary>The registered foreign shader an own section draws with under the factory look, if any.</summary>
    private ForeignShader? FactoryShaderFor(int p_Index, MeshSection p_Section) =>
        FactoryShader != null && m_ForeignShaders.TryGetValue(FactoryShader, out var s_Preset)
            ? s_Preset
            : SectionArtFor(p_Index) ??
              (m_ForeignShaders.TryGetValue(p_Section.Shader, out var s_Own) ? s_Own : null);

    /// <summary>
    /// Per section, the registration that carries THAT MATERIAL's own shipped art — the game's own shader for
    /// it, registered once per material rather than once per shader NAME.
    ///
    /// ⛔ WHY IT EXISTS (the goal: every material must look right, whatever happens): a registration keyed
    /// by shader name is keyed by the wrong thing. Two materials of one
    /// object routinely wear the SAME shader — the LAV-25's hull and its ATGM launchers are both
    /// vehiclepreset_mud, the L85A2's body and its sight rail are both weaponpreset3p — so the first one
    /// registered lent its textures to the other. <see cref="MaterialDress"/> already fixed that for the camo
    /// being authored; this is the same fix for everything drawn AS SHIPPED, which is what the object looks
    /// like before anything is picked and what half the material panel shows.
    /// </summary>
    public IDictionary<int, string> SectionArt { get; set; } = new Dictionary<int, string>();

    /// <summary>
    /// Per CONTEXT section, the registration of the document of the part it belongs to — the rest of a soldier drawn with the skin being
    /// made on it, not as it ships (the whole soldier with the whole skin). Wins over <see cref="SectionArt"/>; empty
    /// means every context section as it ships.
    /// </summary>
    public IDictionary<int, string> ContextDress { get; set; } = new Dictionary<int, string>();

    /// <summary>That section's own-art registration, if the window made one — or its part's document, for context dressed with one.</summary>
    private ForeignShader? SectionArtFor(int p_Index) =>
        ContextDress.TryGetValue(p_Index, out var s_Dressed) && m_ForeignShaders.TryGetValue(s_Dressed, out var s_Part)
            ? s_Part
            : SectionArt.TryGetValue(p_Index, out var s_Name) && m_ForeignShaders.TryGetValue(s_Name, out var s_Found)
            ? s_Found
            : null;

    /// <summary>
    /// Everything one FOREIGN shader needs to draw its sections for real: its own game bytecode, a vertex
    /// shader generated for ITS contract (feeding another family's pixel shader through the active
    /// shader's interpolator layout would hand every input a different meaning), and its own texture set.
    /// </summary>
    private sealed class ForeignShader : IDisposable
    {
        public PixelShader? Ps;
        public VertexShader? Vs;
        public InputLayout? Layout;
        public ShaderResourceView?[] Textures = new ShaderResourceView?[c_TextureSlots];
        public bool Forward;

        /// <summary>Its own external-constants buffer, bound over the authored parameter pattern.</summary>
        public Buffer? Params;

        public int ParamsRegister = 1;

        /// <summary>The bytecode it was registered with — the layout its constants are rebuilt from (SetForeignValues).</summary>
        public byte[] Dxbc = Array.Empty<byte>();

        /// <summary>The material values its constants were last built from — kept to rebuild them when only the emblem changes.</summary>
        public IReadOnlyDictionary<string, string>? MaterialValues;

        /// <summary>
        /// What its own vertex shader feeds each interpolator, as classified from ITS bytecode. Readable
        /// because "the art and the values are right and it still looks wrong" is almost always this: an
        /// interpolator fed a different quantity than the pixel shader reads (a UV pair against one set,
        /// a world position where a UV belongs). A registration that cannot classify says so.
        /// </summary>
        public string Interpolators = "(not classified)";

        /// <summary>
        /// The registers it declares as a texture CUBE. They must never fall back to the authored shader's
        /// set: that art is 2D, and sampling a 2D view through a cube declaration is undefined.
        /// </summary>
        public HashSet<int> CubeRegisters = new();

        /// <summary>The registers its own instructions sample as DXT5nm NORMAL maps (resource swizzle .xywz).</summary>
        public HashSet<int> NormalRegisters = new();

        /// <summary>
        /// The sampler STATE the game binds to each sampler register, when the cached slot map knows it.
        /// Entries are owned by the preview's cache, never disposed here.
        ///
        /// ⛔ Addressing is not in the bytecode and some shaders USE it as an operation: the kit atlas shader
        /// adds its tile atlas sampled at (u, v+1), which the game's `v=Border` turns into "add nothing" for
        /// every piece whose V is positive. Drawn through a Wrap sampler, that fetch lands back inside the
        /// atlas and prints the tile band over the piece.
        /// </summary>
        public SamplerState?[] Samplers = new SamplerState?[c_SamplerSlots];

        /// <summary>What was bound, in words, for the window's Output — a fallback must never pass for a measure.</summary>
        public string SamplerNote = "(none in the cached map — bound as wrap)";

        public void Dispose()
        {
            Ps?.Dispose();
            Vs?.Dispose();
            Layout?.Dispose();
            Params?.Dispose();
            foreach (var s_Texture in Textures)
                s_Texture?.Dispose();
        }
    }

    private readonly Dictionary<string, ForeignShader> m_ForeignShaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a foreign shader (one worn by mesh sections the editor is NOT editing) so its sections
    /// render with the REAL game pixel shader and art. Returns an error string, or null. Textures arrive
    /// as decoded BGRA pixel blocks keyed by the register the shader's own binding table names, each with
    /// the game's sRGB flag for that texture (its view is created accordingly, see <see cref="CreateTexture"/>).
    /// </summary>
    public string? RegisterForeignShader(string p_Name, byte[] p_Dxbc,
        IEnumerable<(int Slot, uint[] Pixels, int Width, int Height, bool Srgb)> p_Textures,
        IReadOnlyDictionary<string, string>? p_MaterialValues = null,
        IReadOnlyDictionary<string, string>? p_Samplers = null)
    {
        if (m_Device == null)
            return "no device";

        try
        {
            var s_Contract = Emit.ShaderContract.Detect(p_Dxbc);
            try
            {
                var s_Disassembly = new ShaderBytecode(p_Dxbc).Disassemble();
                s_Contract.ClassifyInterpolators(Translate.DxbcAsm.Parse(s_Disassembly.Split('\n')));
            }
            catch
            {
                // The shape still renders; only the interpolator FEED quality degrades.
            }

            var s_Foreign = new ForeignShader
            {
                Ps = new PixelShader(m_Device, p_Dxbc),
                Forward = s_Contract.RenderTargets == 1,
                Dxbc = p_Dxbc,
            };

            // Which of its registers are CUBES, read from its own declarations — the binding below must not
            // hand those the authored shader's 2D art.
            try
            {
                foreach (var s_Line in new ShaderBytecode(p_Dxbc).Disassemble().Split('\n'))
                {
                    var s_Trim = s_Line.Trim();

                    // …and which it reads as a NORMAL map: a CONTEXT section's empty register gets a flat normal there (see DrawSectionWith)
                    if (s_Trim.StartsWith("sample", StringComparison.Ordinal) &&
                        System.Text.RegularExpressions.Regex.Match(s_Trim, @",\s*t(\d+)\.xywz\b") is { Success: true } s_Normal)
                        s_Foreign.NormalRegisters.Add(int.Parse(s_Normal.Groups[1].Value));

                    if (!s_Trim.StartsWith("dcl_resource_texturecube", StringComparison.Ordinal))
                        continue;

                    var s_Token = s_Trim.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    if (s_Token is { Length: > 1 } && s_Token[0] == 't' &&
                        int.TryParse(s_Token[1..], out var s_CubeRegister))
                        s_Foreign.CubeRegisters.Add(s_CubeRegister);
                }
            }
            catch
            {
                // No declarations read: the fallback below simply stays as it was.
            }

            s_Foreign.Interpolators = s_Contract.InterpolatorMeanings is { Count: > 0 } s_Meanings
                ? string.Join(" ", s_Meanings.OrderBy(p_M => p_M.Key).Select(p_M => $"{p_M.Key}:{p_M.Value}"))
                : "(none — the rigid default layout)";

            var s_VsCode = ShaderBytecode.Compile(
                PreviewShaders.VertexShaderFor(s_Contract.InterpolatorMeanings), "main", "vs_5_0");
            s_Foreign.Vs = new VertexShader(m_Device, s_VsCode);
            s_Foreign.Layout = new InputLayout(m_Device, s_VsCode, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
                new InputElement("TANGENT", 1, Format.R32_Float, 36, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
                new InputElement("TEXCOORD", 1, Format.R32G32_Float, 48, 0),
                new InputElement("TEXCOORD", 2, Format.R32G32_Float, 56, 0),
            });

            foreach (var (s_Slot, s_Pixels, s_Width, s_Height, s_Srgb) in p_Textures)
                if (s_Slot >= 1 && s_Slot < c_TextureSlots)
                    s_Foreign.Textures[s_Slot] = CreateTexture(s_Width, s_Height, s_Pixels, s_Srgb);

            // The game's own addressing per sampler register, when the cached map carries it. A register it
            // does not name keeps the preview's wrapping sampler, and the note says which registers were
            // really bound — a silent fallback is indistinguishable from a measurement downstream.
            if (p_Samplers is { Count: > 0 })
            {
                var s_Bound = new List<string>();
                foreach (var (s_Register, s_Modes) in p_Samplers)
                {
                    if (!int.TryParse(s_Register, out var s_Index) ||
                        s_Index < 0 || s_Index >= c_SamplerSlots ||
                        SamplerForModes(s_Modes) is not { } s_State)
                        continue;

                    s_Foreign.Samplers[s_Index] = s_State;
                    s_Bound.Add($"s{s_Index}={s_Modes.Replace(",", "/")}");
                }

                if (s_Bound.Count > 0)
                    s_Foreign.SamplerNote = string.Join(" ", s_Bound.OrderBy(p_S => p_S, StringComparer.Ordinal));
            }

            BuildForeignConstants(s_Foreign, p_Dxbc, p_MaterialValues);

            if (m_ForeignShaders.TryGetValue(p_Name, out var s_Old))
                s_Old.Dispose();
            m_ForeignShaders[p_Name] = s_Foreign;
            return null;
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message;
        }
    }

    /// <summary>
    /// Builds a foreign shader's OWN external-constants buffer from its RDEF layout. Without it the foreign
    /// draw read the authored graph's cb1 — a buffer laid out for a DIFFERENT shader and deliberately filled
    /// with a distinctive test pattern — so every camo tint, tiling and wear factor came out as arbitrary
    /// values (the jet's fuselage rendered violet). Fields default to 1 (the multiplicative neutral) and the
    /// level's own material instance values (CamoBrightness, CamoTiling…) overwrite the ones they name.
    /// </summary>
    private void BuildForeignConstants(ForeignShader p_Foreign, byte[] p_Dxbc,
        IReadOnlyDictionary<string, string>? p_MaterialValues)
    {
        var s_Fields = Emit.DxbcSignature.ReadConstantFields(p_Dxbc)
            .Where(p_F => p_F.Name.StartsWith("external_", StringComparison.OrdinalIgnoreCase) &&
                          p_F.Register >= 0 && p_F.Offset >= 0 && p_F.Size > 0)
            .ToList();

        if (s_Fields.Count == 0)
            return;

        var s_Floats = (s_Fields.Max(p_F => p_F.Offset + p_F.Size) + 15) / 16 * 4;
        var s_Data = new float[s_Floats];
        Array.Fill(s_Data, 1f);
        p_Foreign.MaterialValues = p_MaterialValues;

        // The emblem slot's layers (external_EmblemL0..) are never the neutral 1 — a layer of ones is a black circle over the slot
        // (the as-shipped M416's, 2026-09-29): the preview's sample emblem, or zero (no layer), as the game's table defaults them.
        var s_Emblem = Graph.EmblemSlot.PreviewValues();
        var s_EmblemPrefix = "external_" + Graph.Palette.EmblemConstantPrefix;
        // (and a projected slot's frames, external_EmblemF0..: the weapon on screen's, or zero — a frame of ones would draw a square)
        var s_Frames = Graph.EmblemSlot.FramesOf(Graph.EmblemSlot.PreviewSlots);
        var s_FramePrefix = "external_" + Graph.EmblemSlot.FramePrefix;

        foreach (var s_Field in s_Fields)
        {
            if (s_Field.Name.StartsWith(s_EmblemPrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(s_Field.Name[s_EmblemPrefix.Length..], out var s_Layer))
            {
                var s_Vector = s_Emblem != null && s_Layer >= 0 && s_Layer < s_Emblem.Length ? s_Emblem[s_Layer] : new float[4];
                for (var i = 0; i < 4 && i < s_Field.Size / 4 && s_Field.Offset / 4 + i < s_Data.Length; i++)
                    s_Data[s_Field.Offset / 4 + i] = i < s_Vector.Length ? s_Vector[i] : 0f;
                continue;
            }

            if (s_Field.Name.StartsWith(s_FramePrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(s_Field.Name[s_FramePrefix.Length..], out var s_Frame))
            {
                var s_Vector = s_Frame >= 0 && s_Frame < s_Frames.Length ? s_Frames[s_Frame] : new float[4];
                for (var i = 0; i < 4 && i < s_Field.Size / 4 && s_Field.Offset / 4 + i < s_Data.Length; i++)
                    s_Data[s_Field.Offset / 4 + i] = s_Vector[i];
                continue;
            }

            var s_Value = p_MaterialValues?.FirstOrDefault(p_V =>
                s_Field.Name.Equals("external_" + p_V.Key, StringComparison.OrdinalIgnoreCase) ||
                s_Field.Name.Equals(p_V.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (s_Value == null)
                continue;

            var s_Parts = s_Value.Split(',');
            for (var i = 0; i < s_Parts.Length && i < s_Field.Size / 4; i++)
                if (float.TryParse(s_Parts[i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s_Component) &&
                    s_Field.Offset / 4 + i < s_Data.Length)
                    s_Data[s_Field.Offset / 4 + i] = s_Component;
        }

        p_Foreign.ParamsRegister = s_Fields[0].Register;
        p_Foreign.Params = Buffer.Create(m_Device, BindFlags.ConstantBuffer, s_Data);
    }

    public bool HasForeignShader(string p_Name) => m_ForeignShaders.ContainsKey(p_Name);

    /// <summary>Rebuilds every registered foreign shader's constants with the material values they had — after the preview's emblem changed.</summary>
    public void RefreshEmblemValues()
    {
        if (m_Device == null)
            return;

        foreach (var s_Foreign in m_ForeignShaders.Values.Where(p_F => p_F.Dxbc.Length > 0))
        {
            s_Foreign.Params?.Dispose();
            s_Foreign.Params = null;
            BuildForeignConstants(s_Foreign, s_Foreign.Dxbc, s_Foreign.MaterialValues);
        }
    }

    /// <summary>
    /// Re-feeds a registered foreign shader's external constants — the accessory look follows the camo's
    /// sliders, which fire on every tick, and a number must not cost a recompile. False when unregistered.
    /// </summary>
    public bool SetForeignValues(string p_Name, IReadOnlyDictionary<string, string>? p_MaterialValues)
    {
        if (m_Device == null || !m_ForeignShaders.TryGetValue(p_Name, out var s_Foreign) || s_Foreign.Dxbc.Length == 0)
            return false;

        s_Foreign.Params?.Dispose();
        s_Foreign.Params = null;
        BuildForeignConstants(s_Foreign, s_Foreign.Dxbc, p_MaterialValues);
        return true;
    }

    /// <summary>
    /// Replaces one texture slot of a registered foreign shader — the sticker layer of the shader that draws
    /// the weapon as shipped with its stickers, recomposed on every placement. False when the shader is not
    /// registered or the slot is out of range.
    /// </summary>
    public bool SetForeignTexture(string p_Name, int p_Slot, uint[] p_Pixels, int p_Width, int p_Height, bool p_Srgb)
    {
        if (m_Device == null || p_Slot < 1 || p_Slot >= c_TextureSlots || !m_ForeignShaders.TryGetValue(p_Name, out var s_Foreign))
            return false;

        s_Foreign.Textures[p_Slot]?.Dispose();
        s_Foreign.Textures[p_Slot] = CreateTexture(p_Width, p_Height, p_Pixels, p_Srgb);
        return true;
    }

    /// <summary>
    /// Forgets every registered foreign shader, so the next object registers its own.
    ///
    /// ⛔ THESE ARE KEYED BY SHADER BUT HOLD THE OBJECT'S ART. Two weapons wear the same
    /// `bullets_base`/`aimingdots`, and the registration is skipped when the name is already known — so the
    /// SECOND weapon drew its magazine, sights and barrel with the FIRST one's textures, over its own UVs.
    /// It reads as "the UVs are broken", and it needed nothing more than opening two weapons in a row.
    /// </summary>
    public void ClearForeignShaders()
    {
        foreach (var s_Foreign in m_ForeignShaders.Values)
            s_Foreign.Dispose();

        m_ForeignShaders.Clear();
        ForeignGeneration++;
    }

    /// <summary>
    /// How many times the foreign registry has been emptied. Anything that CACHES which foreign shaders it
    /// registered — the per-material dress — has to carry this in its key: a wipe leaves that cache naming
    /// registrations that no longer resolve, and the sections then fall silently back to the single texture
    /// set the authored shader holds, which is whichever material was dressed last. Nothing reports it.
    /// </summary>
    public int ForeignGeneration { get; private set; }

    /// <summary>What a registered foreign shader feeds its interpolators — for the Output to say it.</summary>
    public string ForeignInterpolatorsOf(string p_Name) =>
        m_ForeignShaders.TryGetValue(p_Name, out var s_Found) ? s_Found.Interpolators : "(not registered)";

    /// <summary>The sampler addressing a registered foreign shader was bound with, in words.</summary>
    public string ForeignSamplersOf(string p_Name) =>
        m_ForeignShaders.TryGetValue(p_Name, out var s_Found) ? s_Found.SamplerNote : "(not registered)";

    /// <summary>
    /// The addressing the AUTHORED shader draws with — the state the game binds to the shader this one stands
    /// in for. It is the same law as for a foreign section: a shader whose sampler clamps or borders (the kit
    /// atlas one, the lamps, a scope glass) draws a different picture through a wrapping sampler, and the
    /// canvas is exactly where those get authored. Empty entries keep the preview's own sampler.
    /// </summary>
    private readonly SamplerState?[] m_AuthoredSamplers = new SamplerState?[c_SamplerSlots];

    public string AuthoredSamplerNote { get; private set; } = "(none in the cached map — bound as wrap)";

    /// <summary>Points the authored draw at the game's own addressing for its target shader.</summary>
    public void SetAuthoredSamplers(IReadOnlyDictionary<string, string>? p_Samplers)
    {
        Array.Clear(m_AuthoredSamplers);
        AuthoredSamplerNote = "(none in the cached map — bound as wrap)";
        if (p_Samplers is not { Count: > 0 })
            return;

        var s_Bound = new List<string>();
        foreach (var (s_Register, s_Modes) in p_Samplers)
        {
            if (!int.TryParse(s_Register, out var s_Index) ||
                s_Index < 0 || s_Index >= c_SamplerSlots ||
                SamplerForModes(s_Modes) is not { } s_State)
                continue;

            m_AuthoredSamplers[s_Index] = s_State;
            s_Bound.Add($"s{s_Index}={s_Modes.Replace(",", "/")}");
        }

        if (s_Bound.Count > 0)
            AuthoredSamplerNote = string.Join(" ", s_Bound.OrderBy(p_S => p_S, StringComparer.Ordinal));
    }

    /// <summary>
    /// One sampler state per distinct addressing triple ("Wrap,Border,Border"), created on demand and kept
    /// for the lifetime of the device: the same three or four states serve every shader, and building one
    /// per registration would leak a D3D object on each re-dress.
    /// </summary>
    private readonly Dictionary<string, SamplerState> m_SamplerCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The sampler for one "U,V,W" triple as the game's shaderdb records it, or null when it cannot be read
    /// (the caller then keeps the preview's own wrapping sampler and SAYS so).
    /// </summary>
    private SamplerState? SamplerForModes(string? p_Modes)
    {
        if (m_Device == null || p_Modes is not { Length: > 0 })
            return null;

        if (m_SamplerCache.TryGetValue(p_Modes, out var s_Cached))
            return s_Cached;

        var s_Parts = p_Modes.Split(',', StringSplitOptions.TrimEntries);
        if (s_Parts.Length < 3)
            return null;

        static TextureAddressMode? Parse(string p_Mode) =>
            Enum.TryParse<TextureAddressMode>(p_Mode, true, out var s_Mode) ? s_Mode : null;

        if (Parse(s_Parts[0]) is not { } s_U || Parse(s_Parts[1]) is not { } s_V || Parse(s_Parts[2]) is not { } s_W)
            return null;

        // ⛔ The border colour is TRANSPARENT BLACK, which is what the game's kit shaders rely on: their fetch
        // outside [0,1] has to contribute nothing to a sum. D3D11's own default is the same; it is spelled out
        // here because the whole point of the mode is what it returns out there.
        var s_State = new SamplerState(m_Device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = s_U,
            AddressV = s_V,
            AddressW = s_W,
            BorderColor = new RawColor4(0f, 0f, 0f, 0f),
            ComparisonFunction = Comparison.Never,
            MaximumLod = float.MaxValue,
        });

        m_SamplerCache[p_Modes] = s_State;
        return s_State;
    }

    /// <summary>The loaded mesh's sections, for the editor to know which foreign shaders to fetch.</summary>
    public IReadOnlyList<MeshSection> MeshSections => m_MeshSections;

    /// <summary>
    /// Loads a sectioned game-mesh dump (the RSM1 file dump_mesh_sections writes): per section, the shader
    /// that wears it plus positions/UVs and RELATIVE indices. Normals are accumulated from faces (the game
    /// packs its frame in formats the dump does not carry), tangents from the UV gradients, and the whole
    /// mesh is recentred and scaled to the primitives' size so the camera framing works unchanged.
    /// Returns an error string, or null on success.
    /// </summary>
    public string? LoadMeshSections(string p_Path) => LoadMeshSections(p_Path, null);

    /// <summary>
    /// A mesh drawn TOGETHER with the main one, moved to where the object carries it.
    ///
    /// ⛔ WHY IT EXISTS: a tracked vehicle's running belt is NOT in its body mesh — it is its own skinned mesh
    /// (`vehicles/m1a2/m1a2_tracks_Mesh`, 9 of them in the game) whose bind pose is already the belt wrapped
    /// round the road wheels, but which sits in ITS OWN space, centred on x. Where each of the two belts goes
    /// is the vehicle's business, not the mesh's; the body's part anchors are what say it.
    /// </summary>
    /// <param name="Context">
    /// The game mesh it is, when it is drawn as CONTEXT around the subject (the rest of a soldier: helmet, head, legs around the torso
    /// being authored) — its sections then keep their own identity (<see cref="MeshSection.Context"/>) and are never the edited shader's.
    /// Empty for a piece of the object itself (a tracked vehicle's belts), which behaves exactly as it always did.
    /// </param>
    public readonly record struct MeshInstance(string Path, Vector3 Offset, bool MirrorX, string Context = "");

    public string? LoadMeshSections(string p_Path, IReadOnlyList<MeshInstance>? p_Extras)
    {
        try
        {
            var s_Vertices = new List<Vertex>();
            var s_IndexList = new List<int>();
            var s_Sections = new List<MeshSection>();

            var s_Error = ReadSectionsInto(p_Path, s_Vertices, s_IndexList, s_Sections, Vector3.Zero, false, "");
            if (s_Error != null)
                return s_Error;

            // The extras are appended BEFORE the mesh is finished, so the recentring and the framing take
            // them into account — a belt that arrived afterwards would hang outside the camera's idea of
            // the object. One that cannot be read is skipped: the object still draws.
            foreach (var s_Extra in p_Extras ?? Array.Empty<MeshInstance>())
                ReadSectionsInto(s_Extra.Path, s_Vertices, s_IndexList, s_Sections, s_Extra.Offset, s_Extra.MirrorX, s_Extra.Context);

            s_Error = FinishMesh(s_Vertices, s_IndexList, s_Sections, "the mesh dump has no triangles");
            if (s_Error == null)
                MeshPath = p_Path;

            return s_Error;
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message;
        }
    }

    /// <summary>
    /// Turns sections of the loaded mesh ITSELF into context — drawn as they ship, never the edited shader's — as if they came from a
    /// context mesh named <paramref name="p_Mesh"/> (the subject's own mesh, so their identity is read off its own material list at their
    /// own index). The first-person model of a soldier is one mesh under two parts: its trousers while its upper body is edited, the rest
    /// while its trousers are. Indices into <see cref="MeshSections"/>.
    /// </summary>
    public void MarkContext(IEnumerable<int> p_Sections, string p_Mesh)
    {
        foreach (var s_Index in p_Sections)
            if (s_Index >= 0 && s_Index < m_MeshSections.Count && m_MeshSections[s_Index].Context.Length == 0)
                m_MeshSections[s_Index] = m_MeshSections[s_Index] with { Context = p_Mesh, ContextSection = s_Index };
    }

    /// <summary>Reads one dump's sections into the lists being built, moved and optionally mirrored.</summary>
    private static string? ReadSectionsInto(string p_Path, List<Vertex> p_Vertices, List<int> p_Indices,
        List<MeshSection> p_Sections, Vector3 p_Offset, bool p_MirrorX, string p_Context)
    {
        if (!System.IO.File.Exists(p_Path))
            return $"no dump at '{p_Path}'";

        using var s_Reader = new System.IO.BinaryReader(System.IO.File.OpenRead(p_Path));

        // RSM5 carries BOTH texture coordinate sets per vertex; RSM4 carried one, and its vertices read
        // as a mesh whose two sets are the same — which is what every single-UV subject already is.
        var s_Magic = new string(s_Reader.ReadChars(4));
        if (s_Magic != "RSM4" && s_Magic != "RSM5" && s_Magic != "RSM6" && s_Magic != "RSM7")
            return "not an RSM4/5/6/7 file (an older dump: it is re-dumped when the mesh is picked)";

        var s_TwoSets = s_Magic is "RSM5" or "RSM6" or "RSM7";

        // RSM7 adds a THIRD uv slot per vertex (the jets' TexCoord2, in the order the section's shader hands
        // the sets over — the dump decides the order, this reader keeps it).
        var s_ThreeSets = s_Magic == "RSM7";

        // RSM6 keeps the PART each vertex rides — what splits one section into the objects the game equips
        // separately (stowage against reactive armour). An older dump simply has none, and a section then
        // stays whole, exactly as it always was.
        var s_HasParts = s_Magic is "RSM6" or "RSM7";
        var s_SectionCount = s_Reader.ReadInt32();

        for (var s_Section = 0; s_Section < s_SectionCount; s_Section++)
        {
            var s_Shader = System.Text.Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32()));
            var s_Material = System.Text.Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32()));
            var s_Category = s_Reader.ReadInt32();
            var s_DoubleSided = s_Reader.ReadInt32() != 0;
            var s_VertexBase = p_Vertices.Count;
            var s_VertexCount = s_Reader.ReadInt32();

            for (var i = 0; i < s_VertexCount; i++)
            {
                var s_Position = new Vector3(s_Reader.ReadSingle(), s_Reader.ReadSingle(), s_Reader.ReadSingle());
                var s_Uv = new Vector2(s_Reader.ReadSingle(), s_Reader.ReadSingle());
                var s_Uv1 = s_TwoSets
                    ? new Vector2(s_Reader.ReadSingle(), s_Reader.ReadSingle())
                    : s_Uv;
                var s_Uv2 = s_ThreeSets
                    ? new Vector2(s_Reader.ReadSingle(), s_Reader.ReadSingle())
                    : s_Uv;

                if (p_MirrorX)
                    s_Position.X = -s_Position.X;

                p_Vertices.Add(new Vertex
                {
                    Position = s_Position + p_Offset,
                    TexCoord = s_Uv,
                    TexCoord1 = s_Uv1,
                    TexCoord2 = s_Uv2,
                });
            }

            var s_IndexCount = s_Reader.ReadInt32();
            var s_Start = p_Indices.Count;
            var s_Read = new int[s_IndexCount];
            for (var i = 0; i < s_IndexCount; i++)
                s_Read[i] = s_VertexBase + s_Reader.ReadInt32();

            // Read AFTER the indices, in the order the dump writes them.
            var s_Parts = new int[s_VertexCount];
            if (s_HasParts)
                for (var i = 0; i < s_VertexCount; i++)
                    s_Parts[i] = s_Reader.ReadInt32();
            else
                Array.Fill(s_Parts, -1);

            // The triangles are laid down GROUPED BY PART, so every part is one contiguous range and can be
            // drawn or skipped on its own. With no part data there is a single group and the order is the
            // dump's own — which keeps every existing subject byte-for-byte what it was.
            var s_Order = new List<int>(s_IndexCount / 3);
            var s_Groups = new List<(int Part, int Start, int Count)>();

            var s_Triangles = new List<(int Part, int At)>(s_IndexCount / 3);
            for (var i = 0; i + 2 < s_IndexCount; i += 3)
            {
                var s_Local = s_Read[i] - s_VertexBase;
                s_Triangles.Add((s_Local >= 0 && s_Local < s_Parts.Length ? s_Parts[s_Local] : -1, i));
            }

            foreach (var s_Group in s_Triangles.GroupBy(p_T => p_T.Part).OrderBy(p_G => p_G.Key))
            {
                var s_GroupStart = s_Start + s_Order.Count * 3;
                var s_Count = 0;

                foreach (var (_, i) in s_Group)
                {
                    s_Order.Add(i);
                    s_Count++;
                }

                if (s_Group.Key >= 0)
                    s_Groups.Add((s_Group.Key, s_GroupStart, s_Count * 3));
            }

            // ⛔ Mirroring turns every triangle inside out, and a reversed winding is CULLED — the belt on
            // the far side would simply not be there. The order goes back with it.
            foreach (var i in s_Order)
            {
                p_Indices.Add(s_Read[i]);
                p_Indices.Add(s_Read[p_MirrorX ? i + 2 : i + 1]);
                p_Indices.Add(s_Read[p_MirrorX ? i + 1 : i + 2]);
            }

            p_Sections.Add(new MeshSection(s_Shader.Replace('\\', '/'), s_Material, s_Category, s_DoubleSided,
                s_Start, s_IndexCount / 3 * 3)
            {
                Parts = s_Groups,
                Context = p_Context,
                ContextSection = s_Section,
            });
        }

        return null;
    }

    /// <summary>
    /// Reads a Wavefront OBJ into the preview — positions, texture coordinates and triangulated faces. It is
    /// PREVIEW geometry only: no material, no shader, nothing that reaches a bake.
    ///
    /// OBJ indexes each attribute separately (v/vt/vn) while the pipeline wants one vertex per (position,
    /// texcoord) pair, so pairs are interned as they appear. Faces come in as fans: a quad is two triangles,
    /// an n-gon n-2, which is what every exporter's "triangulate" would have produced anyway.
    /// </summary>
    public string? LoadObj(string p_Path)
    {
        try
        {
            var s_Positions = new List<Vector3>();
            var s_TexCoords = new List<Vector2> { Vector2.Zero };
            var s_Vertices = new List<Vertex>();
            var s_Indices = new List<int>();
            var s_Interned = new Dictionary<(int, int), int>();

            static float Number(string p_Text) =>
                float.TryParse(p_Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s_Value)
                    ? s_Value
                    : 0f;

            // "12", "12/7", "12//4" and "12/7/4" all name a position and maybe a texcoord; negative indices
            // count back from the end, which is legal OBJ and what several exporters write.
            int Corner(string p_Token)
            {
                var s_Parts = p_Token.Split('/');
                var s_Position = int.TryParse(s_Parts[0], out var s_P) ? s_P : 0;
                var s_TexCoord = s_Parts.Length > 1 && int.TryParse(s_Parts[1], out var s_T) ? s_T : 0;

                s_Position = s_Position < 0 ? s_Positions.Count + s_Position : s_Position - 1;
                s_TexCoord = s_TexCoord < 0 ? s_TexCoords.Count + s_TexCoord : s_TexCoord;

                if (s_Position < 0 || s_Position >= s_Positions.Count)
                    return -1;

                if (s_TexCoord < 0 || s_TexCoord >= s_TexCoords.Count)
                    s_TexCoord = 0;

                if (s_Interned.TryGetValue((s_Position, s_TexCoord), out var s_Existing))
                    return s_Existing;

                s_Vertices.Add(new Vertex
                {
                    Position = s_Positions[s_Position],
                    TexCoord = s_TexCoords[s_TexCoord],
                });

                s_Interned[(s_Position, s_TexCoord)] = s_Vertices.Count - 1;
                return s_Vertices.Count - 1;
            }

            foreach (var s_Line in System.IO.File.ReadLines(p_Path))
            {
                var s_Fields = s_Line.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
                if (s_Fields.Length == 0 || s_Fields[0].StartsWith("#", StringComparison.Ordinal))
                    continue;

                switch (s_Fields[0])
                {
                    case "v" when s_Fields.Length >= 4:
                        s_Positions.Add(new Vector3(Number(s_Fields[1]), Number(s_Fields[2]),
                            Number(s_Fields[3])));
                        break;

                    // OBJ's V axis points up, the sampler's down.
                    case "vt" when s_Fields.Length >= 3:
                        s_TexCoords.Add(new Vector2(Number(s_Fields[1]), 1f - Number(s_Fields[2])));
                        break;

                    case "f" when s_Fields.Length >= 4:
                        var s_First = Corner(s_Fields[1]);
                        for (var i = 2; i + 1 < s_Fields.Length; i++)
                        {
                            var s_B = Corner(s_Fields[i]);
                            var s_C = Corner(s_Fields[i + 1]);
                            if (s_First < 0 || s_B < 0 || s_C < 0)
                                continue;

                            s_Indices.Add(s_First);
                            s_Indices.Add(s_B);
                            s_Indices.Add(s_C);
                        }

                        break;
                }
            }

            var s_Name = System.IO.Path.GetFileName(p_Path);
            var s_Error = FinishMesh(s_Vertices, s_Indices,
                new List<MeshSection> { new("", s_Name, 0, false, 0, s_Indices.Count) },
                "the OBJ has no triangular faces (only 'v' and 'f' lines are read)");
            if (s_Error == null)
                MeshPath = p_Path;

            return s_Error;
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message;
        }
    }

    /// <summary>
    /// Everything a loaded mesh needs after its vertices and triangles are known, shared by every reader so
    /// the geometry is derived ONE way: smooth normals and UV-gradient tangents accumulated per face, then
    /// the whole thing recentred and scaled to the primitives' envelope so the camera framing and the light
    /// read the same whatever was loaded.
    /// </summary>
    private string? FinishMesh(List<Vertex> p_Vertices, List<int> p_IndexList, List<MeshSection> p_Sections,
        string p_EmptyMessage)
    {
        {
            var s_Vertices = p_Vertices;
            var s_IndexList = p_IndexList;
            var s_Sections = p_Sections;

            if (s_IndexList.Count == 0)
                return p_EmptyMessage;

            var s_Data = s_Vertices.ToArray();
            var s_Indices = s_IndexList.ToArray();
            var s_Bitangents = AccumulateFrame(s_Data, s_Indices);

            // Recentre + scale to the primitives' envelope so the camera framing and the light read the same.
            var s_Min = new Vector3(float.MaxValue);
            var s_Max = new Vector3(float.MinValue);
            foreach (var s_Vertex in s_Data)
            {
                s_Min = Vector3.Min(s_Min, s_Vertex.Position);
                s_Max = Vector3.Max(s_Max, s_Vertex.Position);
            }

            var s_Centre = (s_Min + s_Max) * 0.5f;
            var s_Extent = Math.Max(Math.Max(s_Max.X - s_Min.X, s_Max.Y - s_Min.Y), s_Max.Z - s_Min.Z);

            // 1.7, not the primitives' 1.1: a vehicle's envelope is dominated by its thinnest extremities
            // (rotor blades, gun barrels), which left the body tiny in the frame. The camera still orbits
            // and zooms, so slight cropping of an extremity at rest is the better trade.
            var s_Scale = s_Extent > 1e-6f ? 1.7f / s_Extent : 1f;

            for (var i = 0; i < s_Data.Length; i++)
            {
                s_Data[i].Position = (s_Data[i].Position - s_Centre) * s_Scale;
                NormaliseFrame(ref s_Data[i], s_Bitangents[i]);
            }

            m_MeshData = (s_Data, s_Indices);
            m_MeshCentre = s_Centre;
            m_MeshScale = s_Scale;
            m_Surface = null;
            m_MeshSections.Clear();
            m_MeshSections.AddRange(s_Sections);
            m_Appended = null;
            // a first-person eye is a place in the PREVIOUS mesh's space: a new mesh goes back to the orbit
            FixedCamera = null;
            if (m_Shape == PreviewShape.Mesh && m_Device != null)
                BuildGeometry();

            return null;
        }
    }

    /// <summary>
    /// Accumulates smooth face normals, UV-gradient tangents and bitangents into the vertices (the bitangents returned, only for the
    /// handedness: a mirrored island's bitangent runs against cross(N, T)).
    /// </summary>
    private static Vector3[] AccumulateFrame(Vertex[] p_Data, int[] p_Indices)
    {
        var s_Bitangents = new Vector3[p_Data.Length];
        for (var i = 0; i < p_Indices.Length; i += 3)
        {
            ref var s_A = ref p_Data[p_Indices[i]];
            ref var s_B = ref p_Data[p_Indices[i + 1]];
            ref var s_C = ref p_Data[p_Indices[i + 2]];

            var s_Edge1 = s_B.Position - s_A.Position;
            var s_Edge2 = s_C.Position - s_A.Position;

            var s_FaceNormal = Vector3.Cross(s_Edge1, s_Edge2);
            s_A.Normal += s_FaceNormal;
            s_B.Normal += s_FaceNormal;
            s_C.Normal += s_FaceNormal;

            var s_DeltaUv1 = s_B.TexCoord - s_A.TexCoord;
            var s_DeltaUv2 = s_C.TexCoord - s_A.TexCoord;
            var s_Determinant = s_DeltaUv1.X * s_DeltaUv2.Y - s_DeltaUv2.X * s_DeltaUv1.Y;
            if (Math.Abs(s_Determinant) > 1e-12f)
            {
                var s_Tangent = (s_Edge1 * s_DeltaUv2.Y - s_Edge2 * s_DeltaUv1.Y) / s_Determinant;
                s_A.Tangent += s_Tangent;
                s_B.Tangent += s_Tangent;
                s_C.Tangent += s_Tangent;
                var s_Bitangent = (s_Edge2 * s_DeltaUv1.X - s_Edge1 * s_DeltaUv2.X) / s_Determinant;
                s_Bitangents[p_Indices[i]] += s_Bitangent;
                s_Bitangents[p_Indices[i + 1]] += s_Bitangent;
                s_Bitangents[p_Indices[i + 2]] += s_Bitangent;
            }
        }

        WeldNormals(p_Data);
        return s_Bitangents;
    }

    /// <summary>
    /// The normals whole across the unwrap's seams — the one rule the stickers use too (<see cref="Graph.StickerSurface.WeldNormals"/>;
    /// the lighting was cut right down the middle of the weapon).
    /// </summary>
    private static void WeldNormals(Vertex[] p_Data)
    {
        var s_Positions = p_Data.Select(p_V => new System.Numerics.Vector3(p_V.Position.X, p_V.Position.Y, p_V.Position.Z)).ToArray();
        var s_Normals = p_Data.Select(p_V => new System.Numerics.Vector3(p_V.Normal.X, p_V.Normal.Y, p_V.Normal.Z)).ToArray();
        Graph.StickerSurface.WeldNormals(s_Positions, s_Normals);
        for (var i = 0; i < p_Data.Length; i++)
            p_Data[i].Normal = new Vector3(s_Normals[i].X, s_Normals[i].Y, s_Normals[i].Z);
    }

    /// <summary>A vertex's accumulated normal and tangent made unit, and its handedness from the accumulated bitangent.</summary>
    private static void NormaliseFrame(ref Vertex p_Vertex, Vector3 p_Bitangent)
    {
        p_Vertex.Normal = p_Vertex.Normal.LengthSquared() > 1e-12f
            ? Vector3.Normalize(p_Vertex.Normal)
            : new Vector3(0, 1, 0);
        p_Vertex.Tangent = p_Vertex.Tangent.LengthSquared() > 1e-12f
            ? Vector3.Normalize(p_Vertex.Tangent)
            : new Vector3(1, 0, 0);
        p_Vertex.Handedness = Vector3.Dot(Vector3.Cross(p_Vertex.Normal, p_Vertex.Tangent), p_Bitangent) < 0f ? -1f : 1f;
    }

    /// <summary>What the loaded mesh was before <see cref="AppendContextMesh"/> added to it (vertex, index and section counts); null when nothing is appended.</summary>
    private (int Vertices, int Indices, int Sections)? m_Appended;

    /// <summary>
    /// Adds a dump's sections to the mesh on screen as CONTEXT (drawn as they ship, never the edited shader's), in the space the mesh is
    /// ALREADY drawn in: the same recentring and scale, so the framing, the body's surface and every placement stay exactly as they were
    /// — the first-person arms around a weapon, dumped in the weapon mesh's own units. One appended mesh at a time; a
    /// new mesh load drops it. Returns an error, or null.
    /// </summary>
    public string? AppendContextMesh(string p_Path, string p_Context)
    {
        if (m_MeshData == null)
            return "no mesh is loaded";

        RemoveAppendedContext();
        var s_Vertices = new List<Vertex>();
        var s_IndexList = new List<int>();
        var s_Sections = new List<MeshSection>();
        var s_Error = ReadSectionsInto(p_Path, s_Vertices, s_IndexList, s_Sections, Vector3.Zero, false, p_Context);
        if (s_Error != null)
            return s_Error;
        if (s_IndexList.Count == 0)
            return "the dump has no triangles";

        var s_Added = s_Vertices.ToArray();
        var s_AddedIndices = s_IndexList.ToArray();
        var s_Bitangents = AccumulateFrame(s_Added, s_AddedIndices);
        for (var i = 0; i < s_Added.Length; i++)
        {
            s_Added[i].Position = (s_Added[i].Position - m_MeshCentre) * m_MeshScale;
            NormaliseFrame(ref s_Added[i], s_Bitangents[i]);
        }

        var (s_Data, s_Indices) = m_MeshData.Value;
        m_Appended = (s_Data.Length, s_Indices.Length, m_MeshSections.Count);
        var s_VertexBase = s_Data.Length;
        var s_IndexBase = s_Indices.Length;
        m_MeshData = (s_Data.Concat(s_Added).ToArray(), s_Indices.Concat(s_AddedIndices.Select(p_I => p_I + s_VertexBase)).ToArray());
        m_MeshSections.AddRange(s_Sections.Select(p_S => p_S with
        {
            StartIndex = p_S.StartIndex + s_IndexBase,
            Parts = p_S.Parts.Select(p_P => (p_P.Part, p_P.Start + s_IndexBase, p_P.Count)).ToList(),
        }));
        if (m_Shape == PreviewShape.Mesh && m_Device != null)
            BuildGeometry();

        return null;
    }

    /// <summary>Takes away what <see cref="AppendContextMesh"/> added; the mesh is as it was loaded. False when nothing was appended.</summary>
    public bool RemoveAppendedContext()
    {
        if (m_Appended is not { } s_Was || m_MeshData == null)
            return false;

        var (s_Data, s_Indices) = m_MeshData.Value;
        m_MeshData = (s_Data[..s_Was.Vertices], s_Indices[..s_Was.Indices]);
        m_MeshSections.RemoveRange(s_Was.Sections, m_MeshSections.Count - s_Was.Sections);
        m_Appended = null;
        if (m_Shape == PreviewShape.Mesh && m_Device != null)
            BuildGeometry();

        return true;
    }

    /// <summary>Whether a mesh is appended as context (<see cref="AppendContextMesh"/>).</summary>
    public bool HasAppendedContext => m_Appended != null;

    public bool HasMesh => m_MeshData != null;

    /// <summary>The loaded mesh's vertex normals as the dump carries them, indexed like <see cref="Surface"/>'s vertices; null without a mesh.</summary>
    public System.Numerics.Vector3[]? MeshVertexNormals =>
        m_MeshData?.Vertices.Select(p_V => new System.Numerics.Vector3(p_V.Normal.X, p_V.Normal.Y, p_V.Normal.Z)).ToArray();

    /// <summary>
    /// The file the geometry on screen came from — so whoever drives this window can check WHICH mesh is
    /// loaded, not merely that one is. Null until a load succeeds; a failed load keeps the previous one.
    /// </summary>
    public string? MeshPath { get; private set; }

    /// <summary>Counts every authored shader accepted, so a driver can tell "recompiled" from "still the old one".</summary>
    public int AuthoredShaderVersion { get; private set; }

    public bool Ready => m_Device != null;
    public string? LastError { get; private set; }

    /// <summary>
    /// Same pipeline without a window, so the preview can be rendered to a file and actually looked at rather
    /// than merely "not crashing".
    /// </summary>
    public bool InitialiseOffscreen(int p_Width, int p_Height)
    {
        try
        {
            m_Width = Math.Max(1, p_Width);
            m_Height = Math.Max(1, p_Height);

            m_Device = new Device(DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 });
            m_Context = m_Device.ImmediateContext;

            BuildStaticResources();
            CreateSizeDependentResources();
            return true;
        }
        catch (Exception s_Exception)
        {
            LastError = s_Exception.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Copies one GBuffer target out as RAW RGBA, with no channel swap and no lighting applied. This is the
    /// pixel shader's actual output, which is what an equivalence test between two shaders has to compare -
    /// diffing the lit resolve would hide any difference the lighting pass happens not to read (RT2.xyz).
    /// </summary>
    public byte[]? CaptureTarget(int p_Index)
    {
        if (m_Device == null || m_Context == null || p_Index < 0 || p_Index >= c_TargetCount)
            return null;

        var s_Source = m_Targets[p_Index];
        if (s_Source == null)
            return null;

        using var s_Staging = new Texture2D(m_Device, new Texture2DDescription
        {
            Width = m_Width,
            Height = m_Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CpuAccessFlags = CpuAccessFlags.Read,
        });

        m_Context.CopyResource(s_Source, s_Staging);

        var s_Box = m_Context.MapSubresource(s_Staging, 0, MapMode.Read, MapFlags.None, out _);
        var s_Bytes = new byte[m_Width * m_Height * 4];

        for (var y = 0; y < m_Height; y++)
            Marshal.Copy(IntPtr.Add(s_Box.DataPointer, y * s_Box.RowPitch), s_Bytes, y * m_Width * 4,
                m_Width * 4);

        m_Context.UnmapSubresource(s_Staging, 0);
        return s_Bytes;
    }

    /// <summary>Copies the resolved image out to BGRA bytes for saving.</summary>
    public byte[]? Capture(out int p_Width, out int p_Height)
    {
        p_Width = m_Width;
        p_Height = m_Height;

        if (m_Device == null || m_Context == null || m_Output == null)
            return null;

        var s_Bytes = ReadPixels(m_Output);
        SwapToBgra(s_Bytes);
        return s_Bytes;
    }

    /// <summary>
    /// Renders one frame and returns it AS SHOWN — the swap chain's back buffer for a window, the offscreen
    /// output otherwise — as BGRA bytes. Read before the present, because after it the back buffer is the
    /// next frame's; so this drives the frame itself rather than reading whatever the last one left.
    /// </summary>
    public byte[]? CaptureShownFrame(out int p_Width, out int p_Height)
    {
        p_Width = m_Width;
        p_Height = m_Height;

        if (m_Device == null || m_Context == null)
            return null;

        m_CaptureNextFrame = true;
        m_CapturedFrame = null;
        Render();
        m_CaptureNextFrame = false;
        return m_CapturedFrame;
    }

    private bool m_CaptureNextFrame;
    private byte[]? m_CapturedFrame;

    /// <summary>One texture's pixels, RGBA as the GPU holds them.</summary>
    private byte[] ReadPixels(Texture2D p_Source)
    {
        using var s_Staging = new Texture2D(m_Device, new Texture2DDescription
        {
            Width = m_Width,
            Height = m_Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CpuAccessFlags = CpuAccessFlags.Read,
        });

        m_Context!.CopyResource(p_Source, s_Staging);

        var s_Box = m_Context.MapSubresource(s_Staging, 0, MapMode.Read, MapFlags.None, out _);
        var s_Bytes = new byte[m_Width * m_Height * 4];

        for (var y = 0; y < m_Height; y++)
            Marshal.Copy(IntPtr.Add(s_Box.DataPointer, y * s_Box.RowPitch), s_Bytes, y * m_Width * 4,
                m_Width * 4);

        m_Context.UnmapSubresource(s_Staging, 0);
        return s_Bytes;
    }

    /// <summary>RGBA on the GPU, BGRA for the encoder.</summary>
    private static void SwapToBgra(byte[] p_Bytes)
    {
        for (var i = 0; i < p_Bytes.Length; i += 4)
            (p_Bytes[i], p_Bytes[i + 2]) = (p_Bytes[i + 2], p_Bytes[i]);
    }

    public bool Initialise(IntPtr p_WindowHandle, int p_Width, int p_Height)
    {
        try
        {
            m_Width = Math.Max(1, p_Width);
            m_Height = Math.Max(1, p_Height);

            var s_Description = new SwapChainDescription
            {
                BufferCount = 2,
                ModeDescription = new ModeDescription(m_Width, m_Height, new Rational(60, 1),
                    Format.R8G8B8A8_UNorm),
                IsWindowed = true,
                OutputHandle = p_WindowHandle,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.Discard,
                Usage = Usage.RenderTargetOutput,
            };

            Device.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 }, s_Description,
                out m_Device, out m_SwapChain);

            m_Context = m_Device.ImmediateContext;

            BuildStaticResources();
            CreateSizeDependentResources();
            return true;
        }
        catch (Exception s_Exception)
        {
            LastError = s_Exception.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Feeds each TEXCOORD what the TARGET shader treats it as. Null or empty restores the measured rigid
    /// layout - which is also what every guardian is pinned against, so the default path never moves.
    /// </summary>
    public void SetInterpolatorMeanings(IReadOnlyDictionary<int, string>? p_Meanings)
    {
        if (m_Device != null)
            BuildCubeVertexShader(p_Meanings);
        MeaningsTag = p_Meanings == null || p_Meanings.Count == 0
            ? "(rigid default)"
            : string.Join(" ", p_Meanings.OrderBy(p_M => p_M.Key).Select(p_M => $"{p_M.Key}:{p_M.Value}"));
    }

    /// <summary>What the authored shader's vertex feed was last built with (SetInterpolatorMeanings), for a seam to read.</summary>
    public string MeaningsTag { get; private set; } = "(none yet)";

    /// <summary>The material values over cb1's pattern (SetExternalValues), "element=x" each, for a seam to read.</summary>
    public string OverridesTag => m_ExternalOverrides == null
        ? "(none)"
        : string.Join(" ", m_ExternalOverrides.OrderBy(p_O => p_O.Element)
            .Select(p_O => $"c{p_O.Element}={p_O.Value[0].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));

    private void BuildCubeVertexShader(IReadOnlyDictionary<int, string>? p_Meanings)
    {
        var s_Device = m_Device!;
        m_CubeVs?.Dispose();
        m_CubeLayout?.Dispose();

        var s_CubeVsCode = ShaderBytecode.Compile(PreviewShaders.VertexShaderFor(p_Meanings), "main", "vs_5_0");
        m_CubeVs = new VertexShader(s_Device, s_CubeVsCode);
        m_CubeLayout = new InputLayout(s_Device, s_CubeVsCode, new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElement("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
            new InputElement("TANGENT", 1, Format.R32_Float, 36, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
            new InputElement("TEXCOORD", 1, Format.R32G32_Float, 48, 0),
            new InputElement("TEXCOORD", 2, Format.R32G32_Float, 56, 0),
        });
    }

    private void BuildStaticResources()
    {
        var s_Device = m_Device!;
        BuildCubeVertexShader(null);

        m_ResolveVs = new VertexShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_ResolveVertexShader, "main", "vs_5_0"));
        m_ResolvePs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_ResolvePixelShader, "main", "ps_5_0"));
        m_ForwardResolvePs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_ForwardResolvePixelShader, "main", "ps_5_0"));
        m_NeutralGBufferPs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_NeutralGBufferPixelShader, "main", "ps_5_0"));
        m_NeutralForwardPs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_NeutralForwardPixelShader, "main", "ps_5_0"));

        var s_OverlayVsCode = ShaderBytecode.Compile(PreviewShaders.c_OverlayVertexShader, "main", "vs_5_0");
        m_OverlayVs = new VertexShader(s_Device, s_OverlayVsCode);
        m_OverlayPs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_OverlayPixelShader, "main", "ps_5_0"));
        m_OverlayLayout = new InputLayout(s_Device, s_OverlayVsCode, new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
        });
        m_DepthNone = new DepthStencilState(s_Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.Always,
        });
        m_OverlayDirty = true;

        var s_PictureVsCode = ShaderBytecode.Compile(PreviewShaders.c_PictureVertexShader, "main", "vs_5_0");
        m_PictureVs = new VertexShader(s_Device, s_PictureVsCode);
        m_PicturePs = new PixelShader(s_Device,
            ShaderBytecode.Compile(PreviewShaders.c_PicturePixelShader, "main", "ps_5_0"));
        m_PictureLayout = new InputLayout(s_Device, s_PictureVsCode, new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        });
        m_PictureDirty = true;

        BuildGeometry();

        // world, view-projection, and g_meshRaw (the mesh position's way back to the dump's own units — MeshPositionInW)
        m_VsConstants = new Buffer(s_Device, 144, ResourceUsage.Dynamic, BindFlags.ConstantBuffer,
            CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        // 21 float4s: the authored shader's cbuffer reaches cameraPos at c20.
        m_ViewConstants = new Buffer(s_Device, 21 * 16, ResourceUsage.Dynamic, BindFlags.ConstantBuffer,
            CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        m_LightConstants = new Buffer(s_Device, 64 + 5 * 16, ResourceUsage.Dynamic, BindFlags.ConstantBuffer,
            CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

        // ⛔ The material parameter buffer (cb1), filled with a DISTINCTIVE pattern rather than left unbound.
        //
        // Unbound would read as zeros - for the game's shader and for ours alike - so the differential test would
        // still say EQUIVALENT while proving nothing at all about whether a parameter was wired to the right
        // slot. That is validating with the neutral value, the exact trap that already cost a cycle on the
        // texture preview. With a pattern, reading cb1[3] where the original reads cb1[2] changes the picture.
        m_ParameterConstants = new Buffer(s_Device, c_ParameterSlots * 16, ResourceUsage.Default,
            BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        // Re-applied here because instance values may have been set BEFORE the device existed - everything
        // pushed at a lazily initialised subsystem must be re-pushed right after it initialises.
        PushParameterBuffer();

        // ⛔⛔ cb0 is DICE's `$Globals` - the outdoor light and light-probe terms a forward shader reads. It was
        // never bound in pass 1, and that is not the same as "reads zeros": the RESOLVE pass binds its own light
        // constants at slot 0, so on a fresh device the game's shader rendered with cb0 EMPTY and ours rendered
        // with the previous pass's leftovers still bound. Two halves of a differential test fed different inputs.
        // It only ever showed on shaders that read cb0 - glass, decals, light cones - which is why chasing it
        // through undefined render targets and resolution twice refuted the wrong hypothesis.
        //
        // A DIFFERENT pattern from cb1's on purpose: identical buffers would hide a graph that confused the two.
        var s_Globals = new RawVector4[c_ParameterSlots];
        for (var i = 0; i < c_ParameterSlots; i++)
            s_Globals[i] = new RawVector4(0.72f - i * 0.011f, 0.19f + i * 0.029f,
                0.44f + i * 0.017f, 0.86f - i * 0.005f);

        // c7..c10 are the light-probe SH rows (lightProbeShR/G/B/O). The shader clamps their dp4 against
        // the normal at zero — with the generic pattern the dp4 went NEGATIVE for the placeholder normal,
        // the whole probe term collapsed to zero on BOTH sides of a differential test, and an equivalence
        // over zeros proves nothing (caught by a tint perturbation that changed nothing). These rows keep
        // |xyz| well under w, so the dp4 stays positive for every normal AND still depends on it.
        for (var i = 7; i <= 10; i++)
            s_Globals[i] = new RawVector4(0.20f - i * 0.010f, 0.12f + i * 0.008f,
                -0.08f + i * 0.012f, 0.55f + i * 0.010f);

        m_GlobalConstants = new Buffer(s_Device, c_ParameterSlots * 16, ResourceUsage.Default,
            BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        s_Device.ImmediateContext.UpdateSubresource(s_Globals, m_GlobalConstants);

        m_Sampler = new SamplerState(s_Device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunction = Comparison.Never,
            MaximumLod = float.MaxValue,
        });

        m_Raster = new RasterizerState(s_Device, new RasterizerStateDescription
        {
            CullMode = CullMode.Back,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true,
        });

        // The same culling under the mirrored world of a loaded mesh: a reflection turns every triangle's
        // winding around, so the front face is the counter-clockwise one there.
        m_RasterMirrored = new RasterizerState(s_Device, new RasterizerStateDescription
        {
            CullMode = CullMode.Back,
            FillMode = FillMode.Solid,
            IsFrontCounterClockwise = true,
            IsDepthClipEnabled = true,
        });

        // For sections whose shader's solutions carry the DoubleSided flag (bit 1): both faces drawn, the
        // way the engine rasterises them — a canopy stays visible from inside, foliage from both sides.
        m_RasterNoCull = new RasterizerState(s_Device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true,
        });

        m_DepthState = new DepthStencilState(s_Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.LessEqual,
        });

        // The forward composite pass: tested against pass 1's depth without writing it, blended
        // premultiplied over the lit backbuffer — the transparent pass's own convention.
        m_DepthReadState = new DepthStencilState(s_Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.LessEqual,
        });

        var s_Blend = new BlendStateDescription();
        s_Blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true,
            SourceBlend = BlendOption.One,
            DestinationBlend = BlendOption.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.One,
            DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
            AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All,
        };
        m_ForwardBlend = new BlendState(s_Device, s_Blend);

        // A neutral checker stands in for an unloaded texture so the cube is never blank. t1/t2 keep exactly the
        // defaults they always had, because the drum's output is verified bit-exact against the game's own shader
        // and must not move by an LSB.
        //
        // ⛔ EXCEPT under PREVIEW_ALPHA_GRADIENT=1, an opt-in for the alpha-coverage differential tests: the
        // default checker's alpha is a CONSTANT 1.0, so every coverage tap passes everywhere and the whole
        // SV_Coverage path compares constant-against-constant — a diff that cannot fail proves nothing. The
        // gradient sweeps alpha across the threshold over the surface, which is what makes wrong tap offsets,
        // a wrong tap count and a wrong threshold all show up as pixels kept or discarded differently.
        m_Textures[1] = Environment.GetEnvironmentVariable("PREVIEW_ALPHA_GRADIENT") == "1"
            ? CreateAlphaGradientTexture()
            : CreateCheckerTexture(0xFFB0B0B0, 0xFF808080);
        m_Textures[2] = CreateFlatTexture(0xFFFF8080);

        // ⛔ Everything else is MAGENTA-ish on purpose, never black. A black default is what hid the
        // missing-binding bug: a shader sampling an unbound slot produced a black object, which is
        // indistinguishable from a shader that legitimately shades to black.
        for (var s_Slot = 3; s_Slot < c_TextureSlots; s_Slot++)
            m_Textures[s_Slot] = CreatePlaceholderTexture(s_Slot);
    }

    /// <summary>
    /// The stand-in for a slot nothing was loaded into: still loud enough to read as "not supplied", but with
    /// NO degenerate channel and NO flat area.
    ///
    /// ⛔⛔⛔ IT USED TO BE FLAT MAGENTA, AND MAGENTA HAS GREEN EXACTLY ZERO. That is the neutral-value trap of
    /// this project's own law, sitting inside the verification harness: any shader whose output ran through the
    /// green channel of an unbound slot multiplied by a hard zero and wrote nothing at all, so the sweep filed
    /// it under "wrote nothing on the test mesh" and could say NOTHING about it - 24 terrain mask passes among
    /// them. A placeholder is an INPUT of the differential test, and the law is that every input gets a
    /// distinctive value, never a zero.
    ///
    /// Flat was the second half of the same mistake: with one colour everywhere, a wrong UV samples the same
    /// texel as the right one, so the whole class of coordinate bugs is invisible by construction. Every
    /// channel now varies across the surface AND differs per slot, which is what makes "reads t7 instead of t5"
    /// and "reads at the wrong coordinate" both show up as a difference.
    ///
    /// ⛔⛔ AND THE PATTERN IS A MONOTONIC GRADIENT, NOT A CHECKER - the first varied version used one and it
    /// was wrong in BOTH directions. A checker is periodic, so a coordinate error of exactly one period reads
    /// back identical and the test says "equivalent" about a shader that samples the wrong place; and its edges
    /// are discontinuous, so a SUB-TEXEL error - the kind two mathematically identical programs produce just by
    /// associating their floats differently - jumps a whole step and reads as a failure. Two sky shaders that
    /// work with Earth-radius magnitudes failed at 5/255 for exactly that reason, and the tell was that the one
    /// channel WITHOUT a checker differed by 1 while the two with it differed by 5 and 3.
    /// A gradient makes the response PROPORTIONAL to the coordinate error and non-aliasing: small error, small
    /// difference; wrong texture or wrong coordinate, large difference.
    /// </summary>
    /// <summary>
    /// The colour checker t1 always had, with the ALPHA swept as a fine diagonal gradient instead of the
    /// constant 1.0. Opt-in via PREVIEW_ALPHA_GRADIENT for coverage tests only: alpha crosses the 0.5
    /// threshold many times across the surface, so the supersampled alpha-test taps land on both sides of
    /// it and their exact offsets become visible in which pixels survive. The period is small (8 texels)
    /// so the threshold edge crosses many derivative-sized neighbourhoods.
    /// </summary>
    private ShaderResourceView CreateAlphaGradientTexture()
    {
        const int c_Size = 32;
        var s_Pixels = new uint[c_Size * c_Size];

        for (var y = 0; y < c_Size; y++)
        for (var x = 0; x < c_Size; x++)
        {
            var s_Colour = (x / 4 + y / 4) % 2 == 0 ? 0x00B0B0B0u : 0x00808080u;
            var s_Alpha = (uint) (16 + (x * 5 + y * 3) % 224);
            s_Pixels[y * c_Size + x] = s_Alpha << 24 | s_Colour;
        }

        return CreateTexture(c_Size, c_Size, s_Pixels);
    }

    private ShaderResourceView CreatePlaceholderTexture(int p_Slot)
    {
        const int c_Size = 32;
        var s_Pixels = new uint[c_Size * c_Size];

        // Each channel ramps along a DIFFERENT axis, so a swizzle mix-up moves the picture too, and each slot
        // sits at its own offset so reading t7 where the shader wanted t5 cannot come out the same.
        var s_Offset = p_Slot * 13 % 40;

        for (var y = 0; y < c_Size; y++)
        for (var x = 0; x < c_Size; x++)
        {
            // ⛔⛔ THE RANGE IS ~0.10..0.55 ON PURPOSE, AND BOTH ENDS ARE A MEASURED MISTAKE, NOT TASTE.
            // A 0 is the neutral-value trap that started all this. But a value near 1 is the OTHER end of the
            // same trap: it SATURATES the shader. Five terrain mask passes computed `dp2_sat(a,a, b,b)`, which
            // is saturate(2ab), and with placeholders around 0.8 that clamps to 1 for every texel; the shader
            // then took `1 - 1 = 0`, failed its own `0 < x` test, skipped its whole body and wrote nothing -
            // so the sweep filed them as "wrote nothing" and could say nothing about them. A saturated input
            // destroys information exactly like a zero does; it just does it at the top of the range.
            // Kept clear of 0 and of anything that makes a doubled product reach 1.
            var s_R = 25 + x * 3 + s_Offset / 2;
            var s_G = 30 + y * 3 + s_Offset / 3;
            var s_B = 20 + (x + y) * 2 + s_Offset / 2;
            var s_A = 35 + (c_Size - 1 - x + y) * 3 / 2;

            s_Pixels[y * c_Size + x] =
                ((uint) Math.Clamp(s_A, 1, 254) << 24) | ((uint) Math.Clamp(s_R, 1, 254) << 16) |
                ((uint) Math.Clamp(s_G, 1, 254) << 8) | (uint) Math.Clamp(s_B, 1, 254);
        }

        return CreateTexture(c_Size, c_Size, s_Pixels);
    }

    private void BuildGeometry()
    {
        m_CubeVertices?.Dispose();
        m_CubeIndices?.Dispose();
        m_IndexFormat = Format.R16_UInt;

        switch (m_Shape)
        {
            case PreviewShape.Sphere:
                BuildSphere();
                return;

            case PreviewShape.Cylinder:
                BuildCylinder();
                return;

            case PreviewShape.Plane:
                BuildPlane();
                return;

            // A real mesh easily exceeds the 16-bit index range, so it gets 32-bit indices; the shape
            // falls back to the cube until a mesh has been loaded.
            case PreviewShape.Mesh when m_MeshData is { } s_Mesh:
                m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Mesh.Vertices);
                m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Mesh.Indices);
                m_IndexCount = s_Mesh.Indices.Length;
                m_IndexFormat = Format.R32_UInt;
                return;
        }

        var s_Faces = new[]
        {
            (Normal: new Vector3(0, 0, -1), Tangent: new Vector3(1, 0, 0)),
            (Normal: new Vector3(0, 0, 1), Tangent: new Vector3(-1, 0, 0)),
            (Normal: new Vector3(-1, 0, 0), Tangent: new Vector3(0, 0, -1)),
            (Normal: new Vector3(1, 0, 0), Tangent: new Vector3(0, 0, 1)),
            (Normal: new Vector3(0, -1, 0), Tangent: new Vector3(1, 0, 0)),
            (Normal: new Vector3(0, 1, 0), Tangent: new Vector3(1, 0, 0)),
        };

        var s_Vertices = new Vertex[24];
        var s_Indices = new ushort[36];

        for (var i = 0; i < 6; i++)
        {
            var s_Normal = s_Faces[i].Normal;
            var s_Tangent = s_Faces[i].Tangent;
            var s_Binormal = Vector3.Cross(s_Normal, s_Tangent);

            // Corners of the face, built from its own basis so every face gets sane uvs and a tangent frame.
            var s_Corners = new[]
            {
                -s_Tangent - s_Binormal, s_Tangent - s_Binormal, s_Tangent + s_Binormal, -s_Tangent + s_Binormal,
            };

            // ⛔ V grows DOWNWARD in D3D (row 0 of the image is V=0): the first corner (-tangent -binormal)
            // is the face's TOP-left, so it takes V=0. The old table gave it V=1, which drew every texture
            // upside down — invisible on organic art for months, obvious the day a texture had text on it.
            var s_Uvs = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            };

            for (var s_Corner = 0; s_Corner < 4; s_Corner++)
                s_Vertices[i * 4 + s_Corner] = new Vertex
                {
                    Position = (s_Normal + s_Corners[s_Corner]) * 0.5f,
                    Normal = s_Normal,
                    Tangent = s_Tangent,
                    TexCoord = s_Uvs[s_Corner],
                };

            var s_Base = i * 4;
            var s_Slot = i * 6;
            s_Indices[s_Slot + 0] = (ushort) (s_Base + 0);
            s_Indices[s_Slot + 1] = (ushort) (s_Base + 1);
            s_Indices[s_Slot + 2] = (ushort) (s_Base + 2);
            s_Indices[s_Slot + 3] = (ushort) (s_Base + 0);
            s_Indices[s_Slot + 4] = (ushort) (s_Base + 2);
            s_Indices[s_Slot + 5] = (ushort) (s_Base + 3);
        }

        m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Vertices);
        m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Indices);
        m_IndexCount = s_Indices.Length;
    }

    private void BuildSphere()
    {
        const int c_Rings = 48;
        const int c_Segments = 96;

        var s_Vertices = new Vertex[(c_Rings + 1) * (c_Segments + 1)];
        var s_Index = 0;

        for (var s_Ring = 0; s_Ring <= c_Rings; s_Ring++)
        {
            var s_V = s_Ring / (float) c_Rings;
            var s_Phi = s_V * MathF.PI;

            for (var s_Segment = 0; s_Segment <= c_Segments; s_Segment++)
            {
                var s_U = s_Segment / (float) c_Segments;
                var s_Theta = s_U * MathF.PI * 2f;

                var s_Normal = new Vector3(
                    MathF.Sin(s_Phi) * MathF.Cos(s_Theta),
                    MathF.Cos(s_Phi),
                    MathF.Sin(s_Phi) * MathF.Sin(s_Theta));

                // Tangent along increasing longitude keeps the frame consistent with the uv direction.
                var s_Tangent = new Vector3(-MathF.Sin(s_Theta), 0f, MathF.Cos(s_Theta));

                s_Vertices[s_Index++] = new Vertex
                {
                    Position = s_Normal * 0.5f,
                    Normal = s_Normal,
                    Tangent = s_Tangent,
                    TexCoord = new Vector2(s_U * 2f, s_V),
                };
            }
        }

        var s_Indices = new ushort[c_Rings * c_Segments * 6];
        var s_Slot = 0;

        for (var s_Ring = 0; s_Ring < c_Rings; s_Ring++)
        for (var s_Segment = 0; s_Segment < c_Segments; s_Segment++)
        {
            var s_A = (ushort) (s_Ring * (c_Segments + 1) + s_Segment);
            var s_B = (ushort) (s_A + 1);
            var s_C = (ushort) (s_A + c_Segments + 1);
            var s_D = (ushort) (s_C + 1);

            // Wound to match the cube, whose triangles are known to face outward: for a quad at the equator
            // this order gives a geometric normal along +X where the surface normal is +X. The reverse order
            // renders the inside of the sphere, which reads as inverted lighting rather than as a hole.
            s_Indices[s_Slot++] = s_A;
            s_Indices[s_Slot++] = s_B;
            s_Indices[s_Slot++] = s_C;
            s_Indices[s_Slot++] = s_B;
            s_Indices[s_Slot++] = s_D;
            s_Indices[s_Slot++] = s_C;
        }

        m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Vertices);
        m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Indices);
        m_IndexCount = s_Indices.Length;
    }

    /// <summary>
    /// A capped cylinder: the side reads curvature and tiling together (the highlight sweeps across it the
    /// way it never can on a flat face), the caps keep it from looking like a tube. Same frame conventions
    /// as the sphere: tangent along increasing longitude, V growing downward.
    /// </summary>
    private void BuildCylinder()
    {
        const int c_Segments = 64;
        const float c_Radius = 0.35f;
        const float c_HalfHeight = 0.5f;

        var s_Vertices = new List<Vertex>();
        var s_Indices = new List<ushort>();

        // Side: two rings of shared vertices, wrapped with one duplicated seam column for clean UVs.
        for (var s_Segment = 0; s_Segment <= c_Segments; s_Segment++)
        {
            var s_U = s_Segment / (float) c_Segments;
            var s_Theta = s_U * MathF.PI * 2f;
            var s_Normal = new Vector3(MathF.Cos(s_Theta), 0f, MathF.Sin(s_Theta));
            var s_Tangent = new Vector3(-MathF.Sin(s_Theta), 0f, MathF.Cos(s_Theta));

            foreach (var (s_Y, s_V) in new[] { (c_HalfHeight, 0f), (-c_HalfHeight, 1f) })
                s_Vertices.Add(new Vertex
                {
                    Position = new Vector3(s_Normal.X * c_Radius, s_Y, s_Normal.Z * c_Radius),
                    Normal = s_Normal,
                    Tangent = s_Tangent,
                    TexCoord = new Vector2(s_U * 2f, s_V),
                });
        }

        for (var s_Segment = 0; s_Segment < c_Segments; s_Segment++)
        {
            var s_Top = (ushort) (s_Segment * 2);
            var s_Bottom = (ushort) (s_Top + 1);
            var s_NextTop = (ushort) (s_Top + 2);
            var s_NextBottom = (ushort) (s_Top + 3);

            s_Indices.AddRange(new[] { s_Top, s_NextTop, s_Bottom, s_NextTop, s_NextBottom, s_Bottom });
        }

        // Caps: a fan around a centre vertex, UVs mapped from the disc.
        foreach (var (s_Y, s_Up) in new[] { (c_HalfHeight, 1f), (-c_HalfHeight, -1f) })
        {
            var s_Normal = new Vector3(0f, s_Up, 0f);
            var s_Centre = (ushort) s_Vertices.Count;
            s_Vertices.Add(new Vertex
            {
                Position = new Vector3(0f, s_Y, 0f), Normal = s_Normal,
                Tangent = new Vector3(1f, 0f, 0f), TexCoord = new Vector2(0.5f, 0.5f),
            });

            for (var s_Segment = 0; s_Segment <= c_Segments; s_Segment++)
            {
                var s_Theta = s_Segment / (float) c_Segments * MathF.PI * 2f;
                var (s_X, s_Z) = (MathF.Cos(s_Theta), MathF.Sin(s_Theta));
                s_Vertices.Add(new Vertex
                {
                    Position = new Vector3(s_X * c_Radius, s_Y, s_Z * c_Radius), Normal = s_Normal,
                    Tangent = new Vector3(1f, 0f, 0f),
                    TexCoord = new Vector2(0.5f + s_X * 0.5f, 0.5f + s_Z * 0.5f * s_Up),
                });
            }

            for (var s_Segment = 0; s_Segment < c_Segments; s_Segment++)
            {
                var s_A = (ushort) (s_Centre + 1 + s_Segment);
                var s_B = (ushort) (s_A + 1);
                s_Indices.AddRange(s_Up > 0
                    ? new[] { s_Centre, s_B, s_A }
                    : new[] { s_Centre, s_A, s_B });
            }
        }

        m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Vertices.ToArray());
        m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Indices.ToArray());
        m_IndexCount = s_Indices.Count;
    }

    /// <summary>
    /// A unit quad, BOTH faces, standing upright facing the camera's home position: the honest view for
    /// decals, terrain layers and anything whose look is really a texture. Two opposed faces rather than a
    /// disabled cull, so orbiting behind it shows a lit surface instead of an inside-out ghost.
    /// </summary>
    private void BuildPlane()
    {
        // The exact per-face construction the cube uses (verified winding + the V-grows-downward law),
        // for the two opposed faces only, scaled to a full unit and flattened to zero thickness.
        var s_Faces = new[]
        {
            (Normal: new Vector3(0, 0, -1), Tangent: new Vector3(1, 0, 0)),
            (Normal: new Vector3(0, 0, 1), Tangent: new Vector3(-1, 0, 0)),
        };

        var s_Vertices = new Vertex[8];
        var s_Indices = new ushort[12];

        for (var i = 0; i < s_Faces.Length; i++)
        {
            var s_Normal = s_Faces[i].Normal;
            var s_Tangent = s_Faces[i].Tangent;
            var s_Binormal = Vector3.Cross(s_Normal, s_Tangent);

            var s_Corners = new[]
            {
                -s_Tangent - s_Binormal, s_Tangent - s_Binormal, s_Tangent + s_Binormal, -s_Tangent + s_Binormal,
            };
            var s_Uvs = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            };

            for (var s_Corner = 0; s_Corner < 4; s_Corner++)
                s_Vertices[i * 4 + s_Corner] = new Vertex
                {
                    Position = s_Corners[s_Corner] * 0.5f,
                    Normal = s_Normal,
                    Tangent = s_Tangent,
                    TexCoord = s_Uvs[s_Corner],
                };

            var s_Base = i * 4;
            var s_Slot = i * 6;
            s_Indices[s_Slot + 0] = (ushort) (s_Base + 0);
            s_Indices[s_Slot + 1] = (ushort) (s_Base + 1);
            s_Indices[s_Slot + 2] = (ushort) (s_Base + 2);
            s_Indices[s_Slot + 3] = (ushort) (s_Base + 0);
            s_Indices[s_Slot + 4] = (ushort) (s_Base + 2);
            s_Indices[s_Slot + 5] = (ushort) (s_Base + 3);
        }

        m_CubeVertices = Buffer.Create(m_Device, BindFlags.VertexBuffer, s_Vertices);
        m_CubeIndices = Buffer.Create(m_Device, BindFlags.IndexBuffer, s_Indices);
        m_IndexCount = s_Indices.Length;
    }

    private ShaderResourceView CreateFlatTexture(uint p_Colour)
    {
        var s_Pixels = new uint[4];
        for (var i = 0; i < s_Pixels.Length; i++)
            s_Pixels[i] = p_Colour;

        return CreateTexture(2, 2, s_Pixels);
    }

    private ShaderResourceView CreateCheckerTexture(uint p_A, uint p_B)
    {
        const int c_Size = 16;
        var s_Pixels = new uint[c_Size * c_Size];
        for (var y = 0; y < c_Size; y++)
        for (var x = 0; x < c_Size; x++)
            s_Pixels[y * c_Size + x] = ((x / 4 + y / 4) & 1) == 0 ? p_A : p_B;

        return CreateTexture(c_Size, c_Size, s_Pixels);
    }

    /// <summary>
    /// ⛔ THE VIEW'S FORMAT IS PART OF THE SHADER'S INPUT. A texture the game flags sRGB is sampled through an
    /// sRGB view there, so the shader receives LINEAR values (a stored 0.12 arrives as 0.013). Creating every
    /// view as plain UNORM handed the shader the stored numbers instead, which is a ×10 error wherever a channel
    /// feeds a mask — the weapon presets' wear mask saturated almost everywhere at the game's own WearAmount and
    /// every camo previewed as worn off. The flag arrives with the pixels; synthetic art stays linear.
    /// </summary>
    /// <summary>The neutral sky bound to the cube registers nobody fills; built once, on demand.</summary>
    private ShaderResourceView? m_NeutralCube;

    /// <summary>
    /// When set, EVERY foreign section's empty 2D registers get the neutral (see DrawSectionWith), not only a context section's — set by
    /// the window for a family whose subjects' own foreign materials must not borrow the edited shader's art either (the soldiers: the
    /// first-person forearms' skin binds no AO, and the empty t2 took the sleeves' camo Mask — black forearms, 2026-09-28).
    /// </summary>
    public bool NeutralEmptySlots { get; set; }

    /// <summary>A context section's empty 2D registers: plain white, and a flat normal (0.5, 0.5, 1) where the shader decodes one.</summary>
    private ShaderResourceView? m_NeutralWhite;

    private ShaderResourceView? m_FlatNormal;

    /// <summary>One flat BGRA colour as a small texture, built once into its field.</summary>
    private ShaderResourceView? NeutralTexture(ref ShaderResourceView? p_Field, uint p_Bgra)
    {
        if (p_Field != null || m_Device == null)
            return p_Field;

        var s_Pixels = new uint[4 * 4];
        Array.Fill(s_Pixels, p_Bgra);
        p_Field = CreateTexture(4, 4, s_Pixels);
        return p_Field;
    }

    /// <summary>
    /// A stand-in sky for a foreign shader's CUBE registers. Mid-grey rather than white or black: it stands
    /// for an overcast ambient, and either extreme is somebody's lethal value (a reflection term that
    /// saturates, or one that deadens the surface).
    /// </summary>
    private ShaderResourceView? NeutralCube()
    {
        if (m_NeutralCube != null || m_Device == null)
            return m_NeutralCube;

        const int c_Side = 4;
        var s_Face = new uint[c_Side * c_Side];
        for (var i = 0; i < s_Face.Length; i++)
            s_Face[i] = 0xFF9AA0A8;

        var s_Handle = GCHandle.Alloc(s_Face, GCHandleType.Pinned);
        try
        {
            var s_Faces = new DataRectangle[6];
            for (var i = 0; i < 6; i++)
                s_Faces[i] = new DataRectangle(s_Handle.AddrOfPinnedObject(), c_Side * 4);

            var s_Texture = new Texture2D(m_Device, new Texture2DDescription
            {
                Width = c_Side,
                Height = c_Side,
                MipLevels = 1,
                ArraySize = 6,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                OptionFlags = ResourceOptionFlags.TextureCube,
            }, s_Faces);

            m_NeutralCube = new ShaderResourceView(m_Device, s_Texture);
            s_Texture.Dispose();
            return m_NeutralCube;
        }
        catch
        {
            // Without it the cube registers keep the old (wrong) fallback rather than the draw failing.
            return null;
        }
        finally
        {
            s_Handle.Free();
        }
    }

    private ShaderResourceView CreateTexture(int p_Width, int p_Height, uint[] p_Pixels, bool p_Srgb = false)
    {
        var s_Handle = GCHandle.Alloc(p_Pixels, GCHandleType.Pinned);
        try
        {
            var s_Texture = new Texture2D(m_Device, new Texture2DDescription
            {
                Width = p_Width,
                Height = p_Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = p_Srgb ? Format.B8G8R8A8_UNorm_SRgb : Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, new DataRectangle(s_Handle.AddrOfPinnedObject(), p_Width * 4));

            var s_View = new ShaderResourceView(m_Device, s_Texture);
            s_Texture.Dispose();
            return s_View;
        }
        finally
        {
            s_Handle.Free();
        }
    }

    /// <summary>
    /// Replaces a texture slot with real BF3 art once the thumbnails have been loaded. <paramref name="p_Srgb"/>
    /// is the game's own flag for that texture (see <see cref="CreateTexture"/>): true = sampled decoded to
    /// linear, as the game does; false = sampled as stored.
    /// </summary>
    public void SetTexture(int p_Register, uint[] p_Pixels, int p_Width, int p_Height, bool p_Srgb = false)
    {
        if (m_Device == null)
            return;

        // ⛔ The old body was `if (register == 2) ... else ...`, which collapsed every other register onto t1: a
        // shader with four textures ended up with three of them writing over each other in slot 1.
        if (p_Register < 0 || p_Register >= c_TextureSlots)
            return;

        var s_View = CreateTexture(p_Width, p_Height, p_Pixels, p_Srgb);
        m_Textures[p_Register]?.Dispose();
        m_Textures[p_Register] = s_View;

        // what the slot holds, for a seam to read (the size and a hash of the pixels: which picture it is, whoever put it there)
        var s_Hash = 2166136261u;
        for (var i = 0; i < p_Pixels.Length; i += Math.Max(1, p_Pixels.Length / 4096))
            s_Hash = (s_Hash ^ p_Pixels[i]) * 16777619u;
        m_SlotTags[p_Register] = $"{p_Width}x{p_Height}:{s_Hash:x8}";
    }

    private readonly string?[] m_SlotTags = new string?[c_TextureSlots];

    /// <summary>What the authored shader's texture slot last took (SetTexture): "WxH:hash", or null.</summary>
    public string? SlotTag(int p_Register) => p_Register >= 0 && p_Register < c_TextureSlots ? m_SlotTags[p_Register] : null;

    /// <summary>Swaps in a freshly compiled authored shader. Returns false and keeps the old one on failure.</summary>
    public bool SetAuthoredShader(byte[] p_Dxbc)
    {
        if (m_Device == null)
            return false;

        try
        {
            var s_Shader = new PixelShader(m_Device, p_Dxbc);
            m_AuthoredPs?.Dispose();
            m_AuthoredPs = s_Shader;
            AuthoredShaderVersion++;

            // One declared target = forward output; the resolve pass composites it instead of lighting it.
            try { m_ForwardOutput = Emit.ShaderContract.Detect(p_Dxbc).RenderTargets == 1; }
            catch { m_ForwardOutput = false; }

            LastError = null;
            return true;
        }
        catch (Exception s_Exception)
        {
            LastError = s_Exception.Message;
            return false;
        }
    }

    public void Resize(int p_Width, int p_Height)
    {
        if (m_Device == null || m_SwapChain == null)
            return;

        p_Width = Math.Max(1, p_Width);
        p_Height = Math.Max(1, p_Height);
        if (p_Width == m_Width && p_Height == m_Height)
            return;

        m_Width = p_Width;
        m_Height = p_Height;

        ReleaseSizeDependentResources();
        m_SwapChain.ResizeBuffers(2, m_Width, m_Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
        CreateSizeDependentResources();
    }

    private void CreateSizeDependentResources()
    {
        var s_Device = m_Device!;

        if (m_SwapChain != null)
        {
            using var s_BackBuffer = m_SwapChain.GetBackBuffer<Texture2D>(0);
            m_Output = null;
            m_BackBufferView = new RenderTargetView(s_Device, s_BackBuffer);
        }
        else
        {
            m_Output = new Texture2D(s_Device, new Texture2DDescription
            {
                Width = m_Width,
                Height = m_Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });

            m_BackBufferView = new RenderTargetView(s_Device, m_Output);
        }

        for (var i = 0; i < c_TargetCount; i++)
        {
            m_Targets[i] = new Texture2D(s_Device, new Texture2DDescription
            {
                Width = m_Width,
                Height = m_Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });

            m_TargetViews[i] = new RenderTargetView(s_Device, m_Targets[i]);
            m_TargetSrvs[i] = new ShaderResourceView(s_Device, m_Targets[i]);
        }

        // Typeless so the same surface can be a depth target and a shader resource for the resolve pass.
        m_Depth = new Texture2D(s_Device, new Texture2DDescription
        {
            Width = m_Width,
            Height = m_Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Typeless,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });

        m_DepthView = new DepthStencilView(s_Device, m_Depth, new DepthStencilViewDescription
        {
            Format = Format.D32_Float,
            Dimension = DepthStencilViewDimension.Texture2D,
        });

        m_DepthSrv = new ShaderResourceView(s_Device, m_Depth, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float,
            Dimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new ShaderResourceViewDescription.Texture2DResource { MipLevels = 1 },
        });
    }

    private void ReleaseSizeDependentResources()
    {
        m_BackBufferView?.Dispose();
        m_BackBufferView = null;
        m_Output?.Dispose();
        m_Output = null;

        for (var i = 0; i < c_TargetCount; i++)
        {
            m_TargetSrvs[i]?.Dispose();
            m_TargetViews[i]?.Dispose();
            m_Targets[i]?.Dispose();
            m_TargetSrvs[i] = null;
            m_TargetViews[i] = null;
            m_Targets[i] = null;
        }

        m_DepthSrv?.Dispose();
        m_DepthView?.Dispose();
        m_Depth?.Dispose();
        m_DepthSrv = null;
        m_DepthView = null;
        m_Depth = null;
    }

    public void Render()
    {
        if (m_Device == null || m_Context == null || m_BackBufferView == null)
            return;

        var s_Context = m_Context;
        var s_Camera = EyePosition();
        var s_ViewProj = ViewMatrix() * ProjectionMatrix();

        var s_Light = Vector3.Normalize(new Vector3(
            (float) (Math.Cos(LightPitch) * Math.Sin(LightYaw)),
            (float) Math.Sin(LightPitch),
            (float) (Math.Cos(LightPitch) * Math.Cos(LightYaw))));

        WriteVsConstants(World, s_ViewProj);
        WriteViewConstants(s_Camera);
        WriteLightConstants(s_ViewProj, s_Camera, s_Light);

        s_Context.Rasterizer.SetViewport(new Viewport(0, 0, m_Width, m_Height, 0f, 1f));
        s_Context.Rasterizer.State = RasterFor(false);
        s_Context.OutputMerger.DepthStencilState = m_DepthState;

        // Pass 1 -- the authored shader fills the four targets.
        var s_Views = new[] { m_TargetViews[0], m_TargetViews[1], m_TargetViews[2], m_TargetViews[3] };
        s_Context.OutputMerger.SetRenderTargets(m_DepthView, s_Views);

        foreach (var s_TargetView in s_Views)
            s_Context.ClearRenderTargetView(s_TargetView, new RawColor4(0, 0, 0, 0));

        s_Context.ClearDepthStencilView(m_DepthView, DepthStencilClearFlags.Depth, 1.0f, 0);

        if (m_AuthoredPs != null)
        {
            s_Context.InputAssembler.InputLayout = m_CubeLayout;
            s_Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            s_Context.InputAssembler.SetVertexBuffers(0,
                new VertexBufferBinding(m_CubeVertices, Utilities.SizeOf<Vertex>(), 0));
            s_Context.InputAssembler.SetIndexBuffer(m_CubeIndices, m_IndexFormat, 0);

            s_Context.VertexShader.Set(m_CubeVs);
            s_Context.VertexShader.SetConstantBuffer(0, m_VsConstants);

            s_Context.PixelShader.Set(m_AuthoredPs);
            s_Context.PixelShader.SetConstantBuffer(0, m_GlobalConstants);
            s_Context.PixelShader.SetConstantBuffer(1, m_ParameterConstants);
            s_Context.PixelShader.SetConstantBuffer(2, m_ViewConstants);
            // ⛔ EVERY sampler slot, not just s0. A shader that fetches through s1 - decals do, and so do several
            // terrain passes - otherwise ran against an UNBOUND sampler in the game's version (D3D11's default
            // state, which clamps) while ours, which only ever declares sampler0, ran against this one, which
            // wraps. Same defect as leaving cb0 unbound: the two halves of the differential test quietly got
            // different inputs, and it surfaced as a 2/255 difference at the edges.
            // ⛔ ALL SIXTEEN, not the first four. The terrain declares five samplers and fetches through s4, so a
            // four-slot loop left that one on D3D11's default state for the game's shader while ours ran on this
            // one - the same asymmetry as the unbound cb0, found the same way, and there is no reason to keep
            // guessing how many a shader might want.
            // ⛔ …AND WITH THE TARGET'S OWN ADDRESSING when the cached map knows it (see SetAuthoredSamplers):
            // the authored shader stands in for a game shader, so it has to be fed the state that one is bound
            // with — authoring the kit atlas shader through a wrapping sampler prints its tile band over every
            // piece, exactly as a foreign section did.
            for (var s_Sampler = 0; s_Sampler < c_SamplerSlots; s_Sampler++)
                s_Context.PixelShader.SetSampler(s_Sampler, m_AuthoredSamplers[s_Sampler] ?? m_Sampler);

            // Rebound every frame: the resolve pass below steals t0..t4 for the g-buffer and then unbinds them.
            for (var s_Slot = 1; s_Slot < c_TextureSlots; s_Slot++)
                s_Context.PixelShader.SetShaderResource(s_Slot, m_Textures[s_Slot]);

            // A loaded game mesh draws PER MATERIAL SECTION. Sections worn by the edited shader run the
            // authored pixel shader; sections worn by a REGISTERED foreign shader run that shader's own
            // game bytecode with its own art and its own contract-matched vertex feed; anything else falls
            // back to a neutral stand-in (opaque sections only) or is skipped. FORWARD sections — the
            // edited shader's included — are queued and composited AFTER the resolve, the way the engine
            // orders its passes, so a glass canopy blends over the lit body instead of into the GBuffer.
            m_ForwardQueue.Clear();
            m_SolidThisFrame.Clear();
            m_RecordingSolid = true;
            if (m_Shape == PreviewShape.Mesh && m_MeshSections.Count > 0 && MeshTargetShader.Length > 0)
            {
                for (var s_Index = 0; s_Index < m_MeshSections.Count; s_Index++)
                {
                    var s_Section = m_MeshSections[s_Index];
                    if (IsolatedSection is { } s_Only && s_Only != s_Index)
                        continue;

                    // A section the subject is NOT wearing (a kit the game equips separately, a rotor's blur
                    // disc) is not drawn at all — unlike a material "kept as shipped", which is drawn with its
                    // own bytecode. Asking for that material by name (isolating it, editing its graph) is the
                    // one thing that shows it: what the user picks explicitly is never left blank.
                    if (HiddenSections.Contains(s_Index) && IsolatedSection != s_Index && TargetSectionOnly != s_Index)
                        continue;

                    // "Own" decides hiding; "mine" decides the shader: under the factory look an own section
                    // is drawn with the game's bytecode of its own shader, never hidden as foreign. A section
                    // the camo is kept OFF draws with its own shader's bytecode too — as it ships.
                    var s_Own = IsTargetSection(s_Section);
                    var s_Off = s_Own && SectionsOff.Contains(s_Index);

                    // The authored shader carrying this MATERIAL's own art, when the window registered one:
                    // the same camo, over the textures and numbers that material really has. ⛔ Never while
                    // ONE material is being edited (TargetSectionOnly): that path owns the draw, and a dress
                    // would quietly replace the edit with the material's shipped art.
                    var s_Dress = s_Own && !s_Off && TargetSectionOnly == null ? DressFor(s_Index) : null;

                    // An OVERRIDE wins over everything, the authored shader included: it is a material whose own
                    // graph was edited, drawn with that edit on its section alone (the window's RefreshMaterialEdits).
                    var s_Mine = s_Own && !FactoryLook && !s_Off && s_Dress == null && OverrideFor(s_Index) == null &&
                                 (TargetSectionOnly == null || TargetSectionOnly == s_Index);
                    var s_Foreign = s_Mine
                        ? null
                        : OverrideFor(s_Index) ?? s_Dress ??
                          (s_Own && !s_Off
                              ? FactoryShaderFor(s_Index, s_Section)
                              : SectionArtFor(s_Index) ??
                                (m_ForeignShaders.TryGetValue(s_Section.Shader, out var s_Found) ? s_Found : null));

                    if (!s_Own && HideForeignSections && OverrideFor(s_Index) == null)
                        continue;

                    if (s_Mine ? m_ForwardOutput : s_Foreign?.Forward == true)
                    {
                        m_ForwardQueue.Add(s_Index);
                        continue;
                    }

                    s_Context.Rasterizer.State = RasterFor(s_Section.DoubleSided);

                    if (s_Mine)
                    {
                        s_Context.PixelShader.Set(m_AuthoredPs);
                        DrawSectionRanges(s_Context, s_Index, s_Section);
                    }
                    else if (s_Foreign != null)
                    {
                        DrawSectionWith(s_Context, s_Foreign, s_Index, s_Section);
                        RestoreAuthoredBindings(s_Context);
                    }
                    else if (s_Section.Category == 0)
                    {
                        // No real shader for it (yet): opaque sections keep the silhouette with the
                        // neutral stand-in; a transparent one is omitted — an opaque grey canopy or
                        // rotor disc OCCLUDES the object it belongs to.
                        s_Context.PixelShader.Set(m_ForwardOutput ? m_NeutralForwardPs : m_NeutralGBufferPs);
                        DrawSectionRanges(s_Context, s_Index, s_Section);
                    }
                }

                s_Context.Rasterizer.State = RasterFor(false);
                m_RecordingSolid = false;

                // what was drawn solid this frame, for the surface a vehicle's emblem slot lies on (see m_SolidSections)
                if (!m_SolidThisFrame.SetEquals(m_SolidSections))
                {
                    m_SolidSections = new HashSet<long>(m_SolidThisFrame);
                    m_SolidVersion++;
                    SolidSectionsChanged?.Invoke();
                }
            }
            else
            {
                s_Context.DrawIndexed(m_IndexCount, 0, 0);
            }
        }

        // Pass 2 -- light what was written. In sectioned-mesh mode the resolve is ALWAYS the GBuffer one:
        // forward sections (the edited shader's included) composite over it in pass 3, the engine's order.
        var s_Sectioned = m_Shape == PreviewShape.Mesh && m_MeshSections.Count > 0 && MeshTargetShader.Length > 0;

        s_Context.OutputMerger.SetRenderTargets((DepthStencilView?) null, m_BackBufferView);
        s_Context.ClearRenderTargetView(m_BackBufferView, new RawColor4(0.05f, 0.05f, 0.06f, 1f));

        // ⛔ The plain state, never the mesh's: the resolve is one fullscreen triangle of fixed (clockwise)
        // winding, and the MIRRORED state pass 1 leaves behind for a loaded mesh calls that a back face —
        // the whole frame then stays the clear colour (measured: a flat 13,13,15 where the weapon was).
        s_Context.Rasterizer.State = m_Raster;
        s_Context.InputAssembler.InputLayout = null;
        s_Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        s_Context.VertexShader.Set(m_ResolveVs);
        s_Context.PixelShader.Set(m_ForwardOutput && !s_Sectioned ? m_ForwardResolvePs : m_ResolvePs);
        s_Context.PixelShader.SetConstantBuffer(0, m_LightConstants);
        s_Context.PixelShader.SetSampler(0, m_Sampler);

        for (var i = 0; i < c_TargetCount; i++)
            s_Context.PixelShader.SetShaderResource(i, m_TargetSrvs[i]);

        s_Context.PixelShader.SetShaderResource(4, m_DepthSrv);
        s_Context.Draw(3, 0);

        // Unbind so the targets are writable again next frame. The whole range, not just the five the resolve
        // pass used: pass 1 now binds up to t15 and a slot left bound is a slot D3D11 refuses to render into.
        for (var i = 0; i < c_TextureSlots; i++)
            s_Context.PixelShader.SetShaderResource(i, null);

        // Pass 3 -- forward sections composite over the lit result: premultiplied blend against the
        // backbuffer, tested against (but not writing) pass 1's depth, exactly the engine's pass order.
        if (m_ForwardQueue.Count > 0)
        {
            s_Context.OutputMerger.SetRenderTargets(m_DepthView, m_BackBufferView);
            s_Context.OutputMerger.DepthStencilState = m_DepthReadState;
            s_Context.OutputMerger.BlendState = m_ForwardBlend;

            s_Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            s_Context.InputAssembler.SetVertexBuffers(0,
                new VertexBufferBinding(m_CubeVertices, Utilities.SizeOf<Vertex>(), 0));
            s_Context.InputAssembler.SetIndexBuffer(m_CubeIndices, m_IndexFormat, 0);

            foreach (var s_QueuedIndex in m_ForwardQueue)
            {
                var s_Section = m_MeshSections[s_QueuedIndex];
                s_Context.Rasterizer.State = RasterFor(s_Section.DoubleSided);

                var s_OwnForward = IsTargetSection(s_Section) && !SectionsOff.Contains(s_QueuedIndex);
                var s_MineForward = s_OwnForward && !FactoryLook && OverrideFor(s_QueuedIndex) == null &&
                                    (TargetSectionOnly == null || TargetSectionOnly == s_QueuedIndex);
                if (s_MineForward)
                {
                    RestoreAuthoredBindings(s_Context);
                    DrawSectionRanges(s_Context, s_QueuedIndex, s_Section);
                }
                else if (OverrideFor(s_QueuedIndex) is { } s_Override)
                {
                    DrawSectionWith(s_Context, s_Override, s_QueuedIndex, s_Section);
                }
                else if ((s_OwnForward
                             ? FactoryShaderFor(s_QueuedIndex, s_Section)
                             : SectionArtFor(s_QueuedIndex) ??
                               (m_ForeignShaders.TryGetValue(s_Section.Shader, out var s_Any) ? s_Any : null))
                         is { } s_Factory)
                {
                    DrawSectionWith(s_Context, s_Factory, s_QueuedIndex, s_Section);
                }
            }

            s_Context.Rasterizer.State = RasterFor(false);
            s_Context.OutputMerger.BlendState = null;
            s_Context.OutputMerger.DepthStencilState = m_DepthState;

            for (var i = 0; i < c_TextureSlots; i++)
                s_Context.PixelShader.SetShaderResource(i, null);
        }

        // Pass 3b -- pictures laid on the body (a vehicle's emblem slot shows the sample emblem in each square): tested against pass
        // 1's depth without writing it, premultiplied over the frame; the outline lines still go over everything after them.
        if (m_PictureVertices.Length > 0 && m_PicturePixels != null && m_PictureVs != null && m_PicturePs != null && m_Sampler != null)
        {
            if (m_PictureView == null)
            {
                var s_Words = new uint[m_PictureSide * m_PictureSide];
                System.Buffer.BlockCopy(m_PicturePixels, 0, s_Words, 0, Math.Min(m_PicturePixels.Length, s_Words.Length * 4));
                m_PictureView = CreateTexture(m_PictureSide, m_PictureSide, s_Words);
            }

            if (m_PictureDirty || m_PictureBuffer == null)
            {
                if (m_PictureBuffer == null || m_PictureCapacity < m_PictureVertices.Length)
                {
                    m_PictureBuffer?.Dispose();
                    m_PictureCapacity = Math.Max(64, m_PictureVertices.Length);
                    m_PictureBuffer = new Buffer(m_Device, m_PictureCapacity * Utilities.SizeOf<PictureVertex>(),
                        ResourceUsage.Dynamic, BindFlags.VertexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
                }

                Upload(m_PictureBuffer, m_PictureVertices);
                m_PictureDirty = false;
            }

            s_Context.OutputMerger.SetRenderTargets(m_DepthView, m_BackBufferView);
            s_Context.OutputMerger.DepthStencilState = m_DepthReadState;
            s_Context.OutputMerger.BlendState = m_ForwardBlend;
            s_Context.Rasterizer.State = m_RasterNoCull;

            s_Context.InputAssembler.InputLayout = m_PictureLayout;
            s_Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            s_Context.InputAssembler.SetVertexBuffers(0,
                new VertexBufferBinding(m_PictureBuffer, Utilities.SizeOf<PictureVertex>(), 0));
            s_Context.VertexShader.Set(m_PictureVs);
            s_Context.VertexShader.SetConstantBuffer(0, m_VsConstants);
            s_Context.PixelShader.Set(m_PicturePs);
            s_Context.PixelShader.SetShaderResource(0, m_PictureView);
            s_Context.PixelShader.SetSampler(0, m_Sampler);
            s_Context.Draw(m_PictureVertices.Length, 0);
            s_Context.PixelShader.SetShaderResource(0, null);

            s_Context.OutputMerger.BlendState = null;
            s_Context.OutputMerger.DepthStencilState = m_DepthState;
            s_Context.Rasterizer.State = RasterFor(false);
        }

        // Pass 4 -- the gizmo overlay, lines over everything (no depth test: an outline projected on the
        // surface must not sink into it), premultiplied over the finished frame.
        if (m_OverlayVertices.Length > 0 && m_OverlayVs != null && m_OverlayPs != null)
        {
            if (m_OverlayDirty || m_OverlayBuffer == null)
            {
                if (m_OverlayBuffer == null || m_OverlayCapacity < m_OverlayVertices.Length)
                {
                    m_OverlayBuffer?.Dispose();
                    m_OverlayCapacity = Math.Max(64, m_OverlayVertices.Length);
                    m_OverlayBuffer = new Buffer(m_Device, m_OverlayCapacity * Utilities.SizeOf<OverlayVertex>(),
                        ResourceUsage.Dynamic, BindFlags.VertexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
                }

                Upload(m_OverlayBuffer, m_OverlayVertices);
                m_OverlayDirty = false;
            }

            s_Context.OutputMerger.SetRenderTargets((DepthStencilView?) null, m_BackBufferView);
            s_Context.OutputMerger.DepthStencilState = m_DepthNone;
            s_Context.OutputMerger.BlendState = m_ForwardBlend;
            s_Context.Rasterizer.State = m_RasterNoCull;

            s_Context.InputAssembler.InputLayout = m_OverlayLayout;
            s_Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineList;
            s_Context.InputAssembler.SetVertexBuffers(0,
                new VertexBufferBinding(m_OverlayBuffer, Utilities.SizeOf<OverlayVertex>(), 0));
            s_Context.VertexShader.Set(m_OverlayVs);
            s_Context.VertexShader.SetConstantBuffer(0, m_VsConstants);
            s_Context.PixelShader.Set(m_OverlayPs);
            s_Context.Draw(m_OverlayVertices.Length, 0);

            s_Context.OutputMerger.BlendState = null;
            s_Context.OutputMerger.DepthStencilState = m_DepthState;
            s_Context.Rasterizer.State = RasterFor(false);
        }

        if (m_CaptureNextFrame)
        {
            var s_Shown = m_SwapChain != null ? m_SwapChain.GetBackBuffer<Texture2D>(0) : m_Output;
            if (s_Shown != null)
            {
                m_CapturedFrame = ReadPixels(s_Shown);
                SwapToBgra(m_CapturedFrame);
            }

            if (m_SwapChain != null)
                s_Shown?.Dispose();
        }

        m_SwapChain?.Present(1, PresentFlags.None);
    }

    /// <summary>Indices into the section list of the forward sections queued for pass 3.</summary>
    private readonly List<int> m_ForwardQueue = new();

    /// <summary>Full per-draw bindings for one foreign-shader section: its VS, layout, PS and textures.</summary>
    private void DrawSectionWith(DeviceContext p_Context, ForeignShader p_Foreign, int p_Index, MeshSection p_Section)
    {
        p_Context.InputAssembler.InputLayout = p_Foreign.Layout;
        p_Context.VertexShader.Set(p_Foreign.Vs);
        p_Context.VertexShader.SetConstantBuffer(0, m_VsConstants);
        p_Context.PixelShader.Set(p_Foreign.Ps);
        p_Context.PixelShader.SetConstantBuffer(0, m_GlobalConstants);
        p_Context.PixelShader.SetConstantBuffer(1, m_ParameterConstants);
        p_Context.PixelShader.SetConstantBuffer(2, m_ViewConstants);

        // Bound AFTER the defaults so a shader whose externals live in cb1 replaces the authored pattern.
        if (p_Foreign.Params != null)
            p_Context.PixelShader.SetConstantBuffer(p_Foreign.ParamsRegister, p_Foreign.Params);

        // ⛔⛔ AN EMPTY SLOT FALLS BACK TO THE AUTHORED SHADER'S SET, AND THAT ART IS 2D. A foreign shader
        // that declares a CUBE there — `vehicles/shaders/vehiclepreset_lights` samples
        // `texture_outdoorLightSkyEnvmap` as a texturecube at t1 — then reads a 2D view through a cube
        // declaration, which is undefined: the LAV-25's lamps came out MAGENTA. Those
        // registers get a neutral sky instead, never the borrowed 2D texture.
        // ⛔⛔ AND A CONTEXT SECTION (the rest of a soldier) NEVER BORROWS THE AUTHORED SET AT ALL (seen in a screenshot: the Aftermath
        // soldiers' faces looked wrong, like badly applied textures). The Aftermath RU heads bind no AO: characterroot_skin_xp4
        // multiplies the whole face by it (colour = AO × Diffuse) and the empty t2 took the TORSO's t2 — its camo Mask, rectangles of
        // black and white — so the faces came out patched with dark blocks. Nothing bound means the shader's neutral: white where it is
        // a colour, an AO or a dirt layer, a flat normal where it decodes a normal map. Weapons and vehicles have no context sections and
        // keep exactly the fallback they had.
        for (var s_Slot = 1; s_Slot < c_TextureSlots; s_Slot++)
            p_Context.PixelShader.SetShaderResource(s_Slot,
                p_Foreign.Textures[s_Slot] ??
                (p_Foreign.CubeRegisters.Contains(s_Slot) ? NeutralCube()
                    : p_Section.Context.Length == 0 && !NeutralEmptySlots ? m_Textures[s_Slot]
                    : p_Foreign.NormalRegisters.Contains(s_Slot) ? NeutralTexture(ref m_FlatNormal, 0xFF8080FF)
                    : NeutralTexture(ref m_NeutralWhite, 0xFFFFFFFF)));

        // ⛔ AND ITS OWN SAMPLERS. Addressing is API state, so a shader drawn through the preview's wrapping
        // sampler is NOT the game's shader with the game's art: the kit atlas one adds its tile atlas sampled
        // at (u, v+1), which the game's `v=Border` makes contribute nothing above V=0 — wrapped, it lands back
        // inside the atlas and prints the tile band over the piece (the Sprut-SD's armour blocks, pale grey).
        // A register the map does not name keeps the preview's sampler, exactly as before.
        for (var s_Sampler = 0; s_Sampler < c_SamplerSlots; s_Sampler++)
            p_Context.PixelShader.SetSampler(s_Sampler, p_Foreign.Samplers[s_Sampler] ?? m_Sampler);

        DrawSectionRanges(p_Context, p_Index, p_Section);
    }

    /// <summary>
    /// Sections left out of the drawing entirely — an object the game equips SEPARATELY and that the subject
    /// is not wearing (the reactive armour of a BMP-2 is one whole section of its hull mesh).
    /// </summary>
    public HashSet<int> HiddenSections { get; } = new();

    /// <summary>
    /// Per section, the composite PARTS that may be drawn; a section not named here is drawn whole.
    ///
    /// ⛔ IT HAS TO BE PER SECTION, not one global set: a Tunguska carries its reactive armour BOTH ways at
    /// once — a section of its own AND parts 46 of the shared kit section — so "only these parts" applied to
    /// every section would delete the piece it is meant to show.
    /// </summary>
    public Dictionary<int, HashSet<int>> VisibleParts { get; } = new();

    /// <summary>
    /// Draws a section, or only the parts of it that are visible. With nothing filtered it is the single
    /// DrawIndexed it always was — the ranges are contiguous because the loader grouped triangles by part.
    /// </summary>
    private void DrawSectionRanges(DeviceContext p_Context, int p_Index, MeshSection p_Section)
    {
        if (p_Section.Parts.Count == 0 || !VisibleParts.TryGetValue(p_Index, out var s_Visible))
        {
            p_Context.DrawIndexed(p_Section.IndexCount, p_Section.StartIndex, 0);
            if (m_RecordingSolid)
                m_SolidThisFrame.Add(SolidKey(p_Index, -1));
            return;
        }

        foreach (var (s_Part, s_Start, s_Count) in p_Section.Parts)
            if (s_Count > 0 && s_Visible.Contains(s_Part))
            {
                p_Context.DrawIndexed(s_Count, s_Start, 0);
                if (m_RecordingSolid)
                    m_SolidThisFrame.Add(SolidKey(p_Index, s_Part));
            }
    }

    /// <summary>Puts the ACTIVE shader's bindings back after a foreign section borrowed the pipeline.</summary>
    private void RestoreAuthoredBindings(DeviceContext p_Context)
    {
        p_Context.InputAssembler.InputLayout = m_CubeLayout;
        p_Context.VertexShader.Set(m_CubeVs);
        p_Context.VertexShader.SetConstantBuffer(0, m_VsConstants);
        p_Context.PixelShader.Set(m_AuthoredPs);
        p_Context.PixelShader.SetConstantBuffer(0, m_GlobalConstants);
        p_Context.PixelShader.SetConstantBuffer(1, m_ParameterConstants);
        p_Context.PixelShader.SetConstantBuffer(2, m_ViewConstants);

        for (var s_Slot = 1; s_Slot < c_TextureSlots; s_Slot++)
            p_Context.PixelShader.SetShaderResource(s_Slot, m_Textures[s_Slot]);

        // A foreign section may have left ITS addressing bound; the authored shader gets its own back.
        for (var s_Sampler = 0; s_Sampler < c_SamplerSlots; s_Sampler++)
            p_Context.PixelShader.SetSampler(s_Sampler, m_AuthoredSamplers[s_Sampler] ?? m_Sampler);
    }

    private void Upload<T>(Buffer p_Buffer, T[] p_Data) where T : struct
    {
        m_Context!.MapSubresource(p_Buffer, MapMode.WriteDiscard, MapFlags.None, out var s_Stream);
        s_Stream.WriteRange(p_Data);
        m_Context.UnmapSubresource(p_Buffer, 0);
    }

    private void WriteVsConstants(Matrix p_World, Matrix p_ViewProj)
    {
        var s_Values = new float[36];
        Matrix.Transpose(p_World).ToArray().CopyTo(s_Values, 0);
        Matrix.Transpose(p_ViewProj).ToArray().CopyTo(s_Values, 16);
        // g_meshRaw: raw = position / scale + centre (w = 1/scale; 0 = off, the components keep their old values)
        if (MeshPositionInW && m_MeshData != null && m_MeshScale > 0f)
        {
            s_Values[32] = m_MeshCentre.X;
            s_Values[33] = m_MeshCentre.Y;
            s_Values[34] = m_MeshCentre.Z;
            s_Values[35] = 1f / m_MeshScale;
        }

        Upload(m_VsConstants!, s_Values);
    }

    /// <summary>
    /// Feeds the mesh-space position (the dump's own units, before the lateral mirror) in the .w of the world position and the first
    /// two tangent rows, as an emblem clone's patched vertex shader does in the game (DxbcMeshPosition) — for a graph that reads it
    /// (Mesh Position). Off, those components keep the distinctive values every other subject is pinned to.
    /// </summary>
    public bool MeshPositionInW { get; set; }

    /// <summary>
    /// Fills the authored shader's cbuffer at the offsets measured on the vanilla shader: time at c0.x,
    /// screenSize at c1, viewportZMinMaxKzKw at c19 and cameraPos at c20. An unfilled field here is a silent
    /// no-op inside the shader, so every slot the layout declares is written.
    /// </summary>
    private void WriteViewConstants(Vector3 p_Camera)
    {
        var s_Values = new float[21 * 4];
        s_Values[0] = Time;

        s_Values[4] = m_Width;
        s_Values[5] = m_Height;
        s_Values[6] = 1f / m_Width;
        s_Values[7] = 1f / m_Height;

        s_Values[19 * 4 + 0] = 0.05f;
        s_Values[19 * 4 + 1] = 100f;

        s_Values[20 * 4 + 0] = p_Camera.X;
        s_Values[20 * 4 + 1] = p_Camera.Y;
        s_Values[20 * 4 + 2] = p_Camera.Z;

        Upload(m_ViewConstants!, s_Values);
    }

    private void WriteLightConstants(Matrix p_ViewProj, Vector3 p_Camera, Vector3 p_Light)
    {
        var s_Inverse = Matrix.Invert(p_ViewProj);
        s_Inverse.Transpose();

        var s_Values = new float[16 + 5 * 4];
        for (var i = 0; i < 16; i++)
            s_Values[i] = s_Inverse[i];

        void Put(int p_Slot, float p_X, float p_Y, float p_Z, float p_W)
        {
            var s_Base = 16 + p_Slot * 4;
            s_Values[s_Base + 0] = p_X;
            s_Values[s_Base + 1] = p_Y;
            s_Values[s_Base + 2] = p_Z;
            s_Values[s_Base + 3] = p_W;
        }

        Put(0, p_Light.X, p_Light.Y, p_Light.Z, 0f);
        Put(1, 1.0f, 0.97f, 0.92f, 0f);
        Put(2, 0.38f, 0.45f, 0.58f, 0f);
        Put(3, 0.16f, 0.15f, 0.14f, 0f);
        Put(4, p_Camera.X, p_Camera.Y, p_Camera.Z, Exposure);

        Upload(m_LightConstants!, s_Values);
    }

    public void Dispose()
    {
        ReleaseSizeDependentResources();

        foreach (var s_Texture in m_Textures)
            s_Texture?.Dispose();
        m_OverlayBuffer?.Dispose();
        m_OverlayBuffer = null;
        m_PictureBuffer?.Dispose();
        m_PictureBuffer = null;
        m_PictureView?.Dispose();
        m_PictureView = null;
        m_PictureLayout?.Dispose();
        m_PictureVs?.Dispose();
        m_PicturePs?.Dispose();
        m_RasterMirrored?.Dispose();
        m_OverlayLayout?.Dispose();
        m_OverlayVs?.Dispose();
        m_OverlayPs?.Dispose();
        m_DepthNone?.Dispose();
        m_AuthoredPs?.Dispose();
        m_ResolvePs?.Dispose();
        m_ForwardResolvePs?.Dispose();
        m_NeutralGBufferPs?.Dispose();
        m_NeutralForwardPs?.Dispose();
        m_DepthReadState?.Dispose();
        m_ForwardBlend?.Dispose();
        m_RasterNoCull?.Dispose();
        foreach (var s_Foreign in m_ForeignShaders.Values)
            s_Foreign.Dispose();
        m_ForeignShaders.Clear();
        m_ResolveVs?.Dispose();
        m_CubeLayout?.Dispose();
        m_CubeVs?.Dispose();
        m_NeutralCube?.Dispose();
        m_NeutralWhite?.Dispose();
        m_FlatNormal?.Dispose();
        m_DepthState?.Dispose();
        m_Raster?.Dispose();
        m_Sampler?.Dispose();

        foreach (var s_Sampler in m_SamplerCache.Values)
            s_Sampler.Dispose();
        m_SamplerCache.Clear();

        m_LightConstants?.Dispose();
        m_ViewConstants?.Dispose();
        m_ParameterConstants?.Dispose();
        m_GlobalConstants?.Dispose();
        m_VsConstants?.Dispose();
        m_CubeIndices?.Dispose();
        m_CubeVertices?.Dispose();
        m_SwapChain?.Dispose();
        m_Device?.Dispose();

        m_Device = null;
        m_Context = null;
        m_SwapChain = null;
    }
}
