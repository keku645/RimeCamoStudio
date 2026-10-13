using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimeShaderEditor.Graph;

/// <summary>
/// A placement resolved on the body: its centre, the surface normal there, its two axes in the tangent
/// plane (x along the picture's width, y down its height, the rotation applied), its half sizes and its
/// reach — the box the picture is projected through — all in the surface's units. <see cref="Scale"/> is
/// how many of those units one texture unit spans under the centre; <see cref="Triangle"/> the body
/// triangle the centre sits on.
/// </summary>
public readonly record struct StickerFrame(Vector3 Origin, Vector3 Normal, Vector3 AxisX, Vector3 AxisY,
    float HalfWidth, float HalfHeight, float Reach, float Scale, int Triangle)
{
    /// <summary>A point of the rectangle's plane: x along the width, y down the height, from the centre.</summary>
    public Vector3 At(float p_X, float p_Y) => Origin + AxisX * p_X + AxisY * p_Y;

    /// <summary>A point in the frame: x and y across the rectangle, z along the normal (above the surface positive).</summary>
    public Vector3 LocalOf(Vector3 p_Point)
    {
        var s_Offset = p_Point - Origin;
        return new Vector3(Vector3.Dot(s_Offset, AxisX), Vector3.Dot(s_Offset, AxisY), Vector3.Dot(s_Offset, Normal));
    }

    /// <summary>Whether a point lies inside the box the picture is projected through.</summary>
    public bool Contains(Vector3 p_Point)
    {
        var s_Local = LocalOf(p_Point);
        return Math.Abs(s_Local.X) <= HalfWidth && Math.Abs(s_Local.Y) <= HalfHeight && Math.Abs(s_Local.Z) <= Reach;
    }

    /// <summary>The rectangle's corners in its plane: top-left, top-right, bottom-right, bottom-left of the picture.</summary>
    public Vector3[] Corners() => new[]
    {
        At(-HalfWidth, -HalfHeight), At(HalfWidth, -HalfHeight), At(HalfWidth, HalfHeight), At(-HalfWidth, HalfHeight),
    };
}

/// <summary>
/// A weapon's body as a sticker sees it — its triangles with positions, texture coordinates and normals,
/// in whatever units the caller holds them — and the decal maths on top of it.
///
/// A sticker is a DECAL: a rectangle in the tangent plane of the point it sits on, projected along the
/// surface normal onto every triangle within its reach. So it runs across the seams of the unwrap — a
/// receiver panel and the rib standing proud beside it are two islands of the texture but one surface to
/// a sticker (a picture was cut at that seam when it could have gone over it) — and it never lands on
/// a part that only shares its patch of texture space (measured on the M416: composed as a plain rectangle
/// in texture space, a sticker dragged onto the handguard came out in fragments on the muzzle, the stock
/// and the magazine; the atlas packs them side by side). Faces turned away from the sticker or edge-on to
/// it are skipped, faces at a slant fade out, as a projected decal does.
///
/// Every quantity is RELATIVE — the decal's size comes from the texel density under its centre, its reach
/// from its size — so one placement composes the same layer from the editor's recentred, rescaled copy of
/// the mesh and from the cached dump the bake reads.
/// </summary>
public sealed class StickerSurface
{
    private readonly Vector3[] m_Positions;
    private readonly Vector3[] m_Normals;
    private readonly Vector2[] m_Uv;
    private readonly int[] m_Indices;
    private readonly Vector3[] m_FaceNormals;
    private readonly int[] m_IslandOfTriangle;
    private readonly Vector3 m_RawOrigin;
    private readonly float m_RawScale;

    /// <summary>Faces turned further than this (85°) from the sticker's normal are not drawn on at all.</summary>
    private const float c_CosSkip = 0.0872f;

    /// <summary>Faces within this (60°) of the sticker's normal are drawn in full; between the two they fade.</summary>
    private const float c_CosFull = 0.5f;

    public int TriangleCount => m_Indices.Length / 3;

    /// <summary>How many pieces (islands of the unwrap: triangles connected through shared vertices) the body has.</summary>
    public int IslandCount { get; }

    /// <summary>
    /// Builds the surface from a triangle list. <paramref name="p_RawOrigin"/> and <paramref name="p_RawScale"/>
    /// say how these positions relate to the mesh's own units (position = (raw − origin) × scale): a
    /// placement's anchor is kept in the mesh's units so the editor's rescaled copy and the bake's dump read
    /// the same point.
    /// </summary>
    public StickerSurface(Vector3[] p_Positions, Vector2[] p_Uv, int[] p_Indices, Vector3 p_RawOrigin = default, float p_RawScale = 1f)
    {
        m_Positions = p_Positions;
        m_Uv = p_Uv;
        m_Indices = p_Indices;
        m_RawOrigin = p_RawOrigin;
        m_RawScale = p_RawScale > 0f ? p_RawScale : 1f;

        // Smooth normals accumulated from the faces (area-weighted, the way the preview lights the mesh) and
        // the face normals themselves, for the facing tests.
        m_Normals = new Vector3[p_Positions.Length];
        m_FaceNormals = new Vector3[p_Indices.Length / 3];
        for (var t = 0; t < m_FaceNormals.Length; t++)
        {
            var s_A = p_Indices[t * 3];
            var s_B = p_Indices[t * 3 + 1];
            var s_C = p_Indices[t * 3 + 2];
            var s_Face = Vector3.Cross(p_Positions[s_B] - p_Positions[s_A], p_Positions[s_C] - p_Positions[s_A]);
            m_Normals[s_A] += s_Face;
            m_Normals[s_B] += s_Face;
            m_Normals[s_C] += s_Face;
            m_FaceNormals[t] = s_Face.LengthSquared() > 1e-18f ? Vector3.Normalize(s_Face) : Vector3.Zero;
        }

        // the same normals the preview lights with: whole across the unwrap's seams (see WeldNormals)
        WeldNormals(p_Positions, m_Normals);
        for (var i = 0; i < m_Normals.Length; i++)
            m_Normals[i] = m_Normals[i].LengthSquared() > 1e-18f ? Vector3.Normalize(m_Normals[i]) : Vector3.UnitY;

        // Islands are no longer what a sticker is clipped to; they name the pieces of the unwrap for whoever
        // checks that a sticker crossed a seam. Union-find over vertex indices: a seam duplicates the
        // vertices along it, so pieces that touch in 3D but not in the unwrap are different islands.
        var s_Parent = new int[p_Positions.Length];
        for (var i = 0; i < s_Parent.Length; i++)
            s_Parent[i] = i;

        int Find(int p_I)
        {
            while (s_Parent[p_I] != p_I)
            {
                s_Parent[p_I] = s_Parent[s_Parent[p_I]];
                p_I = s_Parent[p_I];
            }

            return p_I;
        }

        for (var i = 0; i + 2 < p_Indices.Length; i += 3)
        {
            var s_A = Find(p_Indices[i]);
            var s_B = Find(p_Indices[i + 1]);
            var s_C = Find(p_Indices[i + 2]);
            if (s_A != s_B)
                s_Parent[s_A] = s_B;
            if (Find(s_C) != Find(s_B))
                s_Parent[Find(s_C)] = Find(s_B);
        }

        var s_Ids = new Dictionary<int, int>();
        m_IslandOfTriangle = new int[m_FaceNormals.Length];
        for (var t = 0; t < m_IslandOfTriangle.Length; t++)
        {
            var s_Root = Find(p_Indices[t * 3]);
            if (!s_Ids.TryGetValue(s_Root, out var s_Id))
                s_Ids[s_Root] = s_Id = s_Ids.Count;

            m_IslandOfTriangle[t] = s_Id;
        }

        IslandCount = s_Ids.Count;
    }

    /// <summary>
    /// ⛔ SMOOTH NORMALS ARE WHOLE ACROSS THE UNWRAP'S SEAMS (the lighting was cut right down the middle of the weapon, and a sticker
    /// put there did not sit right). A dump carries position and UV only, so a vertex's normal is summed
    /// from its faces — and the vertex is split wherever its UV is, so along every seam each copy summed only ITS side: the body came
    /// out creased there (the normal target of the first-person view: a hard line down the M416's buffer tube), the light cut in half,
    /// and a sticker whose centre sat on the crease took a tilted frame and dropped the faces of the other side. The game's own normals
    /// are the artist's, whole across a seam. So the copies of a vertex at the same place pool their (area-weighted, unnormalised)
    /// sums — only where they agree within 60°: a real hard edge (a box's corner, 90°) is split for its normals, not for its UV, and
    /// stays hard. Measured: the tube's line gone; the M1A2 and the F2000 from the orbit move 0.13 % / 0.03 % of their pixels.
    /// CAMO_PREVIEW_WELD=0 turns it off (face-only normals, for a comparison). One rule for the preview's lighting and the stickers.
    /// </summary>
    public static void WeldNormals(IReadOnlyList<Vector3> p_Positions, Vector3[] p_Normals)
    {
        if (Environment.GetEnvironmentVariable("CAMO_PREVIEW_WELD") == "0" || p_Positions.Count == 0)
            return;

        // Copies of a vertex sit at exactly the same place (a seam splits the vertex, it does not move it); a small grid absorbs rounding.
        var s_Min = new Vector3(float.MaxValue);
        var s_Max = new Vector3(float.MinValue);
        foreach (var s_Position in p_Positions)
        {
            s_Min = Vector3.Min(s_Min, s_Position);
            s_Max = Vector3.Max(s_Max, s_Position);
        }

        var s_Cell = Math.Max(Math.Max(s_Max.X - s_Min.X, s_Max.Y - s_Min.Y), s_Max.Z - s_Min.Z) * 1e-6f;
        if (s_Cell <= 0f)
            return;

        var s_Groups = new Dictionary<(long, long, long), List<int>>();
        for (var i = 0; i < p_Positions.Count; i++)
        {
            var s_Key = ((long) Math.Round(p_Positions[i].X / s_Cell), (long) Math.Round(p_Positions[i].Y / s_Cell),
                (long) Math.Round(p_Positions[i].Z / s_Cell));
            if (!s_Groups.TryGetValue(s_Key, out var s_List))
                s_Groups[s_Key] = s_List = new List<int>(2);
            s_List.Add(i);
        }

        const float c_SmoothCosine = 0.5f;   // 60°
        var s_Own = (Vector3[]) p_Normals.Clone();
        foreach (var s_Group in s_Groups.Values)
        {
            if (s_Group.Count < 2)
                continue;

            foreach (var a in s_Group)
            {
                if (s_Own[a].LengthSquared() <= 1e-24f)
                    continue;

                var s_Mine = Vector3.Normalize(s_Own[a]);
                var s_Sum = Vector3.Zero;
                foreach (var b in s_Group)
                    if (s_Own[b].LengthSquared() > 1e-24f && Vector3.Dot(s_Mine, Vector3.Normalize(s_Own[b])) >= c_SmoothCosine)
                        s_Sum += s_Own[b];

                p_Normals[a] = s_Sum;
            }
        }
    }

    /// <summary>How many of this surface's units one of the mesh's own units spans (1 for a cached dump read as it is).</summary>
    public float RawScale => m_RawScale;

    /// <summary>The body's bounding box, in this surface's units.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var s_Min = new Vector3(float.MaxValue);
        var s_Max = new Vector3(float.MinValue);
        foreach (var s_Index in m_Indices)
        {
            s_Min = Vector3.Min(s_Min, m_Positions[s_Index]);
            s_Max = Vector3.Max(s_Max, m_Positions[s_Index]);
        }

        return (s_Min, s_Max);
    }

    /// <summary>The texture coordinate at a point of one triangle (the point projected onto its plane), for a placement made from a ray hit.</summary>
    public Vector2 UvAt(int p_Triangle, Vector3 p_Point)
    {
        var s_A = m_Indices[p_Triangle * 3];
        var s_B = m_Indices[p_Triangle * 3 + 1];
        var s_C = m_Indices[p_Triangle * 3 + 2];
        var s_V0 = m_Positions[s_B] - m_Positions[s_A];
        var s_V1 = m_Positions[s_C] - m_Positions[s_A];
        var s_V2 = p_Point - m_Positions[s_A];
        var s_D00 = Vector3.Dot(s_V0, s_V0);
        var s_D01 = Vector3.Dot(s_V0, s_V1);
        var s_D11 = Vector3.Dot(s_V1, s_V1);
        var s_D20 = Vector3.Dot(s_V2, s_V0);
        var s_D21 = Vector3.Dot(s_V2, s_V1);
        var s_Denominator = s_D00 * s_D11 - s_D01 * s_D01;
        if (Math.Abs(s_Denominator) < 1e-18f)
            return m_Uv[s_A];

        var s_WeightB = (s_D11 * s_D20 - s_D01 * s_D21) / s_Denominator;
        var s_WeightC = (s_D00 * s_D21 - s_D01 * s_D20) / s_Denominator;
        return m_Uv[s_A] * (1f - s_WeightB - s_WeightC) + m_Uv[s_B] * s_WeightB + m_Uv[s_C] * s_WeightC;
    }

    /// <summary>A point in the mesh's own units into this surface's units.</summary>
    public Vector3 FromRaw(Vector3 p_Raw) => (p_Raw - m_RawOrigin) * m_RawScale;

    /// <summary>A point of this surface into the mesh's own units — what a placement's anchor stores.</summary>
    public Vector3 ToRaw(Vector3 p_Point) => p_Point / m_RawScale + m_RawOrigin;

    private sbyte[]? m_Handedness;

    /// <summary>
    /// The handedness of a triangle's texture mapping: +1 where the unwrap lays the triangle out the way it
    /// faces, −1 where it lays it out in MIRROR — the two halves of a weapon whose sides share one patch of
    /// texture (the F2000's, measured 2026-09-12: 293 of 1805 texture cells used by both sides) differ in
    /// exactly this, and it is what the game's per-vertex BinormalSign carries into the shader, so a
    /// sticker painted on one half can be kept off the other. Computed from positions and texture
    /// coordinates: the sign of the tangent frame the mapping induces against the face normal.
    /// </summary>
    public int Handedness(int p_Triangle)
    {
        if (p_Triangle < 0 || p_Triangle >= m_FaceNormals.Length)
            return 1;

        if (m_Handedness == null)
        {
            m_Handedness = new sbyte[m_FaceNormals.Length];
            for (var t = 0; t < m_FaceNormals.Length; t++)
            {
                var s_IndexA = m_Indices[t * 3];
                var s_IndexB = m_Indices[t * 3 + 1];
                var s_IndexC = m_Indices[t * 3 + 2];
                var s_Edge1 = m_Positions[s_IndexB] - m_Positions[s_IndexA];
                var s_Edge2 = m_Positions[s_IndexC] - m_Positions[s_IndexA];
                var s_DeltaUv1 = m_Uv[s_IndexB] - m_Uv[s_IndexA];
                var s_DeltaUv2 = m_Uv[s_IndexC] - m_Uv[s_IndexA];
                var s_Determinant = s_DeltaUv1.X * s_DeltaUv2.Y - s_DeltaUv2.X * s_DeltaUv1.Y;
                if (Math.Abs(s_Determinant) < 1e-12f)
                {
                    m_Handedness[t] = 1;
                    continue;
                }

                var s_Tangent = (s_Edge1 * s_DeltaUv2.Y - s_Edge2 * s_DeltaUv1.Y) / s_Determinant;
                var s_Bitangent = (s_Edge2 * s_DeltaUv1.X - s_Edge1 * s_DeltaUv2.X) / s_Determinant;
                // The sign of the UV mapping against the triangle's winding, in the mesh's own space — what
                // the game's shader reads off the geometry it draws, in first AND third person (measured
                // 2026-09-12, F2000: a sticker on a one-hand part of the receiver drew in both views only with
                // this sign, "VU P2"). The studio's preview draws the mesh through a lateral mirror, which
                // flips every triangle's hand: ITS side map stores the opposite (StickerLayer.ComposeSideMap's
                // p_MirrorHand), the shipped maps store this.
                m_Handedness[t] = (sbyte) (Vector3.Dot(Vector3.Cross(s_Tangent, s_Bitangent), Vector3.Cross(s_Edge1, s_Edge2)) >= 0 ? 1 : -1);
            }
        }

        return m_Handedness[p_Triangle];
    }

    /// <summary>The grey a side map stores for a handedness: 85 for +1, 170 for −1 (0 = no sticker here, ungated).</summary>
    public static byte SideValue(int p_Handedness) => (byte) (p_Handedness >= 0 ? 85 : 170);

    /// <summary>
    /// Paints one sticker's SIDES into a side map: every triangle the decal covers, filled solid with the
    /// grey of its own handedness (see <see cref="Handedness"/>), grown like <see cref="DrawInto"/> so the two
    /// maps agree texel for texel. The shader compares this grey with the handedness of the pixel it
    /// draws and keeps the sticker off the mirrored half. Returns the triangles painted.
    /// </summary>
    public List<int> DrawSides(DrawingContext p_Context, StickerFrame p_Frame, int p_Side)
    {
        var s_Painted = new List<int>();
        Point Texel(int p_Vertex) => new(m_Uv[p_Vertex].X * p_Side, m_Uv[p_Vertex].Y * p_Side);
        var s_Brushes = new Dictionary<int, SolidColorBrush>();

        for (var t = 0; t < m_FaceNormals.Length; t++)
        {
            if (!Covers(p_Frame, t, out _))
                continue;

            var s_Hand = Handedness(t);
            if (!s_Brushes.TryGetValue(s_Hand, out var s_Brush))
            {
                var s_Grey = SideValue(s_Hand);
                s_Brush = new SolidColorBrush(Color.FromArgb(255, s_Grey, s_Grey, s_Grey));
                s_Brush.Freeze();
                s_Brushes[s_Hand] = s_Brush;
            }

            var s_IndexA = m_Indices[t * 3];
            var s_IndexB = m_Indices[t * 3 + 1];
            var s_IndexC = m_Indices[t * 3 + 2];
            var s_Grown = Grown(Texel(s_IndexA), Texel(s_IndexB), Texel(s_IndexC), 0.75);
            var s_Geometry = new StreamGeometry();
            using (var s_Figure = s_Geometry.Open())
            {
                s_Figure.BeginFigure(s_Grown[0], true, true);
                s_Figure.LineTo(s_Grown[1], false, false);
                s_Figure.LineTo(s_Grown[2], false, false);
            }

            s_Geometry.Freeze();
            p_Context.DrawGeometry(s_Brush, null, s_Geometry);
            s_Painted.Add(t);
        }

        return s_Painted;
    }

    /// <summary>
    /// Paints EVERY triangle of the body with the grey of its own handedness (85 = +1, 170 = −1) into a
    /// side-map picture — the whole field the side map is built from, for a picture that is compared with
    /// the hand the game's shader reads (an instrumented package paints the body red/green with it).
    /// </summary>
    public void DrawAllSides(DrawingContext p_Context, int p_Side)
    {
        Point Texel(int p_Vertex) => new(m_Uv[p_Vertex].X * p_Side, m_Uv[p_Vertex].Y * p_Side);
        var s_Brushes = new Dictionary<int, SolidColorBrush>();
        for (var t = 0; t < m_FaceNormals.Length; t++)
        {
            var s_Hand = Handedness(t);
            if (!s_Brushes.TryGetValue(s_Hand, out var s_Brush))
            {
                var s_Grey = SideValue(s_Hand);
                s_Brush = new SolidColorBrush(Color.FromArgb(255, s_Grey, s_Grey, s_Grey));
                s_Brush.Freeze();
                s_Brushes[s_Hand] = s_Brush;
            }

            var s_Grown = Grown(Texel(m_Indices[t * 3]), Texel(m_Indices[t * 3 + 1]), Texel(m_Indices[t * 3 + 2]), 0.75);
            var s_Geometry = new StreamGeometry();
            using (var s_Figure = s_Geometry.Open())
            {
                s_Figure.BeginFigure(s_Grown[0], true, true);
                s_Figure.LineTo(s_Grown[1], false, false);
                s_Figure.LineTo(s_Grown[2], false, false);
            }

            s_Geometry.Freeze();
            p_Context.DrawGeometry(s_Brush, null, s_Geometry);
        }
    }

    /// <summary>The piece of the unwrap a triangle belongs to, or -1.</summary>
    public int IslandOfTriangle(int p_Triangle) =>
        p_Triangle >= 0 && p_Triangle < m_IslandOfTriangle.Length ? m_IslandOfTriangle[p_Triangle] : -1;

    /// <summary>
    /// Every triangle whose texture-space triangle contains a coordinate — more than one when the unwrap
    /// lays two parts of the body over the same patch (a mirrored half).
    /// </summary>
    public IEnumerable<int> TrianglesAtUv(Vector2 p_Uv)
    {
        for (var t = 0; t < m_FaceNormals.Length; t++)
            if (Barycentric(t, p_Uv) != null)
                yield return t;
    }

    /// <summary>The first triangle covering a texture coordinate, or -1.</summary>
    public int TriangleAtUv(Vector2 p_Uv)
    {
        foreach (var s_Triangle in TrianglesAtUv(p_Uv))
            return s_Triangle;

        return -1;
    }

    /// <summary>The weights of a triangle's corners at a texture coordinate, or null when it lies outside.</summary>
    private (float A, float B, float C)? Barycentric(int p_Triangle, Vector2 p_Uv)
    {
        var s_A = m_Uv[m_Indices[p_Triangle * 3]];
        var s_B = m_Uv[m_Indices[p_Triangle * 3 + 1]];
        var s_C = m_Uv[m_Indices[p_Triangle * 3 + 2]];
        var s_V0 = s_B - s_A;
        var s_V1 = s_C - s_A;
        var s_V2 = p_Uv - s_A;
        var s_Denominator = s_V0.X * s_V1.Y - s_V1.X * s_V0.Y;
        if (Math.Abs(s_Denominator) < 1e-12f)
            return null;

        var s_WeightB = (s_V2.X * s_V1.Y - s_V1.X * s_V2.Y) / s_Denominator;
        var s_WeightC = (s_V0.X * s_V2.Y - s_V2.X * s_V0.Y) / s_Denominator;
        const float c_Slack = -1e-4f;
        if (s_WeightB < c_Slack || s_WeightC < c_Slack || s_WeightB + s_WeightC > 1f - c_Slack)
            return null;

        return (1f - s_WeightB - s_WeightC, s_WeightB, s_WeightC);
    }

    /// <summary>The surface point and smooth normal at a texture coordinate of one triangle.</summary>
    private (Vector3 Position, Vector3 Normal)? PointOn(int p_Triangle, Vector2 p_Uv)
    {
        if (Barycentric(p_Triangle, p_Uv) is not { } s_Weights)
            return null;

        var s_A = m_Indices[p_Triangle * 3];
        var s_B = m_Indices[p_Triangle * 3 + 1];
        var s_C = m_Indices[p_Triangle * 3 + 2];
        var s_Position = m_Positions[s_A] * s_Weights.A + m_Positions[s_B] * s_Weights.B + m_Positions[s_C] * s_Weights.C;
        var s_Normal = m_Normals[s_A] * s_Weights.A + m_Normals[s_B] * s_Weights.B + m_Normals[s_C] * s_Weights.C;
        return (s_Position, s_Normal.LengthSquared() > 1e-18f ? Vector3.Normalize(s_Normal) : m_FaceNormals[p_Triangle]);
    }

    /// <summary>
    /// The decal frame of a placement: where its centre sits on the body, the surface normal there, its two
    /// axes in the tangent plane (x along the surface's direction of increasing u, turned by the rotation —
    /// counter-clockwise seen from outside — y down from it), its half sizes and its reach, in this
    /// surface's units. The size follows the texel density under the centre (a width of 0.1 is a tenth of
    /// the texture across, as it always was) and the picture keeps its shape on the body even where the
    /// unwrap is stretched. Null when no part of the body covers the coordinate.
    /// </summary>
    public StickerFrame? FrameOf(StickerPlacement p_Sticker, double p_Aspect)
    {
        if (p_Sticker.IsSpatial)
            return SpatialFrameOf(p_Sticker, p_Aspect);

        var s_Uv = new Vector2((float) p_Sticker.U, (float) p_Sticker.V);
        Vector3? s_Anchor = p_Sticker.Anchor is { Length: 3 } s_Raw
            ? FromRaw(new Vector3((float) s_Raw[0], (float) s_Raw[1], (float) s_Raw[2]))
            : null;

        // The triangle under the centre — with an anchor, the covering triangle nearest to it: a mirrored
        // unwrap lays two parts over one patch, and the anchor says which one was clicked.
        var s_Triangle = -1;
        var s_Nearest = float.MaxValue;
        foreach (var s_Candidate in TrianglesAtUv(s_Uv))
        {
            if (s_Anchor == null)
            {
                s_Triangle = s_Candidate;
                break;
            }

            if (PointOn(s_Candidate, s_Uv) is { } s_On)
            {
                var s_Distance = Vector3.DistanceSquared(s_On.Position, s_Anchor.Value);
                if (s_Distance < s_Nearest)
                {
                    s_Nearest = s_Distance;
                    s_Triangle = s_Candidate;
                }
            }
        }

        if (s_Triangle < 0 || PointOn(s_Triangle, s_Uv) is not { } s_Centre)
            return null;

        var s_IndexA = m_Indices[s_Triangle * 3];
        var s_IndexB = m_Indices[s_Triangle * 3 + 1];
        var s_IndexC = m_Indices[s_Triangle * 3 + 2];
        var s_Edge1 = m_Positions[s_IndexB] - m_Positions[s_IndexA];
        var s_Edge2 = m_Positions[s_IndexC] - m_Positions[s_IndexA];
        var s_DeltaUv1 = m_Uv[s_IndexB] - m_Uv[s_IndexA];
        var s_DeltaUv2 = m_Uv[s_IndexC] - m_Uv[s_IndexA];

        // Texel density under the centre: surface units per texture unit, from the areas of the triangle in
        // the two spaces (twice each, the ratio is the same).
        var s_Determinant = s_DeltaUv1.X * s_DeltaUv2.Y - s_DeltaUv2.X * s_DeltaUv1.Y;
        var s_Area = Vector3.Cross(s_Edge1, s_Edge2).Length();
        if (Math.Abs(s_Determinant) < 1e-12f || s_Area < 1e-18f)
            return null;

        var s_Scale = (float) Math.Sqrt(s_Area / Math.Abs(s_Determinant));

        // The direction of increasing u on the surface, made orthogonal to the normal; y is "down" the
        // picture seen from outside, whichever way the unwrap runs — so the picture is never mirrored by the
        // unwrap (the Mirror flag is the user's).
        var s_Tangent = (s_Edge1 * s_DeltaUv2.Y - s_Edge2 * s_DeltaUv1.Y) / s_Determinant;
        var s_Normal = s_Centre.Normal;
        var s_X = s_Tangent - s_Normal * Vector3.Dot(s_Tangent, s_Normal);
        if (s_X.LengthSquared() < 1e-18f)
            s_X = AnyPerpendicular(s_Normal);
        s_X = Vector3.Normalize(s_X);
        var s_Y = Vector3.Cross(s_X, s_Normal);

        var s_Angle = p_Sticker.Rotation * Math.PI / 180.0;
        var s_Cos = (float) Math.Cos(s_Angle);
        var s_Sin = (float) Math.Sin(s_Angle);
        var s_AxisX = s_X * s_Cos - s_Y * s_Sin;
        var s_AxisY = s_X * s_Sin + s_Y * s_Cos;

        var s_HalfWidth = (float) (p_Sticker.Width * s_Scale / 2);
        var s_HalfHeight = (float) (StickerLayer.HeightOf(p_Sticker, p_Aspect) * s_Scale / 2);
        var s_Reach = (float) (StickerLayer.ReachOf(p_Sticker) * 2 * Math.Max(s_HalfWidth, s_HalfHeight));
        return new StickerFrame(s_Centre.Position, s_Normal, s_AxisX, s_AxisY, s_HalfWidth, s_HalfHeight, s_Reach, s_Scale, s_Triangle);
    }

    /// <summary>
    /// The frame of a placement laid out in space (<see cref="StickerPlacement.IsSpatial"/>, a vehicle's emblem slot): centred on its
    /// anchor, facing its normal, upright before its rotation (<see cref="UprightAxes"/>), Width and Height in metres. The unwrap is not
    /// read; the triangle is the one under the centre along the normal (−1 when the square floats off the body).
    /// </summary>
    private StickerFrame? SpatialFrameOf(StickerPlacement p_Sticker, double p_Aspect)
    {
        var s_Raw = p_Sticker.Anchor!;
        var s_Facing = new Vector3((float) p_Sticker.Normal![0], (float) p_Sticker.Normal[1], (float) p_Sticker.Normal[2]);
        if (s_Facing.LengthSquared() < 1e-12f)
            return null;

        var s_Normal = Vector3.Normalize(s_Facing);
        var s_Centre = FromRaw(new Vector3((float) s_Raw[0], (float) s_Raw[1], (float) s_Raw[2]));
        var (s_X, s_Y) = UprightAxes(s_Normal);

        var s_Angle = p_Sticker.Rotation * Math.PI / 180.0;
        var s_Cos = (float) Math.Cos(s_Angle);
        var s_Sin = (float) Math.Sin(s_Angle);
        var s_AxisX = s_X * s_Cos - s_Y * s_Sin;
        var s_AxisY = s_X * s_Sin + s_Y * s_Cos;

        var s_HalfWidth = (float) (p_Sticker.Width * m_RawScale / 2);
        var s_HalfHeight = (float) (StickerLayer.HeightOf(p_Sticker, p_Aspect) * m_RawScale / 2);
        var s_Reach = (float) (StickerLayer.ReachOf(p_Sticker) * 2 * Math.Max(s_HalfWidth, s_HalfHeight));
        var s_Under = RayHit(s_Centre + s_Normal * s_Reach, -s_Normal, s_Reach * 2f, s_Normal);
        return new StickerFrame(s_Centre, s_Normal, s_AxisX, s_AxisY, s_HalfWidth, s_HalfHeight, s_Reach, m_RawScale,
            s_Under?.Triangle ?? -1);
    }

    /// <summary>
    /// The upright axes of a square laid out in space, before its rotation: x to the picture's right and y down it, seen from outside
    /// (x × y = −normal, as every frame here). Upright = its top toward the mesh's +y; on a face looking up or down (+y within 27° of
    /// the normal), toward −z — the way BF4 stands its emblems on a vehicle's roof (measured on the Tunguska and the AAV: top (0, 0, −1)).
    /// </summary>
    public static (Vector3 X, Vector3 Y) UprightAxes(Vector3 p_Normal)
    {
        var s_Up = Vector3.UnitY - p_Normal * p_Normal.Y;
        if (s_Up.LengthSquared() < 0.2f)
            s_Up = -Vector3.UnitZ - p_Normal * Vector3.Dot(-Vector3.UnitZ, p_Normal);
        s_Up = Vector3.Normalize(s_Up);

        var s_Down = -s_Up;
        var s_X = Vector3.Normalize(Vector3.Cross(p_Normal, s_Down));
        return (s_X, Vector3.Cross(s_X, p_Normal));
    }

    private static Vector3 AnyPerpendicular(Vector3 p_Normal)
    {
        var s_Axis = Math.Abs(p_Normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Cross(p_Normal, s_Axis);
    }

    /// <summary>
    /// The nearest triangle a ray meets within a distance, with the smooth normal there; faces turned away
    /// from <paramref name="p_Facing"/> (edge-on ones included) are passed through. Null when none.
    /// </summary>
    public (Vector3 Position, Vector3 Normal, int Triangle, float Distance)? RayHit(Vector3 p_Origin, Vector3 p_Direction,
        float p_MaxDistance, Vector3? p_Facing = null)
    {
        if (Environment.GetEnvironmentVariable("CAMO_STICKER_RAY_LINEAR") == "1")
            return RayHitLinear(p_Origin, p_Direction, p_MaxDistance, p_Facing);

        EnsureBvh();
        var s_Nodes = m_BvhNodes!;
        var s_Order = m_BvhOrder!;
        (Vector3 Position, Vector3 Normal, int Triangle, float Distance)? s_Best = null;
        var s_Stack = new int[96];
        var s_Top = 0;
        s_Stack[s_Top++] = 0;
        while (s_Top > 0)
        {
            ref readonly var s_Node = ref s_Nodes[s_Stack[--s_Top]];

            // (a box entered beyond the best hit cannot hold a nearer one; one entered AT it may hold a tie of a lower triangle)
            var s_Limit = s_Best?.Distance ?? p_MaxDistance;
            if (!EntersBox(s_Node.Min, s_Node.Max, p_Origin, p_Direction, s_Limit))
                continue;

            if (s_Node.Count > 0)
            {
                for (var i = s_Node.First; i < s_Node.First + s_Node.Count; i++)
                    if (TriangleHit(s_Order[i], p_Origin, p_Direction, p_MaxDistance, p_Facing) is { } s_Hit &&
                        (s_Best == null || s_Hit.Distance < s_Best.Value.Distance ||
                         (s_Hit.Distance == s_Best.Value.Distance && s_Hit.Triangle < s_Best.Value.Triangle)))
                        s_Best = s_Hit;
                continue;
            }

            if (s_Top + 2 > s_Stack.Length)
                Array.Resize(ref s_Stack, s_Stack.Length * 2);
            s_Stack[s_Top++] = s_Node.First;
            s_Stack[s_Top++] = s_Node.First + 1;
        }

        return s_Best;
    }

    /// <summary>
    /// <see cref="RayHit"/> as it always was: every triangle in turn (the nearest hit, the lowest triangle on a tie). The yardstick the
    /// hierarchy is checked against, and CAMO_STICKER_RAY_LINEAR=1 = the way back.
    /// </summary>
    public (Vector3 Position, Vector3 Normal, int Triangle, float Distance)? RayHitLinear(Vector3 p_Origin, Vector3 p_Direction,
        float p_MaxDistance, Vector3? p_Facing = null)
    {
        (Vector3 Position, Vector3 Normal, int Triangle, float Distance)? s_Best = null;
        for (var t = 0; t < m_FaceNormals.Length; t++)
            if (TriangleHit(t, p_Origin, p_Direction, p_MaxDistance, p_Facing) is { } s_Hit &&
                (s_Best == null || s_Hit.Distance < s_Best.Value.Distance))
                s_Best = s_Hit;

        return s_Best;
    }

    /// <summary>One triangle against a ray (Möller–Trumbore, both faces), with the smooth normal where it is met; null when it is not.</summary>
    private (Vector3 Position, Vector3 Normal, int Triangle, float Distance)? TriangleHit(int t, Vector3 p_Origin, Vector3 p_Direction,
        float p_MaxDistance, Vector3? p_Facing)
    {
        if (p_Facing != null && Vector3.Dot(m_FaceNormals[t], p_Facing.Value) < c_CosSkip)
            return null;

        var s_IndexA = m_Indices[t * 3];
        var s_IndexB = m_Indices[t * 3 + 1];
        var s_IndexC = m_Indices[t * 3 + 2];
        var s_A = m_Positions[s_IndexA];
        var s_Edge1 = m_Positions[s_IndexB] - s_A;
        var s_Edge2 = m_Positions[s_IndexC] - s_A;

        // Möller–Trumbore.
        var s_P = Vector3.Cross(p_Direction, s_Edge2);
        var s_Det = Vector3.Dot(s_Edge1, s_P);
        if (Math.Abs(s_Det) < 1e-12f)
            return null;

        var s_InvDet = 1f / s_Det;
        var s_T = p_Origin - s_A;
        var s_U = Vector3.Dot(s_T, s_P) * s_InvDet;
        if (s_U < 0f || s_U > 1f)
            return null;

        var s_Q = Vector3.Cross(s_T, s_Edge1);
        var s_V = Vector3.Dot(p_Direction, s_Q) * s_InvDet;
        if (s_V < 0f || s_U + s_V > 1f)
            return null;

        var s_Distance = Vector3.Dot(s_Edge2, s_Q) * s_InvDet;
        if (s_Distance < 0f || s_Distance > p_MaxDistance)
            return null;

        var s_W = 1f - s_U - s_V;
        var s_Normal = m_Normals[s_IndexA] * s_W + m_Normals[s_IndexB] * s_U + m_Normals[s_IndexC] * s_V;
        return (p_Origin + p_Direction * s_Distance,
            s_Normal.LengthSquared() > 1e-18f ? Vector3.Normalize(s_Normal) : m_FaceNormals[t], t, s_Distance);
    }

    // --- a bounding volume hierarchy over the triangles, for RayHit -----------------------------------------------------------------
    //
    // ⏱ (orbiting a vehicle ran at 1–2 fps): every point of a sticker's outline casts a ray
    // (Contact), and every ray walked all the body's triangles — 30 000 on the LAV-25, a few thousand on a weapon. Built once, on the
    // first ray: a node is a box and either two children (First, First + 1) or a run of triangles (Count of them from First in the
    // order). The boxes are padded a little, so a ray the triangle test accepts is never refused by its box.

    private readonly record struct BvhNode(Vector3 Min, Vector3 Max, int First, int Count);

    private BvhNode[]? m_BvhNodes;
    private int[]? m_BvhOrder;

    private void EnsureBvh()
    {
        if (m_BvhNodes != null)
            return;

        var s_Count = m_FaceNormals.Length;
        var s_Order = new int[s_Count];
        var s_Centroids = new Vector3[s_Count];
        var s_Mins = new Vector3[s_Count];
        var s_Maxs = new Vector3[s_Count];
        for (var t = 0; t < s_Count; t++)
        {
            var (s_A, s_B, s_C) = CornersOf(t);
            s_Order[t] = t;
            s_Centroids[t] = (s_A + s_B + s_C) / 3f;
            s_Mins[t] = Vector3.Min(s_A, Vector3.Min(s_B, s_C));
            s_Maxs[t] = Vector3.Max(s_A, Vector3.Max(s_B, s_C));
        }

        var s_Nodes = new List<BvhNode>(Math.Max(1, s_Count / 2));
        s_Nodes.Add(default);
        var s_Pending = new Stack<(int Node, int First, int Count)>();
        s_Pending.Push((0, 0, s_Count));
        while (s_Pending.Count > 0)
        {
            var (s_Node, s_First, s_Run) = s_Pending.Pop();
            var s_Min = new Vector3(float.MaxValue);
            var s_Max = new Vector3(float.MinValue);
            var s_CentreMin = new Vector3(float.MaxValue);
            var s_CentreMax = new Vector3(float.MinValue);
            for (var i = s_First; i < s_First + s_Run; i++)
            {
                s_Min = Vector3.Min(s_Min, s_Mins[s_Order[i]]);
                s_Max = Vector3.Max(s_Max, s_Maxs[s_Order[i]]);
                s_CentreMin = Vector3.Min(s_CentreMin, s_Centroids[s_Order[i]]);
                s_CentreMax = Vector3.Max(s_CentreMax, s_Centroids[s_Order[i]]);
            }

            var s_Pad = (s_Max - s_Min) * 1e-4f + new Vector3(1e-6f);
            s_Min -= s_Pad;
            s_Max += s_Pad;
            if (s_Run <= 4)
            {
                s_Nodes[s_Node] = new BvhNode(s_Min, s_Max, s_First, Math.Max(s_Run, 0));
                continue;
            }

            // split at the middle of the centroids' longest side; a split that leaves one side empty halves the run instead
            var s_Extent = s_CentreMax - s_CentreMin;
            var s_Axis = s_Extent.X >= s_Extent.Y && s_Extent.X >= s_Extent.Z ? 0 : s_Extent.Y >= s_Extent.Z ? 1 : 2;
            float Along(int p_Triangle) => s_Axis == 0 ? s_Centroids[p_Triangle].X : s_Axis == 1 ? s_Centroids[p_Triangle].Y : s_Centroids[p_Triangle].Z;
            var s_Middle = s_Axis == 0 ? (s_CentreMin.X + s_CentreMax.X) / 2 : s_Axis == 1 ? (s_CentreMin.Y + s_CentreMax.Y) / 2 : (s_CentreMin.Z + s_CentreMax.Z) / 2;
            var s_Split = s_First;
            for (var i = s_First; i < s_First + s_Run; i++)
                if (Along(s_Order[i]) < s_Middle)
                {
                    (s_Order[i], s_Order[s_Split]) = (s_Order[s_Split], s_Order[i]);
                    s_Split++;
                }

            if (s_Split == s_First || s_Split == s_First + s_Run)
            {
                Array.Sort(s_Order, s_First, s_Run, Comparer<int>.Create((p_A, p_B) => Along(p_A).CompareTo(Along(p_B))));
                s_Split = s_First + s_Run / 2;
            }

            var s_Children = s_Nodes.Count;
            s_Nodes.Add(default);
            s_Nodes.Add(default);
            s_Nodes[s_Node] = new BvhNode(s_Min, s_Max, s_Children, 0);
            s_Pending.Push((s_Children, s_First, s_Split - s_First));
            s_Pending.Push((s_Children + 1, s_Split, s_First + s_Run - s_Split));
        }

        m_BvhOrder = s_Order;
        m_BvhNodes = s_Nodes.ToArray();
    }

    /// <summary>Whether a ray meets a box no further than <paramref name="p_Limit"/> along it (a ray starting inside meets it at 0).</summary>
    private static bool EntersBox(Vector3 p_Min, Vector3 p_Max, Vector3 p_Origin, Vector3 p_Direction, float p_Limit)
    {
        var s_Near = 0f;
        var s_Far = p_Limit;
        for (var s_Axis = 0; s_Axis < 3; s_Axis++)
        {
            var s_O = s_Axis == 0 ? p_Origin.X : s_Axis == 1 ? p_Origin.Y : p_Origin.Z;
            var s_D = s_Axis == 0 ? p_Direction.X : s_Axis == 1 ? p_Direction.Y : p_Direction.Z;
            var s_Lo = s_Axis == 0 ? p_Min.X : s_Axis == 1 ? p_Min.Y : p_Min.Z;
            var s_Hi = s_Axis == 0 ? p_Max.X : s_Axis == 1 ? p_Max.Y : p_Max.Z;
            if (Math.Abs(s_D) < 1e-20f)
            {
                if (s_O < s_Lo || s_O > s_Hi)
                    return false;
                continue;
            }

            var s_T0 = (s_Lo - s_O) / s_D;
            var s_T1 = (s_Hi - s_O) / s_D;
            if (s_T0 > s_T1)
                (s_T0, s_T1) = (s_T1, s_T0);
            s_Near = Math.Max(s_Near, s_T0);
            s_Far = Math.Min(s_Far, s_T1);
            if (s_Near > s_Far)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Where a point of the rectangle's plane lands on the body, dropped along the sticker's normal within
    /// its reach — the outline and the handles sit there. Null when it overhangs the body.
    /// </summary>
    public (Vector3 Position, Vector3 Normal)? Contact(StickerFrame p_Frame, float p_X, float p_Y)
    {
        var s_Hit = RayHit(p_Frame.At(p_X, p_Y) + p_Frame.Normal * p_Frame.Reach, -p_Frame.Normal, p_Frame.Reach * 2f, p_Frame.Normal);
        return s_Hit == null ? null : (s_Hit.Value.Position, s_Hit.Value.Normal);
    }

    /// <summary>A triangle wholly beyond one face of the box is out of the projection.</summary>
    private static bool Outside(Vector3 p_A, Vector3 p_B, Vector3 p_C, StickerFrame p_Frame) =>
        (p_A.X > p_Frame.HalfWidth && p_B.X > p_Frame.HalfWidth && p_C.X > p_Frame.HalfWidth) ||
        (p_A.X < -p_Frame.HalfWidth && p_B.X < -p_Frame.HalfWidth && p_C.X < -p_Frame.HalfWidth) ||
        (p_A.Y > p_Frame.HalfHeight && p_B.Y > p_Frame.HalfHeight && p_C.Y > p_Frame.HalfHeight) ||
        (p_A.Y < -p_Frame.HalfHeight && p_B.Y < -p_Frame.HalfHeight && p_C.Y < -p_Frame.HalfHeight) ||
        (p_A.Z > p_Frame.Reach && p_B.Z > p_Frame.Reach && p_C.Z > p_Frame.Reach) ||
        (p_A.Z < -p_Frame.Reach && p_B.Z < -p_Frame.Reach && p_C.Z < -p_Frame.Reach);

    /// <summary>Whether the projection paints a triangle at all: within the box and facing the sticker.</summary>
    private bool Covers(StickerFrame p_Frame, int p_Triangle, out float p_Facing)
    {
        p_Facing = Vector3.Dot(m_FaceNormals[p_Triangle], p_Frame.Normal);
        if (p_Facing < c_CosSkip)
            return false;

        return !Outside(p_Frame.LocalOf(m_Positions[m_Indices[p_Triangle * 3]]),
            p_Frame.LocalOf(m_Positions[m_Indices[p_Triangle * 3 + 1]]),
            p_Frame.LocalOf(m_Positions[m_Indices[p_Triangle * 3 + 2]]), p_Frame);
    }

    /// <summary>The triangles a placement is painted on, for whoever checks where a sticker went.</summary>
    public IEnumerable<int> CoveredTriangles(StickerFrame p_Frame)
    {
        for (var t = 0; t < m_FaceNormals.Length; t++)
            if (Covers(p_Frame, t, out _))
                yield return t;
    }

    /// <summary>A triangle's centre in texture space, for whoever reports where a sticker went.</summary>
    public Vector2 UvCentroidOf(int p_Triangle) =>
        (m_Uv[m_Indices[p_Triangle * 3]] + m_Uv[m_Indices[p_Triangle * 3 + 1]] + m_Uv[m_Indices[p_Triangle * 3 + 2]]) / 3f;

    /// <summary>A triangle's centre on the body, in this surface's units.</summary>
    public Vector3 CentroidOf(int p_Triangle) =>
        (m_Positions[m_Indices[p_Triangle * 3]] + m_Positions[m_Indices[p_Triangle * 3 + 1]] + m_Positions[m_Indices[p_Triangle * 3 + 2]]) / 3f;

    /// <summary>How squarely a triangle faces a placement: 1 flat under it, 0 edge-on.</summary>
    public float FacingOf(StickerFrame p_Frame, int p_Triangle) => Vector3.Dot(m_FaceNormals[p_Triangle], p_Frame.Normal);

    /// <summary>A triangle's face normal.</summary>
    public Vector3 FaceNormalOf(int p_Triangle) => m_FaceNormals[p_Triangle];

    /// <summary>A triangle's three corners, in this surface's units.</summary>
    public (Vector3 A, Vector3 B, Vector3 C) CornersOf(int p_Triangle) =>
        (m_Positions[m_Indices[p_Triangle * 3]], m_Positions[m_Indices[p_Triangle * 3 + 1]], m_Positions[m_Indices[p_Triangle * 3 + 2]]);

    /// <summary>
    /// The triangle whose SURFACE passes closest to a point (point-to-triangle distance, not the centre's: a placement's anchor sits on a
    /// face, often a long one) and that distance; -1 on an empty body. Degenerate triangles (two corners on one point) are skipped.
    /// </summary>
    public (int Triangle, float Distance) ClosestTriangle(Vector3 p_Point)
    {
        var s_Best = (-1, float.MaxValue);
        for (var t = 0; t < TriangleCount; t++)
        {
            var (s_A, s_B, s_C) = CornersOf(t);
            if (Vector3.Cross(s_B - s_A, s_C - s_A).LengthSquared() < 1e-20f)
                continue;

            var s_Distance = Vector3.Distance(ClosestOnTriangle(p_Point, s_A, s_B, s_C), p_Point);
            if (s_Distance < s_Best.Item2)
                s_Best = (t, s_Distance);
        }

        return s_Best;
    }

    /// <summary>The point of a triangle nearest another (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    private static Vector3 ClosestOnTriangle(Vector3 p_P, Vector3 p_A, Vector3 p_B, Vector3 p_C)
    {
        var s_Ab = p_B - p_A;
        var s_Ac = p_C - p_A;
        var s_Ap = p_P - p_A;
        var s_D1 = Vector3.Dot(s_Ab, s_Ap);
        var s_D2 = Vector3.Dot(s_Ac, s_Ap);
        if (s_D1 <= 0f && s_D2 <= 0f)
            return p_A;

        var s_Bp = p_P - p_B;
        var s_D3 = Vector3.Dot(s_Ab, s_Bp);
        var s_D4 = Vector3.Dot(s_Ac, s_Bp);
        if (s_D3 >= 0f && s_D4 <= s_D3)
            return p_B;

        var s_Vc = s_D1 * s_D4 - s_D3 * s_D2;
        if (s_Vc <= 0f && s_D1 >= 0f && s_D3 <= 0f)
            return p_A + s_Ab * (s_D1 / (s_D1 - s_D3));

        var s_Cp = p_P - p_C;
        var s_D5 = Vector3.Dot(s_Ab, s_Cp);
        var s_D6 = Vector3.Dot(s_Ac, s_Cp);
        if (s_D6 >= 0f && s_D5 <= s_D6)
            return p_C;

        var s_Vb = s_D5 * s_D2 - s_D1 * s_D6;
        if (s_Vb <= 0f && s_D2 >= 0f && s_D6 <= 0f)
            return p_A + s_Ac * (s_D2 / (s_D2 - s_D6));

        var s_Va = s_D3 * s_D6 - s_D5 * s_D4;
        if (s_Va <= 0f && s_D4 - s_D3 >= 0f && s_D5 - s_D6 >= 0f)
            return p_B + (p_C - p_B) * ((s_D4 - s_D3) / ((s_D4 - s_D3) + (s_D5 - s_D6)));

        var s_Denominator = 1f / (s_Va + s_Vb + s_Vc);
        return p_A + s_Ab * (s_Vb * s_Denominator) + s_Ac * (s_Vc * s_Denominator);
    }

    /// <summary>
    /// Of the triangles a placement covers, how many have their winding normal pointing WITH the vertex
    /// normals the dump carries and how many AGAINST. The side map's handedness is computed from the
    /// winding; the shader reads its own from the interpolated vertex normal — on a part where the two
    /// disagree, the map says one side and the game reads the other, and the gate shuts the sticker.
    /// </summary>
    public (int Agree, int Disagree) WindingAgreement(StickerFrame p_Frame, Vector3[] p_VertexNormals)
    {
        var s_Agree = 0;
        var s_Disagree = 0;
        foreach (var t in CoveredTriangles(p_Frame))
        {
            var s_A = m_Indices[t * 3];
            var s_B = m_Indices[t * 3 + 1];
            var s_C = m_Indices[t * 3 + 2];
            if (s_A >= p_VertexNormals.Length || s_B >= p_VertexNormals.Length || s_C >= p_VertexNormals.Length)
                continue;

            var s_Vertex = p_VertexNormals[s_A] + p_VertexNormals[s_B] + p_VertexNormals[s_C];
            if (Vector3.Dot(m_FaceNormals[t], s_Vertex) < 0)
                s_Disagree++;
            else
                s_Agree++;
        }

        return (s_Agree, s_Disagree);
    }

    /// <summary>The triangles whose centre lies within a distance of a point — for a report of what shares a spot of the body.</summary>
    public IEnumerable<int> TrianglesNear(Vector3 p_Point, float p_Distance)
    {
        var s_Limit = p_Distance * p_Distance;
        for (var t = 0; t < m_FaceNormals.Length; t++)
            if (Vector3.DistanceSquared(CentroidOf(t), p_Point) <= s_Limit)
                yield return t;
    }

    /// <summary>A picture's pixels for the rasteriser: Bgra32, straight alpha, decoded once per picture.</summary>
    public sealed class Picture
    {
        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, Picture> s_Cache = new();

        private Picture(BitmapSource p_Source)
        {
            Width = p_Source.PixelWidth;
            Height = p_Source.PixelHeight;
            var s_Straight = p_Source.Format == PixelFormats.Bgra32 ? p_Source : new FormatConvertedBitmap(p_Source, PixelFormats.Bgra32, null, 0);
            Pixels = new byte[Width * Height * 4];
            s_Straight.CopyPixels(Pixels, Width * 4, 0);
        }

        /// <summary>The pixels of a (frozen) picture, kept while the picture lives.</summary>
        public static Picture Of(BitmapSource p_Source) => s_Cache.GetValue(p_Source, p_S => new Picture(p_S));

        /// <summary>
        /// The picture at a point of its pixel space (pixel i spans [i, i+1)), bilinear, PREMULTIPLIED;
        /// transparent outside the picture, as an image brush that does not tile paints nothing there.
        /// </summary>
        public (float B, float G, float R, float A) Sample(double p_X, double p_Y)
        {
            if (p_X < 0 || p_Y < 0 || p_X >= Width || p_Y >= Height)
                return default;

            var s_U = Math.Clamp(p_X - 0.5, 0, Width - 1);
            var s_V = Math.Clamp(p_Y - 0.5, 0, Height - 1);
            var s_X0 = (int) s_U;
            var s_Y0 = (int) s_V;
            var s_X1 = Math.Min(s_X0 + 1, Width - 1);
            var s_Y1 = Math.Min(s_Y0 + 1, Height - 1);
            var s_Fx = (float) (s_U - s_X0);
            var s_Fy = (float) (s_V - s_Y0);

            (float B, float G, float R, float A) At(int p_Px, int p_Py)
            {
                var i = (p_Py * Width + p_Px) * 4;
                var s_A = Pixels[i + 3] / 255f;
                return (Pixels[i] / 255f * s_A, Pixels[i + 1] / 255f * s_A, Pixels[i + 2] / 255f * s_A, s_A);
            }

            var s_00 = At(s_X0, s_Y0);
            var s_10 = At(s_X1, s_Y0);
            var s_01 = At(s_X0, s_Y1);
            var s_11 = At(s_X1, s_Y1);
            float Mix(float p_00, float p_10, float p_01, float p_11) =>
                (p_00 * (1 - s_Fx) + p_10 * s_Fx) * (1 - s_Fy) + (p_01 * (1 - s_Fx) + p_11 * s_Fx) * s_Fy;
            return (Mix(s_00.B, s_10.B, s_01.B, s_11.B), Mix(s_00.G, s_10.G, s_01.G, s_11.G), Mix(s_00.R, s_10.R, s_01.R, s_11.R), Mix(s_00.A, s_10.A, s_01.A, s_11.A));
        }
    }

    /// <summary>
    /// Paints one sticker into a layer: every triangle within the decal's box and facing it is filled with
    /// the picture mapped through the projection — which pixel of the picture a surface point shows is
    /// decided in 3D, then the triangle is drawn where the unwrap puts it, so the layer reads right in the
    /// game's texture space and the picture runs unbroken across the seams of the unwrap. Rasterised straight into a
    /// premultiplied Bgra32 layer, texel by texel. Its cost follows the texels a sticker covers, not the
    /// triangles times the picture — the image-brush path cost 200–800 ms a refresh on the F2000 (measured
    /// 2026-09-12: the sticker moved only every 2 s), this one a few. Same grown triangles, same slant fade, same
    /// order of painting (later stickers over earlier ones). Returns the triangles painted.
    /// </summary>
    public List<int> DrawInto(byte[] p_Pixels, int p_Side, StickerFrame p_Frame, Picture p_Image, bool p_Mirror)
    {
        var s_Width = p_Image.Width;
        var s_Height = p_Image.Height;
        var s_Painted = new List<int>();

        Point Pixel(Vector3 p_Local) => new(
            (p_Mirror ? 0.5 - p_Local.X / (2 * p_Frame.HalfWidth) : 0.5 + p_Local.X / (2 * p_Frame.HalfWidth)) * s_Width,
            (0.5 + p_Local.Y / (2 * p_Frame.HalfHeight)) * s_Height);

        Point Texel(int p_Vertex) => new(m_Uv[p_Vertex].X * p_Side, m_Uv[p_Vertex].Y * p_Side);

        for (var t = 0; t < m_FaceNormals.Length; t++)
        {
            if (!Covers(p_Frame, t, out var s_Facing))
                continue;

            var s_IndexA = m_Indices[t * 3];
            var s_IndexB = m_Indices[t * 3 + 1];
            var s_IndexC = m_Indices[t * 3 + 2];
            var s_PixelA = Pixel(p_Frame.LocalOf(m_Positions[s_IndexA]));
            var s_PixelB = Pixel(p_Frame.LocalOf(m_Positions[s_IndexB]));
            var s_PixelC = Pixel(p_Frame.LocalOf(m_Positions[s_IndexC]));
            var s_A = Texel(s_IndexA);
            var s_B = Texel(s_IndexB);
            var s_C = Texel(s_IndexC);

            // Barycentric weights against the triangle as the unwrap places it; the map to the picture is
            // affine, so the weights carry a texel to its picture point — and past the edges, for the growth.
            var s_Denominator = (s_B.X - s_A.X) * (s_C.Y - s_A.Y) - (s_C.X - s_A.X) * (s_B.Y - s_A.Y);
            if (Math.Abs(s_Denominator) < 1e-9)
                continue;

            var s_Opacity = s_Facing >= c_CosFull ? 1.0 : Smooth((s_Facing - c_CosSkip) / (c_CosFull - c_CosSkip));
            var s_Grown = Grown(s_A, s_B, s_C, 0.75);
            var s_Area = (s_Grown[1].X - s_Grown[0].X) * (s_Grown[2].Y - s_Grown[0].Y) - (s_Grown[2].X - s_Grown[0].X) * (s_Grown[1].Y - s_Grown[0].Y);
            if (Math.Abs(s_Area) < 1e-9)
                continue;

            var s_X0 = Math.Max(0, (int) Math.Floor(Math.Min(s_Grown[0].X, Math.Min(s_Grown[1].X, s_Grown[2].X))));
            var s_X1 = Math.Min(p_Side - 1, (int) Math.Ceiling(Math.Max(s_Grown[0].X, Math.Max(s_Grown[1].X, s_Grown[2].X))));
            var s_Y0 = Math.Max(0, (int) Math.Floor(Math.Min(s_Grown[0].Y, Math.Min(s_Grown[1].Y, s_Grown[2].Y))));
            var s_Y1 = Math.Min(p_Side - 1, (int) Math.Ceiling(Math.Max(s_Grown[0].Y, Math.Max(s_Grown[1].Y, s_Grown[2].Y))));
            var s_Sign = Math.Sign(s_Area);
            var s_Touched = false;

            for (var y = s_Y0; y <= s_Y1; y++)
            for (var x = s_X0; x <= s_X1; x++)
            {
                var s_Px = x + 0.5;
                var s_Py = y + 0.5;

                // Inside the grown triangle: on the same side of each edge as the third corner.
                var s_E0 = (s_Grown[1].X - s_Grown[0].X) * (s_Py - s_Grown[0].Y) - (s_Grown[1].Y - s_Grown[0].Y) * (s_Px - s_Grown[0].X);
                var s_E1 = (s_Grown[2].X - s_Grown[1].X) * (s_Py - s_Grown[1].Y) - (s_Grown[2].Y - s_Grown[1].Y) * (s_Px - s_Grown[1].X);
                var s_E2 = (s_Grown[0].X - s_Grown[2].X) * (s_Py - s_Grown[2].Y) - (s_Grown[0].Y - s_Grown[2].Y) * (s_Px - s_Grown[2].X);
                if (s_E0 * s_Sign < 0 || s_E1 * s_Sign < 0 || s_E2 * s_Sign < 0)
                    continue;

                var s_Dx = s_Px - s_A.X;
                var s_Dy = s_Py - s_A.Y;
                var s_WeightB = ((s_C.Y - s_A.Y) * s_Dx - (s_C.X - s_A.X) * s_Dy) / s_Denominator;
                var s_WeightC = ((s_B.X - s_A.X) * s_Dy - (s_B.Y - s_A.Y) * s_Dx) / s_Denominator;
                var s_WeightA = 1 - s_WeightB - s_WeightC;
                var s_ImageX = s_WeightA * s_PixelA.X + s_WeightB * s_PixelB.X + s_WeightC * s_PixelC.X;
                var s_ImageY = s_WeightA * s_PixelA.Y + s_WeightB * s_PixelB.Y + s_WeightC * s_PixelC.Y;
                var (s_SB, s_SG, s_SR, s_SA) = p_Image.Sample(s_ImageX, s_ImageY);
                if (s_SA <= 0)
                    continue;

                // Premultiplied "over", the picture at the face's opacity.
                var s_Keep = 1 - (float) s_Opacity * s_SA;
                var i = (y * p_Side + x) * 4;
                p_Pixels[i] = (byte) Math.Min(255, (int) Math.Round(s_SB * s_Opacity * 255 + p_Pixels[i] * s_Keep));
                p_Pixels[i + 1] = (byte) Math.Min(255, (int) Math.Round(s_SG * s_Opacity * 255 + p_Pixels[i + 1] * s_Keep));
                p_Pixels[i + 2] = (byte) Math.Min(255, (int) Math.Round(s_SR * s_Opacity * 255 + p_Pixels[i + 2] * s_Keep));
                p_Pixels[i + 3] = (byte) Math.Min(255, (int) Math.Round(s_SA * s_Opacity * 255 + p_Pixels[i + 3] * s_Keep));
                s_Touched = true;
            }

            if (s_Touched)
                s_Painted.Add(t);
        }

        return s_Painted;
    }

    /// <summary>
    /// Pads a composed layer across the seams of the unwrap: a texel outside every painted triangle that
    /// touches a painted one takes that neighbour's colour, <paramref name="p_Rings"/> texels deep. The game
    /// filters the layer bilinearly and builds mips, so a painted texel next to a transparent one that
    /// belongs to ANOTHER piece of the unwrap would blend with it — a faint line wherever a sticker crosses
    /// a seam. The picture's own edge, inside the painted triangles, is left as it is.
    /// </summary>
    public void PadAcrossSeams(byte[] p_Pixels, int p_Side, IReadOnlyCollection<int> p_Painted, int p_Rings = 2)
    {
        if (p_Painted.Count == 0)
            return;

        // The painted triangles' box in texels, widened by the rings.
        var s_MinX = p_Side;
        var s_MinY = p_Side;
        var s_MaxX = -1;
        var s_MaxY = -1;
        foreach (var s_Triangle in p_Painted)
            for (var c = 0; c < 3; c++)
            {
                var s_Uv = m_Uv[m_Indices[s_Triangle * 3 + c]];
                s_MinX = Math.Min(s_MinX, (int) Math.Floor(s_Uv.X * p_Side));
                s_MinY = Math.Min(s_MinY, (int) Math.Floor(s_Uv.Y * p_Side));
                s_MaxX = Math.Max(s_MaxX, (int) Math.Ceiling(s_Uv.X * p_Side));
                s_MaxY = Math.Max(s_MaxY, (int) Math.Ceiling(s_Uv.Y * p_Side));
            }

        s_MinX = Math.Max(0, s_MinX - p_Rings - 1);
        s_MinY = Math.Max(0, s_MinY - p_Rings - 1);
        s_MaxX = Math.Min(p_Side - 1, s_MaxX + p_Rings + 1);
        s_MaxY = Math.Min(p_Side - 1, s_MaxY + p_Rings + 1);
        var s_Width = s_MaxX - s_MinX + 1;
        var s_Height = s_MaxY - s_MinY + 1;
        if (s_Width <= 0 || s_Height <= 0)
            return;

        // Which texels of the box lie inside a painted triangle (centre inside, the rasteriser's rule).
        var s_Inside = new bool[s_Width * s_Height];
        foreach (var s_Triangle in p_Painted)
        {
            var s_A = m_Uv[m_Indices[s_Triangle * 3]] * p_Side;
            var s_B = m_Uv[m_Indices[s_Triangle * 3 + 1]] * p_Side;
            var s_C = m_Uv[m_Indices[s_Triangle * 3 + 2]] * p_Side;
            var s_Denominator = (s_B.X - s_A.X) * (s_C.Y - s_A.Y) - (s_C.X - s_A.X) * (s_B.Y - s_A.Y);
            if (Math.Abs(s_Denominator) < 1e-9f)
                continue;

            var s_X0 = Math.Max(s_MinX, (int) Math.Floor(Math.Min(s_A.X, Math.Min(s_B.X, s_C.X))));
            var s_X1 = Math.Min(s_MaxX, (int) Math.Ceiling(Math.Max(s_A.X, Math.Max(s_B.X, s_C.X))));
            var s_Y0 = Math.Max(s_MinY, (int) Math.Floor(Math.Min(s_A.Y, Math.Min(s_B.Y, s_C.Y))));
            var s_Y1 = Math.Min(s_MaxY, (int) Math.Ceiling(Math.Max(s_A.Y, Math.Max(s_B.Y, s_C.Y))));
            for (var y = s_Y0; y <= s_Y1; y++)
            for (var x = s_X0; x <= s_X1; x++)
            {
                var s_Px = x + 0.5f - s_A.X;
                var s_Py = y + 0.5f - s_A.Y;
                var s_WeightB = ((s_C.Y - s_A.Y) * s_Px - (s_C.X - s_A.X) * s_Py) / s_Denominator;
                var s_WeightC = ((s_B.X - s_A.X) * s_Py - (s_B.Y - s_A.Y) * s_Px) / s_Denominator;
                if (s_WeightB >= -1e-4f && s_WeightC >= -1e-4f && s_WeightB + s_WeightC <= 1f + 1e-4f)
                    s_Inside[(y - s_MinY) * s_Width + (x - s_MinX)] = true;
            }
        }

        // Ring by ring: an outside texel takes its most opaque painted neighbour, then counts as painted.
        for (var s_Ring = 0; s_Ring < p_Rings; s_Ring++)
        {
            var s_Next = (bool[]) s_Inside.Clone();
            for (var y = 0; y < s_Height; y++)
            for (var x = 0; x < s_Width; x++)
            {
                if (s_Inside[y * s_Width + x])
                    continue;

                var s_Best = -1;
                var s_BestAlpha = -1;
                void Consider(int p_X, int p_Y)
                {
                    if (p_X < 0 || p_Y < 0 || p_X >= s_Width || p_Y >= s_Height || !s_Inside[p_Y * s_Width + p_X])
                        return;

                    var s_At = ((p_Y + s_MinY) * p_Side + (p_X + s_MinX)) * 4;
                    if (p_Pixels[s_At + 3] > s_BestAlpha)
                    {
                        s_BestAlpha = p_Pixels[s_At + 3];
                        s_Best = s_At;
                    }
                }

                Consider(x - 1, y);
                Consider(x + 1, y);
                Consider(x, y - 1);
                Consider(x, y + 1);
                if (s_Best < 0)
                    continue;

                var s_Here = ((y + s_MinY) * p_Side + (x + s_MinX)) * 4;
                p_Pixels[s_Here] = p_Pixels[s_Best];
                p_Pixels[s_Here + 1] = p_Pixels[s_Best + 1];
                p_Pixels[s_Here + 2] = p_Pixels[s_Best + 2];
                p_Pixels[s_Here + 3] = p_Pixels[s_Best + 3];
                s_Next[y * s_Width + x] = true;
            }

            s_Inside = s_Next;
        }
    }

    private static double Smooth(double p_T)
    {
        p_T = Math.Clamp(p_T, 0.0, 1.0);
        return p_T * p_T * (3 - 2 * p_T);
    }

    /// <summary>A triangle grown outward by a distance: each corner moves to where its two edges, shifted out, meet (mitre capped for needle corners).</summary>
    private static Point[] Grown(Point p_A, Point p_B, Point p_C, double p_By)
    {
        var s_Corners = new[] { p_A, p_B, p_C };
        var s_Sign = (p_B.X - p_A.X) * (p_C.Y - p_A.Y) - (p_C.X - p_A.X) * (p_B.Y - p_A.Y) >= 0 ? 1.0 : -1.0;
        var s_Result = new Point[3];
        for (var i = 0; i < 3; i++)
        {
            var s_Previous = s_Corners[(i + 2) % 3];
            var s_Here = s_Corners[i];
            var s_Next = s_Corners[(i + 1) % 3];
            var s_In = OutwardNormal(s_Previous, s_Here, s_Sign);
            var s_Out = OutwardNormal(s_Here, s_Next, s_Sign);
            var s_Dot = s_In.X * s_Out.X + s_In.Y * s_Out.Y;
            var s_Scale = p_By / Math.Max(0.2, 1 + s_Dot);
            s_Result[i] = new Point(s_Here.X + (s_In.X + s_Out.X) * s_Scale, s_Here.Y + (s_In.Y + s_Out.Y) * s_Scale);
        }

        return s_Result;
    }

    /// <summary>The unit normal of an edge pointing out of its triangle, given the triangle's winding sign.</summary>
    private static Point OutwardNormal(Point p_From, Point p_To, double p_Sign)
    {
        var s_X = p_To.X - p_From.X;
        var s_Y = p_To.Y - p_From.Y;
        var s_Length = Math.Sqrt(s_X * s_X + s_Y * s_Y);
        if (s_Length < 1e-9)
            return new Point(0, 0);

        return p_Sign > 0 ? new Point(s_Y / s_Length, -s_X / s_Length) : new Point(-s_Y / s_Length, s_X / s_Length);
    }

    /// <summary>
    /// Reads a sectioned mesh dump (the RSM3 file the studio caches per weapon) and keeps the sections a
    /// filter accepts by their shader's name — the body a sticker lives on — in the mesh's own units. Null
    /// when the file is missing, not an RSM3, or has no such section.
    /// </summary>
    public static StickerSurface? LoadRsm3(string p_Path, Func<string, bool> p_KeepSection)
    {
        try
        {
            if (!File.Exists(p_Path))
                return null;

            using var s_Reader = new BinaryReader(File.OpenRead(p_Path));
            var s_Magic = new string(s_Reader.ReadChars(4));
            if (s_Magic != "RSM4" && s_Magic != "RSM5" && s_Magic != "RSM6" && s_Magic != "RSM7")
                return null;

            // RSM5 keeps a second texture coordinate set per vertex and RSM7 a third; a sticker is composed
            // on the first, the others are skipped.
            var s_ExtraSets = s_Magic switch { "RSM5" or "RSM6" => 1, "RSM7" => 2, _ => 0 };

            // RSM6 also keeps the part index per vertex. A sticker does not care which part it lands on, but
            // the field still has to be SKIPPED or the next section header is read from inside this one.
            var s_HasParts = s_Magic is "RSM6" or "RSM7";

            var s_SectionCount = s_Reader.ReadInt32();
            var s_Positions = new List<Vector3>();
            var s_Uv = new List<Vector2>();
            var s_Indices = new List<int>();

            for (var s_Section = 0; s_Section < s_SectionCount; s_Section++)
            {
                var s_Shader = System.Text.Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32())).Replace('\\', '/');
                _ = System.Text.Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32()));
                _ = s_Reader.ReadInt32();
                _ = s_Reader.ReadInt32();
                var s_Keep = p_KeepSection(s_Shader);
                var s_Base = s_Positions.Count;
                var s_VertexCount = s_Reader.ReadInt32();

                for (var i = 0; i < s_VertexCount; i++)
                {
                    var s_X = s_Reader.ReadSingle();
                    var s_Y = s_Reader.ReadSingle();
                    var s_Z = s_Reader.ReadSingle();
                    var s_U = s_Reader.ReadSingle();
                    var s_V = s_Reader.ReadSingle();
                    for (var s_Extra = 0; s_Extra < s_ExtraSets; s_Extra++)
                    {
                        s_Reader.ReadSingle();
                        s_Reader.ReadSingle();
                    }

                    if (s_Keep)
                    {
                        s_Positions.Add(new Vector3(s_X, s_Y, s_Z));
                        s_Uv.Add(new Vector2(s_U, s_V));
                    }
                }

                var s_IndexCount = s_Reader.ReadInt32();
                for (var i = 0; i < s_IndexCount; i++)
                {
                    var s_Index = s_Reader.ReadInt32();
                    if (s_Keep)
                        s_Indices.Add(s_Base + s_Index);
                }

                if (s_HasParts)
                    s_Reader.BaseStream.Seek(s_VertexCount * (long) sizeof(int), SeekOrigin.Current);
            }

            return s_Indices.Count == 0 ? null : new StickerSurface(s_Positions.ToArray(), s_Uv.ToArray(), s_Indices.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
