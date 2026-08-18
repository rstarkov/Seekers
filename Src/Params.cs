namespace Seekers;

/// <summary>
///     A single optimizable parameter. Each parameter owns its own step semantics: algorithms move parameters by
///     abstract "amounts", and the parameter maps an amount onto its own scale (a fraction of the range for linear
///     parameters, a fraction of the log-range for logarithmic ones, octaves for ratio parameters). This is what makes
///     the core algorithms agnostic of additive vs multiplicative stepping.</summary>
public abstract class VectorParam
{
    /// <summary>Optional name, used only for logging and checkpoints.</summary>
    public string Name { get; set; }

    /// <summary>
    ///     The effective value of the parameter as seen by the evaluation function (rounded for integer parameters).</summary>
    public abstract double Value { get; }

    /// <summary>
    ///     The continuous internal position of the parameter. For integer parameters this can differ from <see
    ///     cref="Value"/> by up to ~0.5. Setting it clamps to the legal continuous range.</summary>
    public abstract double Raw { get; set; }

    /// <summary>True if the parameter was declared with an explicit initial value.</summary>
    public bool HasInitial { get; protected set; }

    /// <summary>Sets the parameter to a uniformly random point in its natural space.</summary>
    public abstract void Randomize(Random rnd);

    /// <summary>
    ///     Moves the parameter by <paramref name="amount"/> in its own step space. Returns true if the effective <see
    ///     cref="Value"/> changed (a move too small to change an integer parameter, or fully absorbed by clamping,
    ///     returns false).</summary>
    public abstract bool Move(double amount);

    /// <summary>Creates an independent copy of this parameter, including its current position.</summary>
    public abstract VectorParam Clone();
}

/// <summary>A continuous parameter moved additively within [Min, Max]. A move amount of 1 spans the whole range.</summary>
public class LinearDblParam : VectorParam
{
    public double Min, Max;
    private double _raw;

    public LinearDblParam(double min, double max, double? initial = null)
    {
        Min = min;
        Max = max;
        if (initial != null) { _raw = Math.Clamp(initial.Value, Min, Max); HasInitial = true; }
        else _raw = min;
    }

    public override double Value => _raw;
    public override double Raw { get => _raw; set => _raw = Math.Clamp(value, Min, Max); }
    public override void Randomize(Random rnd) => _raw = Min + rnd.NextDouble() * (Max - Min);
    public override bool Move(double amount)
    {
        var prev = _raw;
        Raw = _raw + amount * (Max - Min);
        return _raw != prev;
    }
    public override VectorParam Clone() => new LinearDblParam(Min, Max) { Name = Name, HasInitial = HasInitial, _raw = _raw };
}

/// <summary>
///     An integer parameter moved additively within [Min, Max] (inclusive). Internally continuous so that small steps
///     can accumulate; the effective value rounds to the nearest integer. A move amount of 1 spans the whole range.</summary>
public class LinearIntParam : VectorParam
{
    public int Min, Max;
    private double _raw;

    public LinearIntParam(int min, int max, int? initial = null)
    {
        Min = min;
        Max = max;
        if (initial != null) { _raw = Math.Clamp(initial.Value, Min, Max); HasInitial = true; }
        else _raw = min;
    }

    public override double Value => Math.Clamp(Math.Round(_raw), Min, Max);
    public override double Raw { get => _raw; set => _raw = Math.Clamp(value, Min - 0.49, Max + 0.49); }
    public override void Randomize(Random rnd) => _raw = rnd.Next(Min, Max + 1);
    public override bool Move(double amount)
    {
        var prev = Value;
        Raw = _raw + amount * Math.Max(1, Max - Min);
        return Value != prev;
    }
    public override VectorParam Clone() => new LinearIntParam(Min, Max) { Name = Name, HasInitial = HasInitial, _raw = _raw };
}

/// <summary>
///     A continuous parameter moved multiplicatively within [Min, Max]; both bounds must be positive. A move amount of
///     1 spans the whole log-range, so steps have equal relative resolution near both ends. Cannot reach or cross zero.</summary>
public class LogarithmicDblParam : VectorParam
{
    public double Min, Max;
    private double _raw;

    public LogarithmicDblParam(double min, double max, double? initial = null)
    {
        if (min <= 0 || max < min)
            throw new ArgumentException($"LogarithmicDbl requires 0 < min <= max (got {min}, {max})");
        Min = min;
        Max = max;
        if (initial != null) { _raw = Math.Clamp(initial.Value, Min, Max); HasInitial = true; }
        else _raw = min;
    }

    public override double Value => _raw;
    public override double Raw { get => _raw; set => _raw = Math.Clamp(value, Min, Max); }
    public override void Randomize(Random rnd) => _raw = Min * Math.Pow(Max / Min, rnd.NextDouble());
    public override bool Move(double amount)
    {
        var prev = _raw;
        Raw = _raw * Math.Pow(Max / Min, amount);
        return _raw != prev;
    }
    public override VectorParam Clone() => new LogarithmicDblParam(Min, Max) { Name = Name, HasInitial = HasInitial, _raw = _raw };
}

/// <summary>
///     An integer parameter moved multiplicatively within [Min, Max] (inclusive); Min must be at least 1. Internally
///     continuous; the effective value rounds to the nearest integer. A move amount of 1 spans the whole log-range.</summary>
public class LogarithmicIntParam : VectorParam
{
    public int Min, Max;
    private double _raw;

    public LogarithmicIntParam(int min, int max, int? initial = null)
    {
        if (min < 1 || max < min)
            throw new ArgumentException($"LogarithmicInt requires 1 <= min <= max (got {min}, {max})");
        Min = min;
        Max = max;
        if (initial != null) { _raw = Math.Clamp(initial.Value, Min, Max); HasInitial = true; }
        else _raw = min;
    }

    public override double Value => Math.Clamp(Math.Round(_raw), Min, Max);
    public override double Raw { get => _raw; set => _raw = Math.Clamp(value, Math.Max(0.51, Min - 0.49), Max + 0.49); }
    public override void Randomize(Random rnd) => _raw = Min * Math.Pow((double) Max / Min, rnd.NextDouble());
    public override bool Move(double amount)
    {
        var prev = Value;
        Raw = _raw * Math.Pow((double) Max / Min, amount);
        return Value != prev;
    }
    public override VectorParam Clone() => new LogarithmicIntParam(Min, Max) { Name = Name, HasInitial = HasInitial, _raw = _raw };
}

/// <summary>
///     A continuous parameter moved multiplicatively with an absolute step scale: a move amount of 1 doubles the value,
///     -1 halves it, regardless of the bounds. Clamped to [Min, Max]; both bounds must be positive. Randomizes
///     uniformly in log space over the bounds.</summary>
public class RatioDblParam : VectorParam
{
    public double Min, Max;
    private double _raw;

    public RatioDblParam(double min, double max, double? initial = null)
    {
        if (min <= 0 || max < min)
            throw new ArgumentException($"RatioDbl requires 0 < min <= max (got {min}, {max})");
        Min = min;
        Max = max;
        if (initial != null) { _raw = Math.Clamp(initial.Value, Min, Max); HasInitial = true; }
        else _raw = min;
    }

    public override double Value => _raw;
    public override double Raw { get => _raw; set => _raw = Math.Clamp(value, Min, Max); }
    public override void Randomize(Random rnd) => _raw = Min * Math.Pow(Max / Min, rnd.NextDouble());
    public override bool Move(double amount)
    {
        var prev = _raw;
        Raw = _raw * Math.Pow(2, amount);
        return _raw != prev;
    }
    public override VectorParam Clone() => new RatioDblParam(Min, Max) { Name = Name, HasInitial = HasInitial, _raw = _raw };
}

/// <summary>
///     An unbounded positive multiplicative parameter, for problems where no meaningful bounds exist (e.g. mixing
///     weights spanning many orders of magnitude). A move amount of 1 doubles the value, -1 halves it. Must be declared
///     with an initial value; <see cref="Randomize"/> multiplies the initial value by 2^U(-spread, +spread).</summary>
public class MulDblParam : VectorParam
{
    public double Initial;
    /// <summary>How far, in octaves either way, <see cref="Randomize"/> strays from the initial value.</summary>
    public double RandomizeSpread;
    private double _raw;

    public MulDblParam(double initial, double randomizeSpread = 2)
    {
        if (initial <= 0)
            throw new ArgumentException($"MulDbl requires a positive initial value (got {initial})");
        Initial = initial;
        RandomizeSpread = randomizeSpread;
        _raw = initial;
        HasInitial = true;
    }

    public override double Value => _raw;
    public override double Raw { get => _raw; set => _raw = value > 0 ? value : _raw; }
    public override void Randomize(Random rnd) => _raw = Initial * Math.Pow(2, (rnd.NextDouble() * 2 - 1) * RandomizeSpread);
    public override bool Move(double amount)
    {
        var prev = _raw;
        _raw = _raw * Math.Pow(2, amount);
        return _raw != prev;
    }
    public override VectorParam Clone() => new MulDblParam(Initial, RandomizeSpread) { Name = Name, _raw = _raw };
}

/// <summary>
///     The mechanism behind typed vectors. The same user lambda serves two purposes: on the configuration pass (<see
///     cref="Configure{TVector}"/>) each <c>ctx.LinearInt(...)</c>-style call registers a parameter; on every
///     subsequent <see cref="MakeVector{TVector}"/> call the same calls return the parameters' current values, so the
///     lambda materializes a fully typed vector (typically a named tuple) with zero library knowledge of its shape.</summary>
public class VectorContext
{
    private int _index = 0;
    private List<VectorParam> _parameters = new();

    public bool IsConfiguring { get; private set; }
    public IReadOnlyList<VectorParam> Parameters => _parameters;

    public void Configure<TVector>(Func<VectorContext, TVector> makeVector)
    {
        IsConfiguring = true;
        _index = 0;
        _parameters.Clear();
        makeVector(this);
        IsConfiguring = false;
    }

    /// <summary>Called by parameter declaration helpers during the configuration pass.</summary>
    public void ConfigureParameter(VectorParam param)
    {
        _index++;
        if (_parameters.Count < _index)
            _parameters.Add(null);
        _parameters[_index - 1] = param;
    }

    /// <summary>Called by parameter declaration helpers during a value pass.</summary>
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

/// <summary>Parameter declaration helpers for use inside a <c>Seeker.WithVector(ctx => ...)</c> lambda.</summary>
public static class VectorContextExtensions
{
    /// <summary>Declares an additively-stepped integer parameter in [minInclusive, maxInclusive].</summary>
    public static int LinearInt(this VectorContext ctx, int minInclusive, int maxInclusive, int? initial = null)
    {
        if (!ctx.IsConfiguring)
            return (int) ctx.GetParameterValue();
        ctx.ConfigureParameter(new LinearIntParam(minInclusive, maxInclusive, initial));
        return initial ?? minInclusive;
    }

    /// <summary>
    ///     Declares a multiplicatively-stepped integer parameter in [minInclusive, maxInclusive]; minInclusive must be
    ///     at least 1. Steps have equal relative resolution across the range, so this suits parameters whose range
    ///     spans an order of magnitude or more.</summary>
    public static int LogarithmicInt(this VectorContext ctx, int minInclusive, int maxInclusive, int? initial = null)
    {
        if (!ctx.IsConfiguring)
            return (int) ctx.GetParameterValue();
        ctx.ConfigureParameter(new LogarithmicIntParam(minInclusive, maxInclusive, initial));
        return initial ?? minInclusive;
    }

    /// <summary>Declares an additively-stepped continuous parameter in [min, max]. Can reach and cross zero.</summary>
    public static double LinearDbl(this VectorContext ctx, double min, double max, double? initial = null)
    {
        if (!ctx.IsConfiguring)
            return ctx.GetParameterValue();
        ctx.ConfigureParameter(new LinearDblParam(min, max, initial));
        return initial ?? min;
    }

    /// <summary>
    ///     Declares a multiplicatively-stepped continuous parameter in [min, max], with the step scaled to the
    ///     log-range; both bounds must be positive. Cannot reach or cross zero.</summary>
    public static double LogarithmicDbl(this VectorContext ctx, double min, double max, double? initial = null)
    {
        if (!ctx.IsConfiguring)
            return ctx.GetParameterValue();
        ctx.ConfigureParameter(new LogarithmicDblParam(min, max, initial));
        return initial ?? min;
    }

    /// <summary>
    ///     Declares a multiplicatively-stepped continuous parameter in [min, max] with an absolute step scale (a step
    ///     of 1 doubles the value); both bounds must be positive.</summary>
    public static double RatioDbl(this VectorContext ctx, double min, double max, double? initial = null)
    {
        if (!ctx.IsConfiguring)
            return ctx.GetParameterValue();
        ctx.ConfigureParameter(new RatioDblParam(min, max, initial));
        return initial ?? min;
    }

    /// <summary>
    ///     Declares an unbounded positive multiplicatively-stepped parameter (a step of 1 doubles the value).
    ///     Randomization strays up to <paramref name="randomizeSpread"/> octaves from the initial value.</summary>
    public static double MulDbl(this VectorContext ctx, double initial, double randomizeSpread = 2)
    {
        if (!ctx.IsConfiguring)
            return ctx.GetParameterValue();
        ctx.ConfigureParameter(new MulDblParam(initial, randomizeSpread));
        return initial;
    }
}
