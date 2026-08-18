using Xunit;

namespace Seekers.Tests;

public class CheckpointTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"seekertest-{Guid.NewGuid():N}.chk");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path + ".history")) File.Delete(_path + ".history");
    }

    private SeekerConfig<(double a, double b), double> config(int seed) =>
        Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(v => -(v.a - 2) * (v.a - 2) - (v.b - 3) * (v.b - 3))
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(seed)
            .WithCheckpoint(_path);

    [Fact]
    public void SavesOnImprovement_AndRoundtripsValues()
    {
        var cfg = config(1);
        cfg.Checkpoint.MinInterval = TimeSpan.Zero;
        var res = cfg.HillClimb(restarts: 2);
        Assert.True(File.Exists(_path));
        var loaded = cfg.Checkpoint.TryLoadValues();
        Assert.NotNull(loaded);
        Assert.Equal(res.BestValues[0], loaded[0], 12);
        Assert.Equal(res.BestValues[1], loaded[1], 12);
    }

    [Fact]
    public void ResumesAutomatically_OnSeekerCreation()
    {
        var cfg1 = config(2);
        cfg1.Checkpoint.MinInterval = TimeSpan.Zero;
        cfg1.HillClimb(restarts: 2);

        var cfg2 = config(3);
        var s = cfg2.CreateSeeker();
        var vals = s.GetValues();
        Assert.Equal(2, vals[0], 1);
        Assert.Equal(3, vals[1], 1);
    }

    [Fact]
    public void ResumeCanBeDisabled()
    {
        var cfg1 = config(4);
        cfg1.Checkpoint.MinInterval = TimeSpan.Zero;
        cfg1.HillClimb(restarts: 2);

        var cfg2 = config(12349);
        cfg2.Checkpoint.Resume = false;
        var vals = cfg2.CreateSeeker().GetValues();
        Assert.False(Math.Abs(vals[0] - 2) < 0.01 && Math.Abs(vals[1] - 3) < 0.01); // random start, not the checkpoint
    }

    [Fact]
    public void Throttling_DefersButSaveFinalFlushes()
    {
        var cp = new SeekerCheckpoint(_path) { MinInterval = TimeSpan.FromHours(1) };
        cp.Save(new double[] { 1, 2 }, "first"); // first save always writes (throttle starts empty)
        cp.Save(new double[] { 3, 4 }, "second"); // deferred by the throttle
        Assert.Equal(new double[] { 1, 2 }, cp.TryLoadValues());
        cp.SaveFinal(); // flushes the pending best
        Assert.Equal(new double[] { 3, 4 }, cp.TryLoadValues());
    }

    [Fact]
    public void History_AppendsOneLinePerFlush()
    {
        var cp = new SeekerCheckpoint(_path) { MinInterval = TimeSpan.Zero };
        cp.Save(new double[] { 1 }, "a");
        cp.Save(new double[] { 2 }, "b");
        var lines = File.ReadAllLines(_path + ".history");
        Assert.Equal(2, lines.Length);
        Assert.Contains("eval: a", lines[0]);
        Assert.Contains("eval: b", lines[1]);
    }

    [Fact]
    public void TryLoadValues_ReturnsNullForMissingOrMalformedFiles()
    {
        Assert.Null(new SeekerCheckpoint(_path).TryLoadValues());
        File.WriteAllText(_path, "not a checkpoint at all");
        Assert.Null(new SeekerCheckpoint(_path).TryLoadValues());
        File.WriteAllText(_path, "values: 1, banana, 3");
        Assert.Null(new SeekerCheckpoint(_path).TryLoadValues());
    }

    [Fact]
    public void ResumeIgnoresCheckpoint_WithMismatchedParamCount()
    {
        File.WriteAllText(_path, "eval: x\r\nvalues: 1, 2, 3, 4, 5\r\n"); // five values for a two-param problem
        var s = config(5).CreateSeeker();
        Assert.Equal(2, s.GetValues().Length); // constructed fine, checkpoint ignored
    }

    [Fact]
    public void ValuesRoundtrip_FullPrecision()
    {
        var cp = new SeekerCheckpoint(_path) { MinInterval = TimeSpan.Zero };
        var values = new[] { Math.PI, -1e-17, 12345.678901234567 };
        cp.Save(values, "pi");
        Assert.Equal(values, cp.TryLoadValues());
    }
}

public class LoggingTests
{
    [Fact]
    public void Levels_GateOutput()
    {
        var lines = new List<string>();
        var log = SeekerLog.To(lines.Add, SeekerLogLevel.Improvements);
        log.Improvement("i");
        log.Iteration("t");
        log.Step("s");
        Assert.Equal(new[] { "i" }, lines);

        lines.Clear();
        log.Level = SeekerLogLevel.Steps;
        log.Improvement("i");
        log.Iteration("t");
        log.Step("s");
        Assert.Equal(new[] { "i", "t", "s" }, lines);
    }

    [Fact]
    public void WantGuards_MatchLevels()
    {
        var log = SeekerLog.To(_ => { }, SeekerLogLevel.Iterations);
        Assert.True(log.WantImprovements);
        Assert.True(log.WantIterations);
        Assert.False(log.WantSteps);
        log.Sink = null;
        Assert.False(log.WantImprovements); // no sink = fully off regardless of level
    }

    [Fact]
    public void Sub_AccumulatesPrefixes_AndOverridesLevel()
    {
        var lines = new List<string>();
        var log = SeekerLog.To(lines.Add, SeekerLogLevel.Improvements);
        var sub = log.Sub("[inner] ");
        sub.Improvement("x");
        Assert.Equal("[inner] x", lines[^1]);
        var subsub = sub.Sub("[deep] ");
        subsub.Improvement("y");
        Assert.Equal("[inner] [deep] y", lines[^1]);

        var silenced = log.Sub("[quiet] ", SeekerLogLevel.Off);
        silenced.Improvement("should not appear");
        Assert.DoesNotContain(lines, l => l.Contains("should not appear"));
    }

    [Fact]
    public void EngineImprovements_GoToTheConfiguredSink()
    {
        var lines = new List<string>();
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(0, 1), b: ctx.LinearDbl(0, 1)))
            .WithEval(v => v.a + v.b)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(3);
        cfg.Log = SeekerLog.To(lines.Add, SeekerLogLevel.Improvements);
        cfg.HillClimb(restarts: 2);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.StartsWith("IMPROVED", l));
    }

    [Fact]
    public void Num_AdaptsPrecisionToMagnitude()
    {
        Assert.Equal("1,234.5", SeekerLog.Num(1234.5));
        Assert.Equal("0", SeekerLog.Num(0));
        Assert.Contains("e", SeekerLog.Num(0.0000123)); // tiny values switch to scientific notation
        Assert.Equal("-1,234.5", SeekerLog.Num(-1234.5));
    }

    [Fact]
    public void Vec_JoinsValues()
    {
        Assert.Equal("1, 2.5, 3", SeekerLog.Vec(new double[] { 1, 2.5, 3 }));
    }
}
