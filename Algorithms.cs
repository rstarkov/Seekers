namespace Seekers;

/// <summary>
///     Packaged single-threaded search algorithms, as terminal extension methods on <see cref="SeekerConfig{TVector,
///     TEval}"/>. All of them honour <see cref="SeekerBreakException"/> thrown from the evaluation function by
///     returning the best result so far.</summary>
public static class SeekerAlgorithms
{
    /// <summary>Pure random search: evaluates <paramref name="iterations"/> fully random vectors and keeps the best.</summary>
    public static SeekerResult<TVector, TEval> FullyRandomVectors<TVector, TEval>(this SeekerConfig<TVector, TEval> config, int iterations)
    {
        var s = config.CreateSeeker();
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                s.RandomizeAll();
                s.Evaluate();
            }
        }
        catch (SeekerBreakException) { }
        config.Checkpoint?.SaveFinal();
        return s.Result;
    }

    /// <summary>
    ///     The core algorithm: hill climbing along random orthogonal directions, with random restarts. If the
    ///     parameters declare initial values (or <paramref name="startValues"/> is given), the climb starts there;
    ///     then <paramref name="restarts"/> random restarts each get their own climb; finally the global best is
    ///     polished once more. Each climb repeats full orthogonal sweeps until a sweep yields no improvement, up to
    ///     <paramref name="maxSweepsPerClimb"/>.</summary>
    public static SeekerResult<TVector, TEval> HillClimb<TVector, TEval>(this SeekerConfig<TVector, TEval> config,
        int restarts = 10, TraverseOptions options = null, double[] startValues = null, int maxSweepsPerClimb = 100)
    {
        var s = config.CreateSeeker();
        try
        {
            bool haveStart = startValues != null || s.Params.All(p => p.HasInitial) || (config.Checkpoint?.Resume == true && config.Checkpoint.TryLoadValues() != null);
            if (startValues != null)
                s.SetValues(startValues);
            if (haveStart)
            {
                s.Evaluate();
                s.Log.Iteration($"initial point: eval={s.BestEval}");
                climb(s, options, maxSweepsPerClimb);
            }
            for (int r = 0; r < restarts; r++)
            {
                s.ResetChain();
                s.RandomizeAll();
                s.Evaluate();
                s.Log.Iteration($"restart {r + 1}/{restarts}: eval={s.BestEval}");
                climb(s, options, maxSweepsPerClimb);
            }
            s.RestoreGlobalBest();
            climb(s, options, maxSweepsPerClimb);
        }
        catch (SeekerBreakException) { }
        config.Checkpoint?.SaveFinal();
        return s.Result;
    }

    private static void climb<TVector, TEval>(Seeker<TVector, TEval> s, TraverseOptions options, int maxSweeps)
    {
        for (int sweep = 0; sweep < maxSweeps; sweep++)
            if (!s.OrthogonalTraverse(options))
                return;
    }

    /// <summary>
    ///     Per-parameter (axis-aligned) coordinate descent: repeats full sweeps of single-parameter line searches
    ///     until a sweep yields no improvement. Best for problems with many integer parameters or independent axes,
    ///     where rotated directions round away to nothing. Starts from initial values / <paramref
    ///     name="startValues"/> if available, else from a random point.</summary>
    public static SeekerResult<TVector, TEval> CoordinateDescent<TVector, TEval>(this SeekerConfig<TVector, TEval> config,
        int restarts = 0, TraverseOptions options = null, double[] startValues = null, int maxSweepsPerClimb = 1000)
    {
        var s = config.CreateSeeker();
        try
        {
            bool haveStart = startValues != null || s.Params.All(p => p.HasInitial) || (config.Checkpoint?.Resume == true && config.Checkpoint.TryLoadValues() != null);
            if (startValues != null)
                s.SetValues(startValues);
            if (!haveStart)
                s.RandomizeAll();
            s.Evaluate();
            for (int sweep = 0; sweep < maxSweepsPerClimb; sweep++)
                if (!s.CoordinateSweep(options))
                    break;
            for (int r = 0; r < restarts; r++)
            {
                s.ResetChain();
                s.RandomizeAll();
                s.Evaluate();
                s.Log.Iteration($"restart {r + 1}/{restarts}: eval={s.BestEval}");
                for (int sweep = 0; sweep < maxSweepsPerClimb; sweep++)
                    if (!s.CoordinateSweep(options))
                        break;
            }
            s.RestoreGlobalBest();
        }
        catch (SeekerBreakException) { }
        config.Checkpoint?.SaveFinal();
        return s.Result;
    }
}
