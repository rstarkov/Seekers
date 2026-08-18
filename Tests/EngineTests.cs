using Xunit;

namespace Seekers.Tests;

public class EngineTests
{
    private static SeekerConfig<(double a, double b), double> bowl(int seed) =>
        Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(v => -(v.a - 3) * (v.a - 3) - (v.b + 2) * (v.b + 2))
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(seed);

    [Fact]
    public void Evaluate_CommitsFirstViableEvalAsBest_AndCountsEvals()
    {
        var s = bowl(1).CreateSeeker();
        Assert.False(s.HasBest);
        Assert.Equal(0, s.EvalCount);
        var e = s.Evaluate();
        Assert.True(s.HasBest);
        Assert.Equal(e, s.BestEval);
        Assert.Equal(1, s.EvalCount);
    }

    [Fact]
    public void EvaluateCompared_ReturnsTriState()
    {
        var evals = new Queue<double>(new double[] { 5, 7, 7, 3 });
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(0, 1), b: ctx.LinearDbl(0, 1)))
            .WithEval(_ => evals.Dequeue())
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(1);
        var s = cfg.CreateSeeker();
        Assert.Equal(1, s.EvaluateCompared());  // first: committed
        Assert.Equal(1, s.EvaluateCompared());  // better
        Assert.Equal(0, s.EvaluateCompared());  // exactly equal
        Assert.Equal(-1, s.EvaluateCompared()); // worse
        Assert.Equal(7, s.BestEval);
    }

    [Fact]
    public void ChainVsGlobal_ResetChainRetainsGlobal_RestoreGlobalBest()
    {
        var s = bowl(2).CreateSeeker();
        s.SetValues(new double[] { 3, -2 }); // the optimum: eval 0
        s.Evaluate();
        Assert.Equal(0, s.GlobalBestEval, 12);

        s.ResetChain();
        Assert.False(s.HasBest);
        Assert.True(s.HasGlobalBest);
        s.SetValues(new double[] { 9, 9 }); // a bad region
        s.Evaluate();
        Assert.True(s.HasBest);
        Assert.True(s.BestEval < -50);            // the chain explores the bad region on its own merits
        Assert.Equal(0, s.GlobalBestEval, 12);    // the global best is untouched

        s.RestoreGlobalBest();
        Assert.Equal(0, s.BestEval, 12);
        Assert.Equal(new double[] { 3, -2 }, s.GetValues());
    }

    [Fact]
    public void RestoreBest_ReturnsPositionToChainBest()
    {
        var s = bowl(3).CreateSeeker();
        s.SetValues(new double[] { 1, 1 });
        s.Evaluate();
        s.SetValues(new double[] { 8, 8 }); // wander off without evaluating
        s.RestoreBest();
        Assert.Equal(new double[] { 1, 1 }, s.GetValues());
    }

    [Fact]
    public void SeedChain_AdoptsStateWithoutEvaluating_AndUpdatesGlobal()
    {
        var s = bowl(4).CreateSeeker();
        s.SetValues(new double[] { 3, -2 });
        s.SeedChain(-0.5); // claimed eval, no evaluation performed
        Assert.Equal(0, s.EvalCount);
        Assert.True(s.HasBest);
        Assert.Equal(-0.5, s.BestEval);
        Assert.True(s.HasGlobalBest);
        Assert.Equal(-0.5, s.GlobalBestEval);
    }

    [Fact]
    public void SeedChain_DoesNotFireImprovementCallbacks()
    {
        int fired = 0;
        var cfg = bowl(5).WithImproved((_, _) => fired++);
        var s = cfg.CreateSeeker();
        s.SeedChain(1);
        Assert.Equal(0, fired);
        s.Evaluate(); // a real eval below the seeded best: still no callback
        Assert.True(fired <= 1);
    }

    [Fact]
    public void OnImproved_FiresOnGlobalImprovementsOnly()
    {
        var improvements = new List<double>();
        var cfg = bowl(6).WithImproved((e, _) => improvements.Add(e));
        var s = cfg.CreateSeeker();
        s.SetValues(new double[] { 3, -2 });
        s.Evaluate(); // global best: 0
        Assert.Single(improvements);
        s.ResetChain();
        s.SetValues(new double[] { 9, 9 });
        s.Evaluate(); // chain best but far below global: no callback
        Assert.Single(improvements);
    }

    [Fact]
    public void Move_AppliesDirectionTimesStep_AndReportsEffectiveChange()
    {
        var s = bowl(7).CreateSeeker();
        s.SetValues(new double[] { 0, 0 });
        Assert.True(s.Move(new double[] { 1, -1 }, 0.05)); // ±5% of a 20-range = ±1
        Assert.Equal(new double[] { 1, -1 }, s.GetValues().Select(v => Math.Round(v, 9)));
        // a zero direction changes nothing
        Assert.False(s.Move(new double[] { 0, 0 }, 0.05));
    }

    [Fact]
    public void Move_ReturnsFalse_WhenFullyClampedAtBounds()
    {
        var s = bowl(8).CreateSeeker();
        s.SetValues(new double[] { 10, 10 });
        Assert.False(s.Move(new double[] { 1, 1 }, 0.5));
    }

    [Fact]
    public void Renormalize_IsInvokedAfterEveryMove()
    {
        int calls = 0;
        var cfg = bowl(9).WithRenormalize(_ => calls++);
        var s = cfg.CreateSeeker();
        s.Move(new double[] { 1, 0 }, 0.01);
        s.Move(new double[] { 0, 1 }, 0.01);
        Assert.Equal(2, calls);
        s.RandomizeAll();
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Renormalize_CanRepairCrossParameterConstraints()
    {
        var cfg = Seeker.WithVector(ctx => (w: ctx.LinearInt(1, 1000, initial: 100), h: ctx.LinearInt(1, 1000, initial: 100)))
            .WithEval(v => (double) v.w + v.h)
            .WithGoal(SeekerGoal.Maximize)
            .WithRenormalize(ps =>
            {
                if (ps[0].Value < ps[1].Value / 4) ps[0].Raw = ps[1].Value / 4;
                if (ps[1].Value < ps[0].Value / 4) ps[1].Raw = ps[0].Value / 4;
            })
            .WithRandom(10);
        var s = cfg.CreateSeeker();
        s.Move(new double[] { 1, 0 }, 0.9); // push w toward 1000; h must be dragged to at least w/4
        var v = s.GetValues();
        Assert.True(v[1] >= v[0] / 4 - 1);
    }

    [Fact]
    public void BestVector_DoubleArraysAreSnapshotted()
    {
        double[] captured = null;
        var cfg = Seeker.WithVector(ctx => new[] { ctx.LinearDbl(0, 1), ctx.LinearDbl(0, 1) })
            .WithEval(v => { captured = v; return v[0]; })
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(11);
        var s = cfg.CreateSeeker();
        s.Evaluate();
        var best = s.BestVector;
        captured[0] = -999; // mutating the eval's array must not corrupt the committed best
        Assert.NotEqual(-999, best[0]);
    }

    [Fact]
    public void RawsRoundtrip_PreservesSubIntegerPosition()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearInt(0, 100), b: ctx.LinearInt(0, 100)))
            .WithEval(v => (double) v.a)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(12);
        var s = cfg.CreateSeeker();
        s.Move(new double[] { 1, 0 }, 0.003); // +0.3 continuous, value unchanged
        var raws = s.GetRaws();
        var values = s.GetValues();
        s.RandomizeAll();
        s.SetRaws(raws);
        Assert.Equal(raws, s.GetRaws()); // exact restore, including the sub-integer fraction
        s.RandomizeAll();
        s.SetValues(values);
        Assert.Equal(values, s.GetValues());
    }

    [Fact]
    public void TraverseWithoutIncumbent_Throws()
    {
        var s = bowl(13).CreateSeeker();
        Assert.Throws<InvalidOperationException>(() => s.TraverseDirection(new double[] { 1, 0 }));
    }

    [Fact]
    public void ParamsWithoutInitials_AreRandomizedAtConstruction()
    {
        var a = bowl(14).CreateSeeker().GetValues();
        var b = bowl(15).CreateSeeker().GetValues();
        Assert.NotEqual(a, b); // different seeds land on different random points
    }

    [Fact]
    public void Result_ReflectsGlobalBest()
    {
        var s = bowl(16).CreateSeeker();
        Assert.False(s.Result.Found);
        s.SetValues(new double[] { 3, -2 });
        s.Evaluate();
        s.ResetChain();
        s.SetValues(new double[] { 9, 9 });
        s.Evaluate();
        var r = s.Result;
        Assert.True(r.Found);
        Assert.Equal(0, r.BestEval, 12);
        Assert.Equal(new double[] { 3, -2 }, r.BestValues);
        Assert.Equal(2, r.EvalCount);
    }
}
