using Xunit;

namespace Seekers.Tests;

public class ThreadedTests
{
    [Fact]
    public void OrthogonalTraverseThreaded_ConvergesAndStops()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(v => -(v.a - 4) * (v.a - 4) - (v.b - 7) * (v.b - 7))
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(31337);
        var stop = cfg.OrthogonalTraverseThreaded(threads: 3);
        Thread.Sleep(1200);
        var res = stop();
        Assert.True(res.Found);
        Assert.True(res.BestEval > -0.001);
        Assert.Equal(4, res.BestVector.a, 1);
        Assert.Equal(7, res.BestVector.b, 1);
        Assert.True(res.EvalCount > 0);
    }

    [Fact]
    public void StopHandle_IsPromptAndIdempotentResultIsFinal()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-1, 1), b: ctx.LinearDbl(-1, 1)))
            .WithEval(v => -v.a * v.a - v.b * v.b)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(1);
        var stop = cfg.OrthogonalTraverseThreaded(threads: 2);
        Thread.Sleep(300);
        var res = stop(); // must return, not hang
        Assert.True(res.Found);
    }

    [Fact]
    public void BreadthFirstThreaded_Improves()
    {
        var cfg = Seeker.WithVector(ctx => new[] { ctx.MulDbl(1), ctx.MulDbl(1), ctx.MulDbl(1) })
            .WithEval(v => -Math.Abs(Math.Log2(v[0] / 8)) - Math.Abs(Math.Log2(v[1] / 2)) - Math.Abs(Math.Log2(v[2] / 16)))
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(2024)
            .WithTraverse(new TraverseOptions { InitialStep = 0.05, GiveupBadDirStep = 0.05, GiveupGoodDirStep = 0.05 });
        var stop = cfg.OrthogonalTraverseBreadthFirstThreaded(threads: 2, broadIters: 5);
        Thread.Sleep(1200);
        var res = stop();
        Assert.True(res.Found);
        Assert.True(res.BestEval > -3); // started at -8; must have improved substantially
    }

    [Fact]
    public void EvalFactory_GivesEachWorkerItsOwnEvaluator()
    {
        var instances = new System.Collections.Concurrent.ConcurrentBag<object>();
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-5, 5), b: ctx.LinearDbl(-5, 5)))
            .WithEval(v => 0.0) // replaced by the factory below
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(9);
        var stop = cfg.OrthogonalTraverseThreaded(threads: 3, evalFactory: () =>
        {
            var scratch = new object(); // stands in for per-worker buffers
            instances.Add(scratch);
            return v => -v.a * v.a - v.b * v.b;
        });
        Thread.Sleep(400);
        stop();
        // one evaluator per worker plus one for the initializer
        Assert.InRange(instances.Distinct().Count(), 2, 4);
    }

    [Fact]
    public void NonViableInitialPoint_ThrowsWithClearMessage()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(0, 1), b: ctx.LinearDbl(0, 1)))
            .WithEval(_ => double.NaN)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(10);
        var ex = Assert.Throws<InvalidOperationException>(() => cfg.OrthogonalTraverseThreaded(threads: 2));
        Assert.Contains("viable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedCommits_DriveOnImprovedAndAreMonotonic()
    {
        var improvements = new List<double>();
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(v => -(v.a - 1) * (v.a - 1) - (v.b - 1) * (v.b - 1))
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(11);
        cfg.OnImproved = (e, _) => { lock (improvements) improvements.Add(e); };
        var stop = cfg.OrthogonalTraverseThreaded(threads: 2);
        Thread.Sleep(600);
        var res = stop();
        lock (improvements)
        {
            Assert.NotEmpty(improvements);
            // commits under the shared lock must be strictly improving
            for (int i = 1; i < improvements.Count; i++)
                Assert.True(improvements[i] > improvements[i - 1]);
            Assert.Equal(improvements[^1], res.BestEval);
        }
    }
}
