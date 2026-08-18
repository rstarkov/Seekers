using Xunit;

namespace Seekers.Tests;

public class TraverseTests
{
    private static SeekerConfig<(double a, double b), double> bowl(int seed) =>
        Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10, initial: 0), b: ctx.LinearDbl(-10, 10, initial: 0)))
            .WithEval(v => -(v.a - 3) * (v.a - 3) - (v.b + 2) * (v.b + 2))
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(seed);

    [Fact]
    public void TraverseDirection_ImprovesAlongAUsefulDirection()
    {
        var s = bowl(1).CreateSeeker();
        s.Evaluate(); // eval at (0,0): -13
        var before = s.BestEval;
        Assert.True(s.TraverseDirection(new double[] { 1, 0 })); // +a is improving
        Assert.True(s.BestEval > before);
        Assert.True(Math.Abs(s.BestVector.a - 3) < 1); // walked most of the way to a=3
    }

    [Fact]
    public void TraverseDirection_TriesBackwardSense()
    {
        var s = bowl(2).CreateSeeker();
        s.Evaluate();
        Assert.True(s.TraverseDirection(new double[] { 0, 1 })); // +b is worse, -b improves
        Assert.True(s.BestVector.b < 0);
    }

    [Fact]
    public void TraverseDirection_GivesUpOnAUselessDirection_AndRestoresBest()
    {
        // at the optimum every direction is worse
        var s = bowl(3).CreateSeeker();
        s.SetValues(new double[] { 3, -2 });
        s.Evaluate();
        var evalsBefore = s.EvalCount;
        Assert.False(s.TraverseDirection(new double[] { 1, 0 }));
        Assert.Equal(new double[] { 3, -2 }, s.GetValues()); // position restored to the incumbent
        Assert.True(s.EvalCount > evalsBefore); // it did probe
    }

    [Fact]
    public void TraverseDirection_DoesNotMutateTheCallerDirectionArray()
    {
        var s = bowl(4).CreateSeeker();
        s.Evaluate();
        var dir = new double[] { 0, 1 };
        s.TraverseDirection(dir); // internally negated to find the improving sense
        Assert.Equal(new double[] { 0, 1 }, dir);
    }

    [Fact]
    public void CanGrowIfEqual_WalksStaircasePlateaus()
    {
        SeekerResult<(int x, int y), double> run(bool canGrow) =>
            Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 1000, initial: 500), y: ctx.LinearInt(0, 1000, initial: 500)))
                .WithEval(v => (double) (v.x / 100 + v.y / 100)) // staircase: optimum 20
                .WithGoal(SeekerGoal.Maximize)
                .WithRandom(4242)
                .WithTraverse(new TraverseOptions { CanGrowIfEqual = canGrow, InitialStep = 0.001 })
                .HillClimb(restarts: 3);
        Assert.True(run(true).BestEval >= 19);
    }

    [Fact]
    public void MaxStepFactor_BoundsARunawayImprovingDirection()
    {
        // unbounded improvement in +a: without the cap the step would grow forever
        var evals = 0;
        var cfg = Seeker.WithVector(ctx => new[] { ctx.MulDbl(1) })
            .WithEval(v => { evals++; return Math.Log(v[0]); }) // monotone improving as v grows
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(5)
            .WithTraverse(new TraverseOptions { InitialStep = 0.1, MaxStepFactor = 100 });
        var s = cfg.CreateSeeker();
        s.Evaluate();
        Assert.True(s.TraverseDirection(new double[] { 1 }));
        Assert.True(evals < 100); // terminated by the cap, not by running forever
    }

    [Fact]
    public void MoveTooSmallToChangeAnything_SkipsEvaluation()
    {
        // integer params: a step far below one unit changes no effective value, so no eval should be spent
        int evals = 0;
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 10, initial: 5), y: ctx.LinearInt(0, 10, initial: 5)))
            .WithEval(v => { evals++; return (double) v.x; })
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(6)
            .WithTraverse(new TraverseOptions { InitialStep = 0.0001, GiveupBadDirStep = 0.00009, CanGrowIfEqual = false });
        var s = cfg.CreateSeeker();
        s.Evaluate();
        var before = evals;
        s.TraverseDirection(new double[] { 1, 0 }); // probes are all sub-unit: nothing to evaluate
        Assert.Equal(before, evals);
    }

    [Fact]
    public void CoordinateSweep_OptimizesAxisByAxis()
    {
        var s = bowl(7).CreateSeeker();
        s.Evaluate();
        s.CoordinateSweep();
        Assert.True(s.BestEval > -0.5);
    }

    [Fact]
    public void OrthogonalTraverse_ReachesTheOptimumOfASmoothBowl()
    {
        var s = bowl(8).CreateSeeker();
        s.Evaluate();
        while (s.OrthogonalTraverse()) { }
        Assert.True(s.BestEval > -0.001);
        Assert.Equal(3, s.BestVector.a, 1);
        Assert.Equal(-2, s.BestVector.b, 1);
    }

    [Fact]
    public void TraverseDirectionForward_RidesASingleSense()
    {
        var s = bowl(9).CreateSeeker();
        s.Evaluate();
        s.TraverseDirectionForward(new double[] { 1, 0 }, step: 0.01);
        Assert.True(s.BestVector.a > 1); // walked toward 3, never probing backward
    }
}
