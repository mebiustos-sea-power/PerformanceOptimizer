using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PerformanceOptimizer;
using PerformanceOptimizer.Optimizations;
using static Program;

internal static class ReadbackTests
{
    internal static void Run()
    {
        PendingCompletionAndFreshData();
        ErrorsAndIndependentGrids();
        ImmediateAndDisabled();
        ConversionFailuresRemainVisible();
        RejectChangedLayouts();
    }

    private static void Both(Action<ReadbackFixture> configure, Action<ReadbackFixture, ReadbackFixture> check)
    {
        var original = new ReadbackFixture(false); var optimized = new ReadbackFixture(true);
        configure(original); configure(optimized);
        Require(original.Snapshot() == optimized.Snapshot(), "Native requests, completion/error state, copied samples or clearing changed.");
        check(original, optimized);
    }

    private static void PendingCompletionAndFreshData()
    {
        Both(f =>
        {
            f.Step(1); f.Step(2); f.Step(3);
            foreach (var s in f.Buffers) s.Done = true;
            f.Step(4); // Native request still rejects; completion is collected afterwards.
            f.Step(5); // A fresh conversion must precede the next accepted request.
            foreach (var s in f.Buffers) s.Done = true;
            f.Step(6);
        }, (a, b) =>
        {
            Require(a.Buffers.All(s => s.Dispatches == 6) && b.Buffers.All(s => s.Dispatches == 2),
                "Conversions were not limited to frames where the native request can be accepted.");
            Require(b.Buffers.All(s => s.Requests == 2 && s.Copies == 2), "Native request/collection cadence changed.");
            Require(b.World.Source.Arrays[0].Data.All(v => v == 5) && b.World.Source.Arrays[1].Data.All(v => v == 105),
                "The next accepted request delivered a stale conversion.");
            Require(b.World.Source.Arrays.Skip(2).All(array => array.Data.All(v => v == 0)), "Unused displacement grids stopped clearing.");
        });
        Console.WriteLine("PASS pending readbacks: 12 -> 4 conversions; native cadence, copied values and grid clearing identical (modeled GPU boundaries)");
    }

    private static void ErrorsAndIndependentGrids()
    {
        Both(f =>
        {
            f.Step(1);
            f.Buffers[0].Done = true; f.Step(2);
            f.Buffers[1].Error = true; f.Step(3);
            Require(f.Buffers[1].Buffer._hasError && !f.Buffers[1].Buffer._requested && f.Buffers[1].Copies == 0,
                "A failed native request was copied or its error state was hidden.");
            // An error can clear _requested before the request reports done.
            // Preserve the native conversion in this uncommon idle-but-incomplete state.
            f.Step(4);
            f.Buffers[1].Done = true; f.Step(5);
            foreach (var s in f.Buffers) s.Done = true;
            f.Step(6);
        }, (a, b) =>
        {
            Require(b.Buffers[0].Requests == 2 && b.Buffers[1].Requests == 2 && b.Buffers[1].Copies == 2,
                "Independent buffer completion/error recovery changed.");
            Require(a.Buffers.Sum(s => s.Dispatches) > b.Buffers.Sum(s => s.Dispatches), "No redundant conversion was removed.");
        });
        Console.WriteLine("PASS errors/grids: independent completion, errors, retry and idle-but-incomplete requests preserve native behavior");
    }

    private static void ImmediateAndDisabled()
    {
        Both(f =>
        {
            foreach (var s in f.Buffers) s.Immediate = true;
            f.Step(1); f.Step(2);
            f.World.disableReadBack = true; f.Step(3);
            f.World.disableReadBack = false; f.World.readSdr = null!; f.Step(4);
        }, (a, b) => Require(a.Buffers.All(s => s.Dispatches == 2) && b.Buffers.All(s => s.Dispatches == 2 &&
            s.Requests == 2 && s.Copies == 2), "Immediate completion or disabled/missing-shader guards changed."));
        Console.WriteLine("PASS native guards: immediate results, disabled readback and missing shader retain behavior");
    }

    private static void ConversionFailuresRemainVisible()
    {
        foreach (bool optimized in new[] { false, true })
        {
            var f = new ReadbackFixture(optimized) { ThrowDispatch = true };
            Throws<InvalidOperationException>(() => f.Step(1));
            Require(f.Buffers.All(s => s.Requests == 0 && !s.Buffer._requested), "A failed conversion still submitted a request.");
        }
        Console.WriteLine("PASS conversion failure: exception remains visible and no stale request is submitted");
    }

    private static void RejectChangedLayouts()
    {
        var missing = InstructionReader.Read(OceanReadbackDispatch.Target);
        missing.RemoveAll(i => i.Calls(OceanReadbackDispatch.Conversion));
        Throws<InvalidOperationException>(() => OceanReadbackDispatch.Transpile(missing).ToList());
        var duplicate = InstructionReader.Read(OceanReadbackDispatch.Target);
        duplicate.Add(new CodeInstruction(OpCodes.Call, OceanReadbackDispatch.Conversion));
        Throws<InvalidOperationException>(() => OceanReadbackDispatch.Transpile(duplicate).ToList());
        var wrongBuffer = InstructionReader.Read(OceanReadbackDispatch.Target);
        int call = wrongBuffer.FindIndex(i => i.Calls(OceanReadbackDispatch.Conversion));
        wrongBuffer[call + 3].opcode = OpCodes.Ldloc_3;
        var before = wrongBuffer.Select(i => (i.opcode, i.operand)).ToArray();
        Throws<InvalidOperationException>(() => OceanReadbackDispatch.Transpile(wrongBuffer).ToList());
        Require(before.SequenceEqual(wrongBuffer.Select(i => (i.opcode, i.operand))), "Rejected patch modified incoming IL.");
        Console.WriteLine("PASS compatibility rejection: absent/duplicate conversion or mismatched request buffer cannot partially patch the game");
    }

    internal static void InstallOnGameMono()
    {
        var patches = new Harmony(ModInfo.Id + ".tests.native");
        try
        {
            new OceanReadbackDispatch().Install(patches);
            Require(Harmony.GetPatchInfo(OceanReadbackDispatch.Target)!.Owners.Contains(patches.Id),
                "The actual ocean transpiler was not installed on game Mono.");
            var fixture = new ReadbackFixture(true);
            fixture.Buffers[0].Buffer._requested = true;
            // The production guard must return without touching native graphics
            // even when there are no graphics resources in this test process.
            OceanReadbackDispatch.DispatchIfAvailable(null!, 3, null!, null!, fixture.Buffers[0].Buffer);
        }
        finally { patches.UnpatchSelf(); }
        Require(Harmony.GetPatchInfo(OceanReadbackDispatch.Target)?.Owners.Contains(patches.Id) != true,
            "Removing the ocean patch left its owner attached.");
        InstallerIsolation();
        Console.WriteLine("PASS game Mono: actual ocean Harmony installation/removal and per-optimization failure/disable isolation");
    }

    private static void InstallerIsolation()
    {
        string[] ids = { "TestFailure", "TestDisabled", "TestGood" };
        string path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, "[Optimizations]\nTestDisabled=false\n");
            var settings = OptimizationSettings.Load(path, ids);
            var failed = new Probe(ids[0], true); var disabled = new Probe(ids[1], false); var good = new Probe(ids[2], false);
            OptimizationInstaller.Install(new (IOptimization Optimization, bool Enabled)[] { (failed, settings.IsEnabled(failed.Id)), (disabled, settings.IsEnabled(disabled.Id)), (good, settings.IsEnabled(good.Id)) });
            Require(failed.Calls == 1 && disabled.Calls == 0 && good.Calls == 1, "Disable/failure isolation stopped other optimizations.");
            Require(failed.Disposals == 1 && disabled.Disposals == 0 && good.Disposals == 0, "Failure cleanup leaked resources or disposed a different optimization.");
            var owners = Harmony.GetPatchInfo(ProbeTarget)!.Owners;
            Require(!owners.Contains(ModInfo.Id + "." + failed.Id) && owners.Contains(ModInfo.Id + "." + good.Id),
                "A failed optimization retained patches or removed another owner's patch.");
        }
        finally
        {
            OptimizationInstaller.Shutdown();
            foreach (string id in ids) new Harmony(ModInfo.Id + "." + id).UnpatchSelf();
            System.IO.File.Delete(path);
        }
    }

    private static readonly MethodInfo ProbeTarget = typeof(ReadbackTests).GetMethod(nameof(Target), BindingFlags.Static | BindingFlags.NonPublic)!;
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Target() => 1;
    private static void Postfix(ref int __result) => __result++;
    private sealed class Probe : IOptimization
    {
        public string Id { get; }
        private readonly bool fail;
        internal int Calls;
        internal int Disposals;
        internal Probe(string id, bool fail) { Id = id; this.fail = fail; }
        public void Dispose() { Disposals++; }
        public void Install(Harmony patches)
        {
            Calls++;
            patches.Patch(ProbeTarget, postfix: new HarmonyMethod(typeof(ReadbackTests), nameof(Postfix)));
            if (fail) throw new InvalidOperationException("Expected installation failure after patching.");
        }
    }
}
