using Xunit;

namespace Seekers.Tests;

public class ViabilityTests
{
    [Fact]
    public void NaN_NeverBecomesBest_UnderMinimize()
    {
        // the dangerous combination: default comparer's total order ranks NaN smallest, Minimize would invert
        // that into NaN-beats-everything; and the first eval commits without comparison
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(v => v.a < 0 ? double.NaN : (v.a - 3) * (v.a - 3) + v.b * v.b)
            .WithGoal(SeekerGoal.Minimize)
            .WithRandom(1111);
        var res = cfg.HillClimb(restarts: 10);
        Assert.True(res.Found);
        Assert.False(double.IsNaN(res.BestEval));
        Assert.True(res.BestEval < 0.01);
    }

    [Fact]
    public void AllNaN_ReportsFoundFalse()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(-10, 10), b: ctx.LinearDbl(-10, 10)))
            .WithEval(_ => double.NaN)
            .WithGoal(SeekerGoal.Minimize)
            .WithRandom(2);
        var res = cfg.HillClimb(restarts: 3);
        Assert.False(res.Found);
    }

    [Fact]
    public void CustomIsViable_FiltersBeforeComparison()
    {
        bool comparatorSawBad = false;
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 100), y: ctx.LinearInt(0, 100)))
            .WithEval(v => (score: (double) v.x + v.y, converged: v.x % 3 != 0))
            .WithGoal(SeekerGoal.Maximize, (a, b) =>
            {
                if (!a.converged || !b.converged) comparatorSawBad = true;
                return a.score.CompareTo(b.score);
            })
            .WithViable(e => e.converged)
            .WithRandom(3333);
        var res = cfg.FullyRandomVectors(500);
        Assert.True(res.Found);
        Assert.True(res.BestEval.converged);
        Assert.False(comparatorSawBad);
    }

    [Fact]
    public void OptOut_RestoresRawBehavior()
    {
        int evals = 0;
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(0, 1), b: ctx.LinearDbl(0, 1)))
            .WithEval(v => evals++ == 0 ? double.NaN : v.a)
            .WithGoal(SeekerGoal.Maximize)
            .WithViable(_ => true)
            .WithRandom(4);
        var s = cfg.CreateSeeker();
        s.Evaluate();
        Assert.True(s.HasBest);
        Assert.True(double.IsNaN(s.BestEval)); // first NaN committed when opted out
    }

    [Fact]
    public void NullReferenceEvals_RejectedByDefault_ComparatorNeverSeesNull()
    {
        bool sawNull = false;
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 100), y: ctx.LinearInt(0, 100)))
            .WithEval(v => v.x % 2 == 0 ? null : $"{v.x + v.y:000}")
            .WithGoal(SeekerGoal.Maximize, (a, b) =>
            {
                if (a == null || b == null) sawNull = true;
                return string.Compare(a, b, StringComparison.Ordinal);
            })
            .WithRandom(5150);
        var res = cfg.FullyRandomVectors(500);
        Assert.True(res.Found);
        Assert.NotNull(res.BestEval);
        Assert.False(sawNull);
    }

    [Fact]
    public void NullNullableEvals_RejectedByDefault()
    {
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 100), y: ctx.LinearInt(0, 100)))
            .WithEval(v => v.x % 2 == 0 ? (int?) null : v.x + v.y)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(5151);
        var res = cfg.FullyRandomVectors(500);
        Assert.True(res.Found);
        Assert.True(res.BestEval.HasValue);
    }

    [Fact]
    public void NullableDouble_RejectsBothNullAndNaN()
    {
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 99), y: ctx.LinearInt(0, 99)))
            .WithEval(v => v.x % 3 == 0 ? (double?) null : v.x % 3 == 1 ? (double?) double.NaN : v.x + v.y)
            .WithGoal(SeekerGoal.Minimize)
            .WithRandom(5152);
        var res = cfg.FullyRandomVectors(500);
        Assert.True(res.Found);
        Assert.True(res.BestEval.HasValue);
        Assert.False(double.IsNaN(res.BestEval.Value));
    }

    [Fact]
    public void AllNull_ReportsFoundFalse()
    {
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 9), y: ctx.LinearInt(0, 9)))
            .WithEval(_ => (string) null)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(5153);
        var res = cfg.HillClimb(restarts: 3);
        Assert.False(res.Found);
    }

    [Fact]
    public void Infinities_AreViableByDefault()
    {
        // ±Infinity is ordered and legitimately usable as a sentinel; only NaN is filtered
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(0, 1), b: ctx.LinearDbl(0, 1)))
            .WithEval(v => v.a > 0.5 ? double.PositiveInfinity : v.a)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(5154);
        var res = cfg.FullyRandomVectors(50);
        Assert.True(double.IsPositiveInfinity(res.BestEval));
    }

    [Fact]
    public void SeedChain_RejectsNonViableSeed()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(0, 1), b: ctx.LinearDbl(0, 1)))
            .WithEval(v => v.a)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(5155);
        var s = cfg.CreateSeeker();
        Assert.Throws<ArgumentException>(() => s.SeedChain(double.NaN));
    }

    [Fact]
    public void NonViableEvals_StillCount()
    {
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearDbl(0, 1), b: ctx.LinearDbl(0, 1)))
            .WithEval(_ => double.NaN)
            .WithGoal(SeekerGoal.Maximize)
            .WithRandom(5156);
        var res = cfg.FullyRandomVectors(25);
        Assert.Equal(25, res.EvalCount);
    }
}
