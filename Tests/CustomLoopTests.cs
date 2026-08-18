using Xunit;

namespace Seekers.Tests;

public class CustomLoopTests
{
    [Fact]
    public void RandomOrthogonalDirections_StreamsFullBasesIndefinitely()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(0, 1), b: ctx.LinearDbl(0, 1), c: ctx.LinearDbl(0, 1)))
            .WithEval(v => v.a)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(1);
        var s = cfg.CreateSeeker();
        var dirs = s.RandomOrthogonalDirections().Take(10).ToList(); // crosses a 3-direction basis boundary
        Assert.Equal(10, dirs.Count);
        Assert.All(dirs, d => Assert.Equal(3, d.Length));
        Assert.All(dirs, d => Assert.Equal(1.0, Math.Sqrt(d.Sum(x => x * x)), 9));
    }

    [Fact]
    public void SparseSubspaceDirection_MovesOnlySubsetParams()
    {
        var cfg = Seeker.WithVector(ctx => Enumerable.Range(0, 20).Select(_ => ctx.LinearDbl(0, 100, initial: 50)).ToArray())
            .WithEval(v => v.Sum())
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(2);
        var s = cfg.CreateSeeker();
        var dir = new double[20];
        dir[3] = 1;
        dir[17] = -1;
        s.Move(dir, 0.1);
        var v2 = s.GetValues();
        for (int i = 0; i < 20; i++)
            if (i == 3)
                Assert.Equal(60, v2[i], 9);
            else if (i == 17)
                Assert.Equal(40, v2[i], 9);
            else
                Assert.Equal(50, v2[i], 9);
    }

    [Fact]
    public void RestartLoop_ChainPerRestart_GlobalBestSurvives()
    {
        // the run-forever pattern, bounded: restarts explore independently, the global best only ratchets
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-5, 5), b: ctx.LinearDbl(-5, 5)))
            .WithEval(v => Math.Sin(v.a * 2) * Math.Cos(v.b) - 0.05 * v.a * v.a)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(303);
        var s = cfg.CreateSeeker();
        double lastGlobal = double.MinValue;
        for (int r = 0; r < 6; r++)
        {
            s.ResetChain();
            s.RandomizeAll();
            s.Evaluate();
            while (s.OrthogonalTraverse()) { }
            Assert.True(s.HasGlobalBest);
            Assert.True(cfg.CompareUsingGoal(s.GlobalBestEval, lastGlobal == double.MinValue ? s.GlobalBestEval : lastGlobal) >= 0
                || s.GlobalBestEval >= lastGlobal);
            lastGlobal = s.GlobalBestEval;
        }
        Assert.True(lastGlobal > 0.9); // near sin*cos maximum of ~1
    }

    [Fact]
    public void PerAxisUnitStepSearch_ReproducesExponentialIntegerSearch()
    {
        // the ±1-then-double pattern for integer coordinate search
        var evals = new List<int>();
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 1000, initial: 100), y: ctx.LinearInt(0, 1000, initial: 100)))
            .WithEval(v => { evals.Add(v.x); return -Math.Abs(v.x - 163) - Math.Abs(v.y - 100); })
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(4);
        var s = cfg.CreateSeeker();
        s.Evaluate();
        var dir = new double[] { 1, 0 };
        var unit = 1.0 / 1000;
        s.TraverseDirection(dir, new TraverseOptions
        {
            InitialStep = unit, StepGrowShrink = 2,
            GiveupBadDirStep = unit * 0.99, GiveupGoodDirStep = unit * 0.99,
            MaxStepFactor = 1e9,
        });
        Assert.Equal(163, s.BestVector.x); // exact integer optimum reached along one axis
        // the walk is exponential: 101, 103, 107, 115, ... (cumulative doubling)
        Assert.Contains(101, evals);
        Assert.Contains(103, evals);
        Assert.Contains(107, evals);
    }

    [Fact]
    public void SeedChain_CarriesKnownEvalIntoATraversal()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10, initial: 1), b: ctx.LinearDbl(-10, 10, initial: 1)))
            .WithEval(v => -(v.a - 3) * (v.a - 3) - v.b * v.b)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(5);
        var s = cfg.CreateSeeker();
        s.SeedChain(-5); // the true eval at (1,1): -4-1 = -5, known from an enclosing computation
        Assert.Equal(0, s.EvalCount);
        while (s.OrthogonalTraverse()) { }
        Assert.True(s.BestEval > -0.01);
        Assert.Equal(3, s.BestVector.a, 1);
    }
}
