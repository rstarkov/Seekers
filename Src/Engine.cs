namespace Seekers;

/// <summary>Options for direction-traversal line searches. All step sizes are in abstract parameter step space.</summary>
public class TraverseOptions
{
    /// <summary>The first probe distance along a direction.</summary>
    public double InitialStep { get; set; } = 0.01;
    /// <summary>Step growth factor on success / shrink divisor on failure.</summary>
    public double StepGrowShrink { get; set; } = 1.62;
    /// <summary>Give up on a direction that never improved once the step shrinks below this.</summary>
    public double GiveupBadDirStep { get; set; } = 0.001;
    /// <summary>Stop refining a direction that did improve once the step shrinks below this.</summary>
    public double GiveupGoodDirStep { get; set; } = 0.001;
    /// <summary>
    ///     When both probe directions evaluate exactly equal to the incumbent (a plateau — common with integer-valued
    ///     or quantized objectives), grow the step aggressively instead of shrinking. Essential for staircase
    ///     landscapes; harmful when equality means "genuinely converged".</summary>
    public bool CanGrowIfEqual { get; set; } = false;
    /// <summary>Abort a runaway improving direction once the step exceeds InitialStep times this.</summary>
    public double MaxStepFactor { get; set; } = 100_000;

    public static TraverseOptions Default { get; } = new();
}

/// <summary>
///     The low-level search engine: current position, chain-local and global incumbents, evaluation, and traversal
///     primitives. Created via <see cref="SeekerConfig{TVector, TEval}.CreateSeeker"/>. Programs with bespoke search
///     schedules drive this directly; the packaged algorithms in <see cref="SeekerAlgorithms"/> are thin compositions
///     of the same primitives.
///     <para>
///         A "chain" is one hill-climbing run: traversals anchor on the chain best. <see cref="ResetChain"/> starts a
///         fresh chain (e.g. after a random restart) without forgetting the global best, so exploration of a worse
///         region is possible while the best-ever result is retained.</para>
///     <para>
///         Not thread-safe; one instance per thread. Evaluations happen only on the calling thread.</para></summary>
public class Seeker<TVector, TEval>
{
    public SeekerConfig<TVector, TEval> Config { get; }
    public Random Random { get; set; }
    public SeekerLog Log { get; set; }

    private readonly VectorContext _ctx = new();
    private readonly Func<TVector, TEval> _evaluate;
    private readonly Func<TEval, bool> _isViable;
    private double[] _bestRaws;       // chain best
    private double[] _globalRaws;     // global best

    public IReadOnlyList<VectorParam> Params => _ctx.Parameters;
    public long EvalCount { get; private set; }

    /// <summary>The best of the current chain; traversals anchor on this.</summary>
    public bool HasBest { get; private set; }
    public TEval BestEval { get; private set; }
    public TVector BestVector { get; private set; }

    /// <summary>The best across all chains. <see cref="Config"/>.OnImproved and checkpointing fire on this.</summary>
    public bool HasGlobalBest { get; private set; }
    public TEval GlobalBestEval { get; private set; }
    public TVector GlobalBestVector { get; private set; }

    public Seeker(SeekerConfig<TVector, TEval> config, Func<TVector, TEval> evaluateOverride = null)
    {
        Config = config;
        Random = config.Random ?? Seeker.DefaultRnd;
        Log = config.Log ?? SeekerLog.None;
        _evaluate = evaluateOverride ?? config.Evaluate;
        _isViable = config.IsViable ?? defaultViable();
        if (config.MakeVector == null)
            throw new InvalidOperationException("SeekerConfig.MakeVector is not set.");
        if (_evaluate == null && config.EvaluateWithBest == null)
            throw new InvalidOperationException("SeekerConfig.Evaluate is not set.");
        _ctx.Configure(config.MakeVector);
        if (Params.Count == 0)
            throw new InvalidOperationException("The vector declares no parameters.");
        foreach (var p in Params)
            if (!p.HasInitial)
                p.Randomize(Random);
        if (config.Checkpoint != null && config.Checkpoint.Resume)
        {
            var values = config.Checkpoint.TryLoadValues();
            if (values != null && values.Length == Params.Count)
            {
                SetValues(values);
                Log.Iteration($"resumed from checkpoint: {SeekerLog.Vec(values)}");
            }
        }
    }

    /// <summary>The effective values of all parameters at the current position, in declaration order.</summary>
    public double[] GetValues()
    {
        var values = new double[Params.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = Params[i].Value;
        return values;
    }

    /// <summary>Sets the current position from effective values (each clamped to its parameter's legal range).</summary>
    public void SetValues(double[] values)
    {
        for (int i = 0; i < Params.Count; i++)
            Params[i].Raw = values[i];
    }

    /// <summary>The continuous internal position (differs from values for integer parameters). For exact save/restore.</summary>
    public double[] GetRaws()
    {
        var raws = new double[Params.Count];
        for (int i = 0; i < raws.Length; i++)
            raws[i] = Params[i].Raw;
        return raws;
    }

    public void SetRaws(double[] raws)
    {
        for (int i = 0; i < Params.Count; i++)
            Params[i].Raw = raws[i];
    }

    /// <summary>Materializes the typed vector for the current position.</summary>
    public TVector MakeVector() => _ctx.MakeVector(Config.MakeVector);

    /// <summary>Sets every parameter to a random point in its natural space.</summary>
    public void RandomizeAll()
    {
        foreach (var p in Params)
            p.Randomize(Random);
        Config.Renormalize?.Invoke(Params);
    }

    /// <summary>
    ///     Moves the current position by <paramref name="step"/> along <paramref name="direction"/> (one component per
    ///     parameter, in abstract step space). Returns true if any parameter's effective value changed.</summary>
    public bool Move(double[] direction, double step)
    {
        bool changed = false;
        for (int i = 0; i < Params.Count; i++)
            if (direction[i] != 0)
                changed |= Params[i].Move(direction[i] * step);
        Config.Renormalize?.Invoke(Params);
        return changed;
    }

    /// <summary>
    ///     Evaluates the current position, updating the chain and global bests if improved. May throw <see
    ///     cref="SeekerBreakException"/> from the evaluation function; packaged algorithms catch it, custom loops
    ///     should too.</summary>
    public TEval Evaluate()
    {
        EvaluateCompared(out var eval);
        return eval;
    }

    /// <summary>
    ///     Evaluates the current position and returns its comparison against the chain best before this evaluation:
    ///     positive = better (and now committed as the new best), zero = exactly equal, negative = worse.</summary>
    public int EvaluateCompared() => EvaluateCompared(out _);

    private int EvaluateCompared(out TEval eval)
    {
        var vector = MakeVector();
        eval = Config.EvaluateWithBest != null
            ? Config.EvaluateWithBest(vector, HasGlobalBest ? GlobalBestEval : Config.WorstEval)
            : _evaluate(vector);
        EvalCount++;
        if (_isViable != null && !_isViable(eval))
            return -1; // never committed, never shown to the comparison functions
        int cmp = HasBest ? Config.CompareUsingGoal(eval, BestEval) : 1;
        if (cmp > 0)
            commitBest(vector, eval);
        return cmp;
    }

    /// <summary>
    ///     With no user-supplied viability predicate, NaN evaluations are rejected for double/float TEval — a NaN
    ///     incumbent would otherwise be possible (the first evaluation commits without comparison, and the default
    ///     comparer's total order ranks NaN as "smallest", which Minimize inverts into "best") — and null is rejected
    ///     for every TEval that can hold it (reference types and nullable value types).</summary>
    private static Func<TEval, bool> defaultViable()
    {
        if (typeof(TEval) == typeof(double))
            return (Func<TEval, bool>) (object) (Func<double, bool>) (e => !double.IsNaN(e));
        if (typeof(TEval) == typeof(float))
            return (Func<TEval, bool>) (object) (Func<float, bool>) (e => !float.IsNaN(e));
        if (typeof(TEval) == typeof(double?))
            return (Func<TEval, bool>) (object) (Func<double?, bool>) (e => e.HasValue && !double.IsNaN(e.Value));
        if (typeof(TEval) == typeof(float?))
            return (Func<TEval, bool>) (object) (Func<float?, bool>) (e => e.HasValue && !float.IsNaN(e.Value));
        if (default(TEval) is null) // reference types and Nullable<T>
            return e => e is not null;
        return null;
    }

    private void commitBest(TVector vector, TEval eval)
    {
        HasBest = true;
        BestEval = eval;
        BestVector = snapshot(vector);
        _bestRaws = GetRaws();
        if (!HasGlobalBest || Config.CompareUsingGoal(eval, GlobalBestEval) > 0)
        {
            HasGlobalBest = true;
            GlobalBestEval = eval;
            GlobalBestVector = BestVector;
            _globalRaws = _bestRaws;
            if (Log.WantImprovements)
                Log.Improvement($"IMPROVED: eval={eval}, values={SeekerLog.Vec(GetValues())}");
            Config.OnImproved?.Invoke(eval, BestVector);
            Config.Checkpoint?.Save(GetValues(), eval?.ToString());
        }
        else if (Log.WantIterations)
            Log.Iteration($"chain improved: eval={eval}");
    }

    private static TVector snapshot(TVector vector) => vector is double[] arr ? (TVector) (object) arr.Clone() : vector;

    /// <summary>
    ///     Adopts the current position as the chain best with a known evaluation, without evaluating and without
    ///     firing improvement callbacks or logging. For seeding a worker or continuing from externally known state.
    ///     Also updates the global best if better.</summary>
    public void SeedChain(TEval eval)
    {
        if (_isViable != null && !_isViable(eval))
            throw new ArgumentException("Cannot seed with a non-viable evaluation (see SeekerConfig.IsViable).");
        var vector = MakeVector();
        HasBest = true;
        BestEval = eval;
        BestVector = snapshot(vector);
        _bestRaws = GetRaws();
        if (!HasGlobalBest || Config.CompareUsingGoal(eval, GlobalBestEval) > 0)
        {
            HasGlobalBest = true;
            GlobalBestEval = eval;
            GlobalBestVector = BestVector;
            _globalRaws = _bestRaws;
        }
    }

    /// <summary>Starts a new chain: forgets the chain best (the global best is retained).</summary>
    public void ResetChain()
    {
        HasBest = false;
        BestEval = default;
        BestVector = default;
        _bestRaws = null;
    }

    /// <summary>Moves the current position back to the chain best.</summary>
    public void RestoreBest()
    {
        if (_bestRaws != null)
            SetRaws(_bestRaws);
    }

    /// <summary>Moves the current position to the global best and makes it the chain best too.</summary>
    public void RestoreGlobalBest()
    {
        if (_globalRaws == null)
            return;
        SetRaws(_globalRaws);
        HasBest = true;
        BestEval = GlobalBestEval;
        BestVector = GlobalBestVector;
        _bestRaws = _globalRaws;
    }

    /// <summary>An endless stream of random orthogonal direction bases, one direction at a time.</summary>
    public IEnumerable<double[]> RandomOrthogonalDirections()
    {
        while (true)
            foreach (var dir in Seeker.CreateRandomOrthogonalMatrix(Params.Count, Random))
                yield return dir;
    }

    /// <summary>
    ///     Line search along one direction, starting from the chain best. Phase 1 probes forward then backward with a
    ///     shrinking step to find an improving sense; phase 2 rides the improving sense with a growing step until it
    ///     stops paying. Returns true if any improvement was found. Requires an evaluated chain best.</summary>
    public bool TraverseDirection(double[] direction, TraverseOptions o = null)
    {
        o ??= Config.Traverse ?? TraverseOptions.Default;
        ensureBest();
        var dir = (double[]) direction.Clone();
        var maxStep = o.InitialStep * o.MaxStepFactor;
        double step = o.InitialStep;
        var canGrowIfEqual = o.CanGrowIfEqual;
        while (true)
        {
            // Try forward
            RestoreBest();
            int cmp1 = Move(dir, step) ? EvaluateCompared() : 0;
            if (Log.WantSteps) Log.Step($"  probe +{SeekerLog.Num(step)}: {(cmp1 > 0 ? "better" : cmp1 == 0 ? "equal" : "worse")}");
            if (cmp1 > 0)
            {
                step *= o.StepGrowShrink;
                break;
            }
            // Try backward
            RestoreBest();
            negate(dir);
            int cmp2 = Move(dir, step) ? EvaluateCompared() : 0;
            if (Log.WantSteps) Log.Step($"  probe -{SeekerLog.Num(step)}: {(cmp2 > 0 ? "better" : cmp2 == 0 ? "equal" : "worse")}");
            if (cmp2 > 0)
            {
                step *= o.StepGrowShrink;
                break;
            }
            negate(dir);
            // Neither sense improved: plateau or peak
            if (cmp1 == 0 && cmp2 == 0 && canGrowIfEqual)
                step *= 4;
            else
            {
                step /= o.StepGrowShrink;
                canGrowIfEqual = false;
            }
            if (step < o.GiveupBadDirStep)
            {
                RestoreBest();
                return false;
            }
        }
        if (step > maxStep)
        {
            RestoreBest();
            return true;
        }
        return TraverseDirectionForward(dir, step, o);
    }

    /// <summary>
    ///     Rides a single direction sense from the current position with an adaptive step until it stops improving.
    ///     The position is left at the chain best. Returns true (an improvement is assumed to have led here).</summary>
    public bool TraverseDirectionForward(double[] direction, double step, TraverseOptions o = null)
    {
        o ??= Config.Traverse ?? TraverseOptions.Default;
        ensureBest();
        var maxStep = step * o.MaxStepFactor;
        bool growOnEqual = o.CanGrowIfEqual;
        while (step >= o.GiveupGoodDirStep)
        {
            int cmp = Move(direction, step) ? EvaluateCompared() : 0;
            if (Log.WantSteps) Log.Step($"  step {SeekerLog.Num(step)}: {(cmp > 0 ? "better" : cmp == 0 ? "equal" : "worse")}");
            if (cmp > 0)
            {
                step *= o.StepGrowShrink;
                if (step > maxStep)
                    break;
                growOnEqual = true;
            }
            else if (cmp < 0)
            {
                RestoreBest();
                step /= o.StepGrowShrink;
                growOnEqual = false; // to prevent loops walking a plateau back and forth
            }
            else if (growOnEqual)
            {
                if (step > maxStep)
                    break;
                step *= o.StepGrowShrink;
            }
            else
                break;
        }
        RestoreBest();
        return true;
    }

    /// <summary>
    ///     One full sweep of line searches along a fresh random orthogonal basis. Returns true if any direction
    ///     improved. Requires an evaluated chain best (call <see cref="Evaluate"/> first).</summary>
    public bool OrthogonalTraverse(TraverseOptions o = null)
    {
        bool any = false;
        int n = 0;
        foreach (var dir in Seeker.CreateRandomOrthogonalMatrix(Params.Count, Random))
        {
            var improved = TraverseDirection(dir, o);
            any |= improved;
            if (Log.WantIterations) Log.Iteration($"direction {++n}/{Params.Count}: {(improved ? "improved to " + BestEval : "no improvement")}");
        }
        return any;
    }

    /// <summary>
    ///     One full sweep of line searches along the parameter axes (one parameter at a time). Suited to problems
    ///     where parameters are independent or rotations are meaningless (e.g. many integer parameters). Returns true
    ///     if any parameter's line search improved.</summary>
    public bool CoordinateSweep(TraverseOptions o = null)
    {
        bool any = false;
        var dir = new double[Params.Count];
        for (int i = 0; i < Params.Count; i++)
        {
            dir[i] = 1;
            var improved = TraverseDirection(dir, o);
            any |= improved;
            if (Log.WantIterations) Log.Iteration($"axis {Params[i].Name ?? i.ToString()}: {(improved ? "improved to " + BestEval : "no improvement")}");
            dir[i] = 0;
        }
        return any;
    }

    private void ensureBest()
    {
        if (!HasBest)
            throw new InvalidOperationException("No evaluated incumbent: call Evaluate() (or SeedChain) before traversing. "
                + "Note that non-viable evaluations (e.g. NaN) never become the incumbent; check HasBest after evaluating.");
    }

    private static void negate(double[] vector)
    {
        for (int i = 0; i < vector.Length; i++)
            vector[i] = -vector[i];
    }

    /// <summary>The result so far, taken from the global best.</summary>
    public SeekerResult<TVector, TEval> Result => new()
    {
        BestVector = GlobalBestVector,
        BestEval = GlobalBestEval,
        BestValues = HasGlobalBest ? valuesAt(_globalRaws) : null,
        Found = HasGlobalBest,
        EvalCount = EvalCount,
    };

    private double[] valuesAt(double[] raws)
    {
        var save = GetRaws();
        SetRaws(raws);
        var values = GetValues();
        SetRaws(save);
        return values;
    }
}
