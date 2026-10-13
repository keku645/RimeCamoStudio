using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimeShaderEditor.Emit;
using RimeShaderEditor.Graph;
using RimeShaderEditor.View;

namespace RimeCamoStudio.Cache;

/// <summary>
/// Renders one weapon off-screen, exactly as the studio would: its preset's graph, its mesh, its textures.
///
/// ⛔ WRITTEN BECAUSE THE DATA SAID EVERYTHING WAS FINE AND WEAPONS STILL DREW GREEN. Auditing what SHOULD
/// reach the preview cannot answer what the preview DOES; without a way to look at one, every next step is
/// a guess and every check costs a launch of the tool.
/// </summary>
public static class WeaponRender
{
    public sealed class Result
    {
        public string? Error { get; set; }
        public int Textures { get; set; }
        public int Sections { get; set; }

        /// <summary>Material values wired into the constant buffer — the other half of "textured".</summary>
        public int Values { get; set; }

        /// <summary>Share of pixels that are essentially untextured green — the symptom, measured.</summary>
        public double GreenFraction { get; set; }
    }

    /// <summary>
    /// Renders weapons one after another through a SINGLE preview, the way a real session does.
    ///
    /// ⛔ THE ISOLATED SWEEP CANNOT SEE THIS CLASS OF BUG. It builds a fresh preview per weapon, so nothing
    /// can carry over — and "it depends on the order I open them in" is, by definition, something carrying
    /// over. Keeping one preview across the whole run is what makes that reproducible here instead of only
    /// on the user's screen.
    /// </summary>
    public static List<(string Weapon, string Hash)> Session(
        IReadOnlyList<(string Weapon, string Mesh, string Shader,
            IReadOnlyDictionary<string, string> Slots, IReadOnlyDictionary<string, string>? Values)> p_Order,
        string p_OutputDir, int p_Size = 384)
    {
        var s_Hashes = new List<(string, string)>();
        using var s_Preview = new ShaderPreview();
        if (!s_Preview.InitialiseOffscreen(p_Size, p_Size))
            return s_Hashes;

        var s_CurrentShader = "";
        Directory.CreateDirectory(p_OutputDir);

        foreach (var (s_Weapon, s_Mesh, s_Shader, s_Slots, s_Values) in p_Order)
        {
            // The window only recompiles when the PRESET changes; mirror that, or the reproduction differs
            // from the thing being reproduced.
            if (!s_Shader.Equals(s_CurrentShader, StringComparison.OrdinalIgnoreCase))
            {
                if (!Apply(s_Preview, s_Shader))
                    continue;

                s_CurrentShader = s_Shader;
            }

            var s_Rsm = GameCache.MeshFile(s_Mesh);
            if (!File.Exists(s_Rsm) || s_Preview.LoadMeshSections(s_Rsm) != null)
                continue;

            s_Preview.MeshTargetShader = s_Shader;
            s_Preview.MeshTargetAliases = CamoSession.ReplacedBy(s_Shader,
                s_Preview.MeshSections.Select(p_S => p_S.Shader));
            s_Preview.Shape = PreviewShape.Mesh;

            // ⛔ THE STEP WHOSE ABSENCE MADE THIS TEST USELESS. The window registers a real shader for every
            // section the weapon wears that is NOT the edited one, and those registrations are keyed by
            // shader name while holding the WEAPON's art. Without reproducing that here, the run had no
            // shared state to get wrong and passed while the tool was visibly broken on the second weapon.
            // ⛔ And the body's NoCamo preset is NOT foreign: the authored preset replaces it, as in-game.
            s_Preview.ClearForeignShaders();
            foreach (var s_Section in s_Preview.MeshSections
                         .Select(p_S => p_S.Shader)
                         .Where(p_S => p_S.Length > 0 &&
                                       !p_S.Equals(s_Shader, StringComparison.OrdinalIgnoreCase) &&
                                       !s_Preview.MeshTargetAliases.Contains(p_S, StringComparer.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s_SectionDxbc = GameCache.ShaderFile(s_Section);
                if (!File.Exists(s_SectionDxbc))
                    continue;

                s_Preview.RegisterForeignShader(s_Section, File.ReadAllBytes(s_SectionDxbc),
                    TexturesFor(s_Slots));
            }

            PushValues(s_Preview, s_Shader, s_Values);
            foreach (var (s_Register, s_Name) in s_Slots)
                PushTexture(s_Preview, s_Register, s_Name);

            s_Preview.Render();

            var s_Captured = s_Preview.Capture(out var s_Width, out var s_Height);
            if (s_Captured == null)
                continue;

            var s_Image = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null,
                s_Captured, s_Width * 4);

            var s_Encoder = new PngBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(s_Image));
            using (var s_Stream = File.Create(Path.Combine(p_OutputDir, $"{s_Weapon}.png")))
                s_Encoder.Save(s_Stream);

            s_Hashes.Add((s_Weapon, Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(s_Captured))[..12]));
        }

        return s_Hashes;
    }

    private static bool Apply(ShaderPreview p_Preview, string p_Shader)
    {
        var s_GraphPath = Path.Combine(Settings.CacheFolder, "camographs", $"{Sanitize(p_Shader)}.json");
        if (!File.Exists(s_GraphPath))
            return false;

        var s_Contract = ContractOf(p_Shader);
        p_Preview.SetInterpolatorMeanings(s_Contract?.InterpolatorMeanings);

        var s_Emit = new HlslEmitter { Contract = s_Contract }
            .Emit(ShaderGraph.FromJson(File.ReadAllText(s_GraphPath)));

        if (!s_Emit.Ok)
            return false;

        using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
            s_Emit.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);

        return s_Compiled.Bytecode != null && p_Preview.SetAuthoredShader(s_Compiled.Bytecode.Data);
    }

    /// <summary>
    /// The game's sRGB flag of a cached texture, read from its .dds — the same source the window reads
    /// (MainWindow.TextureIsSrgb), so the bench samples every texture exactly as the window does.
    /// </summary>
    private static bool SrgbOf(string p_Name) => DdsImage.SrgbOf(GameCache.TextureFile(p_Name), p_Name).Srgb;

    /// <summary>The weapon's textures as decoded blocks, for a foreign registration.</summary>
    private static List<(int Slot, uint[] Pixels, int Width, int Height, bool Srgb)> TexturesFor(
        IReadOnlyDictionary<string, string> p_Slots)
    {
        var s_Textures = new List<(int, uint[], int, int, bool)>();
        foreach (var (s_Register, s_Name) in p_Slots)
        {
            var s_Png = Path.Combine(Settings.TextureCache, $"{Sanitize(s_Name)}.png");
            if (!File.Exists(s_Png) || !int.TryParse(s_Register, out var s_Slot))
                continue;

            var s_Bitmap = new BitmapImage();
            s_Bitmap.BeginInit();
            s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            s_Bitmap.UriSource = new Uri(s_Png);
            s_Bitmap.EndInit();

            var s_Converted = new FormatConvertedBitmap(s_Bitmap, PixelFormats.Bgra32, null, 0);
            var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);
            s_Textures.Add((s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight, SrgbOf(s_Name)));
        }

        return s_Textures;
    }

    private static void PushTexture(ShaderPreview p_Preview, string p_Register, string p_Name)
    {
        var s_Png = Path.Combine(Settings.TextureCache, $"{Sanitize(p_Name)}.png");
        if (!File.Exists(s_Png) || !int.TryParse(p_Register, out var s_Slot))
            return;

        var s_Bitmap = new BitmapImage();
        s_Bitmap.BeginInit();
        s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
        s_Bitmap.UriSource = new Uri(s_Png);
        s_Bitmap.EndInit();

        var s_Converted = new FormatConvertedBitmap(s_Bitmap, PixelFormats.Bgra32, null, 0);
        var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
        s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);
        p_Preview.SetTexture(s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight, SrgbOf(p_Name));
    }

    private static void PushValues(ShaderPreview p_Preview, string p_Shader,
        IReadOnlyDictionary<string, string>? p_Values)
    {
        var s_Contract = ContractOf(p_Shader);
        if (p_Values == null || s_Contract == null)
            return;

        var s_External = new List<(int Element, float[] Value)>();
        foreach (var (s_Name, s_Text) in p_Values)
        {
            var (s_Register, s_Element) = s_Contract.ExternalFieldOf(s_Name);
            if (s_Register != 1 || s_Element < 0)
                continue;

            var s_Parts = s_Text.Split(',');
            var s_Value = new float[4];
            for (var i = 0; i < 4 && i < s_Parts.Length; i++)
                float.TryParse(s_Parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out s_Value[i]);

            s_External.Add((s_Element, s_Value));
        }

        p_Preview.SetExternalValues(s_External.Count > 0 ? s_External : null);
    }

    private static readonly Dictionary<string, ShaderContract?> s_Contracts = new(StringComparer.OrdinalIgnoreCase);

    private static ShaderContract? ContractOf(string p_Shader)
    {
        if (s_Contracts.TryGetValue(p_Shader, out var s_Cached))
            return s_Cached;

        ShaderContract? s_Contract = null;
        var s_Dxbc = GameCache.ShaderFile(p_Shader);
        if (File.Exists(s_Dxbc))
            try
            {
                var s_Bytes = File.ReadAllBytes(s_Dxbc);
                s_Contract = ShaderContract.Detect(s_Bytes);
                s_Contract.ClassifyInterpolators(RimeShaderEditor.Translate.DxbcAsm.Parse(
                    new SharpDX.D3DCompiler.ShaderBytecode(s_Bytes).Disassemble().Split('\n')));
            }
            catch (Exception)
            {
                s_Contract = null;
            }

        s_Contracts[p_Shader] = s_Contract;
        return s_Contract;
    }

    public static Result Shot(string p_Mesh, string p_Shader, IReadOnlyDictionary<string, string> p_Slots,
        string p_OutputPath, int p_Size = 384, IReadOnlyDictionary<string, string>? p_Values = null)
    {
        var s_Result = new Result();

        var s_GraphPath = Path.Combine(Settings.CacheFolder, "camographs",
            $"{Sanitize(p_Shader)}.json");

        if (!File.Exists(s_GraphPath))
            return Fail(s_Result, $"no cached graph for {p_Shader}");

        var s_Rsm = GameCache.MeshFile(p_Mesh);
        if (!File.Exists(s_Rsm))
            return Fail(s_Result, $"no cached mesh for {p_Mesh}");

        // The contract decides which interpolators the preview feeds; without it this renders a different
        // program than the editor shows, which would make the picture a lie.
        ShaderContract? s_Contract = null;
        var s_Dxbc = GameCache.ShaderFile(p_Shader);
        if (File.Exists(s_Dxbc))
            try
            {
                var s_Bytes = File.ReadAllBytes(s_Dxbc);
                s_Contract = ShaderContract.Detect(s_Bytes);
                s_Contract.ClassifyInterpolators(RimeShaderEditor.Translate.DxbcAsm.Parse(
                    new SharpDX.D3DCompiler.ShaderBytecode(s_Bytes).Disassemble().Split('\n')));
            }
            catch (Exception s_Exception)
            {
                s_Result.Error = $"contract unreadable ({s_Exception.Message})";
            }

        var s_Graph = ShaderGraph.FromJson(File.ReadAllText(s_GraphPath));
        var s_Emit = new HlslEmitter { Contract = s_Contract }.Emit(s_Graph);
        if (!s_Emit.Ok)
            return Fail(s_Result, "graph does not emit: " + string.Join("; ", s_Emit.Errors.Take(2)));

        using var s_Preview = new ShaderPreview();
        if (!s_Preview.InitialiseOffscreen(p_Size, p_Size))
            return Fail(s_Result, $"no D3D11 device: {s_Preview.LastError}");

        s_Preview.SetInterpolatorMeanings(s_Contract?.InterpolatorMeanings);

        using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
            s_Emit.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);

        if (s_Compiled.Bytecode == null || !s_Preview.SetAuthoredShader(s_Compiled.Bytecode.Data))
            return Fail(s_Result, $"shader rejected: {s_Preview.LastError}");

        if (s_Preview.LoadMeshSections(s_Rsm) is { } s_MeshError)
            return Fail(s_Result, $"mesh not loaded: {s_MeshError}");

        s_Result.Sections = s_Preview.MeshSections.Count;
        s_Preview.MeshTargetShader = p_Shader;
        s_Preview.MeshTargetAliases = CamoSession.ReplacedBy(p_Shader,
            s_Preview.MeshSections.Select(p_S => p_S.Shader));
        s_Preview.Shape = PreviewShape.Mesh;

        foreach (var (s_Register, s_Name) in p_Slots)
        {
            var s_Png = Path.Combine(Settings.TextureCache, $"{Sanitize(s_Name)}.png");
            if (!File.Exists(s_Png) || !int.TryParse(s_Register, out var s_Slot))
                continue;

            var s_Bitmap = new BitmapImage();
            s_Bitmap.BeginInit();
            s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            s_Bitmap.UriSource = new Uri(s_Png);
            s_Bitmap.EndInit();

            var s_Converted = new FormatConvertedBitmap(s_Bitmap, PixelFormats.Bgra32, null, 0);
            var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);

            s_Preview.SetTexture(s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight, SrgbOf(s_Name));
            s_Result.Textures++;
        }

        // The material's numbers, resolved to constant-buffer slots through the contract. Without them the
        // presets build a flat green albedo before any texture is read.
        if (p_Values != null && s_Contract != null)
        {
            var s_External = new List<(int Element, float[] Value)>();
            foreach (var (s_Name, s_Text) in p_Values)
            {
                var (s_Register, s_Element) = s_Contract.ExternalFieldOf(s_Name);
                if (s_Register != 1 || s_Element < 0)
                    continue;

                var s_Parts = s_Text.Split(',');
                var s_Value = new float[4];
                for (var i = 0; i < 4 && i < s_Parts.Length; i++)
                    float.TryParse(s_Parts[i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out s_Value[i]);

                s_External.Add((s_Element, s_Value));
            }

            s_Preview.SetExternalValues(s_External.Count > 0 ? s_External : null);
            s_Result.Values = s_External.Count;
        }

        s_Preview.Render();

        // Diagnostic: the individual targets, so "the picture is wrong" can be traced to WHICH of them.
        // RT0 carries the encoded normal (greenish by nature) and RT1 the sqrt of albedo; if RT1 already
        // looks wrong the shader or its textures are at fault, and if it looks right the resolve is.
        if (Environment.GetEnvironmentVariable("CAMO_DUMP_TARGETS") == "1")
            for (var i = 0; i < 2; i++)
            {
                var s_Target = s_Preview.CaptureTarget(i);
                if (s_Target == null)
                    continue;

                var s_TargetImage = BitmapSource.Create(p_Size, p_Size, 96, 96, PixelFormats.Bgra32, null,
                    s_Target, p_Size * 4);

                var s_TargetEncoder = new PngBitmapEncoder();
                s_TargetEncoder.Frames.Add(BitmapFrame.Create(s_TargetImage));
                using var s_TargetStream = File.Create(
                    Path.ChangeExtension(p_OutputPath, null) + $".rt{i}.png");

                s_TargetEncoder.Save(s_TargetStream);
            }

        var s_Captured = s_Preview.Capture(out var s_Width, out var s_Height);
        if (s_Captured == null || s_Width <= 0)
            return Fail(s_Result, $"nothing captured: {s_Preview.LastError}");

        s_Result.GreenFraction = GreenShare(s_Captured);

        var s_Image = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null,
            s_Captured, s_Width * 4);

        var s_Encoder = new PngBitmapEncoder();
        s_Encoder.Frames.Add(BitmapFrame.Create(s_Image));
        using var s_Stream = File.Create(p_OutputPath);
        s_Encoder.Save(s_Stream);

        return s_Result;
    }

    /// <summary>
    /// How much of the picture is the untextured green. Measured rather than eyeballed so a sweep of 59
    /// weapons can be sorted worst-first: green here means G clearly dominates R and B on a lit pixel,
    /// which is what an unlit, untextured surface looks like and what no real weapon texture does.
    /// </summary>
    private static double GreenShare(byte[] p_Bgra)
    {
        var s_Green = 0;
        var s_Lit = 0;

        for (var i = 0; i + 3 < p_Bgra.Length; i += 4)
        {
            int s_B = p_Bgra[i], s_G = p_Bgra[i + 1], s_R = p_Bgra[i + 2];
            if (s_R + s_G + s_B < 60)
                continue; // background / deep shadow

            s_Lit++;
            if (s_G > s_R + 24 && s_G > s_B + 24)
                s_Green++;
        }

        return s_Lit == 0 ? 0 : (double) s_Green / s_Lit;
    }

    private static Result Fail(Result p_Result, string p_Error)
    {
        p_Result.Error = p_Error;
        return p_Result;
    }

    private static string Sanitize(string p_Text)
    {
        var s_Builder = new System.Text.StringBuilder();
        foreach (var s_Char in p_Text)
            s_Builder.Append(char.IsLetterOrDigit(s_Char) ? s_Char : '_');

        return s_Builder.ToString().Trim('_');
    }
}
