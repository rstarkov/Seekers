using Xunit;

namespace Seekers.Tests;

public class VectorContextTests
{
    [Fact]
    public void ConfigurePass_RegistersParams_ValuePass_ReadsThem()
    {
        var ctx = new VectorContext();
        Func<VectorContext, (int a, double b)> make = c => (a: c.LinearInt(1, 10, initial: 4), b: c.LinearDbl(0, 1, initial: 0.5));
        ctx.Configure(make);
        Assert.Equal(2, ctx.Parameters.Count);
        var v = ctx.MakeVector(make);
        Assert.Equal(4, v.a);
        Assert.Equal(0.5, v.b);
    }

    [Fact]
    public void Materialization_ReflectsCurrentParamValues()
    {
        var ctx = new VectorContext();
        Func<VectorContext, (int a, double b)> make = c => (a: c.LinearInt(1, 10, initial: 4), b: c.LinearDbl(0, 1, initial: 0.5));
        ctx.Configure(make);
        ctx.Parameters[0].Raw = 9;
        ctx.Parameters[1].Raw = 0.25;
        var v = ctx.MakeVector(make);
        Assert.Equal(9, v.a);
        Assert.Equal(0.25, v.b);
    }

    [Fact]
    public void CastsAndArithmetic_SurviveTheConfigurePass()
    {
        // user code may cast or transform the returned value; the configure pass must tolerate it
        var ctx = new VectorContext();
        Func<VectorContext, (byte color, int doubled)> make = c => (color: (byte) c.LinearInt(0, 255, initial: 10), doubled: c.LinearInt(1, 5, initial: 2) * 2);
        ctx.Configure(make);
        Assert.Equal(2, ctx.Parameters.Count);
        var v = ctx.MakeVector(make);
        Assert.Equal((byte) 10, v.color);
        Assert.Equal(4, v.doubled);
    }

    [Fact]
    public void DynamicDeclaration_ViaLoop()
    {
        var seeds = new double[] { 1, 10, 100 };
        var ctx = new VectorContext();
        Func<VectorContext, double[]> make = c => seeds.Select(s => c.MulDbl(s)).ToArray();
        ctx.Configure(make);
        Assert.Equal(3, ctx.Parameters.Count);
        Assert.Equal(seeds, ctx.MakeVector(make));
    }
}

public class ConfigTests
{
    [Fact]
    public void CompareUsingGoal_DefaultComparer_MaximizeAndMinimize()
    {
        var max = new SeekerConfig<int, double> { Goal = SeekerGoal.Maximize };
        Assert.True(max.CompareUsingGoal(2, 1) > 0);
        var min = new SeekerConfig<int, double> { Goal = SeekerGoal.Minimize };
        Assert.True(min.CompareUsingGoal(2, 1) < 0);
        Assert.Equal(0, min.CompareUsingGoal(2, 2));
    }

    [Fact]
    public void CompareUsingGoal_CustomComparisonWins_GoalOnlyNegatesForMinimize()
    {
        var cfg = new SeekerConfig<int, (int k, int tie)>
        {
            Goal = SeekerGoal.Minimize,
            Compare = (a, b) => a.k.CompareTo(b.k),
        };
        Assert.True(cfg.CompareUsingGoal((1, 0), (2, 0)) > 0); // smaller k is better under Minimize
    }

    [Fact]
    public void CompareUsingGoal_IComparerPath()
    {
        var cfg = new SeekerConfig<int, string>
        {
            Goal = SeekerGoal.Maximize,
            Comparer = StringComparer.OrdinalIgnoreCase,
        };
        Assert.True(cfg.CompareUsingGoal("b", "A") > 0);
        Assert.Equal(0, cfg.CompareUsingGoal("a", "A"));
    }

    [Fact]
    public void FluentBuilder_BindsComparisonOverload_AndProducesWorkingConfig()
    {
        var cfg = Seeker.WithVector(ctx => (x: ctx.LinearInt(0, 10), y: ctx.LinearInt(0, 10)))
            .WithEval(v => (score: v.x + v.y, tie: v.x))
            .WithGoal(SeekerGoal.Maximize, (a, b) => a.score.CompareTo(b.score));
        Assert.NotNull(cfg.MakeVector);
        Assert.NotNull(cfg.Evaluate);
        Assert.NotNull(cfg.Compare);
        Assert.True(cfg.CompareUsingGoal((5, 0), (3, 9)) > 0);
    }

    [Fact]
    public void CreateConfig_PinsEvalType_WithoutInvokingDummy()
    {
        var cfg = Seeker.CreateConfig(ctx => ctx.LinearDbl(0, 1), () => 0.0);
        Assert.NotNull(cfg.MakeVector);
        Assert.Null(cfg.Evaluate); // dummy is never installed or invoked
    }

    [Fact]
    public void WorstEval_IsPassedToIncumbentAwareEval_BeforeAnyBestExists()
    {
        double? firstIncumbent = null;
        var cfg = Seeker.WithVector(ctx => (a: ctx.LinearInt(0, 10, initial: 5), b: ctx.LinearInt(0, 10, initial: 5)))
            .WithEval((v, best) => { firstIncumbent ??= best; return (double) v.a; }, worstEval: -123.0)
            .WithGoal(SeekerGoal.Maximize);
        cfg.CreateSeeker().Evaluate();
        Assert.Equal(-123, firstIncumbent);
    }

    [Fact]
    public void MissingMakeVectorOrEval_ThrowsOnSeekerCreation()
    {
        var noMake = new SeekerConfig<int, double> { Evaluate = _ => 0 };
        Assert.Throws<InvalidOperationException>(() => noMake.CreateSeeker());
        var noEval = new SeekerConfig<int, double> { MakeVector = ctx => ctx.LinearInt(0, 1) };
        Assert.Throws<InvalidOperationException>(() => noEval.CreateSeeker());
    }
}

public class RandomOrthogonalMatrixTests
{
    [Fact]
    public void Rows_AreOrthonormal()
    {
        var m = Seeker.CreateRandomOrthogonalMatrix(6, new Random(99));
        for (int i = 0; i < 6; i++)
        {
            var norm = Math.Sqrt(m[i].Sum(x => x * x));
            Assert.Equal(1.0, norm, 9);
            for (int j = i + 1; j < 6; j++)
            {
                var dot = Enumerable.Range(0, 6).Sum(k => m[i][k] * m[j][k]);
                Assert.Equal(0.0, dot, 9);
            }
        }
    }

    [Fact]
    public void SeededGeneration_IsDeterministic()
    {
        var a = Seeker.CreateRandomOrthogonalMatrix(4, new Random(7));
        var b = Seeker.CreateRandomOrthogonalMatrix(4, new Random(7));
        for (int i = 0; i < 4; i++)
            Assert.Equal(a[i], b[i]);
    }
}
