using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace RimeShaderEditor;

/// <summary>
/// ⭐ ONE MOUNT FOR MANY SHADERS. Reading a shader out of the game mounts it three times (its bytecode, the list of its textures, the
/// textures themselves) and translating it once more, each a fresh RimeREPL that mounts the whole game first — fine for the one shader a
/// user picks, hours for the cache preparation's hundreds (81 vehicle shaders at three to four minutes each, measured). A BATCH runs the
/// very same commands for every shader in ONE RimeREPL, each shader's commands fenced by echo markers, and hands each reader back its
/// own part of the output, so the code that reads it is the code that read a run of its own.
///
/// How a reader takes part: its game scripts go through RunGameCommands. With no batch open that runs the script as always. With a
/// batch open it either replays what the batch already ran, or — while the batch is PLANNING — notes the commands and stops the reader
/// (BatchDeferred); the batch then runs every noted block in one mount and asks the readers again, round after round, until none stops.
/// A block the batch ran but got no output for (an older RimeREPL with no echo, a run cut short) is run alone the next time: never
/// worse than before.
/// </summary>
public partial class MainWindow
{
    /// <summary>Thrown by RunGameCommands while a batch plans: the reader stops here and is asked again once the batch has run.</summary>
    private sealed class BatchDeferred : Exception
    {
    }

    /// <summary>The commands of one script (between the mount and the exit), and what has to happen before they run.</summary>
    private sealed record BatchBlock(string Commands, Action? Prepare);

    private static readonly object s_BatchLock = new();

    /// <summary>While a batch is open: each block it ran → that block's part of the output.</summary>
    private static Dictionary<string, ProcessResult>? s_BatchOutputs;

    /// <summary>While a batch is open: every block it ran (one with no output is run alone afterwards, never noted again).</summary>
    private static HashSet<string>? s_BatchTried;

    /// <summary>While a batch plans: the blocks the readers asked for.</summary>
    private static List<BatchBlock>? s_BatchPlan;

    [ThreadStatic]
    private static bool s_BatchPlanning;

    /// <summary>How many reads of the game a batch served without a mount of their own (said by whoever opened it).</summary>
    private static int s_BatchReplays;

    /// <summary>
    /// Runs a game script — mount the game, <paramref name="p_Commands"/>, exit — written to <paramref name="p_ScriptPath"/>, after
    /// <paramref name="p_Prepare"/>; or, inside a batch, replays it / notes it (see the class comment).
    /// </summary>
    private static ProcessResult RunGameCommands(string p_Repl, string p_GamePath, string p_ScriptPath, string p_Commands, Action? p_Prepare = null)
    {
        lock (s_BatchLock)
        {
            if (s_BatchOutputs != null && s_BatchOutputs.TryGetValue(p_Commands, out var s_Replayed))
            {
                s_BatchReplays++;
                return s_Replayed;
            }

            if (s_BatchPlanning && s_BatchPlan != null && s_BatchTried != null && !s_BatchTried.Contains(p_Commands))
            {
                s_BatchPlan.Add(new BatchBlock(p_Commands, p_Prepare));
                throw new BatchDeferred();
            }
        }

        p_Prepare?.Invoke();
        File.WriteAllText(p_ScriptPath, $"mount_game \"{p_GamePath}\" Frostbite2_0 true\nselect_game 1\n{p_Commands}exit\n");
        return Run(p_Repl, new[] { p_ScriptPath });
    }

    private static void OpenBatch()
    {
        lock (s_BatchLock)
        {
            s_BatchReplays = 0;
            s_BatchOutputs = new Dictionary<string, ProcessResult>(StringComparer.Ordinal);
            s_BatchTried = new HashSet<string>(StringComparer.Ordinal);
            s_BatchPlan = new List<BatchBlock>();
        }
    }

    private static void CloseBatch()
    {
        lock (s_BatchLock)
        {
            s_BatchOutputs = null;
            s_BatchTried = null;
            s_BatchPlan = null;
        }
    }

    /// <summary>
    /// Runs every block in ONE mount, each fenced by echo markers, and keeps each block's part of the output for its reader (a block
    /// whose markers are not both found is left without one: its reader runs it alone). The preparations run first.
    /// </summary>
    private static void RunBatch(string p_Repl, string p_GamePath, string p_WorkDir, IReadOnlyList<BatchBlock> p_Blocks)
    {
        var s_Blocks = p_Blocks.GroupBy(p_B => p_B.Commands, StringComparer.Ordinal).Select(p_G => p_G.First()).ToList();
        if (s_Blocks.Count == 0)
            return;

        foreach (var s_Block in s_Blocks)
            s_Block.Prepare?.Invoke();

        var s_Script = new StringBuilder($"mount_game \"{p_GamePath}\" Frostbite2_0 true\nselect_game 1\n");
        for (var i = 0; i < s_Blocks.Count; i++)
            s_Script.Append($"echo RIMEBATCH.{i}.BEGIN\n").Append(s_Blocks[i].Commands).Append($"echo RIMEBATCH.{i}.END\n");
        s_Script.Append("exit\n");

        Directory.CreateDirectory(p_WorkDir);
        var s_Path = Path.Combine(p_WorkDir, "batch.rime");
        File.WriteAllText(s_Path, s_Script.ToString());
        var s_Run = Run(p_Repl, new[] { s_Path });

        lock (s_BatchLock)
        {
            for (var i = 0; i < s_Blocks.Count; i++)
            {
                s_BatchTried?.Add(s_Blocks[i].Commands);
                var s_Begin = Regex.Match(s_Run.Output, $@"^RIMEBATCH\.{i}\.BEGIN\r?\n", RegexOptions.Multiline);
                if (!s_Begin.Success)
                    continue;

                var s_End = new Regex($@"^RIMEBATCH\.{i}\.END\r?$", RegexOptions.Multiline).Match(s_Run.Output, s_Begin.Index + s_Begin.Length);
                if (!s_End.Success)
                    continue;

                s_BatchOutputs?.TryAdd(s_Blocks[i].Commands, new ProcessResult
                {
                    ExitCode = s_Run.ExitCode,
                    Output = s_Run.Output[(s_Begin.Index + s_Begin.Length)..s_End.Index],
                });
            }
        }
    }

    /// <summary>
    /// <see cref="PrefetchSectionShader"/> for MANY shaders in a handful of mounts instead of three each: rounds of planning (each
    /// shader runs until it needs the game), one batch per round, until every shader has finished. Each shader's error (null = cached),
    /// in the order given.
    /// </summary>
    internal static List<string?> PrefetchSectionShaders(string p_Repl, string p_GamePath, IReadOnlyList<(string ShaderDb, string Shader)> p_Items,
        string p_MeshCacheDir, string p_TexCacheDir, Action<string>? p_Log = null)
    {
        var s_Results = new string?[p_Items.Count];
        var s_Pending = Enumerable.Range(0, p_Items.Count).ToList();
        OpenBatch();
        try
        {
            // (a shader asks the game at most three times; the cap only guards against a reader that never stops asking)
            for (var s_Round = 1; s_Pending.Count > 0 && s_Round <= 8; s_Round++)
            {
                var s_Deferred = new List<int>();
                lock (s_BatchLock)
                    s_BatchPlan!.Clear();

                s_BatchPlanning = true;
                try
                {
                    foreach (var i in s_Pending)
                    {
                        try
                        {
                            s_Results[i] = PrefetchSectionShader(p_Repl, p_GamePath, p_Items[i].ShaderDb, p_Items[i].Shader, p_MeshCacheDir, p_TexCacheDir);
                        }
                        catch (BatchDeferred)
                        {
                            s_Deferred.Add(i);
                        }
                    }
                }
                finally
                {
                    s_BatchPlanning = false;
                }

                if (s_Deferred.Count == 0)
                {
                    s_Pending.Clear();
                    break;
                }

                List<BatchBlock> s_Plan;
                lock (s_BatchLock)
                    s_Plan = s_BatchPlan!.ToList();

                p_Log?.Invoke($"  one mount for {s_Deferred.Count} shader(s) ({s_Plan.Count} read(s), round {s_Round})");
                RunBatch(p_Repl, p_GamePath, Path.Combine(p_MeshCacheDir, "shaders", "batch"), s_Plan);
                s_Pending = s_Deferred;
            }

            // a shader still asking after the last round finishes the old way (what the batch ran is still replayed)
            foreach (var i in s_Pending)
                s_Results[i] = PrefetchSectionShader(p_Repl, p_GamePath, p_Items[i].ShaderDb, p_Items[i].Shader, p_MeshCacheDir, p_TexCacheDir);
        }
        finally
        {
            p_Log?.Invoke($"  {s_BatchReplays} read(s) of the game served by the batch, without a mount of their own");
            CloseBatch();
        }

        return s_Results.ToList();
    }

    /// <summary>Closes a batch opened ahead when disposed, saying how many reads it served.</summary>
    private sealed class OpenedBatch : IDisposable
    {
        public void Dispose()
        {
            Console.Out.WriteLine($"  {s_BatchReplays} read(s) of the game served by the batch, without a mount of their own");
            CloseBatch();
        }
    }

    /// <summary>
    /// Every shader PretranslateCamo is about to translate read out of the game AHEAD, in one mount (the very read TranslateCore makes,
    /// TranslateExtractCommands, into the folder it reads); the batch stays open for the translations to replay it until the returned
    /// object is disposed. Nothing to read, or no RimeREPL/game: null — no batch, and each translation reads on its own as before.
    /// </summary>
    private static IDisposable? ReadTranslationsAhead(MainWindow p_Window, IReadOnlyList<string> p_Shaders)
    {
        var s_Repl = FindRimeRepl();
        var s_GamePath = p_Window.GamePath;
        if (s_Repl == null || s_GamePath.Length == 0 || !Directory.Exists(s_GamePath))
            return null;

        var s_Work = Path.Combine(p_Window.OutputFolder, "translate");
        var s_Blocks = new List<BatchBlock>();
        foreach (var s_Shader in p_Shaders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            p_Window.RetireStaleTranslation(s_Shader);
            if (File.Exists(p_Window.CamoGraphCachePath(s_Shader)))
                continue;

            var s_Extract = Path.Combine(s_Work, Sanitize(s_Shader));
            s_Blocks.Add(new BatchBlock(TranslateExtractCommands(p_Window.ResolveShaderDb(s_Shader), s_Shader, s_Extract), () =>
            {
                if (Directory.Exists(s_Extract))
                    try { Directory.Delete(s_Extract, true); } catch { }
            }));
        }

        if (s_Blocks.Count < 2)
            return null;

        Console.Out.WriteLine($"  reading the {s_Blocks.Count} shader(s) to translate out of the game in one mount…");
        OpenBatch();
        RunBatch(s_Repl, s_GamePath, s_Work, s_Blocks);
        return new OpenedBatch();
    }
}
