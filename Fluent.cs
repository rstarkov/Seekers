namespace Seekers.Fluent;

public class SeekerBuilderWithVector<TVector>
{
    public SeekerBuilderWithVectorEval<TVector, TEval> WithEval<TEval>(Func<TVector, TEval> eval)
    {
        throw new NotImplementedException();
    }
}

public class SeekerBuilderWithVectorEval<TVector, TEval>
{
    public SeekerConfig<TVector, TEval> WithGoal(SeekerGoal goal)
    {
        return WithGoal(goal, Comparer<TEval>.Default);
    }

    public SeekerConfig<TVector, TEval> WithGoal(SeekerGoal goal, Comparer<TEval> comparer)
    {
        throw new NotImplementedException();
    }

    public SeekerConfig<TVector, TEval> WithGoal(SeekerGoal goal, Comparison<TEval> comparison)
    {
        throw new NotImplementedException();
    }
}
