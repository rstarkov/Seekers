using Xunit;

namespace Seekers.Tests;

public class ParamsTests
{
    [Fact]
    public void LinearDbl_MovesAdditively_ScaledToRange()
    {
        var p = new LinearDblParam(0, 10, initial: 5);
        Assert.True(p.Move(0.1)); // 0.1 of the range = 1.0
        Assert.Equal(6, p.Value, 12);
        Assert.True(p.Move(-0.2));
        Assert.Equal(4, p.Value, 12);
    }

    [Fact]
    public void LinearDbl_ClampsAtBounds_AndReportsNoChangeWhenPinned()
    {
        var p = new LinearDblParam(0, 10, initial: 9.5);
        Assert.True(p.Move(1)); // would be +10, clamps to 10
        Assert.Equal(10, p.Value);
        Assert.False(p.Move(0.5)); // already pinned at the bound
        Assert.Equal(10, p.Value);
        Assert.True(p.Move(-0.05));
    }

    [Fact]
    public void LinearDbl_CanReachAndCrossZero()
    {
        var p = new LinearDblParam(-5, 5, initial: 1);
        p.Move(-0.1); // -1.0
        Assert.Equal(0, p.Value, 12);
        p.Move(-0.1);
        Assert.Equal(-1, p.Value, 12);
    }

    [Fact]
    public void LinearInt_RoundsToNearest_AndAccumulatesSubUnitMoves()
    {
        var p = new LinearIntParam(0, 100, initial: 50);
        Assert.False(p.Move(0.002)); // +0.2 continuous — value still rounds to 50
        Assert.False(p.Move(0.002)); // +0.2 more — 50.4 still rounds to 50
        Assert.True(p.Move(0.002));  // 50.6 rounds to 51: sub-unit moves accumulated
        Assert.Equal(51, p.Value);
    }

    [Fact]
    public void LinearInt_ClampsInclusiveBounds()
    {
        var p = new LinearIntParam(1, 5, initial: 5);
        Assert.False(p.Move(1)); // pinned at max
        Assert.Equal(5, p.Value);
        Assert.True(p.Move(-1));
        Assert.Equal(1, p.Value);
        Assert.False(p.Move(-1));
        Assert.Equal(1, p.Value);
    }

    [Fact]
    public void LogarithmicDbl_MovesMultiplicatively_ScaledToLogRange()
    {
        var p = new LogarithmicDblParam(1, 10000, initial: 100);
        Assert.True(p.Move(0.25)); // a quarter of the log-range = one decade
        Assert.Equal(1000, p.Value, 6);
        Assert.True(p.Move(-0.5)); // two decades down
        Assert.Equal(10, p.Value, 6);
    }

    [Fact]
    public void LogarithmicDbl_RejectsNonPositiveBounds()
    {
        Assert.Throws<ArgumentException>(() => new LogarithmicDblParam(0, 10));
        Assert.Throws<ArgumentException>(() => new LogarithmicDblParam(-1, 10));
        Assert.Throws<ArgumentException>(() => new LogarithmicDblParam(10, 5));
    }

    [Fact]
    public void LogarithmicInt_RoundsAndClamps()
    {
        var p = new LogarithmicIntParam(1, 1000, initial: 10);
        Assert.True(p.Move(1)); // full log-range: ×100, clamps to 1000
        Assert.Equal(1000, p.Value);
        Assert.Throws<ArgumentException>(() => new LogarithmicIntParam(0, 10));
    }

    [Fact]
    public void RatioDbl_StepOfOneDoubles()
    {
        var p = new RatioDblParam(0.001, 1000, initial: 4);
        Assert.True(p.Move(1));
        Assert.Equal(8, p.Value, 9);
        Assert.True(p.Move(-2));
        Assert.Equal(2, p.Value, 9);
    }

    [Fact]
    public void MulDbl_Unbounded_StepOfOneDoubles_RequiresPositiveInitial()
    {
        var p = new MulDblParam(1);
        for (int i = 0; i < 30; i++)
            p.Move(1);
        Assert.Equal(Math.Pow(2, 30), p.Value, 3);
        Assert.Throws<ArgumentException>(() => new MulDblParam(0));
        Assert.Throws<ArgumentException>(() => new MulDblParam(-1));
    }

    [Fact]
    public void MulDbl_RandomizesWithinSpreadAroundInitial()
    {
        var p = new MulDblParam(100, randomizeSpread: 2);
        var rnd = new Random(42);
        for (int i = 0; i < 200; i++)
        {
            p.Randomize(rnd);
            Assert.InRange(p.Value, 100 / 4.0 - 1e-9, 100 * 4.0 + 1e-9);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BoundedParams_RandomizeWithinBounds(int kind)
    {
        var rnd = new Random(kind * 7 + 1);
        VectorParam p = kind switch
        {
            0 => new LinearDblParam(-3, 7),
            1 => new LogarithmicDblParam(0.01, 100),
            2 => new RatioDblParam(0.5, 32),
            _ => throw new Exception(),
        };
        (double min, double max) = kind switch
        {
            0 => (-3.0, 7.0),
            1 => (0.01, 100.0),
            _ => (0.5, 32.0),
        };
        for (int i = 0; i < 500; i++)
        {
            p.Randomize(rnd);
            Assert.InRange(p.Value, min, max);
        }
    }

    [Fact]
    public void LinearInt_RandomizeCoversInclusiveRange()
    {
        var p = new LinearIntParam(1, 3);
        var rnd = new Random(5);
        var seen = new HashSet<int>();
        for (int i = 0; i < 200; i++)
        {
            p.Randomize(rnd);
            seen.Add((int) p.Value);
        }
        Assert.Equal(new[] { 1, 2, 3 }, seen.OrderBy(x => x));
    }

    [Fact]
    public void RawSetter_ClampsToLegalRange()
    {
        var lin = new LinearDblParam(0, 10);
        lin.Raw = 99;
        Assert.Equal(10, lin.Value);
        lin.Raw = -99;
        Assert.Equal(0, lin.Value);

        var li = new LinearIntParam(0, 10);
        li.Raw = 10.4; // within the ±0.49 continuous band
        Assert.Equal(10, li.Value);
        li.Raw = 99;
        Assert.Equal(10, li.Value);
    }

    [Fact]
    public void InitialValues_AreHonoredAndClamped()
    {
        Assert.Equal(7, new LinearDblParam(0, 10, initial: 7).Value);
        Assert.Equal(10, new LinearDblParam(0, 10, initial: 25).Value);
        Assert.True(new LinearDblParam(0, 10, initial: 7).HasInitial);
        Assert.False(new LinearDblParam(0, 10).HasInitial);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var p = new LinearDblParam(0, 10, initial: 3);
        var c = p.Clone();
        p.Move(0.5);
        Assert.Equal(3, c.Value);
        Assert.Equal(8, p.Value, 12);
    }
}
