namespace Seekers;

public enum SeekerGoal { Minimize = 1, Maximize }

/// <summary>
///     Thrown by an evaluation function to abandon the current search gracefully. The search returns its best result
///     so far instead of propagating the exception.</summary>
public class SeekerBreakException : Exception { }

/// <summary>The outcome of a search: the best vector found, its evaluation, and bookkeeping.</summary>
public record SeekerResult<TVector, TEval>
{
    public TVector BestVector { get; init; }
    public TEval BestEval { get; init; }
    /// <summary>The effective values of every declared parameter at the best point, in declaration order.</summary>
    public double[] BestValues { get; init; }
    /// <summary>False if the search ended before completing a single successful evaluation.</summary>
    public bool Found { get; init; }
    public long EvalCount { get; init; }
}

/// <summary>
///     A fully specified optimization problem: how to build the typed vector, how to evaluate it, and how to compare
///     evaluations. Algorithms are terminal extension methods on this record (e.g. <see
///     cref="SeekerAlgorithms.FullyRandomVectors{TVector, TEval}"/>), so one config can be run by several algorithms.</summary>
public record SeekerConfig<TVector, TEval>
{
    public SeekerGoal Goal { get; set; } = SeekerGoal.Maximize;
    public Func<VectorContext, TVector> MakeVector { get; set; }
    public Func<TVector, TEval> Evaluate { get; set; }
    /// <summary>
    ///     Optional alternative to <see cref="Evaluate"/>: also receives the incumbent best evaluation (or <see
    ///     cref="WorstEval"/> before the first success), so the evaluation can abort early once it provably cannot
    ///     beat it.</summary>
    public Func<TVector, TEval, TEval> EvaluateWithBest { get; set; }
    /// <summary>A "worse than anything" sentinel, passed to <see cref="EvaluateWithBest"/> before any best exists.</summary>
    public TEval WorstEval { get; set; }
    public Func<TEval, TEval, int> Compare { get; set; }
    public IComparer<TEval> Comparer { get; set; }
    /// <summary>
    ///     Filters evaluations before they can become the incumbent: a value failing this predicate always ranks as
    ///     worse and is never committed as best (not even the very first evaluation), and the comparison functions are
    ///     never invoked with it. When null (the default), NaN is rejected for <c>double</c>/<c>float</c> evaluations
    ///     and everything is accepted for other TEval types. Set to <c>_ =&gt; true</c> to accept NaN too.</summary>
    public Func<TEval, bool> IsViable { get; set; }
    /// <summary>Randomness source; defaults to <see cref="Seeker.DefaultRnd"/>. Set for reproducible runs.</summary>
    public Random Random { get; set; }
    public SeekerLog Log { get; set; }
    /// <summary>Invoked whenever the global best improves. Receives the new best eval and vector.</summary>
    public Action<TEval, TVector> OnImproved { get; set; }
    /// <summary>
    ///     Invoked after every candidate move, before evaluation. Use for cross-parameter constraints that individual
    ///     parameter bounds cannot express (adjust offending parameters via <see cref="VectorParam.Raw"/>).</summary>
    public Action<IReadOnlyList<VectorParam>> Renormalize { get; set; }
    /// <summary>Optional persistence of the best point; see <see cref="SeekerCheckpoint"/>.</summary>
    public SeekerCheckpoint Checkpoint { get; set; }
    /// <summary>Default options for traversal-based algorithms; individual calls can override.</summary>
    public TraverseOptions Traverse { get; set; }

    /// <summary>
    ///     Compares two evaluations such that a positive result always means "<paramref name="a"/> is better". Uses
    ///     <see cref="Compare"/>, else <see cref="Comparer"/>, else the default comparer, negated under <see
    ///     cref="SeekerGoal.Minimize"/>.</summary>
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

    /// <summary>
    ///     Creates the low-level search engine for this problem, for programs that implement their own search loop.
    ///     See <see cref="Seeker{TVector, TEval}"/>.</summary>
    public Seeker<TVector, TEval> CreateSeeker(Func<TVector, TEval> evaluateOverride = null) => new(this, evaluateOverride);
}

/// <summary>Convenience mutators for <see cref="SeekerConfig{TVector, TEval}"/>, chainable in fluent style.</summary>
public static class SeekerConfigExtensions
{
    public static SeekerConfig<TV, TE> WithLog<TV, TE>(this SeekerConfig<TV, TE> cfg, SeekerLog log) { cfg.Log = log; return cfg; }
    public static SeekerConfig<TV, TE> WithLog<TV, TE>(this SeekerConfig<TV, TE> cfg, SeekerLogLevel level) { cfg.Log = SeekerLog.Console(level); return cfg; }
    public static SeekerConfig<TV, TE> WithRandom<TV, TE>(this SeekerConfig<TV, TE> cfg, Random random) { cfg.Random = random; return cfg; }
    public static SeekerConfig<TV, TE> WithRandom<TV, TE>(this SeekerConfig<TV, TE> cfg, int seed) { cfg.Random = new Random(seed); return cfg; }
    public static SeekerConfig<TV, TE> WithImproved<TV, TE>(this SeekerConfig<TV, TE> cfg, Action<TE, TV> onImproved) { cfg.OnImproved = onImproved; return cfg; }
    public static SeekerConfig<TV, TE> WithViable<TV, TE>(this SeekerConfig<TV, TE> cfg, Func<TE, bool> isViable) { cfg.IsViable = isViable; return cfg; }
    public static SeekerConfig<TV, TE> WithRenormalize<TV, TE>(this SeekerConfig<TV, TE> cfg, Action<IReadOnlyList<VectorParam>> renormalize) { cfg.Renormalize = renormalize; return cfg; }
    public static SeekerConfig<TV, TE> WithCheckpoint<TV, TE>(this SeekerConfig<TV, TE> cfg, string path) { cfg.Checkpoint = new SeekerCheckpoint(path); return cfg; }
    public static SeekerConfig<TV, TE> WithTraverse<TV, TE>(this SeekerConfig<TV, TE> cfg, TraverseOptions options) { cfg.Traverse = options; return cfg; }
}

public static class Seeker
{
    public static Random DefaultRnd { get; set; } = new Random();

    public static SeekerResult<TVector, TEval> CreateResult<TVector, TEval>(TVector vector, TEval eval)
        => new SeekerResult<TVector, TEval> { BestVector = vector, BestEval = eval, Found = true };

    /// <summary>
    ///     Entry point of the fluent API. The lambda both declares the parameters (via <c>ctx.LinearInt(...)</c> etc.)
    ///     and materializes the typed vector — typically a named tuple, so parameters are referenced by name with no
    ///     library ceremony. Continue with <c>.WithEval(...).WithGoal(...)</c>, then run an algorithm on the resulting
    ///     config.</summary>
    public static Fluent.SeekerBuilderWithVector<TVector> WithVector<TVector>(Func<VectorContext, TVector> makeVector)
    {
        return new Fluent.SeekerBuilderWithVector<TVector>(makeVector);
    }

    /// <summary>
    ///     Creates a config directly, without the fluent builder. <paramref name="dummyEval"/> is never invoked; it
    ///     only pins the <typeparamref name="TEval"/> type.</summary>
    public static SeekerConfig<TVector, TEval> CreateConfig<TVector, TEval>(Func<VectorContext, TVector> makeVector, Func<TEval> dummyEval)
    {
        return new SeekerConfig<TVector, TEval> { MakeVector = makeVector };
    }

    /// <summary>
    ///     Generates a uniformly random rotation of the identity basis: <paramref name="dimensions"/> mutually
    ///     orthogonal unit direction vectors. (Decrypted from http://www.cap-lore.com/MathPhys/Field/rorthog.c)</summary>
    public static double[][] CreateRandomOrthogonalMatrix(int dimensions, Random rnd = null)
    {
        rnd ??= DefaultRnd;
        double[][] matrix = new double[dimensions][];
        for (int i = 0; i < dimensions; i++)
        {
            matrix[i] = new double[dimensions];
            matrix[i][i] = 1;
        }

        for (int twists = 0; twists < 64; twists++)
        {
            int dim1 = rnd.Next(0, dimensions), dim2 = rnd.Next(0, dimensions);
            if (dim1 != dim2)
            {
                double theta = rnd.NextDouble() * 2 * Math.PI - Math.PI;
                double si = Math.Sin(theta), co = Math.Cos(theta);
                for (int k = 0; k < dimensions; k++)
                {
                    double t = co * matrix[k][dim1] + si * matrix[k][dim2];
                    matrix[k][dim2] = -si * matrix[k][dim1] + co * matrix[k][dim2];
                    matrix[k][dim1] = t;
                }
            }
        }

        return matrix;
    }
}
