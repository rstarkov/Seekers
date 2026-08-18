using Xunit;

namespace Seekers.Tests;

public class AlgorithmsTests
{
    [Fact]
    public void HillClimb_FindsBowlOptimum_WithMixedParamTypes()
    {
        var cfg = Seeker.WithVector(ctx =>
        (
            a: ctx.LinearDbl(-10, 10),          // optimum at 3
            b: ctx.LogarithmicDbl(0.001, 1000), // optimum at 5
            c: ctx.LinearInt(0, 100)            // optimum at 42
        ))
        .WithEval(v => Math.Pow(v.a - 3, 2) + Math.Pow(Math.Log(v.b / 5), 2) + Math.Pow(v.c - 42, 2))
        .WithGoal(SeekerGoal.Minimize)
        .WithRandom(12345);

        var res = cfg.HillClimb(restarts: 5);
        Assert.True(res.Found);
        Assert.True(res.BestEval < 0.01);
        Assert.Equal(3, res.BestVector.a, 1);
        Assert.Equal(42, res.BestVector.c);
        Assert.Equal(res.BestVector.a, res.BestValues[0], 9);
    }

    [Fact]
    public void FullyRandomVectors_KeepsTheBestOfNSamples()
    {
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(1, 100), y: ctx.LinearInt(1, 100)))
            .WithEval(v => v.x + v.y)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(999);
        var res = cfg.FullyRandomVectors(2000);
        Assert.True(res.Found);
        Assert.True(res.BestEval >= 190);
        Assert.Equal(2000, res.EvalCount);
    }

    [Fact]
    public void SeekerBreakException_AbortsAndReturnsBestSoFar()
    {
        int evals = 0;
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(1, 100), y: (byte) ctx.LinearInt(0, 255)))
            .WithEval(v =>
            {
                evals++;
                if (evals > 500)
                    throw new SeekerBreakException();
                return v.x + v.y;
            })
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(999);
        var res = cfg.FullyRandomVectors(10_000);
        Assert.Equal(501, evals);
        Assert.True(res.Found);
        Assert.True(res.BestEval > 100);
    }

    [Fact]
    public void HillClimb_UsesStartValues_AndInitials()
    {
        // 0 restarts: only the start point is climbed, so the result must be in its basin
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(v => -Math.Min((v.a - 4) * (v.a - 4), (v.a + 4) * (v.a + 4)) - v.b * v.b) // two symmetric optima at a=±4
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(77);
        var res = cfg.HillClimb(restarts: 0, startValues: new double[] { 3, 0 });
        Assert.Equal(4, res.BestVector.a, 1); // climbed to the near optimum, not the far one
    }

    [Fact]
    public void CoordinateDescent_FindsExactIntegerOptimum()
    {
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 500), y: ctx.LinearInt(0, 500)))
            .WithEval(v => -Math.Abs(v.x - 321) - Math.Abs(v.y - 123))
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(606);
        var res = cfg.CoordinateDescent(restarts: 2);
        Assert.Equal(0, res.BestEval);
        Assert.Equal(321, res.BestVector.x);
        Assert.Equal(123, res.BestVector.y);
    }

    [Fact]
    public void SameSeed_SameResult()
    {
        SeekerResult<(double a, double b), double> run() =>
            Seeker.WithVector(ctx => (a: ctx.LinearDbl(-5, 5), b: ctx.LinearDbl(-5, 5)))
                .WithEval(v => Math.Sin(v.a * 3) + Math.Cos(v.b * 2) - 0.1 * v.a * v.a)
                .WithGoal(SeekerGoal.Maximize)
                .WithRandom(31415)
                .HillClimb(restarts: 4);
        var r1 = run();
        var r2 = run();
        Assert.Equal(r1.BestEval, r2.BestEval);
        Assert.Equal(r1.BestValues, r2.BestValues);
        Assert.Equal(r1.EvalCount, r2.EvalCount);
    }

    [Fact]
    public void MulDblParams_ConvergeMultiplicatively()
    {
        var cfg = Seeker.WithVector(ctx => new[] { ctx.MulDbl(1.0), ctx.MulDbl(10.0), ctx.MulDbl(100.0) })
            .WithEval(v => -Math.Pow(Math.Log2(v[0] / 4), 2) - Math.Pow(Math.Log2(v[1] / 80), 2) - Math.Pow(Math.Log2(v[2] / 20), 2))
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(55);
        var res = cfg.HillClimb(restarts: 8);
        Assert.True(res.BestEval > -0.01);
        Assert.Equal(4, res.BestVector[0], 1);
    }

    [Fact]
    public void NestedSearch_InnerResultAsOuterEval()
    {
        var outer = Seeker.WithVector(ctx => (p: ctx.LinearInt(1, 10), q: ctx.LinearInt(1, 10)))
            .WithEval(v1 =>
            {
                var inner = Seeker.WithVector(ctx => (r: ctx.LinearInt(1, 10), s: ctx.LinearInt(1, 10)))
                    .WithEval(v2 => v1.p * v2.r + v1.q * v2.s)
                    .WithGoal(SeekerGoal.Maximize)
                    .WithRandom(7);
                return inner.FullyRandomVectors(50);
            })
            .WithGoal(SeekerGoal.Maximize, (a, b) => a.BestEval.CompareTo(b.BestEval))
            .WithRandom(8);
        var res = outer.FullyRandomVectors(50);
        Assert.True(res.Found);
        // the inner result must be internally consistent with the outer vector it was produced for
        Assert.Equal(res.BestEval.BestEval,
            res.BestVector.p * res.BestEval.BestVector.r + res.BestVector.q * res.BestEval.BestVector.s);
        Assert.True(res.BestEval.BestEval >= 120); // near the 200 maximum; exact value depends on sampling
    }

    [Fact]
    public void MaxSweepsPerClimb_LimitsWork()
    {
        long unlimited = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(v => -v.a * v.a - v.b * v.b)
            .WithGoal(SeekerGoal.Maximize).WithRandom(3)
            .HillClimb(restarts: 2).EvalCount;
        long limited = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(v => -v.a * v.a - v.b * v.b)
            .WithGoal(SeekerGoal.Maximize).WithRandom(3)
            .HillClimb(restarts: 2, maxSweepsPerClimb: 1).EvalCount;
        Assert.True(limited < unlimited);
    }
}
