using System.Collections.Concurrent;

namespace Seekers;

/// <summary>
///     Multi-threaded search algorithms for expensive evaluations. Workers run at lowest thread priority so the
///     machine stays usable. The evaluation delegate is invoked concurrently from multiple threads — it must be
///     thread-safe, or pass an <c>evalFactory</c> that creates one independent evaluator per worker (e.g. each with
///     its own scratch buffers).</summary>
public static class SeekerThreaded
{
    /// <summary>
    ///     Continuous parallel hill climbing: workers pull random orthogonal directions from a shared stream and line-
    ///     search each from the shared best, committing improvements as they are found. Runs until the returned stop
    ///     function is called, which cancels, waits for workers to finish their current direction, and returns the
    ///     best result. The initial step for each direction is randomized in [InitialStep, 50×InitialStep] to mix
    ///     coarse and fine exploration.</summary>
    public static Func<SeekerResult<TVector, TEval>> OrthogonalTraverseThreaded<TVector, TEval>(this SeekerConfig<TVector, TEval> config,
        int threads, TraverseOptions options = null, double[] startValues = null, Func<Func<TVector, TEval>> evalFactory = null)
    {
        options ??= config.Traverse ?? TraverseOptions.Default;
        var shared = new SharedBest<TVector, TEval>(config);
        shared.Initialize(startValues, evalFactory);

        var cts = new CancellationTokenSource();
        var done = new ManualResetEvent(false);
        var directions = new BlockingCollection<double[]>(1);
        var dimensions = shared.ParamCount;
        var rnd = config.Random ?? Seeker.DefaultRnd;

        var consumers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var worker = shared.CreateWorker(evalFactory, $"[T{t}] ");
            try
            {
                foreach (var dir in directions.GetConsumingEnumerable())
                {
                    shared.SeedWorker(worker);
                    double initialStep;
                    lock (rnd)
                        initialStep = options.InitialStep * (1 + rnd.NextDouble() * 49);
                    var o = new TraverseOptions
                    {
                        InitialStep = initialStep,
                        StepGrowShrink = options.StepGrowShrink,
                        GiveupBadDirStep = options.GiveupBadDirStep,
                        GiveupGoodDirStep = options.GiveupGoodDirStep,
                        CanGrowIfEqual = options.CanGrowIfEqual,
                        MaxStepFactor = options.MaxStepFactor,
                    };
                    worker.TraverseDirection(dir, o);
                }
            }
            catch (SeekerBreakException) { }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest }).ToList();

        var producer = new Thread(() =>
        {
            var prnd = new Random(shared.NextSeed());
            try
            {
                while (!cts.IsCancellationRequested)
                    foreach (var dir in Seeker.CreateRandomOrthogonalMatrix(dimensions, prnd))
                    {
                        if (cts.IsCancellationRequested)
                            break;
                        directions.Add(dir, cts.Token);
                    }
            }
            catch (OperationCanceledException) { }
            directions.CompleteAdding();
            foreach (var c in consumers)
                c.Join();
            done.Set();
        })
        { IsBackground = true };

        producer.Start();
        foreach (var c in consumers)
            c.Start();

        return () =>
        {
            cts.Cancel();
            done.WaitOne();
            config.Checkpoint?.SaveFinal();
            return shared.Result;
        };
    }

    /// <summary>
    ///     Breadth-first parallel hill climbing, for extremely expensive evaluations: each worker probes <paramref
    ///     name="broadIters"/> directions once from a frozen snapshot of the best (forward, and backward only when
    ///     forward failed), then rides the single most promising direction for as long as it pays. All improvements
    ///     commit to the shared best immediately. Runs until the returned stop function is called.</summary>
    public static Func<SeekerResult<TVector, TEval>> OrthogonalTraverseBreadthFirstThreaded<TVector, TEval>(this SeekerConfig<TVector, TEval> config,
        int threads, int broadIters, TraverseOptions options = null, double[] startValues = null, Func<Func<TVector, TEval>> evalFactory = null)
    {
        options ??= config.Traverse ?? TraverseOptions.Default;
        var shared = new SharedBest<TVector, TEval>(config);
        shared.Initialize(startValues, evalFactory);

        var cts = new CancellationTokenSource();
        var done = new ManualResetEvent(false);
        var directions = new BlockingCollection<double[]>(1);
        var dimensions = shared.ParamCount;

        var consumers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var worker = shared.CreateWorker(evalFactory, $"[T{t}] ");
            var log = worker.Log;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // Breadth-first phase: probe directions once each from a frozen snapshot of the shared best
                    shared.SeedWorker(worker);
                    var startRaws = worker.GetRaws();
                    var startEval = worker.BestEval;
                    log.Iteration($"breadth first from eval={startEval}");
                    double[] bestDir = null;
                    var bestDirEval = startEval;
                    for (int attempt = 0; attempt < broadIters && !cts.IsCancellationRequested; attempt++)
                    {
                        double[] dir;
                        try { dir = directions.Take(cts.Token); }
                        catch (OperationCanceledException) { break; }

                        worker.SetRaws(startRaws);
                        TEval evalFw = default;
                        int cmpFw = worker.Move(dir, options.InitialStep) ? probe(worker, startEval, out evalFw) : 0;
                        if (cmpFw > 0 && config.CompareUsingGoal(evalFw, bestDirEval) > 0)
                        {
                            bestDir = (double[]) dir.Clone();
                            bestDirEval = evalFw;
                            log.Iteration($"probe #{attempt + 1} fw improved: eval={evalFw}");
                        }
                        if (cmpFw <= 0)
                        {
                            worker.SetRaws(startRaws);
                            var neg = dir.Select(d => -d).ToArray();
                            TEval evalBk = default;
                            int cmpBk = worker.Move(neg, options.InitialStep) ? probe(worker, startEval, out evalBk) : 0;
                            if (cmpBk > 0 && config.CompareUsingGoal(evalBk, bestDirEval) > 0)
                            {
                                bestDir = neg;
                                bestDirEval = evalBk;
                                log.Iteration($"probe #{attempt + 1} bk improved: eval={evalBk}");
                            }
                        }
                    }

                    // Refine phase: ride the best direction from the current shared best
                    if (bestDir == null)
                        log.Iteration("breadth first: all directions are worse");
                    else
                    {
                        shared.SeedWorker(worker);
                        log.Iteration($"refining best direction from eval={worker.BestEval}");
                        worker.TraverseDirectionForward(bestDir, options.InitialStep, options);
                    }
                }
            }
            catch (SeekerBreakException) { }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest }).ToList();

        var producer = new Thread(() =>
        {
            var prnd = new Random(shared.NextSeed());
            try
            {
                while (!cts.IsCancellationRequested)
                    foreach (var dir in Seeker.CreateRandomOrthogonalMatrix(dimensions, prnd))
                    {
                        if (cts.IsCancellationRequested)
                            break;
                        directions.Add(dir, cts.Token);
                    }
            }
            catch (OperationCanceledException) { }
            directions.CompleteAdding();
            foreach (var c in consumers)
                c.Join();
            done.Set();
        })
        { IsBackground = true };

        producer.Start();
        foreach (var c in consumers)
            c.Start();

        return () =>
        {
            cts.Cancel();
            done.WaitOne();
            config.Checkpoint?.SaveFinal();
            return shared.Result;
        };
    }

    /// <summary>Evaluates the worker's current position and returns its comparison against <paramref name="reference"/>.</summary>
    private static int probe<TVector, TEval>(Seeker<TVector, TEval> worker, TEval reference, out TEval eval)
    {
        eval = worker.Evaluate(); // the worker's OnImproved commit path handles shared best updates
        return worker.Config.CompareUsingGoal(eval, reference);
    }

    /// <summary>The synchronized global best shared by all workers of one threaded run.</summary>
    private sealed class SharedBest<TVector, TEval>
    {
        private readonly SeekerConfig<TVector, TEval> _config;
        private readonly object _lock = new();
        private readonly Random _seedRnd;
        private readonly List<Seeker<TVector, TEval>> _workers = new();
        private double[] _raws;
        private TEval _eval;
        private TVector _vector;
        private bool _has;

        public int ParamCount { get; private set; }

        public SharedBest(SeekerConfig<TVector, TEval> config)
        {
            _config = config;
            var rnd = config.Random ?? Seeker.DefaultRnd;
            lock (rnd)
                _seedRnd = new Random(rnd.Next());
        }

        public int NextSeed()
        {
            lock (_lock)
                return _seedRnd.Next();
        }

        /// <summary>Establishes the starting point: initial/start/checkpoint values if available, else a random point.</summary>
        public void Initialize(double[] startValues, Func<Func<TVector, TEval>> evalFactory)
        {
            var s = CreateWorker(evalFactory, "[init] ");
            ParamCount = s.Params.Count;
            if (startValues != null)
                s.SetValues(startValues);
            else if (!s.Params.All(p => p.HasInitial) && !(_config.Checkpoint?.Resume == true && _config.Checkpoint.TryLoadValues() != null))
                s.RandomizeAll();
            s.Evaluate();
        }

        /// <summary>
        ///     Creates a worker seeker whose improvements are committed to the shared best. The worker gets its own
        ///     parameter set and (optionally) its own evaluator instance.</summary>
        public Seeker<TVector, TEval> CreateWorker(Func<Func<TVector, TEval>> evalFactory, string logPrefix)
        {
            Seeker<TVector, TEval> worker = null;
            int seed;
            lock (_lock)
                seed = _seedRnd.Next();
            var cfg = _config with
            {
                Checkpoint = null, // checkpointing is driven from the shared commit path below
                OnImproved = null,
                Random = new Random(seed),
                Log = (_config.Log ?? SeekerLog.None).Sub(logPrefix),
            };
            cfg.OnImproved = (eval, vector) => commit(worker, eval, vector);
            worker = cfg.CreateSeeker(evalFactory?.Invoke());
            lock (_lock)
                _workers.Add(worker);
            return worker;
        }

        private void commit(Seeker<TVector, TEval> worker, TEval eval, TVector vector)
        {
            lock (_lock)
            {
                if (_has && _config.CompareUsingGoal(eval, _eval) <= 0)
                    return;
                _has = true;
                _eval = eval;
                _vector = vector;
                _raws = worker.GetRaws();
                (_config.Log ?? SeekerLog.None).Improvement($"IMPROVEMENT COMMITTED: eval={eval}");
                _config.OnImproved?.Invoke(eval, vector);
                _config.Checkpoint?.Save(worker.GetValues(), eval?.ToString());
            }
        }

        /// <summary>Resets a worker's position and chain to the current shared best.</summary>
        public void SeedWorker(Seeker<TVector, TEval> worker)
        {
            double[] raws;
            TEval eval;
            lock (_lock)
            {
                raws = _raws;
                eval = _eval;
            }
            worker.SetRaws(raws);
            worker.SeedChain(eval);
        }

        public SeekerResult<TVector, TEval> Result
        {
            get
            {
                lock (_lock)
                    return new SeekerResult<TVector, TEval>
                    {
                        BestVector = _vector,
                        BestEval = _eval,
                        BestValues = null,
                        Found = _has,
                        EvalCount = _workers.Sum(w => w.EvalCount),
                    };
            }
        }
    }
}
