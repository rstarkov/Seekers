using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Seekers.Tests;

/// <summary>
///     Information-style performance tests: they measure per-evaluation library overhead and report it against
///     reference numbers, but never fail — machines differ, CI is noisy, and Debug builds are legitimately slower.
///     Filter with <c>dotnet test --filter Category!=Performance</c> to skip them.
///     <para>
///         The canonical way to run these is the test executable itself (xUnit v3 test projects are runnable), which
///         measures in-process without a test host and prints results live to the console:</para>
///     <code>
///         dotnet build Tests -c Release
///         Builds\Release\Seekers.Tests.exe -class "Seekers.Tests.PerformanceInfoTests"
///     </code>
///     <para>
///         Reference numbers were captured that way (Release, development machine). Under <c>dotnet test</c> the
///         numbers land in the per-test output instead and other test classes running in parallel add noise — treat
///         single-run deviations with suspicion and re-run standalone before concluding anything.</para></summary>
[Trait("Category", "Performance")]
public class PerformanceInfoTests
{
    private readonly ITestOutputHelper _out;

    public PerformanceInfoTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static bool librariesUnoptimized =>
        typeof(Seeker).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true;

    /// <summary>True when running via the test executable itself rather than under a test host (dotnet test / IDE).</summary>
    private static bool runningStandalone =>
        string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),
            typeof(PerformanceInfoTests).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes to the per-test output, and also live to the console when running as the standalone exe.</summary>
    private void emit(string line)
    {
        _out.WriteLine(line);
        if (runningStandalone)
            Console.WriteLine(line);
    }

    private void report(string what, double nsPerEval, double referenceNs)
    {
        emit($"{what}: {nsPerEval:0} ns/eval ({1000.0 / nsPerEval:0.00}M evals/sec)");
        emit($"  reference (standalone exe, Release, dev machine): {referenceNs:0} ns/eval — this run is {nsPerEval / referenceNs:0.0}x the reference");
        if (librariesUnoptimized)
            emit("  note: Seekers was built without JIT optimization (Debug) — several times slower than the reference is expected");
        else if (!runningStandalone)
            emit("  note: running under a test host — for the cleanest numbers run the test exe directly (see class doc)");
        else if (nsPerEval > referenceNs * 3)
            emit("  WARNING: substantially slower than the reference — possible regression (informational only, not failing the run)");
    }

    private static SeekerConfig<(double Nl, double Ns, double Na, double sl, double sp), (double profit, double sl, double sp)> macdShapedConfig(int seed) =>
        // the shape of the most overhead-sensitive real problem: 5 linear params, tuple TEval, custom comparison
        Seeker.WithVector(ctx =>
        (
            Nl: ctx.LinearDbl(1, 100),
            Ns: ctx.LinearDbl(1, 300),
            Na: ctx.LinearDbl(1, 300),
            sl: ctx.LinearDbl(0, 10),
            sp: ctx.LinearDbl(0, 10)
        ))
        .WithEval(v => (profit: v.Nl + v.Ns + v.Na - v.sl - v.sp, sl: v.sl, sp: v.sp))
        .WithGoal(SeekerGoal.Maximize, (a, b) => a.profit.CompareTo(b.profit))
        .WithRandom(seed);

    [Fact]
    public void TypedPipelineOverhead_RandomizeAndEvaluate()
    {
        // full per-eval pipeline: randomize 5 params + materialize the tuple + eval + compare + best bookkeeping
        var s = macdShapedConfig(42).CreateSeeker();
        for (int i = 0; i < 100_000; i++) { s.RandomizeAll(); s.Evaluate(); } // warmup
        const int N = 1_000_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) { s.RandomizeAll(); s.Evaluate(); }
        sw.Stop();
        report("random+eval (typed 5-param pipeline)", sw.Elapsed.TotalMilliseconds * 1e6 / N, referenceNs: 215);
    }

    [Fact]
    public void HillClimbPathOverhead_OrthogonalTraverse()
    {
        // the hill-climbing hot path: line searches with move/restore/compare bookkeeping per eval
        var s = macdShapedConfig(43).CreateSeeker();
        s.Evaluate();
        var warmupEnd = Stopwatch.StartNew();
        while (warmupEnd.ElapsedMilliseconds < 150)
            s.OrthogonalTraverse();
        var baseline = s.EvalCount;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
            s.OrthogonalTraverse();
        sw.Stop();
        var evals = s.EvalCount - baseline;
        report("hill-climb path (OrthogonalTraverse)", sw.Elapsed.TotalMilliseconds * 1e6 / evals, referenceNs: 82);
    }

    [Fact]
    public void ThreadedTraverseThroughput_PerWorker()
    {
        const int threads = 3;
        var cfg = macdShapedConfig(44);
        var stop = cfg.OrthogonalTraverseThreaded(threads: threads);
        Thread.Sleep(800);
        var res = stop();
        // workers run at lowest priority, so a busy machine depresses this figure more than the others
        var nsPerEvalPerWorker = 800.0 * 1e6 * threads / res.EvalCount;
        report($"threaded traverse ({threads} workers, per-worker figure)", nsPerEvalPerWorker, referenceNs: 490);
    }

    [Fact]
    public void GuardedLogging_CostsNothingWhenNothingIsLogged()
    {
        // the claim: with logging configured but producing no lines (level Off, or no improvements to report),
        // no strings are built and the hot path is unaffected
        double measure(SeekerLog log)
        {
            var cfg = macdShapedConfig(45);
            cfg.Log = log;
            var s = cfg.CreateSeeker();
            for (int i = 0; i < 50_000; i++) { s.RandomizeAll(); s.Evaluate(); } // warmup; also drives the best up
            const int N = 500_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) { s.RandomizeAll(); s.Evaluate(); }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1e6 / N;
        }
        var off = measure(SeekerLog.None);
        int lines = 0;
        var quiet = measure(SeekerLog.To(_ => lines++, SeekerLogLevel.Steps)); // fully enabled sink; random+eval emits no step lines and improvements dry up after warmup
        emit($"logging off: {off:0} ns/eval; logging enabled but quiet: {quiet:0} ns/eval ({quiet / off:0.00}x)");
        emit($"  lines actually emitted during measurement window: about {lines} (improvements only)");
        if (quiet > off * 1.25)
            emit("  WARNING: enabled-but-quiet logging cost more than 25% — the zero-cost-guard claim may have regressed (informational only)");
    }
}
