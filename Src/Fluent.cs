namespace Seekers.Fluent;

public class SeekerBuilderWithVector<TVector>
{
    private readonly Func<VectorContext, TVector> _makeVector;

    public SeekerBuilderWithVector(Func<VectorContext, TVector> makeVector)
    {
        _makeVector = makeVector;
    }

    public SeekerBuilderWithVectorEval<TVector, TEval> WithEval<TEval>(Func<TVector, TEval> eval)
    {
        return new SeekerBuilderWithVectorEval<TVector, TEval>(_makeVector, eval, null, default);
    }

    /// <summary>
    ///     Declares an incumbent-aware evaluation: it receives the best evaluation so far (or <paramref
    ///     name="worstEval"/> before any exists) so it can abort early once it provably cannot win.</summary>
    public SeekerBuilderWithVectorEval<TVector, TEval> WithEval<TEval>(Func<TVector, TEval, TEval> evalWithBest, TEval worstEval)
    {
        return new SeekerBuilderWithVectorEval<TVector, TEval>(_makeVector, null, evalWithBest, worstEval);
    }
}

public class SeekerBuilderWithVectorEval<TVector, TEval>
{
    private readonly Func<VectorContext, TVector> _makeVector;
    private readonly Func<TVector, TEval> _eval;
    private readonly Func<TVector, TEval, TEval> _evalWithBest;
    private readonly TEval _worstEval;

    public SeekerBuilderWithVectorEval(Func<VectorContext, TVector> makeVector, Func<TVector, TEval> eval,
        Func<TVector, TEval, TEval> evalWithBest, TEval worstEval)
    {
        _makeVector = makeVector;
        _eval = eval;
        _evalWithBest = evalWithBest;
        _worstEval = worstEval;
    }

    public SeekerConfig<TVector, TEval> WithGoal(SeekerGoal goal)
    {
        return makeConfig(goal, null, null);
    }

    public SeekerConfig<TVector, TEval> WithGoal(SeekerGoal goal, Comparer<TEval> comparer)
    {
        return makeConfig(goal, null, comparer);
    }

    public SeekerConfig<TVector, TEval> WithGoal(SeekerGoal goal, IComparer<TEval> comparer)
    {
        return makeConfig(goal, null, comparer);
    }

    public SeekerConfig<TVector, TEval> WithGoal(SeekerGoal goal, Comparison<TEval> comparison)
    {
        return makeConfig(goal, (a, b) => comparison(a, b), null);
    }

    private SeekerConfig<TVector, TEval> makeConfig(SeekerGoal goal, Func<TEval, TEval, int> compare, IComparer<TEval> comparer)
    {
        return new SeekerConfig<TVector, TEval>
        {
            Goal = goal,
            MakeVector = _makeVector,
            Evaluate = _eval,
            EvaluateWithBest = _evalWithBest,
            WorstEval = _worstEval,
            Compare = compare,
            Comparer = comparer,
        };
    }
}
