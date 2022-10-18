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

public abstract class SeekerBase<TVector, TEVal>
{
    public SeekerResult<TVector, TEVal> Result { get; protected set; }
}

public class RandomPointsSeeker<TVector, TEVal> : SeekerBase<TVector, TEVal>
{
    private SeekerConfig<TVector, TEVal> _config;
    private int _iterations;
    private Random _random = Seeker.DefaultRnd;

    private void search()
    {
        var vc = new VectorContext();
        vc.Configure(_config.MakeVector);
        for (int i = 0; i < _iterations; i++)
        {
            foreach (var p in vc.Parameters)
                p.Randomize(_random);
            var vector = _config.MakeVector(vc);
            var eval = _config.Evaluate(vector);
            if (_config.CompareUsingGoal(eval, Result.BestEval) > 0) // eval is greater (better) than BestEval
                Result = Seeker.CreateResult(vector, eval);
        }
    }
}

public class RomanOptim
{
    public int ItersRandomGuess = 10;

    public Action<string> Log = s => { };
    public Func<double[], double[]> GenerateRandomVector = null;
    public Func<double[], double> Evaluate = null;

    public void OptimizeOnce(ref double bestEval, ref double[] bestVector)
    {
        Log($"=== Optimize once: bestEval={bestEval:#,0.#####} ===");
        double[] vector = null;
        for (int i = 0; i < ItersRandomGuess; i++)
        {
            vector = GenerateRandomVector(bestVector);
            var eval = Evaluate(vector);
            Log($"Random vector: eval={eval:#,0.#####} ===");
            OrthogonalTraverse(ref eval, ref vector);
            Log($"Random vector after ortho: eval={eval:#,0.#####} ===");
            //PerDimensionTraverse(ref eval, ref vector);
            Log($"Random vector after per-dim: eval={eval:#,0.#####} ===");
            if (eval > bestEval)
            {
                bestEval = eval;
                bestVector = vector.ToArray();
            }
        }

        OrthogonalTraverse(ref bestEval, ref bestVector);
        Log($"Best vector after ortho: eval={bestEval:#,0.#####} ===");
        //PerDimensionTraverse(ref bestEval, ref bestVector);
        Log($"Best vector after per-dim: eval={bestEval:#,0.#####} ===");
    }

    public void OptimiseThreaded(double bestEval, double[] bestVector, int threadCount)
    {
        var threads = Enumerable.Range(0, threadCount).Select(_ => new Thread(() =>
        {
            while (true)
            {
                //OptimizeOnce(ref eval, ref vector, (newEval, newVector) =>
                //{
                //    lock ("quickhack-lock")
                //    {
                //        if (improved(newEval, bestEval))
                //        {
                //            bestVector = newVector.ToArray();
                //            bestEval = newEval;
                //            Log($"IMPROVEMENT COMMITTED: eval={bestEval:#,0.#####}");
                //        }
                //    }
                //});
            }
        })).ToList();
        foreach (var t in threads)
        {
            t.Priority = ThreadPriority.Lowest;
            t.Start();
        }
        foreach (var t in threads)
            t.Join();
    }

    private static void moveVector(double[] vector, double[] direction, double step)
    {
        static double mul(double size) => size > 0 ? 1 + size : size < 0 ? 1 / (1 - size) : 1;
        for (int i = 0; i < vector.Length; i++)
            vector[i] *= mul(direction[i] * step); // (dir[i]*step) = 1 means "double"; -1 means "halve"; 0 means no change
    }

    private static void negateDir(double[] vector)
    {
        for (int i = 0; i < vector.Length; i++)
            vector[i] = -vector[i];
    }

    private bool improved(double eval, double bestEval)
    {
        return eval > bestEval;
    }

    public bool OrthogonalTraverse(ref double bestEval, ref double[] bestVector, double initialStep = 0.01, double stepGrowShrink = 1.62, double giveupBadDirStep = 0.001, double giveupGoodDirStep = 0.001)
    {
        bool anyImprovements = false;
        foreach (var dir in CreateRandomOrthogonalMatrix(bestVector.Length))
            anyImprovements |= TraverseDirection(dir, ref bestEval, ref bestVector, initialStep, stepGrowShrink, giveupBadDirStep, giveupGoodDirStep);
        return anyImprovements;
    }

    public void OrthogonalTraverseThreaded(double bestEval, double[] bestVector, int threads, double initialStep = 0.01, double stepGrowShrink = 1.62, double giveupBadDirStep = 0.001, double giveupGoodDirStep = 0.001)
    {
        var directions = new BlockingCollection<double[]>(1);
        var producer = new Thread(() =>
        {
            while (true)
                foreach (var dir in CreateRandomOrthogonalMatrix(bestVector.Length))
                    directions.Add(dir);
        });
        producer.Start();
        var consumers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            foreach (var dir in directions.GetConsumingEnumerable())
            {
                var eval = bestEval;
                var vector = bestVector.ToArray();
                Log($"STARTING: eval={eval:#,0.#####}, vector={string.Join(", ", vector.Select(v => $"{v:#,0.#####}"))}");
                TraverseDirection(dir, ref eval, ref vector, Rnd.NextDouble(initialStep, 50 * initialStep), stepGrowShrink, giveupBadDirStep, giveupGoodDirStep, (newEval, newVector) =>
                  {
                      lock ("quickhack-lock")
                      {
                          if (improved(newEval, bestEval))
                          {
                              bestVector = newVector.ToArray();
                              bestEval = newEval;
                              Log($"IMPROVEMENT COMMITTED: eval={bestEval:#,0.#####}");
                          }
                      }
                  });
            }
        })).ToList();
        foreach (var t in consumers)
        {
            t.Priority = ThreadPriority.Lowest;
            t.Start();
        }
        producer.Join(); // infinite wait
    }

    public void OrthogonalTraverseBreadthFirstThreaded(double bestEval, double[] bestVector, int threads, int broadIters, double initialStep = 0.01, double stepGrowShrink = 1.62, double giveupStep = 0.001)
    {
        var directions = new BlockingCollection<double[]>(1);
        var producer = new Thread(() =>
        {
            while (true)
                foreach (var dir in CreateRandomOrthogonalMatrix(bestVector.Length))
                    directions.Add(dir);
        });
        producer.Start();
        var consumers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            while (true)
            {
                // breadth first part
                Log($"BREADTH FIRST: eval={bestEval:#,0.#####}, vector={string.Join(", ", bestVector.Select(v => $"{v:#,0.#####}"))}");
                int attempts = 0;
                var startEval = bestEval;
                var startVector = bestVector.ToArray();
                double[] bestDir = null;
                var bestDirEval = bestEval;
                foreach (var dir in directions.GetConsumingEnumerable())
                {
                    attempts++;
                    if (attempts > broadIters)
                        break;
                    var vector = startVector.ToArray();

                    // Try forward
                    Log($"breadth first attempt #{attempts}, forwards");
                    vector = startVector.ToArray();
                    moveVector(vector, dir, initialStep);
                    var eval1 = Evaluate(vector);
                    if (improved(eval1, bestEval))
                    {
                        bestVector = vector.ToArray();
                        bestEval = eval1;
                        Log($"IMPROVEMENT COMMITTED: eval={bestEval:#,0.#####}");
                    }
                    if (improved(eval1, bestDirEval))
                    {
                        bestDir = dir.ToArray();
                        bestDirEval = eval1;
                        Log($"breadth first improved (fw): eval={bestDirEval:#,0.#####}");
                    }
                    if (!improved(eval1, startEval))
                    {
                        // Try backward
                        Log($"breadth first attempt #{attempts}, backwards");
                        negateDir(dir);
                        vector = startVector.ToArray();
                        moveVector(vector, dir, initialStep);
                        var eval2 = Evaluate(vector);
                        if (improved(eval2, bestEval))
                        {
                            bestVector = vector.ToArray();
                            bestEval = eval2;
                            Log($"IMPROVEMENT COMMITTED: eval={bestEval:#,0.#####}");
                        }
                        if (improved(eval2, bestDirEval))
                        {
                            bestDir = dir.ToArray();
                            bestDirEval = eval2;
                            Log($"breadth first improved (bk): eval={bestDirEval:#,0.#####}");
                        }
                    }
                }

                // now work the best dir for as long as we can
                if (bestDir == null)
                {
                    Log("Breadth first: all directions are worse");
                }
                else
                {
                    var eval = bestEval;
                    var vector = bestVector.ToArray();
                    Log($"REFINE: eval={eval:#,0.#####}, vector={string.Join(", ", vector.Select(v => $"{v:#,0.#####}"))}");
                    TraverseDirectionForward(bestDir, ref eval, ref vector, initialStep, stepGrowShrink, giveupStep, (newEval, newVector) =>
                    {
                        lock ("quickhack-lock")
                        {
                            if (improved(newEval, bestEval))
                            {
                                bestVector = newVector.ToArray();
                                bestEval = newEval;
                                Log($"IMPROVEMENT COMMITTED: eval={bestEval:#,0.#####}");
                            }
                        }
                    });
                }
            }
        })).ToList();
        foreach (var t in consumers)
        {
            t.Priority = ThreadPriority.Lowest;
            t.Start();
        }
        producer.Join(); // infinite wait
    }

    public bool TraverseDirection(double[] dir, ref double bestEval, ref double[] bestVector, double initialStep = 0.01, double stepGrowShrink = 1.62, double giveupBadDirStep = 0.001, double giveupGoodDirStep = 0.001, Action<double, double[]> onImproved = null)
    {
        // Phase 1: evaluate this direction and pick forward or backward sense
        /////////////////Log($"  ortho dir phase 1 start: eval={bestEval:#,0.#####}");
        double step = initialStep;
        double[] vector;
        var canGrowIfEqual = true;
        while (true)
        {
            // Try forward
            vector = bestVector.ToArray();
            moveVector(vector, dir, step);
            var eval1 = Evaluate(vector);
            /////////////////Log($"    forward dir: eval={bestEval:#,0.#####}");
            if (improved(eval1, bestEval))
            {
                bestVector = vector.ToArray();
                bestEval = eval1;
                onImproved?.Invoke(bestEval, bestVector);
                step *= stepGrowShrink;
                break;
            }
            // Try backward
            negateDir(dir);
            vector = bestVector.ToArray();
            moveVector(vector, dir, step);
            var eval2 = Evaluate(vector);
            /////////////////Log($"    backward dir: eval={bestEval:#,0.#####}");
            if (improved(eval2, bestEval))
            {
                bestVector = vector.ToArray();
                bestEval = eval2;
                onImproved?.Invoke(bestEval, bestVector);
                step *= stepGrowShrink;
                break;
            }
            negateDir(dir);
            // Neither was better: check if the step is too small
            if (eval1 == bestEval && eval2 == bestEval && canGrowIfEqual)
            {
                /////////////////Log($"    eval unchanged in both directions; step is too small");
                step *= 4;
            }
            else
            {
                /////////////////Log($"    both directions are worse; shrinking step");
                step /= stepGrowShrink;
                canGrowIfEqual = false;
            }
            // Time to give up on this direction?
            if (step < giveupBadDirStep)
            {
                /////////////////Log($"    both directions are consistently bad; giving up");
                return false; // not improved
            }
        }

        // Phase 2: continue going in this direction until it stops improving
        return TraverseDirectionForward(dir, ref bestEval, ref bestVector, step, stepGrowShrink, giveupGoodDirStep, onImproved);
    }

    public bool TraverseDirectionForward(double[] dir, ref double bestEval, ref double[] bestVector, double step = 0.01, double stepGrowShrink = 1.62, double giveupStep = 0.001, Action<double, double[]> onImproved = null)
    {
        var vector = bestVector.ToArray();
        Log($"  traverse forward start: eval={bestEval:#,0.#####}");
        bool growOnEqual = true;
        while (step >= giveupStep)
        {
            moveVector(vector, dir, step);
            var eval = Evaluate(vector);
            Log($"    step={step:0.####}, eval={eval:#,0.#####}");
            if (improved(eval, bestEval))
            {
                bestVector = vector.ToArray();
                bestEval = eval;
                onImproved?.Invoke(bestEval, bestVector);
                step *= stepGrowShrink;
                growOnEqual = true;
            }
            else if (eval != bestEval) // so it's worse
            {
                vector = bestVector.ToArray();
                step /= stepGrowShrink;
                growOnEqual = false; // to prevent loops
            }
            else if (growOnEqual) // equal
            {
                step *= stepGrowShrink;
            }
        }

        return true; // improved
    }

    //public void PerDimensionTraverse(ref double bestEval, ref double[] bestVector)
    //{
    //    bool anyImprovements;
    //    do
    //    {
    //        anyImprovements = false;
    //        var vector = bestVector.ToArray();
    //        Log($" per-dim iter start: eval={bestEval:#,0.#####}");
    //        for (int dim = 0; dim < vector.Length; dim++)
    //        {
    //            for (int sign = 1; sign >= -1; sign -= 2)
    //            {
    //                bool anyImprovementsSign = false;
    //                double step = 0.01;
    //                while (step > 0.0001)
    //                {
    //                    static double mul(double size) => size > 0 ? 1 + size : size < 0 ? 1 / (1 - size) : 1;
    //                    var was = vector[dim];
    //                    vector[dim] *= mul(step * sign);
    //                    var eval = Evaluate(vector);
    //                    Log($"   dim #{dim} step={step:#,0.#####}, eval={eval:#,0.#####}");
    //                    if (eval > bestEval)
    //                    {
    //                        bestEval = eval;
    //                        bestVector = vector.ToArray();
    //                        step *= 1.62;
    //                        anyImprovements = anyImprovementsSign = true;
    //                    }
    //                    else
    //                    {
    //                        vector[dim] = was;
    //                        step /= 1.62;
    //                    }
    //                }
    //                if (anyImprovements)
    //                    break; // no need to go in the reverse direction
    //            }
    //        }
    //    } while (anyImprovements);
    //}

    public static double[][] CreateRandomOrthogonalMatrix(int dimensions)
    {
        // This routine has been decrypted from here: http://www.cap-lore.com/MathPhys/Field/rorthog.c
        double[][] matrix = new double[dimensions][];
        for (int i = 0; i < dimensions; i++)
        {
            matrix[i] = new double[dimensions];
            matrix[i][i] = 1;
        }

        for (int twists = 0; twists < 64; twists++)
        {
            int dim1 = Rnd.Next(0, dimensions), dim2 = Rnd.Next(0, dimensions);
            if (dim1 != dim2)
            {
                double theta = Rnd.NextDouble(-Math.PI, Math.PI);
                // twist(dimensions, matrix, dim1, dim2, theta);
                {
                    double a, b, c;
                    double si = Math.Sin(theta), co = Math.Cos(theta);
                    a = co;
                    b = si;
                    c = -si;

                    for (int k = 0; k < dimensions; k++)
                    {
                        double t = a * matrix[k][dim1] + b * matrix[k][dim2];
                        matrix[k][dim2] = c * matrix[k][dim1] + a * matrix[k][dim2];
                        matrix[k][dim1] = t;
                    }
                }
            }
        }

        return matrix;
    }

    private static class Rnd
    {
        private static Random _rnd = new Random();

        public static double NextDouble(double min, double max)
        {
            return min + _rnd.NextDouble() * (max - min);
        }

        public static int Next(int min, int maxExclusive)
        {
            return _rnd.Next(min, maxExclusive);
        }
    }
}
