using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RimeShaderEditor.Emit;

/// <summary>
/// The pixel-shader compiler and disassembler the bake and the translation use, IN THE PROCESS: the Direct3D compiler library
/// (d3dcompiler_47.dll) the application ships beside itself — the redistributable build of the Windows SDK, the same one the SDK's
/// fxc.exe drives — so nobody needs the SDK installed, and every machine compiles the same bytes. Each call mirrors the fxc command
/// line it replaces: <c>/T ps_5_0 /E main /O3 /Fo</c> and <c>/dumpbin /Fc</c>.
/// </summary>
public static class ShaderCompiler
{
    /// <summary>What stands where the path of an external compiler used to be passed around: the compiler is built in.</summary>
    public const string BuiltIn = "(built-in shader compiler)";

    /// <summary>Whether a "compiler" handed around names one that can compile: the built-in one, or an fxc.exe that exists.</summary>
    public static bool IsAvailable(string? p_Compiler) => p_Compiler == BuiltIn || (!string.IsNullOrEmpty(p_Compiler) && File.Exists(p_Compiler));

    /// <summary>
    /// <c>fxc /T ps_5_0 /E main /O3 /Fo dxbc hlsl</c>: the HLSL file compiled into the DXBC file. Null when it compiled, else the
    /// compiler's messages. The source goes in as the file's own bytes, as fxc reads it.
    /// </summary>
    public static string? CompilePixelShader(string p_HlslPath, string p_DxbcPath)
    {
        try
        {
            using var s_Result = SharpDX.D3DCompiler.ShaderBytecode.Compile(File.ReadAllBytes(p_HlslPath), "main", "ps_5_0",
                SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3, SharpDX.D3DCompiler.EffectFlags.None, null, null, p_HlslPath);
            if (s_Result.Bytecode == null || s_Result.HasErrors)
                return string.IsNullOrWhiteSpace(s_Result.Message) ? "compilation failed; no code produced" : s_Result.Message.Trim();

            File.WriteAllBytes(p_DxbcPath, s_Result.Bytecode.Data);
            return null;
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message.Trim();
        }
    }

    /// <summary><c>fxc /dumpbin dxbc /Fc asm</c>: the DXBC file's disassembly written to the text file. Null when it was written, else why not.</summary>
    public static string? Disassemble(string p_DxbcPath, string p_AsmPath)
    {
        try
        {
            using var s_Code = new SharpDX.D3DCompiler.ShaderBytecode(File.ReadAllBytes(p_DxbcPath));
            File.WriteAllText(p_AsmPath, s_Code.Disassemble(SharpDX.D3DCompiler.DisassemblyFlags.None));
            return null;
        }
        catch (Exception s_Exception)
        {
            return s_Exception.Message.Trim();
        }
    }

    /// <summary>The compiler library this process has loaded — the shipped one, not the system's — or null before the first compile.</summary>
    public static string? LoadedLibrary() =>
        Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .FirstOrDefault(p_M => p_M.ModuleName.Equals("d3dcompiler_47.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
}
