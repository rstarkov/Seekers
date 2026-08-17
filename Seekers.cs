using System.Collections.Concurrent;

namespace Seekers;

public record SeekerResult<TVector, TEval>
{
    public TVector BestVector { get; init; }
    public TEval BestEval { get; init; }
}

public record SeekerConfig<TVector, TEval>
{
    public SeekerGoal Goal { get; set; } = SeekerGoal.Maximize;
    public Func<VectorContext, TVector> MakeVector { get; set; }
    public Func<TVector, TEval> Evaluate { get; set; }
    public Func<TEval, TEval, int> Compare { get; set; }
    public IComparer<TEval> Comparer { get; set; }

    public int CompareUsingGoal(TEval a, TEval b)
    {
        int result;
        if (Compare != null)
            result = Compare(a, b);
        else if (Comparer != null)
            result = Comparer.Compare(a, b);
        else
            result = Comparer<TEval>.Default.Compare(a, b);
        if (Goal == SeekerGoal.Minimize)
            result = -result;
        return result;
    }
}

public enum SeekerGoal { Minimize = 1, Maximize }

public class SeekerBreakException : Exception { }

public static class Seeker
{
    public static Random DefaultRnd { get; set; } = new Random();

    public static SeekerResult<TVector, TEval> CreateResult<TVector, TEval>(TVector vector, TEval eval)
        => new SeekerResult<TVector, TEval> { BestVector = vector, BestEval = eval };

    public static Fluent.SeekerBuilderWithVector<TVector> WithVector<TVector>(Func<VectorContext, TVector> makeVector)
    {
        throw new NotImplementedException();
    }
    public static SeekerConfig<TVector, TEval> CreateConfig<TVector, TEval>(Func<VectorContext, TVector> makeVector, Func<TEval> dummyEval)
    {
        throw new NotImplementedException();
    }

    public static SeekerResult<TVector, TEval> FullyRandomVectors<TVector, TEval>(this SeekerConfig<TVector, TEval> config, int iterations)
    {
        throw new NotImplementedException();
    }
}

public abstract class VectorParam
{
    public double Value { get; protected set; }
    public abstract void Randomize(Random rnd);
    public abstract bool Move(double amount);
}

public class VectorContext
{
    private int _index = 0;
    private List<VectorParam> _parameters = new();

    public bool IsConfiguring { get; private set; }
    public IList<VectorParam> Parameters => _parameters.AsReadOnly();

    public void Configure<TVector>(Func<VectorContext, TVector> makeVector)
    {
        IsConfiguring = true;
        _index = 0;
        makeVector(this);
        IsConfiguring = false;
    }

    public void ConfigureParameter(VectorParam param)
    {
        _index++;
        if (_parameters.Count < _index)
            _parameters.Add(null);
        _parameters[_index - 1] = param;
    }

    public double GetParameterValue()
    {
        _index++;
        return _parameters[_index - 1].Value;
    }

    public TVector MakeVector<TVector>(Func<VectorContext, TVector> makeVector)
    {
        _index = 0;
        return makeVector(this);
    }
}

public static class VectorContextExtensions
{
    public class LinearIntParam : VectorParam
    {
        public int MinInclusive { get; set; }
        public int MaxInclusive { get; set; }

        public override bool Move(double amount)
        {
            var prev = Value;
            Value += amount;
            if (Value < MinInclusive - 0.49)
                Value = MinInclusive - 0.49;
            else if (Value > MaxInclusive + 0.49)
                Value = MaxInclusive + 0.49;
            return prev != Value;
        }

        public override void Randomize(Random rnd)
        {
            Value = rnd.Next(MinInclusive, MaxInclusive + 1);
        }
    }

    public static int LinearInt(this VectorContext ctx, int minInclusive, int maxInclusive)
    {
        if (!ctx.IsConfiguring)
            return (int)Math.Round(ctx.GetParameterValue());
        ctx.ConfigureParameter(new LinearIntParam { MinInclusive = minInclusive, MaxInclusive = maxInclusive });
        return 0;
    }
    public static int LogarithmicInt(this VectorContext ctx, int minInclusive, int maxInclusive)
    {
        throw new NotImplementedException();
    }
    public static double LinearDbl(this VectorContext ctx, double min, double max)
    {
        throw new NotImplementedException();
    }
    public static double RatioDbl(this VectorContext ctx, double min, double max)
    {
        throw new NotImplementedException();
    }
    public static double LogarithmicDbl(this VectorContext ctx, double min, double max)
    {
        throw new NotImplementedException();
    }
}

#if false
public abstract class SeekerBase<TVector, TEVal>
{
    private SeekerConfig<TVector, TEVal> _config;
    private Random _random = Seeker.DefaultRnd;

    public SeekerResult<TVector, TEVal> Result { get; protected set; }
    protected void EvaluateOnce()
    {
        var vector = vc.MakeVector(_config.MakeVector);
        var eval = _config.Evaluate(vector);
        if (_config.CompareUsingGoal(eval, Result.BestEval) > 0) // eval is greater (better) than BestEval
            Result = Seeker.CreateResult(vector, eval);
    }
}

public class RandomPointsSeeker<TVector, TEVal> : SeekerBase<TVector, TEVal>
{
    private int _iterations;

    public RandomPointsSeeker(SeekerConfig<TVector, TEVal> config, int iterations)
    {
        _config = config;
        _iterations = iterations;
    }

    private void search()
    {
        var vc = new VectorContext();
        vc.Configure(_config.MakeVector);
        for (int i = 0; i < _iterations; i++)
        {
            foreach (var p in vc.Parameters)
                p.Randomize(_random);
            EvaluateOnce();
        }
    }
}
#endif
