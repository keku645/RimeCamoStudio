using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using RimeShaderEditor.Emit;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int p_ProcessId);

    protected override void OnStartup(StartupEventArgs p_Args)
    {
        base.OnStartup(p_Args);

        var s_Arguments = p_Args.Args;
        if (s_Arguments.Length > 0 && s_Arguments[0].StartsWith("--"))
        {
            AttachConsole(-1);
            Shutdown(RunHeadless(s_Arguments));
            return;
        }

        new MainWindow().Show();
    }

    private static int RunHeadless(string[] p_Arguments)
    {
        // ⛔⛔ A headless seam must leave the MACHINE exactly as it found it, and this one did not: the hidden
        // windows the test modes build persist the editor's settings when the app shuts down (Closing fires),
        // so every --selecttest/--translatetest run SAVED ITS SCRATCH WORK DIR as the user's output folder. The
        // next real session then wrote its caches into a session-temp directory that gets cleaned - the user's
        // own editor ended up pointing at a temporary test folder. A test that mutates the user's settings is a
        // test with a side effect on the product.
        RimeShaderEditor.MainWindow.Headless = true;

        // RSE_SETTINGS points every seam at a scratch settings file, which is the only way to photograph or
        // exercise a mode the harness has to CHOOSE (authoring style, preview options). Without it a shot
        // silently shows whatever the machine happens to be set to and proves nothing about the mode being
        // tested. ⛔ It lives HERE, before the dispatch, and not inside one case: it was wired into
        // --windowshot alone, so --settingsshot — the seam that photographs the settings window itself —
        // could not pick a mode. A seam that cannot CHOOSE the state it is documenting is not a seam.
        if (Environment.GetEnvironmentVariable("RSE_SETTINGS") is { Length: > 0 } s_SettingsOverride)
            RimeShaderEditor.EditorSettings.PathOverride = s_SettingsOverride;

        try
        {
            switch (p_Arguments[0])
            {
                case "--emit":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --emit <graph.json> <out.hlsl> [contract.dxbc] [reference.dxbc]");
                        return 2;
                    }

                    // ⛔ A graph does not carry its target's contract, so emitting without one silently uses the
                    // RIGID-MESH default: the same graph came out with `WorldPos : TEXCOORD0` here and
                    // `Interp0 : TEXCOORD0` in the verification path, four lines different on a sky shader.
                    // An inspection mode that shows different code from the one being verified is worse than no
                    // inspection mode, so it takes the shader it belongs to and says so when it has none.
                    ShaderContract? s_EmitContract = null;
                    if (p_Arguments.Length > 3 && File.Exists(p_Arguments[3]))
                        try { s_EmitContract = ShaderContract.Detect(File.ReadAllBytes(p_Arguments[3])); }
                        catch { }

                    if (s_EmitContract == null)
                        Console.Out.WriteLine("(no contract dxbc given; emitting with the rigid-mesh default - " +
                                              "this is NOT what --shaderdiff or --sweep compile for other families)");

                    var s_EmitGraph = ShaderGraph.FromJson(File.ReadAllText(p_Arguments[1]));

                    // The REFERENCE flavour the graph's registers belong to. A bake remaps them onto the
                    // flavour being emitted (a lightmapped one keeps the engine in t1..t4 and its material
                    // at t5..t9), so without this the inspection mode shows code the bake never compiles —
                    // it does not even build: the graph asks for t1 and t1 is not the material's there.
                    if (p_Arguments.Length > 4 && File.Exists(p_Arguments[4]) && s_EmitContract != null)
                        try
                        {
                            var s_Reference = ShaderContract.Detect(File.ReadAllBytes(p_Arguments[4]));
                            foreach (var s_Node in s_EmitGraph.Nodes)
                            {
                                if (!int.TryParse(s_Node.GetParam("Register"), out var s_Old))
                                    continue;

                                var s_New = s_EmitContract.RemapRegisterFrom(s_Reference, s_Old);
                                if (s_New >= 0 && s_New != s_Old)
                                    s_Node.Params["Register"] = s_New.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }
                        catch { }

                    return Emit(s_EmitGraph, p_Arguments[2], s_EmitContract);

                case "--selftest":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --selftest <out.hlsl> <out.json>");
                        return 2;
                    }

                    // ⛔ THIS USED TO WRITE TWO FILES AND SAY NOTHING. It was the one member of the battery
                    // that could not fail: whether the emission was still byte-identical had to be checked by
                    // hand against a previous run, which means in practice it was not checked. What it can
                    // verify on its own is the property the battery actually depends on — a graph saved and
                    // read back emits the SAME shader, so nothing that reaches the bytecode is lost in the
                    // document format.
                    var s_Graph = SampleGraphs.VanillaEquivalent();
                    var s_Json = s_Graph.ToJson();
                    File.WriteAllText(p_Arguments[2], s_Json);

                    var s_Direct = Emit(s_Graph, p_Arguments[1]);
                    if (s_Direct != 0)
                    {
                        Console.Error.WriteLine("SELFTEST: FAIL - the sample graph did not emit.");
                        return s_Direct;
                    }

                    var s_Reloaded = Path.Combine(Path.GetDirectoryName(p_Arguments[1]) ?? ".",
                        Path.GetFileNameWithoutExtension(p_Arguments[1]) + "_roundtrip.hlsl");

                    if (Emit(ShaderGraph.FromJson(File.ReadAllText(p_Arguments[2])), s_Reloaded) != 0)
                    {
                        Console.Error.WriteLine("SELFTEST: FAIL - the graph read back from its own document " +
                                                "did not emit.");
                        return 1;
                    }

                    var s_First = File.ReadAllBytes(p_Arguments[1]);
                    var s_Second = File.ReadAllBytes(s_Reloaded);

                    if (!s_First.AsSpan().SequenceEqual(s_Second))
                    {
                        Console.Error.WriteLine($"SELFTEST: FAIL - the graph read back from '{p_Arguments[2]}' " +
                                                $"emits a DIFFERENT shader ({s_First.Length} vs " +
                                                $"{s_Second.Length} bytes; see '{s_Reloaded}'). Something the " +
                                                "emitter reads is not being saved.");
                        return 1;
                    }

                    Console.Out.WriteLine($"SELFTEST: PASS - saved, read back and re-emitted byte-identical " +
                                          $"({s_First.Length} bytes).");
                    return 0;

                case "--previewshot":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --previewshot <graph.json> <out.png> [seconds]");
                        return 2;
                    }

                    return PreviewShot(p_Arguments[1], p_Arguments[2],
                        p_Arguments.Length > 3 && float.TryParse(p_Arguments[3],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var s_Seconds)
                            ? s_Seconds
                            : 0f);

                case "--nodes":
                    // With "udk", exactly what the panel shows while authoring in that vocabulary — the
                    // only way to check the filter without clicking through the window.
                    var s_UdkView = p_Arguments.Length > 1 && p_Arguments[1] == "udk";
                    foreach (var s_Def in (s_UdkView ? Graph.Palette.VisibleFor(true) : Graph.Palette.All)
                                 .OrderBy(p_D => p_D.CategoryFor(s_UdkView)).ThenBy(p_D => p_D.Kind))
                    {
                        var s_Params = s_Def.Params.Count == 0
                            ? "-"
                            : string.Join(" ", s_Def.Params.Select(p_P => $"{p_P.Name}({p_P.Kind})"));
                        var s_Editable = s_Def.Inputs.Where(p_P => p_P.IsEditable).Select(p_P => p_P.Name).ToList();
                        var s_Frozen = s_Def.Inputs.Where(p_P => !p_P.IsEditable).Select(p_P => p_P.Name).ToList();

                        Console.Out.WriteLine(
                            $"{s_Def.CategoryFor(s_UdkView),-18} {s_Def.TitleFor(s_UdkView),-26} {s_Def.Kind,-20} style={s_Def.Style}" +
                            $" alias={(s_Def.Aliases.Count == 0 ? "-" : string.Join(",", s_Def.Aliases))}" +
                            $" params={s_Params}" +
                            $" | editable={(s_Editable.Count == 0 ? "-" : string.Join(",", s_Editable))}" +
                            $" | expr={(s_Frozen.Count == 0 ? "-" : string.Join(",", s_Frozen))}");
                    }

                    return 0;

                case "--udkcollapse":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --udkcollapse <graph.json> [out.json]");
                        return 2;
                    }

                    return UdkCollapseTest(p_Arguments[1], p_Arguments.Length > 2 ? p_Arguments[2] : null);

                case "--udkconvert":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --udkconvert <graph.json> [out.json]");
                        return 2;
                    }

                    return UdkConvertTest(p_Arguments[1], p_Arguments.Length > 2 ? p_Arguments[2] : null);

                case "--udkblendtest":
                    return UdkTwinTest.BlendModes(Console.Out.WriteLine);

                case "--udktwintest":
                    // No work dir: the check is a comparison of emitted text, not of files on disk.
                    return UdkTwinTest.Run(Console.Out.WriteLine);

                case "--nodetest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --nodetest <work-dir>");
                        return 2;
                    }

                    return NodeTest(p_Arguments[1]);

                case "--textures":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --textures <target-shader> <cache-dir> [game-path]");
                        return 2;
                    }

                    return LoadTextures(p_Arguments[1], p_Arguments[2],
                        p_Arguments.Length > 3 ? p_Arguments[3] : null);

                case "--foreignfetch":
                    // The EXACT function the mesh preview runs to fetch a foreign section's shader — the
                    // chosen permutation's bytecode plus its texture cache — runnable headlessly so the
                    // whole fetch path can be verified without clicking through the popup.
                    if (p_Arguments.Length < 5)
                    {
                        Console.Error.WriteLine(
                            "Usage: --foreignfetch <shader> <dxbc-out> <slotmap-out> <game-path> [shaderdb]");
                        return 2;
                    }

                    var s_FetchRepl = RimeShaderEditor.MainWindow.FindRimeRepl();
                    if (s_FetchRepl == null)
                    {
                        Console.Error.WriteLine("ERROR: RimeREPL not found.");
                        return 1;
                    }

                    var s_FetchDb = p_Arguments.Length > 5 ? p_Arguments[5] : "levels/mp_017/mp_017/shaderdb";
                    var s_FetchError = RimeShaderEditor.MainWindow.FetchForeignShaderCore(s_FetchRepl,
                        p_Arguments[4], s_FetchDb, p_Arguments[1], p_Arguments[2], p_Arguments[3]);
                    Console.Out.WriteLine(s_FetchError ?? "FETCHED OK");
                    return s_FetchError == null ? 0 : 1;

                case "--colortest":
                    Console.Out.WriteLine("--- picker conversion round trip ---");
                    var s_Maths = View.ColourPicker.RoundTripSelfTest(Console.Out.WriteLine);

                    // Both orders, because the reported bug is exactly the difference between them.
                    Console.Out.WriteLine("--- fresh node, straight to the picker ---");
                    var s_Fresh = RimeShaderEditor.MainWindow.ColourPickSelfTest(false);
                    Console.Out.WriteLine("--- same, but after typing in a field first ---");
                    var s_Edited = RimeShaderEditor.MainWindow.ColourPickSelfTest(true);
                    return !s_Maths || s_Fresh != 0 || s_Edited != 0 ? 1 : 0;

                case "--desctest":
                    return RimeShaderEditor.MainWindow.DescriptionSelfTest();

                case "--variationshot":
                    if (p_Arguments.Length < 4)
                    {
                        Console.Error.WriteLine("Usage: --variationshot <work-dir> <target-shader> <out.png>");
                        return 2;
                    }

                    return RimeShaderEditor.MainWindow.VariationShot(p_Arguments[1], p_Arguments[2],
                        p_Arguments[3]);

                case "--translate":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --translate <disassembly.txt> <out.json> [target-name]");
                        return 2;
                    }

                    var s_Parsed2 = RimeShaderEditor.Translate.DxbcAsm.Parse(File.ReadAllLines(p_Arguments[1]));

                    // ⛔ The contract has to come along, or this mode translates a DIFFERENT thing from the button:
                    // without it the shader's own parameter names are unknown and every cbuffer read collapses to
                    // a literal 0. A seam that quietly does less than the real path is worse than no seam.
                    ShaderContract? s_TranslateContract = null;
                    var s_Companion = p_Arguments.Length > 4 ? p_Arguments[4]
                        : Path.ChangeExtension(p_Arguments[1], ".dxbc");

                    if (File.Exists(s_Companion))
                        try { s_TranslateContract = ShaderContract.Detect(File.ReadAllBytes(s_Companion)); }
                        catch { }

                    Console.Out.WriteLine(s_TranslateContract == null
                        ? $"(no bytecode next to the disassembly; parameters will be unnamed)"
                        : $"(contract from {Path.GetFileName(s_Companion)}: " +
                          $"{s_TranslateContract.ConstantFields.Count(p_F => p_F.Buffer != "viewConstants")} " +
                          "named parameter field(s))");

                    var s_Builder = new RimeShaderEditor.Translate.GraphBuilder(s_TranslateContract);
                    var s_Translated = s_Builder.Build(s_Parsed2,
                        p_Arguments.Length > 3 ? p_Arguments[3] : "translated",
                        Path.GetFileNameWithoutExtension(p_Arguments[2]));

                    File.WriteAllText(p_Arguments[2], s_Translated.ToJson());
                    Console.Out.WriteLine($"{s_Parsed2.Count} instruction(s) -> {s_Translated.Nodes.Count} nodes, " +
                                          $"{s_Translated.Connections.Count} connections -> {p_Arguments[2]}");

                    // Why the compact path declined, or that it fired - the single most asked question when a
                    // graph comes out big, and it was only answered inside the sweep before.
                    Console.Out.WriteLine(s_Builder.FrameRecognised
                        ? "  frame: recognised"
                        : $"  frame declined: {s_Builder.FrameRejection}");

                    foreach (var s_Warning in s_Builder.Warnings.Distinct())
                        Console.Out.WriteLine("  WARN: " + s_Warning);

                    return 0;

                case "--rdef":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --rdef <shader.dxbc>");
                        return 2;
                    }

                    var s_Rdef = File.ReadAllBytes(p_Arguments[1]);
                    foreach (var s_Binding in RimeShaderEditor.Emit.DxbcSignature.ReadResources(s_Rdef))
                        Console.Out.WriteLine($"  bind  {s_Binding.Name,-40} type={s_Binding.InputType} " +
                                              $"register={s_Binding.Register}");

                    foreach (var s_Field in RimeShaderEditor.Emit.DxbcSignature.ReadConstantFields(s_Rdef))
                        Console.Out.WriteLine($"  field {s_Field.Name,-40} {s_Field.Buffer} " +
                                              $"cb{s_Field.Register}[{s_Field.Element}]" +
                                              $".{"xyzw"[s_Field.Component]} offset={s_Field.Offset} size={s_Field.Size}");

                    return 0;

                case "--graphshot":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --graphshot <graph.json> <out.png> [width] [height]");
                        return 2;
                    }

                    return GraphShot(p_Arguments[1], p_Arguments[2],
                        p_Arguments.Length > 3 && int.TryParse(p_Arguments[3], out var s_W) ? s_W : 1600,
                        p_Arguments.Length > 4 && int.TryParse(p_Arguments[4], out var s_H) ? s_H : 1000);

                case "--grouptest":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --grouptest <graph.json> <out.png>");
                        return 2;
                    }

                    return GroupTest(p_Arguments[1], p_Arguments[2]);

                case "--sweep":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --sweep <extract-root> [max] [texcache-dir]");
                        return 2;
                    }

                    return Sweep(p_Arguments[1],
                        p_Arguments.Length > 2 && int.TryParse(p_Arguments[2], out var s_Limit) ? s_Limit : 50,
                        p_Arguments.Length > 3 ? p_Arguments[3] : null);

                case "--probeize":
                    // Seam for the differential test of the forward probe twin: applies the measured
                    // plain→probe-cbuffer delta to a graph file, so --shaderdiff can compare the result
                    // against the vanilla probe solution without going through a full bake.
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --probeize <graph.json> <out.json>");
                        return 2;
                    }

                    var s_ProbeGraph = Graph.ShaderGraph.FromJson(File.ReadAllText(p_Arguments[1]));
                    if (!RimeShaderEditor.Emit.ForwardProbe.TryProbeize(s_ProbeGraph, out var s_ProbeWhy))
                    {
                        Console.Error.WriteLine($"PROBEIZE FAILED: {s_ProbeWhy}");
                        return 1;
                    }

                    File.WriteAllText(p_Arguments[2], s_ProbeGraph.ToJson());
                    Console.Out.WriteLine($"Probeized -> {p_Arguments[2]} ({s_ProbeGraph.Nodes.Count} nodes).");
                    return 0;

                case "--structure":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --structure <extract-root> [max] [out.tsv] [allperms]");
                        return 2;
                    }

                    return StructureCensus(p_Arguments[1],
                        p_Arguments.Length > 2 && int.TryParse(p_Arguments[2], out var s_StructMax) ? s_StructMax : int.MaxValue,
                        p_Arguments.Length > 3 && p_Arguments[3] != "-" ? p_Arguments[3] : null,
                        p_Arguments.Any(p_A => p_A == "allperms"));

                // Control for the census: KEY 2 and KEY 3 report the same cluster count, which is either a real
                // property of the corpus or a copy-paste bug. This prints both keys for one shader so the two can
                // be seen to be different strings produced by different code.
                case "--keytest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --keytest <disassembly.asm>");
                        return 2;
                    }

                    var s_KeyInstr = RimeShaderEditor.Translate.DxbcAsm.Parse(File.ReadAllLines(p_Arguments[1]));
                    var s_KeyShape = RimeShaderEditor.Translate.StructureKey.Shape(s_KeyInstr);
                    var s_KeyFlow = RimeShaderEditor.Translate.StructureKey.Dataflow(s_KeyInstr);
                    Console.WriteLine($"{s_KeyInstr.Count} instruction(s)");
                    Console.WriteLine($"KEY2 shape    ({s_KeyShape.Length} chars): {s_KeyShape[..Math.Min(300, s_KeyShape.Length)]}");
                    Console.WriteLine($"KEY3 dataflow ({s_KeyFlow.Length} chars): {s_KeyFlow[..Math.Min(300, s_KeyFlow.Length)]}");
                    Console.WriteLine($"identical strings: {s_KeyShape == s_KeyFlow}");
                    return 0;

                case "--translatetest":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine(
                            "Usage: --translatetest <target-shader> <work-dir> [bf3-path] [level]");
                        return 2;
                    }

                    return RimeShaderEditor.MainWindow.TranslateSelfTest(p_Arguments[1], p_Arguments[2],
                        p_Arguments.Length > 3 ? p_Arguments[3] : null,
                        p_Arguments.Length > 4 ? p_Arguments[4] : null);

                case "--selecttest":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --selecttest <target-shader> <work-dir> [bf3-path] [level]");
                        return 2;
                    }

                    return RimeShaderEditor.MainWindow.SelectSelfTest(p_Arguments[1], p_Arguments[2],
                        p_Arguments.Length > 3 ? p_Arguments[3] : null,
                        p_Arguments.Length > 4 ? p_Arguments[4] : null);

                case "--settingstest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --settingstest <work-dir>");
                        return 2;
                    }

                    return RimeShaderEditor.MainWindow.SettingsSelfTest(p_Arguments[1]);

                case "--customtextest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --customtextest <work-dir>");
                        return 2;
                    }

                    return RimeShaderEditor.MainWindow.CustomTextureSelfTest(p_Arguments[1]);

                case "--tabtest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --tabtest <work-dir>");
                        return 2;
                    }

                    return RimeShaderEditor.MainWindow.TabSelfTest(p_Arguments[1]);

                case "--propshot":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --propshot <node-kind> <out.png> [variation-name]");
                        return 2;
                    }

                    return PropShot(p_Arguments[1], p_Arguments[2],
                        p_Arguments.Length > 3 ? p_Arguments[3] : null);

                case "--luatest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --luatest <out.lua>");
                        return 2;
                    }

                    return LuaTest(p_Arguments[1]);

                case "--contract":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --contract <shader.dxbc> [more.dxbc ...]");
                        return 2;
                    }

                    // What the emitter thinks a compiled flavour IS, without mounting anything. Which
                    // flavour a bake declines is otherwise only visible inside a bake log, minutes in.
                    foreach (var s_File in p_Arguments.Skip(1))
                    {
                        if (!File.Exists(s_File))
                        {
                            Console.Error.WriteLine($"not found: {s_File}");
                            return 1;
                        }

                        Console.Out.WriteLine($"{Path.GetFileName(s_File),-44} " +
                                              RimeShaderEditor.Emit.ShaderContract
                                                  .Detect(File.ReadAllBytes(s_File)).Describe());
                    }

                    return 0;

                case "--stubtest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --stubtest <work-dir>");
                        return 2;
                    }

                    return StubTest(p_Arguments[1]);

                case "--udksurfacetest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --udksurfacetest <work-dir>");
                        return 2;
                    }

                    return RimeShaderEditor.MainWindow.UdkSurfaceSelfTest(p_Arguments[1]);

                case "--settingsshot":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --settingsshot <out.png>");
                        return 2;
                    }

                    return SettingsShot(p_Arguments[1]);

                case "--firstrunshot":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --firstrunshot <out.png>");
                        return 2;
                    }

                    return FirstRunShot(p_Arguments[1]);

                case "--asmparse":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --asmparse <disassembly.txt>");
                        return 2;
                    }

                    var s_Asm = RimeShaderEditor.Translate.DxbcAsm.Parse(File.ReadAllLines(p_Arguments[1]));
                    Console.Out.WriteLine($"{s_Asm.Count} instruction(s)");
                    foreach (var s_Instruction in s_Asm)
                        Console.Out.WriteLine("  " + s_Instruction);

                    var s_Opcodes = s_Asm.Select(p_I => p_I.Opcode).Distinct().OrderBy(p_O => p_O);
                    Console.Out.WriteLine("opcodes: " + string.Join(", ", s_Opcodes));
                    return 0;

                case "--sigtest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --sigtest <dir-with-numbered-shader-folders> [expect.txt]");
                        return 2;
                    }

                    return SignatureTest(p_Arguments[1]);

                case "--indextest":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --indextest <dump-log>");
                        return 2;
                    }

                    var s_Parsed = RimeShaderEditor.Emit.ShaderIndex.ParseDump(File.ReadAllLines(p_Arguments[1]));
                    var s_Dupes = s_Parsed.GroupBy(p_P => p_P.Name, StringComparer.OrdinalIgnoreCase)
                        .Where(p_G => p_G.Count() > 1).ToList();

                    Console.Out.WriteLine($"parsed {s_Parsed.Count} distinct shader(s); duplicate names: {s_Dupes.Count}");
                    foreach (var s_Entry in s_Parsed.Take(10))
                        Console.Out.WriteLine($"  {s_Entry.Name}  tex={s_Entry.Textures}");

                    // Building the same dictionary the browser builds: this is what threw on the user's machine.
                    try
                    {
                        _ = s_Parsed.ToDictionary(p_P => p_P.Name, p_P => p_P.Textures,
                            StringComparer.OrdinalIgnoreCase);
                        Console.Out.WriteLine("INDEXTEST: PASS - names are distinct, the browser can key by them.");
                    }
                    catch (ArgumentException s_Duplicate)
                    {
                        Console.Error.WriteLine($"INDEXTEST: FAIL - {s_Duplicate.Message}");
                        return 1;
                    }

                    return 0;

                case "--windowshot":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --windowshot <out.png> [names-file] [filter]");
                        return 2;
                    }

                    var s_ShotIndex = new List<RimeShaderEditor.Emit.ShaderIndex.Entry>();
                    if (p_Arguments.Length > 2 && File.Exists(p_Arguments[2]))
                        s_ShotIndex = File.ReadAllLines(p_Arguments[2])
                            .Select(p_L => p_L.Contains('\t') ? p_L[(p_L.IndexOf('\t') + 1)..].Trim() : p_L.Trim())
                            .Where(p_L => p_L.Length > 0)
                            .Select(p_L => new RimeShaderEditor.Emit.ShaderIndex.Entry { Name = p_L, Textures = 2 })
                            .ToList();

                    var s_Encoder2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    s_Encoder2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                        RimeShaderEditor.MainWindow.Snapshot(s_ShotIndex,
                            p_Arguments.Length > 3 ? p_Arguments[3] : "")));

                    using (var s_Stream2 = File.Create(p_Arguments[1]))
                        s_Encoder2.Save(s_Stream2);

                    Console.Out.WriteLine($"Rendered the window with {s_ShotIndex.Count} shaders -> {p_Arguments[1]}");
                    return 0;

                case "--tree":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --tree <names-file> [max-depth]");
                        return 2;
                    }

                    // Accepts a plain name list or the tab-separated index extract_shader_dxbc writes.
                    var s_TreeNames = File.ReadAllLines(p_Arguments[1])
                        .Select(p_L => p_L.Contains('\t') ? p_L[(p_L.IndexOf('\t') + 1)..].Trim() : p_L.Trim())
                        .Where(p_L => p_L.Length > 0)
                        .ToList();

                    var s_Depth = p_Arguments.Length > 2 && int.TryParse(p_Arguments[2], out var s_D) ? s_D : 2;
                    var s_Tree = RimeShaderEditor.Emit.ShaderIndex.Build(s_TreeNames);
                    Console.Out.WriteLine($"{s_TreeNames.Count} names -> {s_Tree.ShaderCount} shaders in the tree");
                    PrintTree(s_Tree, 0, s_Depth);
                    return 0;

                case "--shaderdiff":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --shaderdiff <graph.json> <original.dxbc> [texcache-dir]");
                        return 2;
                    }

                    return ShaderDiff(p_Arguments[1], p_Arguments[2],
                        p_Arguments.Length > 3 ? p_Arguments[3] : null);

                case "--levels":
                    var s_GameForList = p_Arguments.Length > 1 ? p_Arguments[1] : EditorSettings.DetectGamePath();
                    if (string.IsNullOrWhiteSpace(s_GameForList))
                    {
                        Console.Error.WriteLine("ERROR: no BF3 path given and none detectable.");
                        return 1;
                    }

                    var s_Levels = RimeShaderEditor.Emit.LevelScanner.Scan(s_GameForList);
                    foreach (var s_Group in s_Levels.GroupBy(RimeShaderEditor.Emit.LevelScanner.GroupOf)
                                 .OrderBy(p_G => RimeShaderEditor.Emit.LevelScanner.GroupRank(p_G.Key)))
                    {
                        Console.Out.WriteLine($"{s_Group.Key} ({s_Group.Count()}):");
                        foreach (var s_Level in s_Group.OrderBy(p_L => p_L, StringComparer.OrdinalIgnoreCase))
                            Console.Out.WriteLine($"    {s_Level,-16} {RimeShaderEditor.Emit.LevelScanner.ShaderDbFor(s_Level)}");
                    }

                    Console.Out.WriteLine($"TOTAL: {s_Levels.Count}");
                    return 0;

                case "--bakeshot":
                    if (p_Arguments.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: --bakeshot <out.png> [bf3-path]");
                        return 2;
                    }

                    return BakeShot(p_Arguments[1], p_Arguments.Length > 2 ? p_Arguments[2] : null);

                case "--bake":
                    if (p_Arguments.Length < 6)
                    {
                        Console.Error.WriteLine(
                            "Usage: --bake <graph.json> <map[,map...]> <modname> <bundlename> <outdir> [additive]");
                        return 2;
                    }

                    return HeadlessBake(p_Arguments[1], p_Arguments[2], p_Arguments[3], p_Arguments[4],
                        p_Arguments[5], p_Arguments.Length > 6 && p_Arguments[6] == "additive");

                case "--colorshot":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --colorshot <out.png> <r,g,b,a>");
                        return 2;
                    }

                    return ColourShot(p_Arguments[1], p_Arguments[2]);

                case "--ddstest":
                    if (p_Arguments.Length < 3)
                    {
                        Console.Error.WriteLine("Usage: --ddstest <in.dds> <out.png>");
                        return 2;
                    }

                    return DdsTest(p_Arguments[1], p_Arguments[2]);

                default:
                    Console.Error.WriteLine($"Unknown option '{p_Arguments[0]}'.");
                    return 2;
            }
        }
        catch (Exception s_Exception)
        {
            Console.Error.WriteLine($"FAILED: {s_Exception.Message}");
            return 1;
        }
    }

    /// <summary>Reference art the sweep puts in t1/t2 for every shader, so both harnesses feed the same bytes.</summary>
    private const string c_FallbackTextureTarget = "Props/StreetProps/OilDrumBarrel_01/SS_OilDrumBarrel_01";

    private static void LoadCachedTexturesInto(View.ShaderPreview p_Preview, string p_CacheDir, string p_Target)
    {
        var s_MapPath = Path.Combine(p_CacheDir, RimeShaderEditor.MainWindow.SlotMapFileName(p_Target));

        // ⛔⛔ THE TWO HARNESSES WERE FEEDING DIFFERENT INPUTS, WHICH MAKES REPRODUCING A SWEEP FAILURE
        // IMPOSSIBLE BY CONSTRUCTION. The sweep loads the reference art into t1/t2 for EVERY shader; this path
        // looked up the target's own slot map, found none for anything but the drum, and silently left the
        // built-in checker there instead. A sky shader then measured 4/255 in the sweep and 2/255 here, on the
        // very artefacts the sweep had dumped so it COULD be reproduced - and a disagreement between two
        // harnesses is worth more attention than either number.
        if (!File.Exists(s_MapPath))
        {
            var s_Fallback = Path.Combine(p_CacheDir,
                RimeShaderEditor.MainWindow.SlotMapFileName(c_FallbackTextureTarget));

            if (!File.Exists(s_Fallback))
            {
                Console.Out.WriteLine($"(no slot map at {s_MapPath}; using placeholder textures)");
                return;
            }

            Console.Out.WriteLine($"(no slot map for {p_Target}; using the reference art the sweep uses)");
            s_MapPath = s_Fallback;
        }

        var s_Slots = RimeShaderEditor.MainWindow.ReadSlotMap(s_MapPath);
        if (s_Slots == null)
            return;

        foreach (var (s_Register, s_Name) in s_Slots)
        {
            var s_Png = Path.Combine(p_CacheDir, Sanitize(s_Name) + ".png");
            if (!File.Exists(s_Png))
                continue;

            var s_Bitmap = new System.Windows.Media.Imaging.BitmapImage();
            s_Bitmap.BeginInit();
            s_Bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            s_Bitmap.UriSource = new Uri(s_Png);
            s_Bitmap.EndInit();

            var s_Converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                s_Bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);

            var s_Pixels = new uint[s_Converted.PixelWidth * s_Converted.PixelHeight];
            s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);

            if (int.TryParse(s_Register, out var s_Slot))
            {
                // The game's sRGB flag for that texture rides with the pixels, from the .dds beside the
                // thumbnail — the view's format is an input of the shader (see ShaderPreview.CreateTexture).
                var (s_Srgb, s_FromHeader) = RimeShaderEditor.View.DdsImage.SrgbOf(
                    Path.Combine(p_CacheDir, Sanitize(s_Name) + ".dds"), s_Name);

                p_Preview.SetTexture(s_Slot, s_Pixels, s_Converted.PixelWidth, s_Converted.PixelHeight, s_Srgb);
                Console.Out.WriteLine($"t{s_Slot} <- {Path.GetFileName(s_Png)} " +
                                      $"({(s_Srgb ? "sRGB" : "linear")}{(s_FromHeader ? "" : ", guessed: no .dds")})");
            }
        }
    }

    private static string Sanitize(string p_Text)
    {
        var s_Chars = p_Text.Select(p_C => char.IsLetterOrDigit(p_C) ? p_C : '_').ToArray();
        return new string(s_Chars).Trim('_');
    }

    /// <summary>
    /// Renders a graph through the real preview pipeline with no window and saves the frame, so the result can
    /// be looked at instead of inferred from the app not crashing.
    /// </summary>
    /// <summary>
    /// Photographs the Settings dialog's content the way the human sees it — the dialog is a hand-built
    /// visual, so its correctness is judged by LOOKING, not by counting its controls.
    /// </summary>
    /// <summary>
    /// Photographs the window someone sees ONCE, the first time they open the editor — which is exactly the
    /// screen nobody on this machine will ever see again, and therefore the one most likely to ship wrong.
    /// </summary>
    private static int FirstRunShot(string p_OutputPath)
    {
        var s_Window = new RimeShaderEditor.View.FirstRunWindow(RimeShaderEditor.EditorSettings.Load());
        var s_Root = (System.Windows.UIElement) s_Window.Content;
        s_Window.Content = null;

        var s_Host = new System.Windows.Controls.Border
        {
            Background = s_Window.Background,
            Child = s_Root,
        };

        s_Host.Measure(new System.Windows.Size(560, 2000));
        s_Host.Arrange(new System.Windows.Rect(0, 0, 560, s_Host.DesiredSize.Height));
        s_Host.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            560, (int) Math.Ceiling(s_Host.DesiredSize.Height), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);

        s_Bitmap.Render(s_Host);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using (var s_Stream = File.Create(p_OutputPath))
            s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"Rendered the first-run window -> {p_OutputPath}");
        return 0;
    }

    private static int SettingsShot(string p_OutputPath)
    {
        var s_Owner = new RimeShaderEditor.MainWindow();

        // ⛔ This built `new EditorSettings()` — a blank object — so the shot pictured the DEFAULTS whatever
        // the settings said, and no harness run could ever photograph a mode that was actually switched on.
        // Load() reads the scratch file when RSE_SETTINGS points at one and still falls back to the same
        // deterministic defaults without it (headless + no override), so the plain shot is unchanged.
        var s_Dialog = new RimeShaderEditor.SettingsWindow(
            s_Owner, RimeShaderEditor.EditorSettings.Load(), () => { });

        var s_Root = (System.Windows.UIElement) s_Dialog.Content;
        s_Dialog.Content = null;

        var s_Host = new System.Windows.Controls.Border
        {
            Background = s_Dialog.Background,
            Child = s_Root,
        };

        s_Host.Measure(new System.Windows.Size(480, 2000));
        s_Host.Arrange(new System.Windows.Rect(0, 0, 480, s_Host.DesiredSize.Height));
        s_Host.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            480, (int) Math.Ceiling(s_Host.DesiredSize.Height), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        s_Bitmap.Render(s_Host);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using var s_Stream = File.Create(p_OutputPath);
        s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"Settings dialog rendered -> {p_OutputPath}");
        return 0;
    }

    private static int PreviewShot(string p_GraphPath, string p_OutputPath, float p_Seconds)
    {
        var s_Graph = Graph.ShaderGraph.FromJson(File.ReadAllText(p_GraphPath));
        // ⛔ The graph's target decides the CONTRACT, and the contract decides both the PsIn this compiles
        // against and what the preview's vertex shader feeds each interpolator. Without it this seam emitted
        // with the rigid default and rendered with the rigid inputs - a different program than the editor
        // shows, on the very mode whose whole purpose is to photograph what the editor shows.
        ShaderContract? s_ShotContract = null;
        var s_TargetDxbc = Environment.GetEnvironmentVariable("PREVIEW_CONTRACT");
        if (!string.IsNullOrWhiteSpace(s_TargetDxbc) && File.Exists(s_TargetDxbc))
            try
            {
                s_ShotContract = ShaderContract.Detect(File.ReadAllBytes(s_TargetDxbc));
                var s_Text = new SharpDX.D3DCompiler.ShaderBytecode(File.ReadAllBytes(s_TargetDxbc))
                    .Disassemble();
                s_ShotContract.ClassifyInterpolators(
                    RimeShaderEditor.Translate.DxbcAsm.Parse(s_Text.Split('\n')));
            }
            catch { }

        var s_Emit = new HlslEmitter { Contract = s_ShotContract }.Emit(s_Graph);
        foreach (var s_Error in s_Emit.Errors)
            Console.Error.WriteLine($"ERROR: {s_Error}");

        if (!s_Emit.Ok)
            return 1;

        using var s_Preview = new View.ShaderPreview();
        if (!s_Preview.InitialiseOffscreen(512, 512))
        {
            Console.Error.WriteLine($"ERROR: no D3D11 device: {s_Preview.LastError}");
            return 1;
        }

        s_Preview.SetInterpolatorMeanings(s_ShotContract?.InterpolatorMeanings);

        using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
            s_Emit.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1);

        if (s_Compiled.Bytecode == null || !s_Preview.SetAuthoredShader(s_Compiled.Bytecode.Data))
        {
            Console.Error.WriteLine($"ERROR: authored shader rejected: {s_Preview.LastError}");
            return 1;
        }

        s_Preview.UseSphere = Environment.GetEnvironmentVariable("PREVIEW_SPHERE") == "1";

        // The shape seam, additive over the historical two-shape switch: PREVIEW_SHAPE names any
        // PreviewShape and wins when set, so the new geometries can be rendered to a file and looked at.
        // PREVIEW_MESH_RSM loads a sectioned game-mesh dump for the Mesh shape; PREVIEW_MESH_TARGET names
        // the edited shader (sections it wears run the graph, the rest draw neutral); PREVIEW_MESH_ONLY=1
        // hides the foreign sections instead.
        if (Environment.GetEnvironmentVariable("PREVIEW_MESH_RSM") is { Length: > 0 } s_MeshRsm &&
            File.Exists(s_MeshRsm) && s_Preview.LoadMeshSections(s_MeshRsm) is { } s_MeshError)
            Console.Error.WriteLine($"WARN: mesh dump not loaded: {s_MeshError}");

        s_Preview.MeshTargetShader = Environment.GetEnvironmentVariable("PREVIEW_MESH_TARGET") ?? "";
        s_Preview.HideForeignSections = Environment.GetEnvironmentVariable("PREVIEW_MESH_ONLY") == "1";

        // PREVIEW_MESH_FOREIGN: a manifest ("shaderpath|dxbcfile[|slotmap]" per line) of REAL shaders to
        // register for the mesh's foreign sections, so the sectioned composite can be photographed
        // headlessly; the optional slot map pulls that shader's cached art (PREVIEW_TEXCACHE names the
        // cache) through the same loader the editor uses.
        if (Environment.GetEnvironmentVariable("PREVIEW_MESH_FOREIGN") is { Length: > 0 } s_ForeignManifest &&
            File.Exists(s_ForeignManifest))
            foreach (var s_Line in File.ReadAllLines(s_ForeignManifest))
            {
                var s_Parts = s_Line.Split('|');
                if (s_Parts.Length < 2 || !File.Exists(s_Parts[1]))
                    continue;

                var s_HasMap = s_Parts.Length >= 3 && File.Exists(s_Parts[2].Trim());
                var s_ForeignArt = s_HasMap &&
                                   Environment.GetEnvironmentVariable("PREVIEW_TEXCACHE") is { Length: > 0 } s_ForeignCache
                    ? RimeShaderEditor.MainWindow.LoadForeignTexturesFromCache(s_Parts[2].Trim(), s_ForeignCache,
                        Environment.GetEnvironmentVariable("PREVIEW_MESH_NAME"))
                    : new List<(int, uint[], int, int, bool)>();

                var s_ForeignError = s_Preview.RegisterForeignShader(s_Parts[0].Trim(),
                    File.ReadAllBytes(s_Parts[1].Trim()), s_ForeignArt,
                    s_HasMap
                        ? RimeShaderEditor.MainWindow.LoadForeignMaterialValues(s_Parts[2].Trim(),
                            Environment.GetEnvironmentVariable("PREVIEW_MESH_NAME"))
                        : null);
                Console.Out.WriteLine($"foreign '{s_Parts[0].Trim()}': " +
                                      (s_ForeignError ?? $"registered ({s_ForeignArt.Count} texture(s))"));
            }

        if (Enum.TryParse<RimeShaderEditor.View.PreviewShape>(
                Environment.GetEnvironmentVariable("PREVIEW_SHAPE"), true, out var s_Shape))
            s_Preview.Shape = s_Shape;

        // Same cache the window reads, so this shot shows the real BF3 art rather than the placeholder.
        var s_Cache = Environment.GetEnvironmentVariable("PREVIEW_TEXCACHE");
        if (!string.IsNullOrWhiteSpace(s_Cache) && Directory.Exists(s_Cache))
            LoadCachedTexturesInto(s_Preview, s_Cache, s_Graph.TargetShader);

        s_Preview.Time = p_Seconds;
        s_Preview.Render();

        var s_Pixels = s_Preview.Capture(out var s_Width, out var s_Height);
        if (s_Pixels == null)
        {
            Console.Error.WriteLine("ERROR: nothing captured.");
            return 1;
        }

        var s_Bitmap = System.Windows.Media.Imaging.BitmapSource.Create(s_Width, s_Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, s_Pixels, s_Width * 4);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using var s_Stream = File.Create(p_OutputPath);
        s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"Rendered {s_Width}x{s_Height} at t={p_Seconds}s -> {p_OutputPath}");
        return 0;
    }

    /// <summary>
    /// Puts every palette node into a one-node graph, emits it and runs it through fxc. A node that compiles
    /// nowhere is worse than a missing node, so the whole palette is checked rather than just the sample graph.
    /// </summary>
    /// <summary>
    /// Folds the texture/mask pairs of a graph and PROVES the fold changed nothing: the emitted HLSL
    /// before and after must be identical. Rewriting an author's graph is only acceptable while that
    /// holds — this is the check that makes the rewrite safe rather than merely tidy.
    /// </summary>
    private static int UdkCollapseTest(string p_GraphPath, string? p_OutPath)
    {
        Graph.ShaderGraph s_Graph;
        try
        {
            s_Graph = Graph.ShaderGraph.FromJson(File.ReadAllText(p_GraphPath));
        }
        catch (Exception s_Exception)
        {
            Console.Error.WriteLine($"ERROR: could not read the graph ({s_Exception.Message}).");
            return 1;
        }

        var s_Before = new Emit.HlslEmitter().Emit(s_Graph);
        if (!s_Before.Ok)
        {
            Console.Error.WriteLine($"ERROR: the graph does not emit before folding ({string.Join("; ", s_Before.Errors)}).");
            return 1;
        }

        var s_Report = Graph.UdkCollapse.Collapse(s_Graph);
        var s_After = new Emit.HlslEmitter().Emit(s_Graph);
        if (!s_After.Ok)
        {
            Console.Error.WriteLine($"ERROR: the folded graph does not emit ({string.Join("; ", s_After.Errors)}).");
            return 1;
        }

        Console.Out.WriteLine($"folded {s_Report.Folded} pair(s), left {s_Report.Skipped} alone.");
        foreach (var s_Reason in s_Report.Reasons)
            Console.Out.WriteLine($"  {s_Reason}");

        if (p_OutPath != null)
            File.WriteAllText(p_OutPath, s_Graph.ToJson());

        // The SAME normalisation the twin test uses — including folding the trivial copy that chaining
        // through the mask node creates, which is precisely what folding removes.
        if (UdkTwinTest.Normalise(s_Before.Hlsl) != UdkTwinTest.Normalise(s_After.Hlsl))
        {
            Console.Error.WriteLine("UDKCOLLAPSE: FAIL - folding CHANGED the emitted shader.");
            return 1;
        }

        Console.Out.WriteLine("UDKCOLLAPSE: PASS - the folded graph emits the same shader.");
        return 0;
    }

    /// <summary>
    /// Rewrites a graph into the UDK vocabulary and PROVES the rewrite changed nothing: same demand as the
    /// fold, for a much bigger rewrite. A translated shader comes back as this engine's nodes, so this is the
    /// step that lets someone authoring in the other vocabulary work on it — and the only thing that makes
    /// rewriting someone's graph acceptable is that the shader it emits is byte for byte the one they had.
    /// </summary>
    private static int UdkConvertTest(string p_GraphPath, string? p_OutPath)
    {
        Graph.ShaderGraph s_Graph;
        try
        {
            s_Graph = Graph.ShaderGraph.FromJson(File.ReadAllText(p_GraphPath));
        }
        catch (Exception s_Exception)
        {
            Console.Error.WriteLine($"ERROR: could not read the graph ({s_Exception.Message}).");
            return 1;
        }

        var s_Before = new Emit.HlslEmitter().Emit(s_Graph);
        if (!s_Before.Ok)
        {
            Console.Error.WriteLine($"ERROR: the graph does not emit before converting " +
                                    $"({string.Join("; ", s_Before.Errors)}).");
            return 1;
        }

        var s_Report = Graph.UdkConvert.Run(s_Graph);
        var s_After = new Emit.HlslEmitter().Emit(s_Graph);
        if (!s_After.Ok)
        {
            Console.Error.WriteLine($"ERROR: the converted graph does not emit " +
                                    $"({string.Join("; ", s_After.Errors)}).");
            return 1;
        }

        Console.Out.WriteLine($"converted {s_Report.Converted} node(s), folded {s_Report.Folded} pair(s), " +
                              $"left {s_Report.Left} alone.");
        foreach (var s_Note in s_Report.Notes)
            Console.Out.WriteLine($"  {s_Note}");

        if (p_OutPath != null)
            File.WriteAllText(p_OutPath, s_Graph.ToJson());

        if (UdkTwinTest.Normalise(s_Before.Hlsl) != UdkTwinTest.Normalise(s_After.Hlsl))
        {
            Console.Error.WriteLine("UDKCONVERT: FAIL - converting CHANGED the emitted shader.");

            var s_Left = UdkTwinTest.Normalise(s_Before.Hlsl).Split('\n');
            var s_Right = UdkTwinTest.Normalise(s_After.Hlsl).Split('\n');
            for (var i = 0; i < Math.Max(s_Left.Length, s_Right.Length); i++)
            {
                var s_A = i < s_Left.Length ? s_Left[i] : "(none)";
                var s_B = i < s_Right.Length ? s_Right[i] : "(none)";
                if (s_A != s_B)
                    Console.Error.WriteLine($"  line {i + 1}:\n    before: {s_A}\n    after : {s_B}");
            }

            return 1;
        }

        Console.Out.WriteLine("UDKCONVERT: PASS - the converted graph emits the same shader.");
        return 0;
    }

    /// <summary>
    /// Photographs the PROPERTIES panel for one node kind. The panel is where most of this editor's surface
    /// lives, and until now the only way to see a control land in it was to open the window and click — so a
    /// control added in the wrong place, or one that never rendered at all, was invisible to every check.
    /// </summary>
    private static int PropShot(string p_Kind, string p_OutputPath, string? p_Variation)
    {
        if (!Graph.Palette.Has(p_Kind))
        {
            Console.Error.WriteLine($"ERROR: no node kind '{p_Kind}'.");
            return 1;
        }

        var s_Window = new RimeShaderEditor.MainWindow();

        // A panel whose controls depend on the document has to be photographable in EITHER state, or the
        // shot only ever documents the empty one — the same trap the settings shot was in.
        s_Window.Canvas.Graph.BakeVariation = p_Variation;

        // Placing selects it, which is the real path that rebuilds the panel.
        s_Window.Canvas.AddNode(p_Kind);

        var s_Panel = s_Window.PropertiesPanel;
        if (s_Panel.Children.Count == 0)
        {
            Console.Error.WriteLine($"ERROR: the properties panel came out empty for '{p_Kind}'.");
            return 1;
        }

        var s_Host = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x26)),
            Padding = new System.Windows.Thickness(8),
            Child = new System.Windows.Controls.ContentControl(),
        };

        // The panel already has a parent (the window), so it is re-hosted rather than re-parented.
        var s_Parent = (System.Windows.Controls.Panel?) System.Windows.Media.VisualTreeHelper.GetParent(s_Panel)
                       as System.Windows.Controls.Panel;
        s_Parent?.Children.Remove(s_Panel);
        ((System.Windows.Controls.ContentControl) s_Host.Child).Content = s_Panel;

        s_Host.Measure(new System.Windows.Size(320, 4000));
        s_Host.Arrange(new System.Windows.Rect(0, 0, 320, s_Host.DesiredSize.Height));
        s_Host.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            320, (int) Math.Ceiling(s_Host.DesiredSize.Height), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        s_Bitmap.Render(s_Host);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using var s_Stream = File.Create(p_OutputPath);
        s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"properties for '{p_Kind}': {s_Panel.Children.Count} control(s) -> {p_OutputPath}");
        return 0;
    }

    /// <summary>
    /// Writes the mod script a variation bake would ship, so it can be read before it reaches a game. Checks
    /// the one thing a generator gets wrong silently: brace escaping in the interpolated template, which would
    /// ship a script that cannot parse — and the user would only find out as a mod that never loads.
    ///
    /// ⚠ The script is ONLY the loader now (mount + bundle order). Applying a variation to the level is done
    /// in the bake's DATA, not here: pointing the level's copies at a new variation was measured not to work.
    /// </summary>
    private static int LuaTest(string p_OutputPath)
    {
        var s_Request = new RimeShaderEditor.View.BakeRequest
        {
            ModName = "LuaTest",
            BundleName = "luatest",
            Levels = { "MP_017" },
        };

        var s_Variations = new List<RimeShaderEditor.Emit.VariationSpec>
        {
            new()
            {
                VariationAsset = "Props/Test/LuaTest_Variation",
                CloneShader = "Props/Test/LuaTest_NEW_SHADER",
                MvdbName = "Props/Test/LuaTest_NEW_MVDB",
                Mesh = "props/test/luatest_mesh",
            },
        };

        var s_Lua = RimeShaderEditor.Emit.ShaderBaker.PreviewVariationLua(s_Request, new[] { "MP_017" },
            s_Variations);
        File.WriteAllText(p_OutputPath, s_Lua, new System.Text.UTF8Encoding(false));

        var s_Problems = new List<string>();

        // A C# interpolated template writes a literal brace as {{ }}; getting it wrong leaves doubled braces
        // in the output, which is the failure this seam exists for.
        if (s_Lua.Contains("{{") || s_Lua.Contains("}}"))
            s_Problems.Add("doubled braces survived into the script (brace escaping is wrong)");

        // Block balance: every opener needs its end. ⚠ Comments and strings must come out FIRST — this script
        // is written in English, so "for", "do" and "end" appear in prose all over it and counting those
        // makes the check fail on a perfectly good file (it did).
        // ⛔ QUOTES, BEFORE ANYTHING ELSE. This script is emitted from a C# verbatim string, where a literal
        // quote is written DOUBLED — so a lua empty string needs FOUR quotes, and writing two produces a
        // single quote that opens a string and never closes it. That shipped: the mod died at load with
        // "unfinished string near", and every check below had passed it, because the braces and the blocks
        // were perfectly fine. An odd number of quotes on a line is the tell.
        var s_Raw = s_Lua.Split('\n');
        for (var i = 0; i < s_Raw.Length; i++)
        {
            var s_Line = s_Raw[i];
            var s_At = s_Line.IndexOf("--", StringComparison.Ordinal);

            // Only strip a comment marker that is NOT itself inside a string, or the count goes wrong.
            if (s_At >= 0 && s_Line[..s_At].Count(p_C => p_C == '"') % 2 == 0)
                s_Line = s_Line[..s_At];

            if (s_Line.Count(p_C => p_C == '"') % 2 != 0)
                s_Problems.Add($"line {i + 1} has an odd number of quotes — an unterminated string (check " +
                               $"how quotes are doubled in the template): {s_Line.Trim()}");
        }

        var s_Code = string.Join("\n", s_Lua.Split('\n')
            .Select(p_L => p_L.Contains("--") ? p_L[..p_L.IndexOf("--", StringComparison.Ordinal)] : p_L));
        s_Code = System.Text.RegularExpressions.Regex.Replace(s_Code, "\"[^\"]*\"", "\"\"");

        var s_Words = System.Text.RegularExpressions.Regex.Matches(s_Code, @"(function|if|for|while|do|end|then|else|elseif|repeat|until)")
            .Select(p_M => p_M.Value)
            .ToList();

        var s_Depth = 0;
        foreach (var s_Word in s_Words)
        {
            if (s_Word is "function" or "if" or "for" or "while")
                s_Depth++;
            else if (s_Word == "end")
                s_Depth--;

            if (s_Depth < 0)
                break;
        }

        if (s_Depth != 0)
            s_Problems.Add($"block openers and 'end's do not balance (depth {s_Depth})");

        foreach (var (s_Open, s_Close, s_What) in new[] { ('(', ')', "parentheses"), ('{', '}', "braces"), ('[', ']', "brackets") })
        {
            var s_Count = s_Code.Count(p_C => p_C == s_Open) - s_Code.Count(p_C => p_C == s_Close);
            if (s_Count != 0)
                s_Problems.Add($"unbalanced {s_What} ({s_Count:+#;-#;0})");
        }

        // What the loader has to do: mount its superbundle, order its bundles, and report whether the mod's
        // own partitions actually arrived — the question four failed fixes rested on.
        foreach (var s_Needed in new[]
                 {
                     "MountSuperBundle", "PREPENDED", "p_Hook:Pass", "Level:LoadResources",
                     "EXPECTED = {", "luatest_new_mvdb", "NEVER ARRIVED", "MeshVariationDatabase",
                 })
            if (!s_Lua.Contains(s_Needed))
                s_Problems.Add($"the script does not contain '{s_Needed}'");

        // And what it must NOT do any more, so the dead approach cannot quietly come back.
        foreach (var s_Gone in new[] { "REDIRECT = {", "instanceObjectVariation", "MakeWritable" })
            if (s_Lua.Contains(s_Gone))
                s_Problems.Add($"the script still carries '{s_Gone}' — the runtime redirect was removed");

        // ⛔ WHERE THE VARIATION BUNDLE GOES IS A CRASH, NOT A PREFERENCE, and the two cases are opposite.
        // A variation that takes over the level's own copies has to register BEFORE the level's database to
        // win the key, and it survives being that early only because a stand-in mesh partition rides with it.
        // An ordinary one has no stand-in: early means its entry reads a mesh that is not loaded yet, which
        // killed the client on the load thread with no minidump. Both orders are asserted here because the
        // one that ships is picked from the spec, and a wrong pick reads exactly like a working bake.
        // The database rides in front of it — see the take-over assertion further down for why — so what is
        // checked here is that both come before anything else, the variation bundle included.
        if (!s_Lua.Contains("local s_New = { m_DbEarly, m_VarBundle }") ||
            s_Lua.Contains("s_New[#s_New + 1] = m_VarBundle"))
            s_Problems.Add("a variation that takes over the level's objects did not put its VARIATION bundle " +
                           "first — its entries would reach the variation key after the level's");

        var s_Ordinary = RimeShaderEditor.Emit.ShaderBaker.PreviewVariationLua(s_Request, new[] { "MP_017" },
            new List<RimeShaderEditor.Emit.VariationSpec>
            {
                new()
                {
                    ApplyToLevel = false,
                    VariationAsset = "Props/Test/LuaTest_Variation",
                    CloneShader = "Props/Test/LuaTest_NEW_SHADER",
                    MvdbName = "Props/Test/LuaTest_NEW_MVDB",
                    Mesh = "props/test/luatest_mesh",
                },
            });

        if (!s_Ordinary.Contains("if l_Bundle == s_Level then"))
            s_Problems.Add("an ordinary variation did not put its bundle after the level's own");

        // Which BATCH, not just which position in it: waiting for the batch that carries the level's own
        // bundle means every batch before it already registered, and the first to reach a key keeps it.
        if (!s_Lua.Contains("if m_Prepended then"))
            s_Problems.Add("a take-over waits for a particular batch instead of taking the earliest one");

        if (!s_Ordinary.Contains("if not s_HasLevel then"))
            s_Problems.Add("an ordinary variation does not wait for the level's own bundle — its entry " +
                           "points into it and would not resolve earlier");

        // ⛔ AND THE ONE THAT DECIDES WHETHER A REPLACEMENT WORKS AT ALL. A shader name is answered by the
        // database registered LAST, so this bundle must come after the level's own. It shipped the other way
        // round for a long time, on the belief that order did not matter for databases — which made every
        // vanilla-name replacement silently inert and forced take-overs down routes that cannot reach a
        // grouped copy.
        // The replacement loader is a SEPARATE script and it shipped with the losing order while every check
        // here went through the variation one. Both are asserted now.
        var s_Replacement = RimeShaderEditor.Emit.ShaderBaker.PreviewReplacementLua(s_Request, new[] { "MP_017" });

        // ⛔ AND THE TAKE-OVER IS THE ONE CASE WHERE "LAST" IS WRONG, WHICH THIS TEST USED TO DEMAND. Its key
        // is a name nothing else defines, so it has nobody to outrank; what it does have is a variation
        // database, ingested the moment its own bundle loads, that resolves each material's shader BY NAME
        // right there. With the database registered after the level, that name did not exist yet: the
        // materials came up with no shader and 24 grouped copies rendered as nothing at all.
        if (!s_Lua.Contains("local s_New = { m_DbEarly, m_VarBundle }", StringComparison.Ordinal))
            s_Problems.Add("a take-over does not register its shader database BEFORE its variation bundle — " +
                           "the variation's materials would look up a shader name that is not registered " +
                           "yet, and the objects would not be drawn at all");

        // ⛔ A MOD CARRYING BOTH KINDS: the two need OPPOSITE sides of the level's own bundle, so it ships
        // TWO databases. Shipping one would make whichever kind it was not built for silently inert — the
        // replacement served by the level's database, or the variation's materials resolving a name that is
        // not registered yet. Asserted on the SAME script the mixed bake writes.
        var s_Mixed = RimeShaderEditor.Emit.ShaderBaker.PreviewVariationLua(s_Request, new[] { "MP_017" },
            new List<RimeShaderEditor.Emit.VariationSpec>
            {
                new()
                {
                    ApplyToLevel = true,
                    VariationAsset = "Props/Test/LuaTest_Variation",
                    CloneShader = "Props/Test/LuaTest_NEW_SHADER",
                    MvdbName = "Props/Test/LuaTest_NEW_MVDB",
                    Mesh = "props/test/luatest_mesh",
                },
            }, true);

        var s_MixedEarly = s_Mixed.IndexOf("local s_New = { m_DbEarly, m_VarBundle }", StringComparison.Ordinal);
        var s_MixedLevel = s_Mixed.IndexOf("s_New[#s_New + 1] = l_Bundle", StringComparison.Ordinal);
        var s_MixedLate = s_Mixed.IndexOf("s_New[#s_New + 1] = m_DbBundle", StringComparison.Ordinal);

        if (s_MixedEarly < 0 || s_MixedLate < 0 || s_MixedLate < s_MixedLevel)
            s_Problems.Add("a mod mixing a take-over with a replacement does not ship BOTH databases with the " +
                           "variation one first and the replacement one after the level — one of the two would " +
                           "install and change nothing");

        // And a mod with no replacement must not name a bundle it never built.
        if (s_Lua.Contains("s_New[#s_New + 1] = m_DbBundle", StringComparison.Ordinal))
            s_Problems.Add("a take-over with no replacement still references the replacement database bundle, " +
                           "which this bake never built — an unresolvable id in the load list");

        foreach (var (s_Script, s_What) in new[]
                 {
                     (s_Ordinary, "an ordinary variation"), (s_Replacement, "a replacement"),
                 })
        {
            var s_Level = s_Script.IndexOf("s_New[#s_New + 1] = l_Bundle", StringComparison.Ordinal);

            // WHICH database this mode ships: an ordinary variation carries only the clone-key one
            // (m_DbEarly), a replacement only the vanilla-key one. Both must land after the level's own —
            // the first because the variation bundle that reads those names follows it, the second because
            // the last database registered is the one that answers for a name.
            var s_Db = new[] { "m_DbEarly", "m_DbBundle", "m_Bundle" }
                .Select(p_N => s_Script.IndexOf($"s_New[#s_New + 1] = {p_N}", StringComparison.Ordinal))
                .Where(p_I => p_I >= 0)
                .DefaultIfEmpty(-1)
                .Min();

            if (s_Db < 0 || s_Db < s_Level)
                s_Problems.Add($"{s_What} registers its shader database BEFORE the level's own, so the " +
                               "level's answers for every vanilla name and a replacement does nothing");
        }

        if (s_Ordinary.Contains("local s_New = { m_DbEarly, m_VarBundle }"))
            s_Problems.Add("an ordinary variation loads its bundle first — its entry would read a null mesh");

        // ⛔ THE OTHER THREE SHAPES WERE ONLY EVER ASSERTED, NEVER LOOKED AT. An assertion checks the line you
        // thought of; the file shows the one you did not. They cost nothing to write and this harness is the
        // only place a mixed mod's loader can be read without baking one.
        foreach (var (s_Name, s_Text) in new[]
                 {
                     ("ordinary", s_Ordinary), ("replacement", s_Replacement), ("mixed", s_Mixed),
                 })
            File.WriteAllText(Path.ChangeExtension(p_OutputPath, $".{s_Name}.lua"), s_Text);

        Console.Out.WriteLine($"wrote {s_Lua.Length} chars -> {p_OutputPath} " +
                              "(+ .ordinary/.replacement/.mixed for reading)");

        foreach (var s_Problem in s_Problems)
            Console.Error.WriteLine("  " + s_Problem);

        if (s_Problems.Count > 0)
        {
            Console.Error.WriteLine($"LUATEST: FAIL - {s_Problems.Count} problem(s) in the generated script.");
            return 1;
        }

        Console.Out.WriteLine("LUATEST: PASS - the generated loader is balanced and does what a loader does.");
        return 0;
    }

    /// <summary>
    /// The stand-in mesh partition, checked against a dump shaped like a real one.
    ///
    /// ⛔ THIS EXISTS BECAUSE OF ONE WRONG GUID. The partition to repoint off is the dump's OWN, and the
    /// value was being taken from the first "PartitionGuid" in the text — which belongs to the mesh's LodGroup
    /// reference, several fields earlier. The repoint then moved nothing, the entry shipped still pointing
    /// into the level's bundle, and with the mod's bundle loading first that is a null mesh read on the load
    /// thread: silent client crash, no minidump, one wasted boot. The fixture puts an outgoing reference
    /// BEFORE the partition guid on purpose, so that mistake cannot pass again.
    /// </summary>
    private static int StubTest(string p_WorkDir)
    {
        Directory.CreateDirectory(p_WorkDir);

        const string c_RealPart = "76beb44d-6b4f-2336-c4b6-7145a294b02f";
        const string c_MeshInst = "ae45d3c3-5cb6-4c26-9b90-178d9d87d971";
        const string c_MatInst = "cfefbda2-b835-2d9a-c739-0808dd0272c5";
        const string c_Foreign = "64991a4a-4c5e-11de-b1f5-fe435f0a1d8f";

        // The shape a partition dump really has, with the outgoing LodGroup reference sitting BEFORE the
        // partition's own guid — the layout that made a text-order read pick the wrong one.
        var s_Dump = Path.Combine(p_WorkDir, "stubtest_dump.json");
        File.WriteAllText(s_Dump, @"{
  ""PrimaryInstanceGuid"": ""MESH_INST"",
  ""Instances"": {
    ""MESH_INST"": {
      ""$type"": ""CompositeMeshAsset"",
      ""Name"": ""props/test/stub_mesh"",
      ""LodGroup"": { ""PartitionGuid"": ""FOREIGN"", ""InstanceGuid"": ""6f61313b-c996-c1cb-ce6f-34392f6cc1e1"" },
      ""NameHash"": 3517542969,
      ""Materials"": [ { ""PartitionGuid"": ""REAL_PART"", ""InstanceGuid"": ""MAT_INST"" } ]
    },
    ""MAT_INST"": {
      ""$type"": ""MeshMaterial"",
      ""ShaderInstance"": null,
      ""Shader"": {
        ""Shader"": { ""PartitionGuid"": ""5a2eeead-5f1a-11de-8e25-a20b13b49ad1"", ""InstanceGuid"": ""8792a462-f0b7-0426-49f6-35b855acf65b"" },
        ""BoolParameters"": [], ""VectorParameters"": [], ""VectorArrayParameters"": [], ""TextureParameters"": []
      }
    }
  },
  ""Name"": ""props/test/stub_mesh"",
  ""PartitionGuid"": ""REAL_PART""
}".Replace("MESH_INST", c_MeshInst).Replace("MAT_INST", c_MatInst)
                .Replace("REAL_PART", c_RealPart).Replace("FOREIGN", c_Foreign));

        // ⛔ A FILE LEFT BY AN EARLIER BAKE, PLANTED ON PURPOSE. The clone's shader partition path was
        // assigned and consumed with nothing in between ever writing it, so a work directory reused across
        // bakes published the PREVIOUS one: this variation's name carrying another shader's graph. The engine
        // then looked up a key that exists nowhere, the materials came up with no shader, and the level's
        // copies rendered as nothing at all — with every other check green. The fixture writes that stale
        // file first, so the failure mode cannot come back.
        File.WriteAllText(Path.Combine(p_WorkDir, "variation_stub.json"),
            "{\"PrimaryInstanceGuid\": \"00000000-0000-0000-0000-000000000009\", \"Instances\": {" +
            "\"00000000-0000-0000-0000-000000000009\": {\"$type\": \"ShaderGraph\", " +
            "\"Name\": \"Some/Other/Shader_FROM_AN_EARLIER_BAKE\", \"MaxSubMaterialCount\": 8, " +
            "\"GammaCorrectionEnable\": true}}, \"Name\": \"Some/Other/Shader_FROM_AN_EARLIER_BAKE\", " +
            "\"PartitionGuid\": \"00000000-0000-0000-0000-00000000000a\"}");

        var s_Spec = RimeShaderEditor.Emit.VariationPlan.Build("SS_Test", "Props/Test/StubTest_NEW",
            "props/test/stub_mesh", p_WorkDir, null);

        var s_Problems = new List<string>();

        // The one field the variation hangs from: the graph's name IS the database key its materials ask for.
        if (RimeShaderEditor.Emit.VariationPlan.StubGraphName(s_Spec) != s_Spec.CloneShader)
            s_Problems.Add("the clone's shader partition does not name the clone — its materials would look " +
                           "up a shader that does not exist and the objects would not be drawn at all");

        // And it has to be the partition the variation asset points at, or the reference dangles.
        var s_StubJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(s_Spec.StubJsonPath))!
            .AsObject();
        var s_VarJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(s_Spec.VariationJsonPath))!
            .AsObject();
        var s_ShaderRef = s_VarJson["Instances"]![s_Spec.MmvInstGuid]!["Shader"]!["Shader"]!;

        if ((string?) s_StubJson["PartitionGuid"] != (string?) s_ShaderRef["PartitionGuid"] ||
            s_StubJson["Instances"]!.AsObject().First().Key != (string?) s_ShaderRef["InstanceGuid"])
            s_Problems.Add("the variation asset points at guids the clone's shader partition does not carry");

        if (!RimeShaderEditor.Emit.VariationPlan.WriteMeshStub(s_Spec, s_Dump, out var s_Error))
        {
            Console.Error.WriteLine($"STUBTEST: FAIL - the stand-in was refused ({s_Error}).");
            return 1;
        }

        if (s_Spec.MeshRealPartGuid != c_RealPart)
            s_Problems.Add($"the partition to repoint off is '{s_Spec.MeshRealPartGuid}', not the dump's own " +
                           $"'{c_RealPart}' — a repoint aimed there moves nothing");

        var s_Stub = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(s_Spec.MeshStubJsonPath))!
            .AsObject();

        if ((string?) s_Stub["PartitionGuid"] != s_Spec.MeshStubPartGuid)
            s_Problems.Add("the stand-in kept the real partition guid");

        if ((string?) s_Stub["Name"] == "props/test/stub_mesh")
            s_Problems.Add("the stand-in kept the real partition NAME — it would dedupe against the real one");

        var s_Instances = s_Stub["Instances"]!.AsObject();
        if (s_Instances.Count != 2 || s_Instances[c_MeshInst] == null || s_Instances[c_MatInst] == null)
            s_Problems.Add("the stand-in lost an instance or renamed one (the entry's refs would dangle)");

        var s_Mesh = s_Instances[c_MeshInst]!;
        if ((uint?) s_Mesh["NameHash"] != 3517542969)
            s_Problems.Add("the stand-in lost the NameHash — that value IS the variation key");

        if (s_Mesh["LodGroup"] is not null and not System.Text.Json.Nodes.JsonValue)
            s_Problems.Add("a reference OUT of the partition survived — the bundle would import from the " +
                           "level's, which is what loading first cannot do");

        if ((string?) s_Mesh["Materials"]![0]!["PartitionGuid"] != s_Spec.MeshStubPartGuid)
            s_Problems.Add("an in-partition reference was not repointed onto the stand-in");

        if (s_Instances[c_MatInst]!["Shader"]!["Shader"] is not null and not System.Text.Json.Nodes.JsonValue)
            s_Problems.Add("the material's surface shader reference out of the partition survived");

        // ── How the level PLACES the mesh ───────────────────────────────────────────────────────────────
        // ⛔ The trap this fixture reproduces: the group member does NOT reference the mesh, it references a
        // mesh ENTITY DATA that references the mesh. Searching members for the mesh's own guid finds nothing
        // and reads as "this mesh is not in the group" — a wrong turn that was taken for real.
        const string c_EntityData = "4d41f832-f59a-b38a-dd55-9155dd5598a6";
        var s_Level = Path.Combine(p_WorkDir, "stubtest_level.json");

        string LevelJson(string p_Variations) => (@"{
  ""PrimaryInstanceGuid"": ""00000000-0000-0000-0000-000000000001"",
  ""Instances"": {
    ""ENTITY"": { ""$type"": ""CompositeMeshEntityData"",
                  ""Mesh"": { ""PartitionGuid"": ""REAL_PART"", ""InstanceGuid"": ""MESH_INST"" } },
    ""00000000-0000-0000-0000-000000000002"": { ""$type"": ""StaticModelGroupEntityData"", ""MemberDatas"": [
      { ""MeshEntityType"": { ""PartitionGuid"": ""FOREIGN"", ""InstanceGuid"": ""00000000-0000-0000-0000-0000000000ff"" },
        ""InstanceCount"": 7, ""InstanceObjectVariation"": [] },
      { ""MeshEntityType"": { ""PartitionGuid"": ""FOREIGN"", ""InstanceGuid"": ""ENTITY"" },
        ""InstanceCount"": 49, ""InstanceObjectVariation"": [ " + p_Variations + @" ] }
    ] }
  },
  ""Name"": ""levels/test/test"", ""PartitionGuid"": ""00000000-0000-0000-0000-0000000000aa""
}").Replace("ENTITY", c_EntityData).Replace("REAL_PART", c_RealPart)
                .Replace("MESH_INST", c_MeshInst).Replace("FOREIGN", c_Foreign);

        File.WriteAllText(s_Level, LevelJson(""));

        if (!RimeShaderEditor.Emit.VariationPlan.InspectLevelUse(s_Level, c_RealPart, out var s_Count,
                out var s_Hashes, out var s_Grouped))
            s_Problems.Add("the level's placement of the mesh was not found at all (the member points at a " +
                           "mesh entity data, not at the mesh)");
        else if (!s_Grouped)
            s_Problems.Add("copies inside the level's static model group were not reported as grouped — a " +
                           "take-over would ship for the one case measured NOT to work");
        else if (s_Count != 49)
            s_Problems.Add($"counted {s_Count} placed instance(s) instead of 49 (a member of another mesh " +
                           "was counted in, or the right one was missed)");
        else if (s_Hashes.Count != 0)
            s_Problems.Add("reported a variation on copies that carry none");

        // And the case that must STOP a take-over: copies the map gave a variation of their own.
        File.WriteAllText(s_Level, LevelJson("3789926777, 3789926777"));

        if (!RimeShaderEditor.Emit.VariationPlan.InspectLevelUse(s_Level, c_RealPart, out _, out var s_Own,
                out _) ||
            s_Own.Count != 2 || s_Own[0] != 3789926777)
            s_Problems.Add("copies carrying their own variation were not reported — a base take-over would " +
                           "ship silently doing nothing");

        // ── The clone's own shader partition, copied from the vanilla one ───────────────────────────────
        // The fixture is the real shape of a shipped surface shader partition: ONE ShaderGraph whose Name is
        // the key a shader database is searched by. The copy takes that shader's FIELDS (they are the
        // shader's, not the shape's defaults) but our name and our guids, because the variation asset points
        // at them and the name is the key the bake wrote into the database.
        var s_ShaderDump = Path.Combine(p_WorkDir, "stubtest_shader.json");
        File.WriteAllText(s_ShaderDump,
            "{\"PrimaryInstanceGuid\":\"8792a462-f0b7-0426-49f6-35b855acf65b\",\"Instances\":{" +
            "\"8792a462-f0b7-0426-49f6-35b855acf65b\":{\"$type\":\"ShaderGraph\",\"Name\":\"SS_Test\"," +
            "\"MaxSubMaterialCount\":8,\"GammaCorrectionEnable\":true}}," +
            // ⛔ MIXED CASE ON PURPOSE. The dumper echoes the name it was ASKED for, and the bake asks in the
            // case the editor targets by — so a dump looks like this while the SHIPPED partition is
            // lowercase. Taking this name at face value added the override under a name of its own, which
            // overrides nothing and looks exactly like "the engine kept its copy". One boot.
            "\"Name\":\"SS_Test\",\"PartitionGuid\":\"5a2eeead-5f1a-11de-8e25-a20b13b49ad1\"}");

        if (!RimeShaderEditor.Emit.VariationPlan.WriteShaderStubFrom(s_Spec, s_ShaderDump, out var s_ShError))
        {
            s_Problems.Add($"the clone's shader partition could not be copied from the vanilla one ({s_ShError})");
        }
        else
        {
            var s_Copy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(s_Spec.StubJsonPath))!
                .AsObject();

            // Its identity is OURS: the variation asset points at these guids, and the vanilla partition must
            // be left alone (this route no longer touches it at all).
            if ((string?) s_Copy["PartitionGuid"] != s_Spec.StubPartGuid ||
                (string?) s_Copy["Name"] != s_Spec.CloneShader)
                s_Problems.Add("the copied partition kept the vanilla shader's identity instead of the " +
                               "clone's — it would collide with the shader it was copied from");

            var s_Graph = s_Copy["Instances"]!.AsObject().First().Value!;
            if ((string?) s_Graph["Name"] != s_Spec.CloneShader)
                s_Problems.Add("the copied graph does not name the clone, so its materials would look up a " +
                               "shader that does not exist and the objects would not be drawn");

            if ((int?) s_Graph["MaxSubMaterialCount"] != 8 || (bool?) s_Graph["GammaCorrectionEnable"] != true)
                s_Problems.Add("the copy dropped a field of the vanilla shader instead of carrying it over");
        }

        // ── The one mix that is a CRASH, not a preference ───────────────────────────────────────────────
        // A take-over and a hand-placed variation share one bundle; the take-over makes it load before the
        // level's own, and the hand-placed one's entry points at a mesh that is not loaded then. The
        // registrar reads it anyway — null mesh, client dead on the load thread, no minidump. The bake
        // refuses before mounting anything, which is what this asserts (it returns without touching a game).
        var s_Hand = new RimeShaderEditor.Emit.VariationSpec
        {
            ApplyToLevel = false, VariationAsset = "Props/Test/ByHand", CloneShader = "SS_Test_BYHAND",
            MvdbName = "Props/Test/ByHand_MVDB", Mesh = "props/test/other_mesh",
        };

        var s_Mixed = RimeShaderEditor.Emit.ShaderBaker.Bake(
            new RimeShaderEditor.View.BakeRequest
            {
                Levels = { "MP_017" }, ModName = "StubTest", BundleName = "stubtest",
                OutputFolder = p_WorkDir, AdditiveDb = true,
            },
            new[]
            {
                new RimeShaderEditor.Emit.ShaderBaker.BakeShader("SS_Test", "nowhere.dxbc", s_Spec),
                new RimeShaderEditor.Emit.ShaderBaker.BakeShader("SS_Other", "nowhere.dxbc", s_Hand),
            },
            "no-game", "no-repl", Path.Combine(p_WorkDir, "mix"), _ => { });

        if (s_Mixed.Levels.All(p_L => p_L.Failure == null || !p_L.Failure.Contains("load BEFORE the level")))
            s_Problems.Add("a bake mixing a take-over with a hand-placed variation was NOT refused — they " +
                           "share a bundle, and the hand-placed entry would read a null mesh on the load thread");

        foreach (var s_Problem in s_Problems)
            Console.Error.WriteLine("  " + s_Problem);

        if (s_Problems.Count > 0)
        {
            Console.Error.WriteLine($"STUBTEST: FAIL - {s_Problems.Count} problem(s) in the stand-in.");
            return 1;
        }

        Console.Out.WriteLine("STUBTEST: PASS - the stand-in keeps the key and the instances, repoints what " +
                              "stays inside it and cuts what leaves; the level's placement reads back through " +
                              "the group member.");
        return 0;
    }

    private static int NodeTest(string p_WorkDir)
    {
        var s_Fxc = RimeShaderEditor.MainWindow.FindSdkFxc();
        if (s_Fxc == null)
        {
            Console.Error.WriteLine("ERROR: fxc.exe not found.");
            return 1;
        }

        Directory.CreateDirectory(p_WorkDir);
        var s_Failures = 0;
        var s_Tested = 0;

        foreach (var s_Def in Graph.Palette.All.OrderBy(p_D => p_D.Kind))
        {
            if (!s_Def.IsRoot && s_Def.Outputs.Count == 0)
                continue;

            var s_Graph = new Graph.ShaderGraph { Name = $"nodetest_{s_Def.Kind}" };

            // Root nodes are exercised on their own; everything else is wired into a StandardRoot so the
            // emitter actually reaches it (unreachable nodes are not emitted at all).
            if (s_Def.IsRoot)
            {
                s_Graph.Nodes.Add(new Graph.GraphNode { Kind = s_Def.Kind, X = 100, Y = 0 });
            }
            else
            {
                var s_Node = new Graph.GraphNode { Kind = s_Def.Kind, X = -300, Y = 0 };
                var s_Root = new Graph.GraphNode { Kind = "StandardRoot", X = 100, Y = 0 };
                s_Graph.Nodes.Add(s_Node);
                s_Graph.Nodes.Add(s_Root);
                s_Graph.Connect(s_Node.Id, s_Def.Outputs[0].Name, s_Root.Id, "Diffuse");
            }

            s_Tested++;
            var s_Emit = new HlslEmitter().Emit(s_Graph);
            if (!s_Emit.Ok)
            {
                Console.Out.WriteLine($"FAIL {s_Def.Kind}: {string.Join("; ", s_Emit.Errors)}");
                s_Failures++;
                continue;
            }

            var s_HlslPath = Path.Combine(p_WorkDir, $"{s_Def.Kind}.hlsl");
            File.WriteAllText(s_HlslPath, s_Emit.Hlsl);

            var s_Process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s_Fxc)
            {
                Arguments = $"/nologo /T ps_5_0 /E main /O1 /Fo \"{Path.ChangeExtension(s_HlslPath, ".dxbc")}\" \"{s_HlslPath}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (s_Process == null)
            {
                Console.Out.WriteLine($"FAIL {s_Def.Kind}: could not start fxc");
                s_Failures++;
                continue;
            }

            var s_Output = s_Process.StandardOutput.ReadToEnd() + s_Process.StandardError.ReadToEnd();
            s_Process.WaitForExit();

            if (s_Process.ExitCode != 0)
            {
                Console.Out.WriteLine($"FAIL {s_Def.Kind}: {s_Output.Trim().Replace("\r\n", " | ")}");
                s_Failures++;
            }
        }

        Console.Out.WriteLine($"NODETEST: {s_Tested - s_Failures}/{s_Tested} nodes compiled.");
        return s_Failures == 0 ? 0 : 1;
    }

    /// <summary>Runs the exact thumbnail pipeline the button uses, so it can be verified without the GUI.</summary>
    private static int LoadTextures(string p_Target, string p_CacheDir, string? p_GamePath)
    {
        var s_GamePath = p_GamePath ?? EditorSettings.DetectGamePath();
        if (string.IsNullOrWhiteSpace(s_GamePath))
        {
            Console.Error.WriteLine("ERROR: BF3 path not given and not detectable from the registry.");
            return 1;
        }

        var s_Repl = RimeShaderEditor.MainWindow.FindRimeRepl();
        if (s_Repl == null)
        {
            Console.Error.WriteLine("ERROR: RimeREPL.exe not found.");
            return 1;
        }

        Directory.CreateDirectory(p_CacheDir);

        // Same map filename the window uses, so a cache warmed from the command line is picked up by the GUI.
        var s_MapPath = Path.Combine(p_CacheDir,
            RimeShaderEditor.MainWindow.SlotMapFileName(p_Target));

        // A contract makes the map complete (paired registers, mesh names): detected from a compiled
        // reference of the target when one is passed — the same detection the window runs on level load.
        RimeShaderEditor.Emit.ShaderContract? s_Contract = null;
        var s_ContractPath = Environment.GetEnvironmentVariable("RSE_CONTRACT_DXBC");
        if (s_ContractPath is { Length: > 0 } && File.Exists(s_ContractPath))
            try
            {
                var s_Bytes = File.ReadAllBytes(s_ContractPath);
                s_Contract = RimeShaderEditor.Emit.ShaderContract.Detect(s_Bytes);
                var s_Disasm = new SharpDX.D3DCompiler.ShaderBytecode(s_Bytes).Disassemble();
                s_Contract.ClassifyInterpolators(RimeShaderEditor.Translate.DxbcAsm.Parse(s_Disasm.Split('\n')));
                Console.Out.WriteLine($"Contract detected from {s_ContractPath}.");
            }
            catch (Exception s_Exception)
            {
                Console.Error.WriteLine($"WARN: contract detection failed ({s_Exception.Message}); " +
                                        "registers will be unknown.");
            }

        var s_Log = RimeShaderEditor.MainWindow.LoadTexturesCore(s_Repl, s_GamePath,
            RimeShaderEditor.MainWindow.ShaderDbForTarget(p_Target), p_Target, p_CacheDir, s_MapPath, s_Contract);

        foreach (var s_Line in s_Log)
            Console.Out.WriteLine(s_Line);

        return 0;
    }

    /// <summary>
    /// Runs the ORIGINAL game shader and the one our graph emits over IDENTICAL inputs and diffs the four GBuffer
    /// targets. This is what turns "I read the disassembly and deduced the maths" into a measurement: the two
    /// shaders either agree on every pixel or they do not, and the report says by how much and where.
    ///
    /// Precision note: the targets are R8G8B8A8_UNorm, which is the precision BF3 itself uses for the GBuffer
    /// (it stores sqrt(albedo) precisely because 8 bits are not enough for linear albedo), so a difference of 1
    /// LSB is at the limit of what the format can express and a real formula error shows up as many.
    /// </summary>
    private static int ShaderDiff(string p_GraphPath, string p_OriginalDxbc, string? p_TexCache)
    {
        var s_Graph = ShaderGraph.FromJson(File.ReadAllText(p_GraphPath));

        // ⛔ The contract comes from the shader being compared against, exactly as the sweep does it. Emitting
        // with the rigid-mesh default instead was a real inconsistency BETWEEN THE TWO VERIFICATION PATHS: the
        // same graph produced a different PsIn depending on which harness ran it, so a shader could come out
        // EQUIVALENT in one and DIFFERENT in the other - and until they agree, neither number means anything.
        ShaderContract? s_DiffContract = null;
        try { s_DiffContract = ShaderContract.Detect(File.ReadAllBytes(p_OriginalDxbc)); }
        catch { }

        // The interpolator meanings come from the shader's own disassembly, exactly as the sweep and the
        // editor compute them - the three harnesses must feed the same vertex shader or a failure reproduced
        // in one of them is a different experiment.
        try
        {
            var s_Disassembly = new SharpDX.D3DCompiler.ShaderBytecode(File.ReadAllBytes(p_OriginalDxbc))
                .Disassemble();
            s_DiffContract?.ClassifyInterpolators(
                RimeShaderEditor.Translate.DxbcAsm.Parse(s_Disassembly.Split('\n')));
        }
        catch { }

        Console.Out.WriteLine($"contract: {s_DiffContract?.Family.ToString() ?? "none"}, " +
                              $"{s_DiffContract?.Interpolators.Count() ?? 0} interpolator(s), " +
                              $"{s_DiffContract?.RenderTargets ?? 0} target(s)");

        var s_Emit = new HlslEmitter { Contract = s_DiffContract }.Emit(s_Graph);
        foreach (var s_Error in s_Emit.Errors)
            Console.Error.WriteLine($"ERROR: {s_Error}");

        if (!s_Emit.Ok)
            return 1;

        using var s_Preview = new View.ShaderPreview();
        if (!s_Preview.InitialiseOffscreen(512, 512))
        {
            Console.Error.WriteLine($"ERROR: no D3D11 device: {s_Preview.LastError}");
            return 1;
        }

        // ⛔ THE SAME GEOMETRY THE SWEEP USES, and it has to stay that way: this mode exists to reproduce a
        // sweep result one shader at a time, and a reproduction on a different mesh reproduces nothing.
        s_Preview.UseSphere = Environment.GetEnvironmentVariable("PREVIEW_SPHERE") != "0";

        // Real art rather than the placeholder: both shaders get the same input either way, but the checker is
        // flat in channels the barrel actually reads, which would hide a difference that real textures expose.
        var s_Cache = p_TexCache ?? Environment.GetEnvironmentVariable("PREVIEW_TEXCACHE");
        if (!string.IsNullOrWhiteSpace(s_Cache) && Directory.Exists(s_Cache))
            LoadCachedTexturesInto(s_Preview, s_Cache, s_Graph.TargetShader);
        else
            Console.Out.WriteLine("(no texture cache given; diffing against the placeholder art)");

        // Nothing between the two passes may change: same time, same camera, same textures.
        s_Preview.Time = 0f;
        s_Preview.SetInterpolatorMeanings(s_DiffContract?.InterpolatorMeanings);

        if (!s_Preview.SetAuthoredShader(File.ReadAllBytes(p_OriginalDxbc)))
        {
            Console.Error.WriteLine($"ERROR: the game shader was rejected: {s_Preview.LastError}");
            Console.Error.WriteLine("If this is a signature mismatch, the preview's vertex layout does not " +
                                    "match what that shader expects - that is a finding, not a bug in the test.");
            return 1;
        }

        s_Preview.Render();
        var s_Original = CaptureAll(s_Preview);

        using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
            s_Emit.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3);

        if (s_Compiled.Bytecode == null || !s_Preview.SetAuthoredShader(s_Compiled.Bytecode.Data))
        {
            Console.Error.WriteLine($"ERROR: our shader was rejected: {s_Preview.LastError}");
            return 1;
        }

        s_Preview.Render();
        var s_Ours = CaptureAll(s_Preview);

        Console.Out.WriteLine($"original {new FileInfo(p_OriginalDxbc).Length} bytes vs " +
                              $"ours {s_Compiled.Bytecode.Data.Length} bytes");

        // The object mask: off-object every target is cleared to (0,0,0,0) and the pixel shader never runs, so
        // "any byte non-zero" is exactly "the shader wrote here". Including the background would dilute every
        // average until a real difference looked small.
        var s_Mask = Coverage(s_Original, s_Ours, 4, out var s_Covered);

        Console.Out.WriteLine($"object covers {s_Covered} of {s_Mask.Length} pixels");
        if (s_Covered == 0)
        {
            Console.Error.WriteLine("ERROR: neither shader wrote anything - the diff would be vacuous.");
            return 1;
        }

        var s_Names = new[] { "RT0 normal.xyz/smoothness.w", "RT1 sqrt(albedo)/spec.w", "RT2 lighting model",
            "RT3 emissive" };
        var s_Worst = 0;

        for (var t = 0; t < 4; t++)
        {
            var s_Max = new int[4];
            var s_Sum = new long[4];
            var s_Differing = 0;

            for (var i = 0; i < s_Mask.Length; i++)
            {
                if (!s_Mask[i])
                    continue;

                var s_Any = false;
                for (var c = 0; c < 4; c++)
                {
                    var s_Delta = Math.Abs(s_Original[t][i * 4 + c] - s_Ours[t][i * 4 + c]);
                    s_Sum[c] += s_Delta;
                    if (s_Delta > s_Max[c])
                        s_Max[c] = s_Delta;

                    if (s_Delta > 1)
                        s_Any = true;
                }

                if (s_Any)
                    s_Differing++;
            }

            var s_TargetMax = s_Max.Max();
            s_Worst = Math.Max(s_Worst, s_TargetMax);

            Console.Out.WriteLine(
                $"  {s_Names[t],-28} max/channel R{s_Max[0]} G{s_Max[1]} B{s_Max[2]} A{s_Max[3]}  " +
                $"mean {string.Join("/", s_Sum.Select(p_S => (p_S / (double) s_Covered).ToString("0.00")))}  " +
                $"pixels off by >1 LSB: {s_Differing} ({100.0 * s_Differing / s_Covered:0.00}%)");
        }

        Console.Out.WriteLine(s_Worst <= 1
            ? "SHADERDIFF: EQUIVALENT - no channel differs by more than 1 LSB anywhere on the object."
            : $"SHADERDIFF: DIFFERENT - worst channel differs by {s_Worst}/255. The graph is NOT the original.");

        return s_Worst <= 1 ? 0 : 1;
    }

    /// <summary>
    /// Translates a whole extracted level and differentially tests EVERY shader it can, against that shader's own
    /// bytecode.
    ///
    /// It exists because one probe is not an audit. The write-mask bug this sweep was written after was invisible
    /// on the oil drum - the one shader the translator had been proven against - and would have stayed invisible
    /// for as long as the only evidence was a single hand-picked shader. A translator's correctness claim has to
    /// be measured over a corpus, not over its favourite example.
    ///
    /// Shaders whose interpolator layout the preview's vertex stage cannot feed (terrain, vegetation, skinning)
    /// are reported as SIGNATURE rather than silently counted as passes.
    /// </summary>
    private static int Sweep(string p_Root, int p_Max, string? p_TexCache)
    {
        var s_IndexPath = Path.Combine(p_Root, "index.txt");
        if (!File.Exists(s_IndexPath))
        {
            Console.Error.WriteLine($"ERROR: no index.txt under {p_Root}.");
            return 2;
        }

        var s_Fxc = RimeShaderEditor.MainWindow.FindSdkFxc();
        if (s_Fxc == null)
        {
            Console.Error.WriteLine("ERROR: fxc.exe not found.");
            return 2;
        }

        using var s_Preview = new View.ShaderPreview();
        // ⛔ 512, the same size --shaderdiff uses. At 256 the screen-space derivatives are twice as large, which
        // moves the mip level `sample` picks, and 18 shaders came out DIFFERENT by 2-3/255 here while measuring
        // EQUIVALENT one at a time. A verification harness that disagrees with itself depending on its window
        // size is not a verification harness.
        if (!s_Preview.InitialiseOffscreen(512, 512))
        {
            Console.Error.WriteLine($"ERROR: no D3D11 device: {s_Preview.LastError}");
            return 1;
        }

        // A cube gives every interpolator a DEGENERATE value over whole faces: the world normal is exactly
        // (1,0,0) or (0,0,1), so anything reading a tangent-frame component reads a hard 0 or a hard 1 across
        // a quarter of the picture. Shader families whose interpolators mean something else there - a particle
        // fade, a vertex colour - then multiply by that zero and write nothing at all. A sphere varies all of
        // them continuously, so the same shaders produce something comparable. Measured: 6 more shaders verified
        // and the unverifiable ones down from 20 to 14, with all five guardians still exact on the sphere.
        s_Preview.UseSphere = Environment.GetEnvironmentVariable("PREVIEW_SPHERE") != "0";

        if (!string.IsNullOrWhiteSpace(p_TexCache) && Directory.Exists(p_TexCache))
            LoadCachedTexturesInto(s_Preview, p_TexCache,
                "Props/StreetProps/OilDrumBarrel_01/SS_OilDrumBarrel_01");

        s_Preview.Time = 0f;

        var s_Rows = File.ReadAllLines(s_IndexPath)
            .Select(p_L => p_L.Split('\t'))
            .Where(p_P => p_P.Length >= 2)
            .Take(p_Max)
            .ToList();

        var s_Work = Path.Combine(p_Root, "_sweep");
        Directory.CreateDirectory(s_Work);

        int s_Equivalent = 0, s_Different = 0, s_Gap = 0, s_Signature = 0, s_Broken = 0, s_Blank = 0;
        var s_Unstable = 0;
        var s_Failures = new List<string>();
        var s_GapOpcodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var s_GapConstants = new Dictionary<string, int>(StringComparer.Ordinal);
        var s_NodeCounts = new List<int>();
        var s_FramedNodes = new List<int>();
        var s_KindCensus = new Dictionary<string, int>(StringComparer.Ordinal);
        var s_SwizzleSingle = 0;
        var s_Ratios = new List<double>();
        var s_Framed = 0;

        // Verdict split by whether the shader HAS branches. Without this, "465 equivalent" says nothing about
        // whether the if-conversion is trustworthy: a shader whose branch the test never exercises passes for
        // reasons that have nothing to do with the translation being right.
        var s_Verdicts = new Dictionary<string, int>(StringComparer.Ordinal);
        var s_Branchy = new Dictionary<string, int>(StringComparer.Ordinal);
        var s_Rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var s_GapSets = new List<List<string>>();
        var s_GapNames = new List<string>();
        var s_BlankNames = new List<string>();
        var s_RejectionNames = new List<string>();
        var s_UnstableNames = new List<string>();

        foreach (var s_Row in s_Rows)
        {
            var s_Folder = Path.Combine(p_Root, s_Row[0]);
            if (!Directory.Exists(s_Folder))
                continue;

            var s_Files = new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").ToList();
            if (s_Files.Count == 0)
                continue;

            var s_Chosen = RimeShaderEditor.Emit.ShaderIndex.ChooseGBufferPermutation(s_Files, new List<string>());
            var s_Short = Path.Combine(s_Work, "s.dxbc");
            var s_Asm = Path.Combine(s_Work, "s.asm");
            File.Copy(s_Chosen.FullName, s_Short, true);
            if (File.Exists(s_Asm))
                File.Delete(s_Asm);

            RunProcess(s_Fxc, new[] { "/nologo", "/dumpbin", s_Short, "/Fc", s_Asm });
            if (!File.Exists(s_Asm))
            {
                s_Broken++;
                continue;
            }

            ShaderContract? s_Contract = null;
            try { s_Contract = ShaderContract.Detect(File.ReadAllBytes(s_Short)); }
            catch { }

            var s_Instructions = RimeShaderEditor.Translate.DxbcAsm.Parse(File.ReadAllLines(s_Asm));

            // Feed each interpolator what THIS shader treats it as - same classification the editor and the
            // single diff apply, so all three harnesses render with the same vertex shader. Empty for the
            // measured families, which keeps every guardian's input bit-identical.
            s_Contract?.ClassifyInterpolators(s_Instructions);
            s_Preview.SetInterpolatorMeanings(s_Contract?.InterpolatorMeanings);
            var s_HasBranch = s_Instructions.Any(p_I =>
                p_I.Opcode is "if_nz" or "if_z" or "movc" or "discard_nz" or "discard_z");

            void Verdict(string p_Verdict)
            {
                s_Verdicts[p_Verdict] = s_Verdicts.GetValueOrDefault(p_Verdict) + 1;
                if (s_HasBranch)
                    s_Branchy[p_Verdict] = s_Branchy.GetValueOrDefault(p_Verdict) + 1;
            }
            var s_Builder = new RimeShaderEditor.Translate.GraphBuilder(s_Contract);
            var s_Graph = s_Builder.Build(s_Instructions, s_Row[1], s_Row[1].Split('/').Last());

            // Census over every graph the sweep builds: which node kinds the translator actually emits, and
            // the shapes that read as literalisms (a Swizzle carrying a single channel should not exist - that
            // is a MixOut pin). This is the audit that keeps "how graphs display" measured instead of assumed.
            foreach (var s_CensusNode in s_Graph.Nodes)
            {
                s_KindCensus[s_CensusNode.Kind] = s_KindCensus.GetValueOrDefault(s_CensusNode.Kind) + 1;
                if (s_CensusNode.Kind == "Swizzle" &&
                    s_CensusNode.GetParam("Channels").Distinct().Count() <= 1)
                    s_SwizzleSingle++;
            }

            // A known gap is not a wrong answer: it is a shader this editor already says it cannot do exactly.
            // Counting those as failures would drown the ones that are genuinely mistranslated. Which gap it was
            // is tallied, because that tally is the work list for whatever comes next.
            if (s_Builder.UntranslatedOpcodes.Count > 0 || s_Builder.UnmodelledConstants.Count > 0)
            {
                s_Gap++;
                Verdict("gap");
                foreach (var s_Opcode in s_Builder.UntranslatedOpcodes)
                    s_GapOpcodes[s_Opcode] = s_GapOpcodes.GetValueOrDefault(s_Opcode) + 1;

                foreach (var s_Buffer in s_Builder.UnmodelledConstants.Select(p_C => p_C.Split('[')[0]).Distinct())
                    s_GapConstants[s_Buffer] = s_GapConstants.GetValueOrDefault(s_Buffer) + 1;

                // ⛔ The SET of gaps per shader, not just a tally per gap. Fixing the most frequent feature can
                // unlock nothing at all if every shader that needs it also needs a second one - which is exactly
                // what happened with sample_l_indexable: 141 shaders, zero gained, because all of them also read
                // cb1. Only the combinations say what is worth building.
                var s_Set = s_Builder.UntranslatedOpcodes
                    .Concat(s_Builder.UnmodelledConstants.Select(p_C => p_C.Split('[')[0]))
                    .Distinct().OrderBy(p_G => p_G, StringComparer.Ordinal).ToList();

                s_GapSets.Add(s_Set);

                // And WHICH shaders, by folder, so the next feature can be read on real examples instead of
                // hunted for. The set-cover above says what to build; this says where to go and look at it.
                s_GapNames.Add($"  GAP  {s_Row[0]}  [{string.Join(" ", s_Set)}]  " +
                               $"{s_Row[1]}  ({s_Instructions.Count} instr, {s_Chosen.Name})");

                continue;
            }

            var s_Emit = new HlslEmitter { Contract = s_Contract }.Emit(s_Graph);
            if (!s_Emit.Ok)
            {
                s_Broken++;
                s_Failures.Add($"  EMIT      {s_Row[1]}: {string.Join("; ", s_Emit.Errors)}");
                continue;
            }

            if (!s_Preview.SetAuthoredShader(File.ReadAllBytes(s_Short)))
            {
                // The GAME's own shader will not bind to the preview's vertex stage - terrain, vegetation and
                // skinning declare interpolators this rig does not produce. Nothing about our translation.
                s_Signature++;
                Verdict("signature");
                continue;
            }

            s_Preview.Render();
            var s_OriginalTargets = CaptureAll(s_Preview);

            byte[][] s_OurTargets;
            try
            {
                using var s_Compiled = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    s_Emit.Hlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3);

                if (s_Compiled.Bytecode == null || !s_Preview.SetAuthoredShader(s_Compiled.Bytecode.Data))
                {
                    s_Broken++;
                    s_Failures.Add($"  REJECTED  {s_Row[1]}: {s_Preview.LastError}");
                    continue;
                }

                s_Preview.Render();
                s_OurTargets = CaptureAll(s_Preview);
            }
            catch (Exception s_Exception)
            {
                s_Broken++;
                s_Failures.Add($"  COMPILE   {s_Row[1]}: {s_Exception.Message.Split('\n')[0].Trim()}");

                // Dumped exactly like a mistranslation: a compile failure is just as reproducible and just as
                // much a defect. Hunting for "which permutation was it" by hand picks a different one and
                // reproduces nothing.
                Dump(s_Work, s_Row, s_Graph, s_Emit.Hlsl, s_Short, s_Asm);
                continue;
            }

            var s_Worst = WorstDelta(s_OriginalTargets, s_OurTargets,
                s_Contract?.RenderTargets ?? 4, out var s_Covered);
            if (s_Covered == 0)
            {
                // ⚠ A DIFFERENT situation from a signature mismatch, and lumping the two together hid which one
                // was growing: here the shader binds fine but writes nothing on this cube, so a match would be
                // the empty test rather than a pass.
                s_Blank++;
                Verdict("blank");

                // Named, like the gaps: "44 shaders the harness cannot speak about" is a number nobody can act
                // on, and the list is what says whether they are one family or forty accidents.
                s_BlankNames.Add($"  BLANK {s_Row[0]}  {s_Row[1]}  ({s_Instructions.Count} instr, " +
                                 $"{s_Chosen.Name}, {s_Contract?.RenderTargets ?? 4} RT)");

                continue;
            }

            if (s_Worst <= 1)
            {
                s_Equivalent++;
                Verdict("equivalent");

                // How compact the result actually is, and whether the frame matcher fired. Without this the
                // "it collapses to ten nodes" claim rests on one shader.
                s_NodeCounts.Add(s_Graph.Nodes.Count);
                s_Ratios.Add(s_Graph.Nodes.Count / (double) Math.Max(1, s_Instructions.Count));
                if (s_Builder.FrameRecognised)
                {
                    s_Framed++;
                    s_FramedNodes.Add(s_Graph.Nodes.Count);
                }
                else
                {
                    s_Rejections[s_Builder.FrameRejection] =
                        s_Rejections.GetValueOrDefault(s_Builder.FrameRejection) + 1;

                    // Named per shader, because the histogram alone cannot hand over an EXAMPLE: widening a
                    // check starts by reading three shaders from its bucket, and hunting those by hand is how
                    // an afternoon disappears.
                    s_RejectionNames.Add($"{s_Row[0]}\t{s_Row[1]}\t{s_Instructions.Count}\t" +
                                         $"{s_Chosen.Name}\t{s_Builder.FrameRejection}");
                }
            }
            else if (CompilationSpread(s_Preview, s_Emit.Hlsl, s_OriginalTargets, s_OurTargets,
                         s_Contract?.RenderTargets ?? 4) is var s_Spread && s_Spread.Closest <= 1)
            {
                // A build of OUR graph reproduces the game exactly, so the graph computes the game's function
                // and the residual at -O3 is a contraction choice. That is equivalence, not an excuse.
                s_Equivalent++;
                Verdict("equivalent");
                s_NodeCounts.Add(s_Graph.Nodes.Count);
                s_Ratios.Add(s_Graph.Nodes.Count / (double) Math.Max(1, s_Instructions.Count));
                if (s_Builder.FrameRecognised)
                {
                    s_Framed++;
                    s_FramedNodes.Add(s_Graph.Nodes.Count);
                }
                else
                {
                    s_Rejections[s_Builder.FrameRejection] =
                        s_Rejections.GetValueOrDefault(s_Builder.FrameRejection) + 1;
                }

                s_UnstableNames.Add($"  EXACT-UNDER-STRICT {s_Row[0]}  {s_Row[1]}: -O3 differs {s_Worst}/255, " +
                                    $"but an IEEE-strict build of the same graph matches the game");
            }
            else if (s_Spread.Noise >= s_Worst)
            {
                // ⛔⛔ NOT A THRESHOLD I PICKED - a per-shader MEASUREMENT of how far this shader moves when the
                // SAME maths is compiled under a different floating-point contraction policy. Two sky shaders
                // subtract two numbers of magnitude 4.06e13 that differ by 2e7, which leaves about 20% of the
                // result inside one float32 ULP of the operands: whether the compiler fuses a multiply-add or
                // rounds twice changes the answer by more than the whole difference against the game. Measured,
                // our own shader against itself at IEEE strictness moves R5 G3 B3 on one and R7 G5 B4 on the
                // other - the SAME per-channel maxima as the game-versus-us difference, on both.
                //
                // So this bucket says "below the resolution of this test", which is a different claim from
                // "equivalent" and is never folded into it: it is counted apart and every member is named, so a
                // real defect hiding under a shader's own noise is at least visible as a shader nobody verified.
                s_Unstable++;
                Verdict("unstable");
                s_UnstableNames.Add($"  UNSTABLE {s_Row[0]}  {s_Row[1]}: differs {s_Worst}/255 from the game, " +
                                    $"and {s_Spread.Noise}/255 from ITSELF recompiled ({s_Instructions.Count} instr)");
            }
            else
            {
                s_Different++;
                Verdict("different");

                // The two pictures, side by side, plus the amplified difference. Four hypotheses about this
                // group died reasoning about numbers; a rendered image answers questions nobody thought to ask.
                DumpImages(Path.Combine(s_Work, "failures", s_Row[0]), s_OriginalTargets, s_OurTargets);
                s_Failures.Add($"  DIFFERENT {s_Row[1]}: worst channel {s_Worst}/255 " +
                               $"({s_Instructions.Count} instr, {s_Chosen.Name})");

                // The exact inputs of the failure, kept so it can be reproduced one at a time instead of being
                // re-guessed: the sweep and the single-shader diff disagreed on these, and a disagreement
                // between two harnesses is only settled by running both on the identical artefacts.
                Dump(s_Work, s_Row, s_Graph, s_Emit.Hlsl, s_Short, s_Asm);
            }
        }

        Console.Out.WriteLine($"swept {s_Rows.Count} shader(s) from {p_Root}");
        Console.Out.WriteLine($"  EQUIVALENT to their own bytecode : {s_Equivalent}");
        Console.Out.WriteLine($"  DIFFERENT  (mistranslated)       : {s_Different}");
        Console.Out.WriteLine($"  known gap  (opcode/constant)     : {s_Gap}");
        Console.Out.WriteLine($"  not comparable (input signature) : {s_Signature}");
        Console.Out.WriteLine($"  below the test's own precision   : {s_Unstable}");
        Console.Out.WriteLine($"  wrote nothing on the test mesh    : {s_Blank}");
        Console.Out.WriteLine($"  failed to emit/compile           : {s_Broken}");

        if (s_NodeCounts.Count > 0)
        {
            s_NodeCounts.Sort();
            Console.Out.WriteLine($"  nodes per equivalent graph : median {s_NodeCounts[s_NodeCounts.Count / 2]}, " +
                                  $"min {s_NodeCounts[0]}, max {s_NodeCounts[^1]}, " +
                                  $"mean ratio {s_Ratios.Average():0.00} nodes/instruction");

            Console.Out.WriteLine($"  standard frame recognised  : {s_Framed} of {s_NodeCounts.Count}" +
                                  (s_FramedNodes.Count > 0
                                      ? $" (median {s_FramedNodes.OrderBy(p_N => p_N).ElementAt(s_FramedNodes.Count / 2)} nodes)"
                                      : ""));
        }

        if (s_KindCensus.Count > 0)
        {
            Console.Out.WriteLine("  node census (all translated graphs): " + string.Join(", ", s_KindCensus
                .OrderByDescending(p_K => p_K.Value)
                .Select(p_K => $"{p_K.Key} {p_K.Value}")));

            Console.Out.WriteLine($"  single-channel Swizzles (0 = every channel read is a MixOut pin): " +
                                  $"{s_SwizzleSingle}");
        }

        if (s_GapSets.Count > 0)
        {
            // What each feature would ACTUALLY unlock: shaders whose ONLY remaining gap is that one.
            var s_Alone = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s_Set in s_GapSets.Where(p_S => p_S.Count == 1))
                s_Alone[s_Set[0]] = s_Alone.GetValueOrDefault(s_Set[0]) + 1;

            Console.Out.WriteLine($"  shaders blocked by exactly ONE feature: {s_GapSets.Count(p_S => p_S.Count == 1)}" +
                                  $" of {s_GapSets.Count}");

            foreach (var s_Entry in s_Alone.OrderByDescending(p_E => p_E.Value).Take(8))
                Console.Out.WriteLine($"    unlock x{s_Entry.Value,-4} if only '{s_Entry.Key}' were supported");

            // Greedy: repeatedly add the feature that frees the most shaders once the chosen ones are supported.
            var s_Chosen = new HashSet<string>(StringComparer.Ordinal);
            for (var s_Round = 0; s_Round < 6; s_Round++)
            {
                var s_Best = s_GapSets.SelectMany(p_S => p_S).Distinct()
                    .Where(p_F => !s_Chosen.Contains(p_F))
                    .Select(p_F => (Feature: p_F, Freed: s_GapSets.Count(p_S =>
                        p_S.Contains(p_F) && p_S.All(p_G => p_G == p_F || s_Chosen.Contains(p_G)))))
                    .OrderByDescending(p_C => p_C.Freed).FirstOrDefault();

                if (s_Best.Freed <= 0)
                    break;

                s_Chosen.Add(s_Best.Feature);
                var s_Total = s_GapSets.Count(p_S => p_S.All(s_Chosen.Contains));
                Console.Out.WriteLine($"    + {s_Best.Feature,-32} -> {s_Total} shader(s) would translate " +
                                      $"(this step frees {s_Best.Freed})");
            }
        }

        Console.Out.WriteLine("  --- shaders WITH branches (if / movc / discard) ---");
        foreach (var s_Entry in s_Verdicts.OrderByDescending(p_E => p_E.Value))
            Console.Out.WriteLine($"    {s_Entry.Key,-12} {s_Branchy.GetValueOrDefault(s_Entry.Key),4} of {s_Entry.Value,4}");

        foreach (var s_Reason in s_Rejections.OrderByDescending(p_R => p_R.Value))
            Console.Out.WriteLine($"  frame declined x{s_Reason.Value,-4} {s_Reason.Key}");

        if (s_GapOpcodes.Count > 0)
            Console.Out.WriteLine("  gap by opcode   : " + string.Join(", ", s_GapOpcodes
                .OrderByDescending(p_G => p_G.Value).Select(p_G => $"{p_G.Key} x{p_G.Value}")));

        if (s_GapConstants.Count > 0)
            Console.Out.WriteLine("  gap by cbuffer  : " + string.Join(", ", s_GapConstants
                .OrderByDescending(p_G => p_G.Value).Select(p_G => $"{p_G.Key} x{p_G.Value}")));

        if (s_RejectionNames.Count > 0)
        {
            var s_RejectionPath = Path.Combine(s_Work, "rejections.txt");
            File.WriteAllLines(s_RejectionPath, s_RejectionNames);
            Console.Out.WriteLine($"  per-shader frame rejections -> {s_RejectionPath}");
        }

        foreach (var s_Line in s_UnstableNames.Take(25))
            Console.Out.WriteLine(s_Line);

        foreach (var s_Line in s_BlankNames.Take(50))
            Console.Out.WriteLine(s_Line);

        foreach (var s_Line in s_GapNames.Take(45))
            Console.Out.WriteLine(s_Line);

        if (s_GapNames.Count > 45)
            Console.Out.WriteLine($"  ... and {s_GapNames.Count - 45} more gap shader(s)");

        foreach (var s_Line in s_Failures.Take(25))
            Console.Out.WriteLine(s_Line);

        Console.Out.WriteLine(s_Different == 0
            ? "SWEEP: PASS - every comparable shader translated to an equivalent graph."
            : $"SWEEP: FAIL - {s_Different} shader(s) translated WRONG.");

        return s_Different == 0 ? 0 : 1;
    }

    /// <summary>
    /// Worst per-channel difference over the pixels the original actually covered, across the render targets the
    /// original actually DECLARES.
    ///
    /// ⛔ Comparing all four regardless was wrong and it produced 18 false failures: a forward or decal shader
    /// declares one target, so targets 1-3 hold undefined memory rather than zeros. Our graph writes 0 there,
    /// the game's shader writes nothing, and the difference is real but meaningless - it is a comparison of
    /// values the hardware never promised. Undefined is not zero, and a test must not assert on it.
    /// </summary>
    /// <summary>
    /// Renders the node canvas with a graph on it, to a PNG, without opening a window.
    ///
    /// "The nodes no longer overlap" is a claim about a PICTURE, and a picture is the only thing that settles it:
    /// a node count or a coordinate dump would read fine while the graph was still unusable, which is the state
    /// the user had to drag nodes apart to get out of before anything about it could be told.
    /// </summary>
    /// <summary>
    /// Exercises the comment boxes through the same public paths the keyboard and mouse handlers call:
    /// select-all + C-key grouping, a title-bar drag (MoveGroup), rename, JSON round-trip - then renders the
    /// result to a PNG, because whether a comment box READS well is a question only a picture answers.
    /// </summary>
    private static int GroupTest(string p_GraphPath, string p_OutputPath)
    {
        var s_Canvas = new View.GraphCanvas
        {
            Graph = ShaderGraph.FromJson(File.ReadAllText(p_GraphPath)),
            Width = 1600,
            Height = 1000,
        };

        var s_Graph = s_Canvas.Graph;
        if (s_Graph.Nodes.Count == 0)
        {
            Console.Error.WriteLine("FAIL: the graph has no nodes to group.");
            return 1;
        }

        // 1. Group the whole selection, as the C key does.
        s_Canvas.SelectAll();
        var s_Group = s_Canvas.GroupSelection(new System.Windows.Point(0, 0));

        var s_Wraps = s_Graph.Nodes.All(p_N =>
            p_N.X >= s_Group.X && p_N.Y >= s_Group.Y &&
            p_N.X + 172 <= s_Group.X + s_Group.Width && p_N.Y <= s_Group.Y + s_Group.Height);

        Console.Out.WriteLine($"group wraps all {s_Graph.Nodes.Count} node(s): {(s_Wraps ? "OK" : "FAIL")}");

        // 2. Drag by the title bar: the box AND its contents move together.
        var s_Before = s_Graph.Nodes.ToDictionary(p_N => p_N.Id, p_N => (p_N.X, p_N.Y));
        s_Canvas.MoveGroup(s_Group, new System.Windows.Vector(80, 48));
        var s_Moved = s_Graph.Nodes.All(p_N =>
            Math.Abs(p_N.X - s_Before[p_N.Id].X - 80) < 0.01 && Math.Abs(p_N.Y - s_Before[p_N.Id].Y - 48) < 0.01);

        Console.Out.WriteLine($"title-bar drag carried every node: {(s_Moved ? "OK" : "FAIL")}");

        // 3. Rename + colour + a node comment, then prove the file format keeps all of it. The comment goes on
        // the topmost node so its bubble floats over the box's title strip, not over another node.
        s_Group.Title = "Normal chain";
        s_Group.Colour = 3;
        var s_Commented = s_Graph.Nodes.OrderBy(p_N => p_N.Y).First();
        s_Commented.Comment = "Comment Test";

        var s_Reloaded = ShaderGraph.FromJson(s_Graph.ToJson());
        var s_Survives = s_Reloaded.Groups.Count == 1 &&
                         s_Reloaded.Groups[0].Title == "Normal chain" &&
                         s_Reloaded.Groups[0].Colour == 3 &&
                         Math.Abs(s_Reloaded.Groups[0].X - s_Group.X) < 0.01 &&
                         s_Reloaded.FindNode(s_Commented.Id)?.Comment == "Comment Test";

        Console.Out.WriteLine($"JSON round-trip keeps the group and the node comment: {(s_Survives ? "OK" : "FAIL")}");

        // 4. The picture. Framing goes BETWEEN Measure and Arrange: Measure resets the viewport to centre and
        // Arrange records the retained drawing, so framing applied any later never reaches the bitmap.
        var s_Size = new System.Windows.Size(1600, 1000);
        s_Canvas.Measure(s_Size);
        s_Canvas.FrameGraph(1600, 1000);
        s_Canvas.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), s_Size));
        s_Canvas.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            1600, 1000, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        s_Bitmap.Render(s_Canvas);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using (var s_Stream = File.Create(p_OutputPath))
            s_Encoder.Save(s_Stream);

        // 5. Prove the framing reached the PIXELS, not just the fields. The title bar is opaque colour, so the
        // pixel at its centre must differ clearly from the canvas background (37,37,38). A stale render pass
        // leaves background there and this check fails even while every field looks right.
        var s_TitleOnScreen = s_Canvas.ScreenRectOf(
            new System.Windows.Rect(s_Group.X, s_Group.Y, s_Group.Width, 22));
        var s_GroupOnScreen = s_Canvas.ScreenRectOf(
            new System.Windows.Rect(s_Group.X, s_Group.Y, s_Group.Width, s_Group.Height));
        var s_OnScreen = s_GroupOnScreen.Left >= 0 && s_GroupOnScreen.Top >= 0 &&
                         s_GroupOnScreen.Right <= 1600 && s_GroupOnScreen.Bottom <= 1000;

        var s_Pixel = new byte[4];
        s_Bitmap.CopyPixels(new System.Windows.Int32Rect(
            (int) (s_TitleOnScreen.X + s_TitleOnScreen.Width * 0.5),
            (int) (s_TitleOnScreen.Y + s_TitleOnScreen.Height * 0.5), 1, 1), s_Pixel, 4, 0);

        // The test sets colour 3 (brick, 140/85/85 at alpha 210), so the title pixel must be clearly
        // red-dominant - the background, grid lines and node chrome are all neutral. A "differs from
        // background" check alone once passed by landing on a bright grid line.
        var s_Painted = s_Pixel[2] > 90 && s_Pixel[2] - s_Pixel[1] > 25;
        Console.Out.WriteLine(
            $"framing reached the pixels (probe at {(int) (s_TitleOnScreen.X + s_TitleOnScreen.Width * 0.5)}," +
            $"{(int) (s_TitleOnScreen.Y + s_TitleOnScreen.Height * 0.5)}, " +
            $"group on screen at {(int) s_GroupOnScreen.X},{(int) s_GroupOnScreen.Y} " +
            $"{(int) s_GroupOnScreen.Width}x{(int) s_GroupOnScreen.Height}, " +
            $"title centre = {s_Pixel[2]},{s_Pixel[1]},{s_Pixel[0]}, " +
            $"group fully on screen: {s_OnScreen}): {(s_OnScreen && s_Painted ? "OK" : "FAIL")}");

        // 6. The comment bubble, probed against ITS expected paint - on a dedicated 1:1 render pointed at the
        // bubble, because in the framed shot a big graph shrinks the bubble to a few pixels where border and
        // anti-aliasing drown the fill and the probe measures its own resolution instead of the paint. A
        // single-pixel probe would land on the bubble's own dark text, so measure the BULK: the pale fill
        // (238/238/230) must dominate the bubble area. Nothing else pale covers that region.
        var s_Zoomed = new View.GraphCanvas { Graph = s_Graph, Width = 1600, Height = 1000 };
        s_Zoomed.Measure(s_Size);
        s_Zoomed.LookAt(s_Zoomed.CommentBubbleRect(s_Commented), 1600, 1000);
        s_Zoomed.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), s_Size));
        s_Zoomed.UpdateLayout();

        var s_ZoomBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            1600, 1000, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        s_ZoomBitmap.Render(s_Zoomed);

        var s_BubbleOnScreen = s_Zoomed.ScreenRectOf(s_Zoomed.CommentBubbleRect(s_Commented));
        var s_BubbleArea = new System.Windows.Int32Rect(
            Math.Max(0, (int) s_BubbleOnScreen.X), Math.Max(0, (int) s_BubbleOnScreen.Y),
            Math.Max(1, (int) s_BubbleOnScreen.Width), Math.Max(1, (int) s_BubbleOnScreen.Height));

        var s_BubblePixels = new byte[s_BubbleArea.Width * s_BubbleArea.Height * 4];
        s_ZoomBitmap.CopyPixels(s_BubbleArea, s_BubblePixels, s_BubbleArea.Width * 4, 0);

        var s_Pale = 0;
        for (var i = 0; i < s_BubblePixels.Length; i += 4)
            if (s_BubblePixels[i + 2] > 200 && s_BubblePixels[i + 1] > 200 && s_BubblePixels[i] > 195)
                s_Pale++;

        var s_PaleFraction = s_Pale / (double) (s_BubbleArea.Width * s_BubbleArea.Height);
        var s_BubblePainted = s_PaleFraction >= 0.4;
        Console.Out.WriteLine(
            $"comment bubble reached the pixels (pale fill covers {s_PaleFraction:P0} of " +
            $"{s_BubbleArea.Width}x{s_BubbleArea.Height} at 1:1): {(s_BubblePainted ? "OK" : "FAIL")}");

        // 7. Copy/paste carries the box and the node comment; deleting a selected box (the toolbar path)
        // removes the box ALONE. Runs after the picture so the shot shows the untouched graph. Paste drops a
        // SECOND root by design (an ambiguous target set otherwise), so the expected count subtracts it.
        var s_NodesBefore = s_Graph.Nodes.Count;
        var s_Roots = s_Graph.Nodes.Count(p_N => p_N.Def.IsRoot);
        var s_ExpectedNodes = s_NodesBefore * 2 - s_Roots;
        var s_ExpectedComments = s_Commented.Def.IsRoot ? 1 : 2;

        s_Canvas.SelectAll();
        var s_PasteOk = s_Canvas.CopySelection() && s_Canvas.PasteSelection() &&
                        s_Graph.Nodes.Count == s_ExpectedNodes && s_Graph.Groups.Count == 2 &&
                        s_Graph.Nodes.Count(p_N => p_N.Comment == "Comment Test") == s_ExpectedComments;

        Console.Out.WriteLine($"copy/paste cloned the nodes and carried box + comment " +
                              $"({s_Graph.Nodes.Count}/{s_ExpectedNodes} nodes, {s_Graph.Groups.Count}/2 boxes): " +
                              $"{(s_PasteOk ? "OK" : "FAIL")}");

        s_Canvas.SelectGroup(s_Graph.Groups[^1]);
        s_Canvas.DeleteSelected();
        var s_DeleteOk = s_Graph.Groups.Count == 1 && s_Graph.Nodes.Count == s_ExpectedNodes;
        Console.Out.WriteLine($"deleting a selected box removes it alone: {(s_DeleteOk ? "OK" : "FAIL")}");

        Console.Out.WriteLine($"rendered -> {p_OutputPath}");
        var s_Pass = s_Wraps && s_Moved && s_Survives && s_OnScreen && s_Painted && s_BubblePainted &&
                     s_PasteOk && s_DeleteOk;
        Console.Out.WriteLine(s_Pass ? "GROUPTEST: PASS" : "GROUPTEST: FAIL");
        return s_Pass ? 0 : 1;
    }

    private static int GraphShot(string p_GraphPath, string p_OutputPath, int p_Width, int p_Height)
    {
        var s_Canvas = new View.GraphCanvas
        {
            Graph = ShaderGraph.FromJson(File.ReadAllText(p_GraphPath)),
            Width = p_Width,
            Height = p_Height,
        };

        // Framing goes BETWEEN Measure and Arrange: Measure resets the viewport and Arrange records the
        // retained drawing, so framing applied any later never reaches the bitmap.
        var s_Size = new System.Windows.Size(p_Width, p_Height);
        s_Canvas.Measure(s_Size);
        s_Canvas.FrameGraph(p_Width, p_Height);
        s_Canvas.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), s_Size));
        s_Canvas.UpdateLayout();

        var s_Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            p_Width, p_Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

        s_Bitmap.Render(s_Canvas);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using (var s_Stream = File.Create(p_OutputPath))
            s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"{s_Canvas.Graph.Nodes.Count} node(s) rendered -> {p_OutputPath}");
        return 0;
    }

    /// <summary>
    /// Writes what the game's shader drew, what ours drew, and the difference amplified 8x, per render target.
    /// A 255/255 delta in a column of numbers says "wrong"; the picture says WHERE and in what shape.
    /// </summary>
    private static void DumpImages(string p_Directory, byte[][] p_Original, byte[][] p_Ours)
    {
        try
        {
            Directory.CreateDirectory(p_Directory);
            var s_Side = (int) Math.Round(Math.Sqrt(p_Original[0].Length / 4.0));

            for (var t = 0; t < 4; t++)
            {
                // ⛔ Alpha forced opaque. A render target's own alpha is DATA, not transparency, and writing it
                // as transparency made both pictures come out blank-white while the difference image showed a
                // coloured cube - a contradiction that was my own visualisation lying, not the shaders agreeing.
                // The alpha channel gets its own greyscale image instead of silently erasing the other three.
                WritePng(Path.Combine(p_Directory, $"rt{t}_game.png"), Opaque(p_Original[t]), s_Side);
                WritePng(Path.Combine(p_Directory, $"rt{t}_ours.png"), Opaque(p_Ours[t]), s_Side);
                WritePng(Path.Combine(p_Directory, $"rt{t}a_game.png"), AlphaOnly(p_Original[t]), s_Side);
                WritePng(Path.Combine(p_Directory, $"rt{t}a_ours.png"), AlphaOnly(p_Ours[t]), s_Side);

                var s_Delta = new byte[p_Original[t].Length];
                for (var i = 0; i < s_Delta.Length; i++)
                    s_Delta[i] = i % 4 == 3
                        ? (byte) 255
                        : (byte) Math.Min(255, Math.Abs(p_Original[t][i] - p_Ours[t][i]) * 8);

                WritePng(Path.Combine(p_Directory, $"rt{t}_diff8x.png"), s_Delta, s_Side);
            }
        }
        catch { }
    }

    private static byte[] Opaque(byte[] p_Rgba)
    {
        var s_Result = (byte[]) p_Rgba.Clone();
        for (var i = 3; i < s_Result.Length; i += 4)
            s_Result[i] = 255;

        return s_Result;
    }

    /// <summary>The alpha channel as greyscale, so it can be looked at instead of disappearing.</summary>
    private static byte[] AlphaOnly(byte[] p_Rgba)
    {
        var s_Result = new byte[p_Rgba.Length];
        for (var i = 0; i + 3 < p_Rgba.Length; i += 4)
        {
            s_Result[i] = s_Result[i + 1] = s_Result[i + 2] = p_Rgba[i + 3];
            s_Result[i + 3] = 255;
        }

        return s_Result;
    }

    private static void WritePng(string p_Path, byte[] p_Rgba, int p_Side)
    {
        var s_Bitmap = System.Windows.Media.Imaging.BitmapSource.Create(p_Side, p_Side, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, Swizzled(p_Rgba), p_Side * 4);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Bitmap));
        using var s_Stream = File.Create(p_Path);
        s_Encoder.Save(s_Stream);
    }

    /// <summary>The captures are RGBA; WPF wants BGRA, and getting that backwards would recolour the evidence.</summary>
    private static byte[] Swizzled(byte[] p_Rgba)
    {
        var s_Result = new byte[p_Rgba.Length];
        for (var i = 0; i + 3 < p_Rgba.Length; i += 4)
        {
            s_Result[i] = p_Rgba[i + 2];
            s_Result[i + 1] = p_Rgba[i + 1];
            s_Result[i + 2] = p_Rgba[i];
            s_Result[i + 3] = p_Rgba[i + 3];
        }

        return s_Result;
    }

    /// <summary>Everything needed to reproduce one sweep failure on its own, one directory per shader.</summary>
    private static void Dump(string p_Work, string[] p_Row, ShaderGraph p_Graph, string p_Hlsl,
        string p_Dxbc, string p_Asm)
    {
        try
        {
            var s_Dir = Path.Combine(p_Work, "failures", p_Row[0]);
            Directory.CreateDirectory(s_Dir);
            File.WriteAllText(Path.Combine(s_Dir, "graph.json"), p_Graph.ToJson());
            File.WriteAllText(Path.Combine(s_Dir, "shader.hlsl"), p_Hlsl);
            File.WriteAllText(Path.Combine(s_Dir, "name.txt"), p_Row[1]);
            File.Copy(p_Dxbc, Path.Combine(s_Dir, "original.dxbc"), true);
            File.Copy(p_Asm, Path.Combine(s_Dir, "original.asm"), true);
        }
        catch { }
    }

    /// <summary>
    /// Which pixels either shader actually wrote, across every compared target.
    ///
    /// ⛔⛔⛔ THIS USED TO BE "RT0.rgb IS NON-ZERO IN THE ORIGINAL", AND THAT IS NOT THE SAME QUESTION.
    /// It threw away two whole classes of shader as "wrote nothing on the test mesh":
    ///  · one that writes a legitimate ZERO into RT0.rgb and its real output somewhere else - a decal, a mask
    ///    pass, anything whose payload lives in RT0.w or RT2.x - so the harness could not say ANYTHING about
    ///    them even though they were perfectly comparable;
    ///  · and, worse, one where the original writes nothing and OURS writes something, which is a
    ///    mistranslation the old mask reported as "not comparable" instead of as the failure it is.
    /// Taking the UNION of both sides can only turn a blank into a verdict, never the other way round; a
    /// genuinely empty pair (both silent everywhere, e.g. a pass that discards every pixel) still reports
    /// blank, which is the only case where the comparison really is vacuous.
    /// Off-object this stays false for free: the targets are cleared to (0,0,0,0) and the pixel shader never
    /// runs there.
    /// </summary>
    /// <summary>
    /// How far a shader moves when the SAME HLSL is compiled with a different floating-point policy - the
    /// resolution limit of the differential test FOR THAT SHADER, measured rather than assumed.
    ///
    /// IEEE strictness is the right second setting because it is exactly the freedom the two programs were
    /// never guaranteed to use the same way: contracting a multiply and an add into one rounding, and
    /// reassociating sums. On a numerically stable shader this returns 0 and changes nothing.
    /// Returns 0 if the strict compile fails, so a failure here can only make the verdict STRICTER.
    /// </summary>
    /// <summary>
    /// Recompiles our own HLSL under the floating-point policies that -O3 is free to differ on, and reports
    /// both things worth knowing: how far the shader moves against ITSELF, and how close the CLOSEST of those
    /// builds gets to the game.
    ///
    /// ★★★ The second number is the one that settles the question, and it took asking it properly to see it.
    /// "Our -O3 build differs from the game by 4" and "our -O3 build differs from itself by 3" are two
    /// distances that have to be compared to conclude anything, and at the edge that comparison flips on a
    /// single LSB. But "SOME equivalent compilation of our graph reproduces the game exactly" is a direct
    /// statement about the graph: if it holds, the graph computes the game's function and the residual is the
    /// compiler's choice of contraction, not a translation error.
    /// </summary>
    private static (int Noise, int Closest) CompilationSpread(RimeShaderEditor.View.ShaderPreview p_Preview,
        string p_Hlsl, byte[][] p_Original, byte[][] p_Ours, int p_Targets)
    {
        var s_Noise = 0;
        var s_Closest = WorstDelta(p_Original, p_Ours, p_Targets, out _);

        foreach (var s_Flags in new[]
                 {
                     SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3 |
                     SharpDX.D3DCompiler.ShaderFlags.IeeeStrictness,
                     SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel0,
                     SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel1,
                 })
        {
            try
            {
                using var s_Variant = SharpDX.D3DCompiler.ShaderBytecode.Compile(p_Hlsl, "main", "ps_5_0",
                    s_Flags);

                if (s_Variant.Bytecode == null || !p_Preview.SetAuthoredShader(s_Variant.Bytecode.Data))
                    continue;

                p_Preview.Render();
                var s_Targets = CaptureAll(p_Preview);
                s_Noise = Math.Max(s_Noise, WorstDelta(p_Ours, s_Targets, p_Targets, out _));
                s_Closest = Math.Min(s_Closest, WorstDelta(p_Original, s_Targets, p_Targets, out _));
            }
            catch
            {
                // A compile that fails here can only make the verdict stricter, which is the safe direction.
            }
        }

        return (s_Noise, s_Closest);
    }

    private static int SelfNoise(RimeShaderEditor.View.ShaderPreview p_Preview, string p_Hlsl, byte[][] p_Ours,
        int p_Targets)
    {
        // ⚠ The quantity wanted is the SPREAD across equivalent compilations, and one alternative under-samples
        // it: with a single strict build, a sky shader measured 3 while it differed from the game by 4 and got
        // filed as mistranslated, purely because that one sample missed the wider end of its own range. Two
        // settings that disagree with -O3 in different ways give a better estimate of the same thing. This is
        // widening the MEASUREMENT, not loosening the verdict - both are still measured per shader.
        var s_Worst = 0;

        foreach (var s_Flags in new[]
                 {
                     SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3 |
                     SharpDX.D3DCompiler.ShaderFlags.IeeeStrictness,
                     SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel0,
                 })
        {
            try
            {
                using var s_Variant = SharpDX.D3DCompiler.ShaderBytecode.Compile(p_Hlsl, "main", "ps_5_0",
                    s_Flags);

                if (s_Variant.Bytecode == null || !p_Preview.SetAuthoredShader(s_Variant.Bytecode.Data))
                    continue;

                p_Preview.Render();
                s_Worst = Math.Max(s_Worst, WorstDelta(p_Ours, CaptureAll(p_Preview), p_Targets, out _));
            }
            catch
            {
                // A compile that fails here can only make the verdict stricter, which is the safe direction.
            }
        }

        return s_Worst;
    }

    private static bool[] Coverage(byte[][] p_Original, byte[][] p_Ours, int p_Targets, out int p_Covered)
    {
        var s_Compared = Math.Clamp(p_Targets, 1, 4);
        var s_Mask = new bool[p_Original[0].Length / 4];
        p_Covered = 0;

        for (var i = 0; i < s_Mask.Length; i++)
        {
            var s_Base = i * 4;
            for (var t = 0; t < s_Compared && !s_Mask[i]; t++)
            for (var c = 0; c < 4 && !s_Mask[i]; c++)
                s_Mask[i] = p_Original[t][s_Base + c] != 0 || p_Ours[t][s_Base + c] != 0;

            if (s_Mask[i])
                p_Covered++;
        }

        return s_Mask;
    }

    private static int WorstDelta(byte[][] p_Original, byte[][] p_Ours, int p_Targets, out int p_Covered)
    {
        var s_Worst = 0;
        var s_Compared = Math.Clamp(p_Targets, 1, 4);
        var s_Mask = Coverage(p_Original, p_Ours, p_Targets, out p_Covered);

        for (var i = 0; i < s_Mask.Length; i++)
        {
            if (!s_Mask[i])
                continue;

            var s_Base = i * 4;
            for (var t = 0; t < s_Compared; t++)
            for (var c = 0; c < 4; c++)
                s_Worst = Math.Max(s_Worst, Math.Abs(p_Original[t][s_Base + c] - p_Ours[t][s_Base + c]));
        }

        return s_Worst;
    }

    /// <summary>
    /// Measures how REPETITIVE a level's shaders are once constants, texture slots and register allocation are
    /// normalised away.
    ///
    /// This exists because the obvious measurement — how many shaders share a bytecode hash — answers the wrong
    /// question. Two props authored from the same preset with different tiling and different textures
    /// compile to different bytecode and to the SAME graph, so a bytecode census under-reports the repetition by
    /// construction. What decides whether a node editor can ship hand-authored graphs is how many shaders share a
    /// STRUCTURE, because a structure is what one graph plus a parameter block covers.
    ///
    /// Four keys of different tightness are counted side by side, deliberately including one that is known to be
    /// too loose, so the headline number can be read against a control instead of taken on faith.
    /// </summary>
    private static int StructureCensus(string p_Root, int p_Max, string? p_Csv, bool p_AllPermutations)
    {
        var s_IndexPath = Path.Combine(p_Root, "index.txt");
        if (!File.Exists(s_IndexPath))
        {
            Console.Error.WriteLine($"ERROR: no index.txt under {p_Root}.");
            return 2;
        }

        var s_Fxc = RimeShaderEditor.MainWindow.FindSdkFxc();
        if (s_Fxc == null)
        {
            Console.Error.WriteLine("ERROR: fxc.exe not found.");
            return 2;
        }

        var s_Rows = File.ReadAllLines(s_IndexPath)
            .Select(p_L => p_L.Split('\t'))
            .Where(p_P => p_P.Length >= 2)
            .Take(p_Max)
            .ToList();

        var s_Work = Path.Combine(p_Root, "_structure");
        Directory.CreateDirectory(s_Work);

        var s_Records = new List<(string Name, string File, int Shader, int Count, int Targets, int Samples,
                                  bool Fresnel, string Loose, string Multiset, string Shape, string Dataflow,
                                  string Collapsed)>();

        var s_Broken = 0;
        var s_ShaderIndex = -1;
        foreach (var s_Row in s_Rows)
        {
            var s_Folder = Path.Combine(p_Root, s_Row[0]);
            if (!Directory.Exists(s_Folder))
                continue;

            var s_Files = new DirectoryInfo(s_Folder).GetFiles("*_ps.dxbc").ToList();
            if (s_Files.Count == 0)
                continue;

            s_ShaderIndex++;

            // One permutation per shader is the honest default — but WHICH one is a judgement call, and a
            // shader with 142 permutations and one with 2 are not being sampled comparably. "allperms" walks
            // every pixel permutation instead, so the clustering result can be checked against a run where that
            // choice was not made at all.
            var s_Batch = p_AllPermutations
                ? s_Files
                : new List<FileInfo> { RimeShaderEditor.Emit.ShaderIndex.ChooseGBufferPermutation(s_Files, new List<string>()) };

            foreach (var s_Chosen in s_Batch)
            {
                var s_Short = Path.Combine(s_Work, "s.dxbc");
                var s_Asm = Path.Combine(s_Work, "s.asm");
                File.Copy(s_Chosen.FullName, s_Short, true);
                if (File.Exists(s_Asm))
                    File.Delete(s_Asm);

                RunProcess(s_Fxc, new[] { "/nologo", "/dumpbin", s_Short, "/Fc", s_Asm });
                if (!File.Exists(s_Asm))
                {
                    s_Broken++;
                    continue;
                }

                var s_Targets = 0;
                try { s_Targets = ShaderContract.Detect(File.ReadAllBytes(s_Short)).RenderTargets; }
                catch { }

                var s_Instructions = RimeShaderEditor.Translate.DxbcAsm.Parse(File.ReadAllLines(s_Asm));
                var s_Semantics = RimeShaderEditor.Translate.StructureKey.Semantics(s_Instructions);

                s_Records.Add((
                    s_Row[1], s_Chosen.Name, s_ShaderIndex, s_Instructions.Count, s_Targets, s_Semantics.Samples,
                    s_Semantics.Fresnel,
                    RimeShaderEditor.Translate.StructureKey.OpcodeSequence(s_Instructions),
                    RimeShaderEditor.Translate.StructureKey.OpcodeMultiset(s_Instructions),
                    RimeShaderEditor.Translate.StructureKey.Shape(s_Instructions),
                    RimeShaderEditor.Translate.StructureKey.Dataflow(s_Instructions),
                    RimeShaderEditor.Translate.StructureKey.Collapsed(s_Instructions)));

                if (s_Records.Count % 250 == 0)
                    Console.Error.WriteLine($"  ... {s_Records.Count} permutations disassembled");
            }
        }

        var s_Total = s_Records.Count;
        var s_Shaders = s_Records.Select(p_R => p_R.Shader).Distinct().Count();
        Console.WriteLine($"STRUCTURE CENSUS over {s_Shaders} shaders / {s_Total} pixel permutations " +
                          $"({s_Broken} could not be disassembled). Mode: {(p_AllPermutations ? "ALL permutations" : "one g-buffer permutation per shader")}.");
        Console.WriteLine($"Root: {p_Root}");
        Console.WriteLine();

        void Report(string p_Label, string p_Note, Func<int, string> p_Key)
        {
            var s_Clusters = Enumerable.Range(0, s_Total)
                .GroupBy(p_Key, StringComparer.Ordinal)
                .OrderByDescending(p_G => p_G.Count())
                .ToList();

            Console.WriteLine($"=== {p_Label} ===");
            Console.WriteLine($"    {p_Note}");
            Console.WriteLine($"    distinct structures : {s_Clusters.Count}");
            Console.WriteLine($"    singletons          : {s_Clusters.Count(p_C => p_C.Count() == 1)} " +
                              $"({100.0 * s_Clusters.Count(p_C => p_C.Count() == 1) / s_Total:0.0}% of records)");

            // Greedy set cover over SHADERS. With one permutation per shader this is just the clusters in size
            // order; with all permutations it is the real answer to "how many graphs do I have to author", since
            // a shader is covered as soon as ONE of its permutations matches an authored structure.
            var s_Remaining = new HashSet<int>(s_Records.Select(p_R => p_R.Shader));
            var s_Sets = s_Clusters
                .Select(p_C => new HashSet<int>(p_C.Select(p_I => s_Records[p_I].Shader)))
                .ToList();

            var s_Picked = new List<(int Index, int Gain, int Cumulative)>();
            var s_Cumulative = 0;
            for (var s_Step = 0; s_Step < 100 && s_Remaining.Count > 0; s_Step++)
            {
                var s_Best = -1;
                var s_BestGain = 0;
                for (var s_I = 0; s_I < s_Sets.Count; s_I++)
                {
                    var s_Gain = s_Sets[s_I].Count(p_S => s_Remaining.Contains(p_S));
                    if (s_Gain > s_BestGain)
                    {
                        s_BestGain = s_Gain;
                        s_Best = s_I;
                    }
                }

                if (s_Best < 0)
                    break;

                s_Cumulative += s_BestGain;
                s_Picked.Add((s_Best, s_BestGain, s_Cumulative));
                s_Remaining.ExceptWith(s_Sets[s_Best]);
                s_Sets[s_Best] = new HashSet<int>();
            }

            foreach (var s_N in new[] { 1, 5, 10, 20, 50, 100 })
            {
                if (s_N > s_Picked.Count)
                    break;

                var s_Covered = s_Picked[s_N - 1].Cumulative;
                Console.WriteLine($"    top {s_N,3} structures cover {s_Covered,4}/{s_Shaders} shaders = {100.0 * s_Covered / s_Shaders,5:0.0}%");
            }

            Console.WriteLine($"    structures needed for 100% of shaders: " +
                              (s_Picked.Count > 0 && s_Picked[^1].Cumulative == s_Shaders
                                  ? s_Picked.Count.ToString()
                                  : $">100 (100 cover {(s_Picked.Count > 0 ? s_Picked[^1].Cumulative : 0)})"));

            Console.WriteLine("    largest clusters:");
            foreach (var s_Cluster in s_Clusters.Take(20))
            {
                var s_Members = s_Cluster.Select(p_I => s_Records[p_I]).ToList();
                var s_Distinct = s_Members.Select(p_M => p_M.Shader).Distinct().Count();
                Console.WriteLine($"      {s_Cluster.Count(),4} records / {s_Distinct,4} shaders | {s_Members[0].Count,3} instr | " +
                                  $"{s_Members[0].Samples} samp, {s_Members[0].Targets} RT | " +
                                  $"e.g. {Short(s_Members[0].Name)}");

                foreach (var s_Extra in s_Members.Where(p_M => p_M.Shader != s_Members[0].Shader)
                                                 .GroupBy(p_M => p_M.Shader).Take(3))
                    Console.WriteLine($"                          {Short(s_Extra.First().Name)}");
            }

            Console.WriteLine();
        }

        Report("KEY 0 - LOOSE CONTROL (deliberately wrong)",
            "opcode sequence only; no operands, no masks, no dataflow. Cannot distinguish mul(a,b) from mul(b,c).",
            p_I => s_Records[p_I].Loose);

        Report("KEY 1 - OPCODE MULTISET (coarse)",
            "unordered bag of opcodes. Same instruction budget and operation mix, any wiring.",
            p_I => s_Records[p_I].Multiset);

        Report("KEY 2 - SHAPE (the asked-for key)",
            "ordered (opcode, saturate, dest write mask, operand kinds + swizzles); literals, register indices, " +
            "cb elements and texture slots erased.",
            p_I => s_Records[p_I].Shape);

        Report("KEY 3 - DATAFLOW (strict; this is the one that decides the design)",
            "KEY 2 plus alpha-renamed wiring: temps/inputs/resources renumbered by first appearance, cb INDEX " +
            "kept, cb ELEMENT erased, o0..o3 kept literal. Same expression tree, different values and textures.",
            p_I => s_Records[p_I].Dataflow);

        Report("KEY 5 - PARAMETERISED FAMILY (loose, tests the 'preset + layer count' hypothesis)",
            "KEY 2 with adjacent repeated instruction blocks collapsed to one copy, so a 3-layer and a 5-layer " +
            "instance of the same template land together. Its clusters need a graph WITH A REPEAT, not a fixed graph.",
            p_I => s_Records[p_I].Collapsed);

        // EXACT structural equality is a cliff: one extra instruction and two shaders stop being "the same". That
        // makes it a bad instrument for "are these authored from one preset", because a preset with an optional
        // detail-normal toggle produces neighbours that are three instructions apart and share no exact key. So
        // measure the DISTANCE to the nearest other shader as well, as a bag distance over opcodes: the number of
        // instructions that would have to be added or removed to turn one shader's opcode mix into the other's.
        if (!p_AllPermutations)
        {
            var s_Bags = s_Records
                .Select(p_R => p_R.Multiset.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p_T => p_T.Split('*'))
                    .ToDictionary(p_T => p_T[0], p_T => int.Parse(p_T[1]), StringComparer.Ordinal))
                .ToList();

            var s_Nearest = new int[s_Total];
            for (var s_I = 0; s_I < s_Total; s_I++)
            {
                var s_Best = int.MaxValue;
                for (var s_J = 0; s_J < s_Total; s_J++)
                {
                    if (s_I == s_J)
                        continue;

                    var s_Distance = 0;
                    foreach (var s_Pair in s_Bags[s_I])
                        s_Distance += Math.Abs(s_Pair.Value - s_Bags[s_J].GetValueOrDefault(s_Pair.Key));

                    foreach (var s_Pair in s_Bags[s_J])
                        if (!s_Bags[s_I].ContainsKey(s_Pair.Key))
                            s_Distance += s_Pair.Value;

                    if (s_Distance < s_Best)
                        s_Best = s_Distance;
                }

                s_Nearest[s_I] = s_Best;
            }

            Console.WriteLine("=== NEAREST-NEIGHBOUR DISTANCE (opcode bag distance to the closest OTHER shader) ===");
            Console.WriteLine("    How far a shader is from its closest sibling, in instructions added+removed.");
            foreach (var s_Band in new[] { 0, 1, 2, 3, 5, 10, 20 })
            {
                var s_Within = s_Nearest.Count(p_D => p_D <= s_Band);
                Console.WriteLine($"    within {s_Band,2} instructions of another shader: {s_Within,4}/{s_Total} = {100.0 * s_Within / s_Total,5:0.0}%");
            }

            var s_Sorted = s_Nearest.OrderBy(p_D => p_D).ToArray();
            Console.WriteLine($"    median nearest-neighbour distance: {s_Sorted[s_Total / 2]} instructions " +
                              $"(p25 {s_Sorted[s_Total / 4]}, p75 {s_Sorted[s_Total * 3 / 4]}, max {s_Sorted[^1]})");
            Console.WriteLine();
        }

        // WHOLE-SHADER equality is one altitude. The altitude a node editor actually works at is the MOTIF: the
        // little repeated block of instructions that an artist would recognise as one node. If whole shaders are
        // all different but their motifs come from a small vocabulary, the answer to "can graphs be small" is yes
        // — via a node LIBRARY, not via a graph library. This measures how concentrated that vocabulary is.
        Console.WriteLine("=== MOTIF VOCABULARY (shape-normalised instruction n-grams) ===");
        Console.WriteLine("    How concentrated the corpus's LOCAL structure is, regardless of whole-shader identity.");
        foreach (var s_N in new[] { 1, 3, 5, 8 })
        {
            var s_Grams = new Dictionary<string, int>(StringComparer.Ordinal);
            var s_Occurrences = 0L;
            foreach (var s_Record in s_Records)
            {
                var s_Tokens = s_Record.Shape.Split(';', StringSplitOptions.RemoveEmptyEntries);
                for (var s_I = 0; s_I + s_N <= s_Tokens.Length; s_I++)
                {
                    var s_Gram = string.Join(";", s_Tokens.Skip(s_I).Take(s_N));
                    s_Grams[s_Gram] = s_Grams.GetValueOrDefault(s_Gram) + 1;
                    s_Occurrences++;
                }
            }

            var s_Ranked = s_Grams.Values.OrderByDescending(p_V => p_V).ToList();
            int Needed(double p_Fraction)
            {
                long s_Sum = 0;
                for (var s_I = 0; s_I < s_Ranked.Count; s_I++)
                {
                    s_Sum += s_Ranked[s_I];
                    if (s_Sum >= p_Fraction * s_Occurrences)
                        return s_I + 1;
                }

                return s_Ranked.Count;
            }

            Console.WriteLine($"    {s_N}-gram: {s_Grams.Count,6} distinct over {s_Occurrences,7} occurrences | " +
                              $"{Needed(0.5),5} cover 50%, {Needed(0.8),5} cover 80%, {Needed(0.9),5} cover 90%");
        }

        Console.WriteLine();

        // Semantic key last: it is not a structure, it is a description of one.
        var s_Semantic = Enumerable.Range(0, s_Total)
            .GroupBy(p_I => $"{s_Records[p_I].Samples} samp / {s_Records[p_I].Targets} RT / " +
                            $"fresnel={(s_Records[p_I].Fresnel ? "yes" : "no")}", StringComparer.Ordinal)
            .OrderByDescending(p_G => p_G.Count())
            .ToList();

        Console.WriteLine("=== KEY 4 - SEMANTIC (texture samples / render targets / fresnel-shaped chain) ===");
        Console.WriteLine($"    distinct classes: {s_Semantic.Count}");
        foreach (var s_Class in s_Semantic)
            Console.WriteLine($"      {s_Class.Count(),4}  {s_Class.Key}   e.g. {Short(s_Records[s_Class.First()].Name)}");

        Console.WriteLine();
        Console.WriteLine($"    shaders with a fresnel-shaped chain: {s_Records.Count(p_R => p_R.Fresnel)}/{s_Total}");
        Console.WriteLine();

        // Instruction-count distribution: the "126-159 nodes of spaghetti" complaint in numbers.
        var s_Counts = s_Records.Select(p_R => p_R.Count).OrderBy(p_C => p_C).ToList();
        if (s_Counts.Count > 0)
            Console.WriteLine($"    instructions per chosen permutation: min {s_Counts[0]}, " +
                              $"p25 {s_Counts[s_Counts.Count / 4]}, median {s_Counts[s_Counts.Count / 2]}, " +
                              $"p75 {s_Counts[s_Counts.Count * 3 / 4]}, max {s_Counts[^1]}");

        if (!string.IsNullOrWhiteSpace(p_Csv))
        {
            var s_DataflowIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var s_ShapeIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var s_Lines = new List<string> { "shader\tpermutation\tinstructions\ttargets\tsamples\tfresnel\tshapeId\tdataflowId" };
            foreach (var s_Record in s_Records)
            {
                if (!s_ShapeIds.TryGetValue(s_Record.Shape, out var s_S))
                    s_ShapeIds[s_Record.Shape] = s_S = s_ShapeIds.Count;

                if (!s_DataflowIds.TryGetValue(s_Record.Dataflow, out var s_D))
                    s_DataflowIds[s_Record.Dataflow] = s_D = s_DataflowIds.Count;

                s_Lines.Add($"{s_Record.Name}\t{s_Record.File}\t{s_Record.Count}\t{s_Record.Targets}\t" +
                            $"{s_Record.Samples}\t{s_Record.Fresnel}\t{s_S}\t{s_D}\t{s_Record.Shape.GetHashCode():x8}");
            }

            File.WriteAllLines(p_Csv, s_Lines);
            Console.WriteLine($"    per-shader table written to {p_Csv}");
        }

        return 0;
    }

    private static string Short(string p_Name)
    {
        var s_Parts = p_Name.Split('/');
        return s_Parts.Length <= 3 ? p_Name : string.Join("/", s_Parts.TakeLast(3));
    }

    private static void RunProcess(string p_Executable, string[] p_Arguments)
    {
        var s_Info = new System.Diagnostics.ProcessStartInfo(p_Executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };

        foreach (var s_Argument in p_Arguments)
            s_Info.ArgumentList.Add(s_Argument);

        using var s_Process = System.Diagnostics.Process.Start(s_Info);
        s_Process?.StandardOutput.ReadToEnd();
        s_Process?.StandardError.ReadToEnd();
        s_Process?.WaitForExit();
    }

    /// <summary>
    /// Known-answer test for the signature parser and the family classifier: the six shaders below were measured by
    /// hand with fxc /dumpbin, so the expected family is not a guess. If the parser regresses, this says so.
    /// </summary>
    private static int SignatureTest(string p_Root)
    {
        var s_Expected = new (string Folder, string Label, string Family)[]
        {
            ("0028", "OilDrumBarrel (rigid mesh)", "RigidMesh"),
            ("0013", "Terrain 0MV 2d", "Terrain"),
            ("0006", "Terrain 0MS MaskScale2d", "Terrain"),
            ("0032", "Terrain 0MD_2MD_5MD 3d", "Terrain"),
            ("0140", "VegetationPreset", "Vegetation"),
            ("0611", "Female1PGlove (skinned)", "Skinned"),
            ("0025", "Parachute (submaterial)", "RigidMeshSubMaterial"),
            ("0138", "Barrack CorrugatedMetal (lightmapped)", "RigidMesh"),
        };

        var s_Failures = 0;
        foreach (var (s_Folder, s_Label, s_Family) in s_Expected)
        {
            var s_Dir = Path.Combine(p_Root, s_Folder);
            if (!Directory.Exists(s_Dir))
            {
                Console.Out.WriteLine($"  SKIP {s_Label}: no folder {s_Folder}");
                continue;
            }

            var s_File = new DirectoryInfo(s_Dir).GetFiles("*_ps.dxbc")
                .OrderByDescending(p_F => p_F.Length).FirstOrDefault();

            if (s_File == null)
            {
                Console.Out.WriteLine($"  SKIP {s_Label}: no pixel shader");
                continue;
            }

            var s_Contract = RimeShaderEditor.Emit.ShaderContract.Detect(File.ReadAllBytes(s_File.FullName));
            var s_Ok = s_Contract.Family.ToString() == s_Family;
            if (!s_Ok)
                s_Failures++;

            Console.Out.WriteLine($"  {(s_Ok ? "ok  " : "FAIL")} {s_Label,-32} -> {s_Contract.Describe()}");
            if (!s_Ok)
                Console.Out.WriteLine($"       expected {s_Family}");

            var s_Selector = s_Contract.SubMaterialSelector;
            if (s_Selector != null)
                Console.Out.WriteLine($"       submaterial selector: {s_Selector}");

            // The registers are the point: "declaration order maps to t1, t2" is false for lightmapped shaders.
            var s_Material = s_Contract.MaterialTextures;
            if (s_Material.Count > 0)
                Console.Out.WriteLine($"       material textures: {string.Join(", ", s_Material)}");

            var s_Engine = s_Contract.EngineTextures;
            if (s_Engine.Count > 0)
                Console.Out.WriteLine($"       engine textures:   {string.Join(", ", s_Engine)}");
        }

        Console.Out.WriteLine(s_Failures == 0
            ? "SIGTEST: PASS - every measured family is classified from its bytecode alone."
            : $"SIGTEST: FAIL - {s_Failures} misclassified.");

        return s_Failures == 0 ? 0 : 1;
    }

    private static void PrintTree(RimeShaderEditor.Emit.ShaderTreeNode p_Node, int p_Level, int p_MaxDepth)
    {
        if (p_Level > p_MaxDepth)
            return;

        var s_Pad = new string(' ', p_Level * 2);
        foreach (var s_Child in p_Node.Children)
        {
            if (s_Child.IsShader)
            {
                if (p_Level < p_MaxDepth)
                    Console.Out.WriteLine($"{s_Pad}  - {s_Child.Name}");

                continue;
            }

            Console.Out.WriteLine($"{s_Pad}  {s_Child.Name}/  ({s_Child.ShaderCount})");
            PrintTree(s_Child, p_Level + 1, p_MaxDepth);
        }
    }

    private static byte[][] CaptureAll(View.ShaderPreview p_Preview)
    {
        var s_All = new byte[4][];
        for (var i = 0; i < 4; i++)
            s_All[i] = p_Preview.CaptureTarget(i) ?? Array.Empty<byte>();

        return s_All;
    }

    /// <summary>Renders the bake dialog to a PNG so the picker can be looked at without opening it.</summary>
    private static int BakeShot(string p_OutputPath, string? p_GamePath)
    {
        var s_Game = p_GamePath ?? EditorSettings.DetectGamePath() ?? "";
        var s_Levels = RimeShaderEditor.Emit.LevelScanner.Scan(s_Game);

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
            View.BakeDialog.Snapshot(s_Levels, "MyShaderMod", @"C:\Mods",
                "Props/StreetProps/OilDrumBarrel_01/SS_OilDrumBarrel_01")));

        using var s_Stream = File.Create(p_OutputPath);
        s_Encoder.Save(s_Stream);
        Console.Out.WriteLine($"Rendered the bake dialog with {s_Levels.Count} level(s) -> {p_OutputPath}");
        return 0;
    }

    /// <summary>
    /// Runs the REAL baker with no window, so the automation of a pipeline that costs in-game boots is verified
    /// end to end instead of assumed from "it compiles".
    /// </summary>
    private static int HeadlessBake(string p_GraphPath, string p_Maps, string p_ModName, string p_BundleName,
        string p_OutDir, bool p_AdditiveDb = false)
    {
        var s_Game = EditorSettings.DetectGamePath();
        if (string.IsNullOrWhiteSpace(s_Game))
        {
            Console.Error.WriteLine("ERROR: BF3 path not detectable.");
            return 1;
        }

        var s_Repl = RimeShaderEditor.MainWindow.FindRimeRepl();
        var s_Fxc = RimeShaderEditor.MainWindow.FindFxc();
        if (s_Repl == null || s_Fxc == null)
        {
            Console.Error.WriteLine($"ERROR: RimeREPL={s_Repl ?? "missing"} fxc={s_Fxc ?? "missing"}");
            return 1;
        }

        var s_Request = new View.BakeRequest
        {
            Levels = p_Maps.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p_M => p_M.Trim()).ToList(),
            ModName = p_ModName,
            BundleName = p_BundleName.ToLowerInvariant(),
            OutputFolder = p_OutDir,
            AdditiveDb = p_AdditiveDb,
        };

        var s_Work = Path.Combine(p_OutDir, "bake");
        Directory.CreateDirectory(s_Work);

        // Comma-separated graphs all bake into the one mod, and every one of them compiles against the
        // contract detected from ITS OWN target's bytecode — including the first: emitting under the rigid
        // default is only right for rigid-mesh shaders, and headless has no live contract to fall back on.
        var s_GraphPaths = p_GraphPath.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p_P => p_P.Trim()).ToList();

        var (s_Shaders, s_CustomTextures, s_PrepError) = RimeShaderEditor.MainWindow.PrepareAdditionalGraphs(
            s_GraphPaths, s_Repl, s_Fxc, s_Game, s_Request.Levels[0],
            Path.Combine(p_OutDir, "texcache"), Path.Combine(s_Work, "graphs"), Console.Out.WriteLine);

        if (s_PrepError != null)
        {
            Console.Error.WriteLine($"ERROR: {s_PrepError}");
            return 1;
        }

        // Duplicate TARGETS are fine when each lane delivers a different variation of the object; the
        // refused case is two graphs claiming the same (target, variation) pair.
        var s_Lanes = s_Shaders
            .Select(p_S => $"{p_S.Target}|{p_S.Variation?.VariationAsset ?? ""}".ToLowerInvariant())
            .ToList();
        if (s_Lanes.Distinct().Count() != s_Lanes.Count)
        {
            Console.Error.WriteLine("ERROR: two of the graphs deliver the same shader (and same variation).");
            return 1;
        }

        var s_Result = RimeShaderEditor.Emit.ShaderBaker.Bake(s_Request, s_Shaders, s_Game, s_Repl,
            s_Work, Console.Out.WriteLine, s_CustomTextures);

        Console.Out.WriteLine("--- summary ---");
        foreach (var s_Level in s_Result.Levels)
        {
            Console.Out.WriteLine($"  {s_Level.Level,-14} perms={s_Level.Permutations,-3} " +
                                  $"db={s_Level.ShaderDbBytes,12:N0} sb={s_Level.SuperBundleBytes,10:N0} " +
                                  $"toc={s_Level.TocBytes,-6} {(s_Level.Ok ? "OK" : "-> " + s_Level.Failure)}");
        }

        Console.Out.WriteLine($"mod folder: {s_Result.ModFolder}");
        return s_Result.Levels.Any(p_L => p_L.Ok) ? 0 : 1;
    }

    /// <summary>Renders the colour picker to a PNG so the widget itself can be eyeballed without opening it.</summary>
    private static int ColourShot(string p_OutputPath, string p_Seed)
    {
        var s_Parts = p_Seed.Split(',');
        var s_Values = new[] { 1f, 1f, 1f, 1f };
        for (var i = 0; i < 4 && i < s_Parts.Length; i++)
            if (float.TryParse(s_Parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s_V))
                s_Values[i] = s_V;

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(View.ColourPicker.Snapshot(s_Values)));
        using var s_Stream = File.Create(p_OutputPath);
        s_Encoder.Save(s_Stream);

        Console.Out.WriteLine("Rendered the picker seeded with " + string.Join(",",
            s_Values.Select(p_V => p_V.ToString(System.Globalization.CultureInfo.InvariantCulture))) +
            $" -> {p_OutputPath}");
        return 0;
    }

    /// <summary>Decodes a DDS through the preview path and re-encodes it as PNG, so the result can be eyeballed.</summary>
    private static int DdsTest(string p_Input, string p_OutputPath)
    {
        var s_Image = View.DdsImage.Load(p_Input);
        if (s_Image == null)
        {
            Console.Error.WriteLine($"ERROR: could not decode {p_Input}");
            return 1;
        }

        var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Image));
        using var s_Stream = File.Create(p_OutputPath);
        s_Encoder.Save(s_Stream);

        Console.Out.WriteLine($"Decoded {s_Image.PixelWidth}x{s_Image.PixelHeight} -> {p_OutputPath}");
        return 0;
    }

    private static int Emit(ShaderGraph p_Graph, string p_OutputPath, ShaderContract? p_Contract = null)
    {
        var s_Result = new HlslEmitter { Contract = p_Contract }.Emit(p_Graph);
        foreach (var s_Error in s_Result.Errors)
            Console.Error.WriteLine($"ERROR: {s_Error}");

        if (!s_Result.Ok)
            return 1;

        File.WriteAllText(p_OutputPath, s_Result.Hlsl);
        Console.Out.WriteLine($"Wrote {p_OutputPath} ({s_Result.Hlsl.Length} chars, {p_Graph.Nodes.Count} nodes).");
        return 0;
    }
}
