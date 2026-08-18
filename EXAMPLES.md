# Seekers by example

A cookbook of scenarios drawn from real optimization problems, including the odd ones. Every snippet compiles
against the current API. See README.md for the basics; this file assumes them.

- [Declaring vectors](#declaring-vectors)
- [Objectives and comparison](#objectives-and-comparison)
- [Constraints](#constraints)
- [Custom search loops](#custom-search-loops)
- [Expensive and cheap evaluations](#expensive-and-cheap-evaluations)
- [Reproducibility, logging, persistence](#reproducibility-logging-persistence)

## Declaring vectors

### A typed named-tuple vector

The everyday case. Tuple field names are the parameter names; the library never sees them.

```csharp
var result = Seeker.WithVector(ctx =>
    (
        width: ctx.LinearInt(1, 100),
        color: (byte) ctx.LinearInt(0, 255),      // casts and arithmetic on the returned value are fine
        minPixels: ctx.LogarithmicInt(100, 10_000)
    ))
    .WithEval(v => Render(v.width, v.color, v.minPixels))
    .WithGoal(SeekerGoal.Minimize)
    .HillClimb(restarts: 10);
```

### A `double[]` vector

When positional access is the natural shape (or the eval already takes an array), make `TVector` a `double[]`.
The engine snapshots arrays when committing a best, so the eval's array can be reused.

```csharp
var config = Seeker.WithVector(ctx => new[] { ctx.LinearDbl(-5, 5), ctx.LinearDbl(-5, 5) })
    .WithEval(v => Cost(v))               // Func<double[], double>
    .WithGoal(SeekerGoal.Minimize);
```

### A domain object as the vector

The lambda can build any type — including one of your own model classes — as long as the `ctx.…` calls happen in
the same order every time. Fields not being optimized are simply copied in.

```csharp
var config = Seeker.WithVector(ctx => new Blob
    {
        X = ctx.LinearInt(1, 4000, initial: start.X),
        Y = ctx.LinearInt(1, 4000, initial: start.Y),
        Intensity = ctx.LinearInt(-260, 260, initial: start.Intensity),
        Tag = start.Tag,                  // constant, not a parameter
    })
    .WithEval(blob => Error(blob))
    .WithGoal(SeekerGoal.Minimize);
```

### A dynamically-shaped vector

The declaration can loop over data, so the parameter count is decided at runtime. The only rule: the same
declarations in the same order on every invocation of the lambda.

```csharp
double[] seeds = LoadSeedValues();
var config = Seeker.WithVector(ctx => seeds.Select(s => ctx.MulDbl(s)).ToArray())
    .WithEval(v => Compress(v))
    .WithGoal(SeekerGoal.Maximize);
```

### Mixed step semantics in one vector

Additive and multiplicative parameters coexist; each declaration picks its own stepping. Additive parameters can
reach and cross zero; multiplicative ones never can — choose accordingly for signed quantities.

```csharp
Seeker.WithVector(ctx =>
(
    period: ctx.LinearDbl(1, 100),            // additive: equal absolute resolution everywhere
    threshold: ctx.LogarithmicDbl(0.001, 10), // multiplicative: equal relative resolution across 4 decades
    stop: ctx.RatioDbl(0.01, 100),            // multiplicative with absolute octave steps
    offset: ctx.LinearDbl(-50, 50)            // signed — must be additive
))
```

### Unbounded scale factors around a known-good vector

For positive weights with no meaningful bounds, `MulDbl` takes an initial value and randomizes multiplicatively
around it — `randomizeSpread: 2` means restarts land within ×0.25 … ×4 of the initial.

```csharp
var best = new double[] { 0.128, 0.480, 1.298, 62.4, 43.2 };
var config = Seeker.WithVector(ctx => best.Select(v => ctx.MulDbl(v, randomizeSpread: 2)).ToArray())
    .WithEval(Evaluate)
    .WithGoal(SeekerGoal.Maximize);
```

### Variable-length candidates via fixed slots

The library has no variable-dimensionality support, and usually doesn't need it: declare the maximum number of
slots and give the encoding a natural "off" state. Here, actions scheduled past the simulated horizon simply never
fire, so the search can enable and disable slots by moving their time.

```csharp
// up to 4 timed actions; the simulation runs ticks 0..399, so tick >= 400 = slot disabled
Seeker.WithVector(ctx =>
(
    t1: ctx.LinearInt(1, 499), amount1: ctx.LinearDbl(0, 1),
    t2: ctx.LinearInt(1, 499), amount2: ctx.LinearDbl(0, 1),
    t3: ctx.LinearInt(1, 499), amount3: ctx.LinearDbl(0, 1),
    t4: ctx.LinearInt(1, 499), amount4: ctx.LinearDbl(0, 1)
))
```

## Objectives and comparison

### Non-scalar evaluations with a lexicographic comparison

`TEval` can be any type. Supply a comparison; the goal only decides whether "greater" or "smaller" wins.

```csharp
.WithEval(v => Encode(v))   // returns (int blur, double ssim, ...)
// smallest blur wins; among equal blurs, largest ssim wins
.WithGoal(SeekerGoal.Minimize, (a, b) =>
    a.blur != b.blur ? a.blur.CompareTo(b.blur) : b.ssim.CompareTo(a.ssim))
```

### A feasibility gate inside the comparison

"Any candidate covering >70% beats any that doesn't; among covered, fewer artifacts win; among uncovered, more
coverage wins." Encode gates as the first key of the comparison:

```csharp
.WithGoal(SeekerGoal.Maximize, (a, b) =>
{
    bool aGood = a.coverage > 70, bGood = b.coverage > 70;
    if (aGood != bGood) return aGood ? 1 : -1;
    return aGood ? b.artifacts.CompareTo(a.artifacts)   // both feasible: fewer artifacts is "greater"
                 : a.coverage.CompareTo(b.coverage);    // both infeasible: chase coverage
})
```

### Banded (epsilon) equality

When scores within a tolerance should count as *equal* (so tie-breaks and plateau logic engage), put the banding in
the comparison:

```csharp
static double band(double profit) => profit < 1.01 ? profit : Math.Round(profit, 7);
.WithGoal(SeekerGoal.Maximize, (a, b) => band(a.profit).CompareTo(band(b.profit)))
```

### Tie-breaking on the parameters themselves

The comparison sees only `TEval`, so carry whatever the tie-break needs into the evaluation result:

```csharp
.WithEval(v => (profit: Backtest(v), stop: v.stop))
.WithGoal(SeekerGoal.Maximize, (a, b) =>
{
    var c = a.profit.CompareTo(b.profit);
    return c != 0 ? c : b.stop.CompareTo(a.stop);   // equal profit: the tighter stop wins
})
```

### Sentinel infeasible evaluations

A failed or infeasible evaluation can return a "worse than anything" value; the search simply never accepts it.
Log the failure yourself — the vector is in scope.

NaN and null need no sentinel: NaN is rejected automatically for `double`/`float` evaluations (plain or nullable),
and null for any reference-type or nullable `TEval` — never committed as best, never passed to your comparison, so
an eval can simply `return null` for "no result". For richer viability rules, declare a predicate — the same
guarantees then apply:

```csharp
.WithViable(e => !double.IsNaN(e.score) && e.converged)
```

Note that a search whose *every* evaluation is non-viable has no incumbent: packaged algorithms skip climbing such
starts and return `Found = false` if nothing viable ever appeared; custom loops should check `HasBest` after the
anchoring `Evaluate()`.

```csharp
.WithEval(v =>
{
    try { return -Compress(v); }
    catch (Exception e)
    {
        File.AppendAllLines("crash.log", new[] { $"{e.Message} at {SeekerLog.Vec(v)}" });
        return double.MinValue;
    }
})
```

### Staircase objectives (integer-valued, quantized, plateau-ridden)

If the objective is piecewise constant — integer byte counts, rounded scores, `Math.Ceiling` anywhere in the model —
tiny steps produce *exactly equal* evaluations and a naive line search gives up immediately. `CanGrowIfEqual` makes
the traversal grow the step across plateaus instead:

```csharp
.WithTraverse(new TraverseOptions { CanGrowIfEqual = true, InitialStep = 0.01 })
```

Leave it off when exact equality means "genuinely converged" and ties must never be walked.

### Incumbent-aware evaluation (early abort)

When an evaluation is itself a long tournament or accumulation, give it the best-so-far so it can stop as soon as
it provably cannot win. The second argument is the incumbent, or the supplied worst-sentinel before any exists.
Parallelism *inside* the evaluation is fine — the search loop stays single-threaded.

```csharp
.WithEval((v, best) =>
{
    var result = new Score();
    Parallel.ForEach(Matches(v), (m, state) =>
    {
        Play(m, result);
        if (result.UpperBound() <= best.Wins)   // cannot beat the incumbent even if it wins everything left
            state.Stop();
    });
    return result;
}, worstEval: new Score { Wins = -1 })
```

### Aborting a search from inside the evaluation

Throw `SeekerBreakException` to abandon the current search gracefully; the best-so-far is returned. Useful for
degenerate results that make continuing pointless, or for user-imposed budgets.

```csharp
.WithEval(v =>
{
    var diff = Measure(v);
    if (diff.present == 0)                  // degenerate: blank output, nothing left to compare
        throw new SeekerBreakException();
    return diff;
})
```

### A nested search as the evaluation

An outer evaluation may construct and run an entire inner search and return its `SeekerResult` as the eval value.
Everything is instance-based, so nesting is safe to any depth; results compose (`outer.BestEval.BestVector`).

```csharp
var outer = Seeker.WithVector(ctx => (roadWidth: ctx.LinearInt(1, 100), color: (byte) ctx.LinearInt(0, 255)))
    .WithEval(v1 =>
    {
        var expensive = RenderBase(v1.roadWidth, v1.color);      // amortized over the whole inner search
        var inner = Seeker.WithVector(ctx => (erode: ctx.LinearInt(1, 100), blur: ctx.LinearInt(1, 100)))
            .WithEval(v2 => Score(PostProcess(expensive, v2.erode, v2.blur)))
            .WithGoal(SeekerGoal.Maximize);
        return inner.FullyRandomVectors(10);
    })
    .WithGoal(SeekerGoal.Maximize, (a, b) => a.BestEval.CompareTo(b.BestEval));
var full = outer.FullyRandomVectors(10);
// full.BestVector — outer params; full.BestEval.BestVector — the matching inner params
```

## Constraints

### Cross-parameter constraints via `Renormalize`

Per-parameter bounds can't express "W and H within 4:1 of each other". The `Renormalize` hook runs after every
move; repair offending parameters through their `Raw` position:

```csharp
.WithRenormalize(ps =>
{
    if (ps[2].Value < ps[3].Value / 4) ps[2].Raw = ps[3].Value / 4;
    if (ps[3].Value < ps[2].Value / 4) ps[3].Raw = ps[2].Value / 4;
})
```

### Constraint by rejection inside the vector→world mapping

When feasibility is per-component (a point must stay inside a polygon), let the mapping drop just the offending
components — the rest of the move still happens, and the search sees the resulting evaluation:

```csharp
.WithEval(pos =>
{
    for (int i = 0; i < count; i++)
    {
        var p = new PointD(pos[2 * i], pos[2 * i + 1]);
        if (region.ContainsPoint(p))     // an offending point simply doesn't move
            items[i].Pos = p;
    }
    return Measure(items);
})
```

### Cyclic parameters (angles)

There is no wrap-around parameter type yet. If the evaluation is genuinely periodic (trigonometry of an angle),
declare wide linear bounds around the current value and let the eval be periodic; wrap only for presentation:

```csharp
angle: ctx.LinearInt(start.Angle - 3600, start.Angle + 3600, initial: start.Angle)
// after the search: result.Angle = ((result.Angle % 360) + 360) % 360;
```

### Constraint by construction (encoding)

Often the best constraint is no constraint: choose an encoding where every vector is legal. Ordered quantities can
be encoded as a base plus non-negative deltas; capacity limits can be encoded as fixed slots where surplus slots are
inert. This beats repair hooks because the search never wastes evaluations on infeasible points.

```csharp
// t1 <= t2 <= t3 by construction:
Seeker.WithVector(ctx =>
{
    var t1 = ctx.LinearInt(0, 400);
    var t2 = t1 + ctx.LinearInt(0, 400);
    var t3 = t2 + ctx.LinearInt(0, 400);
    return (t1, t2, t3);
})
```

## Custom search loops

The packaged algorithms are compositions of engine primitives; anything they don't cover, compose yourself via
`config.CreateSeeker()`.

### Run forever, printing improvements

```csharp
config.OnImproved = (eval, v) => Console.WriteLine($"New best: {eval} at {v}");
var s = config.CreateSeeker();
while (true)
{
    s.ResetChain();          // fresh climb; the global best is retained
    s.RandomizeAll();
    s.Evaluate();
    while (s.OrthogonalTraverse()) { }
}
```

### A bespoke schedule: jitter, orthogonal sweeps, per-axis polish

Mix phases freely; the chain/global split keeps exploration honest (a jittered start is explored on its own merits
even when it begins worse than the global best):

```csharp
var s = config.CreateSeeker();
s.Evaluate();
while (true)
{
    // a tiny multiplicative jitter around the global best, explored as its own chain
    s.RestoreGlobalBest();
    var jittered = s.GetValues().Select(v => v * (0.999 + Random.Shared.NextDouble() * 0.002)).ToArray();
    s.ResetChain();
    s.SetValues(jittered);
    s.Evaluate();
    s.OrthogonalTraverse();

    // three sweeps from the global best
    s.RestoreGlobalBest();
    for (int i = 0; i < 3; i++)
        s.OrthogonalTraverse();

    // finish with an axis-aligned pass
    s.CoordinateSweep();
}
```

### Per-axis integer line searches with unit steps

For integer parameters, a rotated direction rounds away to nothing; what works is one axis at a time, stepping in
whole units that double while improving. Express "one unit" per axis by dividing by that parameter's range:

```csharp
var s = config.CreateSeeker();
s.Evaluate();
var dir = new double[s.Params.Count];
for (int i = 0; i < s.Params.Count; i++)
{
    var p = (LinearIntParam) s.Params[i];
    var unit = 1.0 / Math.Max(1, p.Max - p.Min);
    dir[i] = 1;
    s.TraverseDirection(dir, new TraverseOptions
    {
        InitialStep = unit, StepGrowShrink = 2,                       // ±1, then 2, 4, 8 … while improving
        GiveupBadDirStep = unit * 0.99, GiveupGoodDirStep = unit * 0.99, // give up as soon as ±1 both fail
        MaxStepFactor = 1e9,
    });
    dir[i] = 0;
}
```

### Subspace search over a huge parameter space

With tens of thousands of parameters, full random orthogonal bases are impossible (the matrix is n²). Declare the
full space once, then hand the seeker *sparse* directions: generate a small orthogonal matrix over a random subset
and scatter it into a full-length direction (zeros elsewhere are skipped efficiently). Per-group step scaling is
just component scaling:

```csharp
var subset = PickRandomIndexes(count: 40, total: s.Params.Count);
foreach (var small in Seeker.CreateRandomOrthogonalMatrix(subset.Count).Take(5))
{
    var dir = new double[s.Params.Count];
    for (int k = 0; k < subset.Count; k++)
        dir[subset[k]] = small[k] * (IsSlowGroup(subset[k]) ? 0.1 : 1.0);
    s.TraverseDirection(dir);
}
```

### Incremental stepping from a UI timer (persistent incumbent)

Keep one seeker alive across calls; do a bounded chunk of work per tick. Tune `TraverseOptions` so a direction
costs only a couple of evaluations (give-up just below the initial step ≈ one probe per sense):

```csharp
// field: Seeker<State, double> _seeker;
void TimerTick()
{
    _seeker ??= MakeConfig().CreateSeeker();
    if (!_seeker.HasBest)
        _seeker.Evaluate();
    foreach (var dir in _seeker.RandomOrthogonalDirections().Take(5))
        _seeker.TraverseDirection(dir, new TraverseOptions
            { InitialStep = 0.0005, GiveupBadDirStep = 0.00049, GiveupGoodDirStep = 0.00049, MaxStepFactor = 100 });
}
```

### Re-anchoring on drifting external state

If the world evolves between optimizer calls (physics, live data), a persistent incumbent goes stale. Instead,
build a *fresh* seeker per call with parameters seeded from the current world state (`initial:` values), evaluate
once to anchor, search briefly, and write the best back into the world at the end:

```csharp
void StepOptimizer()
{
    var config = Seeker.WithVector(ctx => items.SelectMany(it => new[]
        {
            ctx.LinearDbl(-100, 100, initial: it.Pos.X),
            ctx.LinearDbl(-100, 100, initial: it.Pos.Y),
        }).ToArray())
        .WithEval(WriteToWorldAndMeasure)
        .WithGoal(SeekerGoal.Minimize);
    var s = config.CreateSeeker();
    s.Evaluate();
    foreach (var dir in s.RandomOrthogonalDirections().Take(20))
        s.TraverseDirection(dir);
    s.RestoreGlobalBest();
    s.Evaluate();     // leave the world holding the best arrangement, not the last candidate
}
```

### Seeding from known state without re-evaluating

When the incumbent's evaluation is already known (carried over from an enclosing computation, or received from
another worker), `SeedChain` adopts the current position with that evaluation and skips the redundant eval — which
matters when evaluations cost minutes:

```csharp
s.SetValues(knownGoodValues);
s.SeedChain(knownGoodEval);
s.OrthogonalTraverse();
```

### Block-structured incremental evaluation

When the model is a list of components and evaluating one candidate component against a cached "everything else"
is far cheaper than evaluating the whole model, keep that structure in your loop and give each component its own
small search. Re-evaluate the full model at the end of each sweep to purge floating-point drift from the
incremental bookkeeping:

```csharp
while (true)
{
    var cache = BuildFull(model);
    var initialError = FullError(cache);
    var bestError = initialError;
    for (int i = 0; i < model.Count; i++)
    {
        Subtract(cache, model[i]);
        var cfg = MakeSingleComponentConfig(model[i], cache);   // eval: cache + candidate component
        var s = cfg.CreateSeeker();
        s.SeedChain(bestError);                                  // carry the incremental error over
        s.CoordinateSweep();
        if (s.HasGlobalBest && s.GlobalBestEval < bestError)
        {
            bestError = s.GlobalBestEval;
            model[i] = s.GlobalBestVector;
        }
        Add(cache, model[i]);
    }
    if (!(FullError(BuildFull(model)) < initialError))          // drift-proof re-check
        break;
}
```

### Hybrid closed-form + search

Let the optimizer own only the nuisance parameters and compute the rest analytically inside the evaluation — a
1-dimensional search wrapping an exact regression beats a 3-dimensional search:

```csharp
var result = Seeker.WithVector(ctx => ctx.LinearDbl(0.1, 0.6, initial: 0.32))   // just the time-shift
    .WithEval(shift => { ApplyShift(points, shift); return OrthoLinReg(points).PerpRMS; })
    .WithGoal(SeekerGoal.Minimize)
    .HillClimb(restarts: 10);
var line = OrthoLinReg(points);   // slope/intercept recovered in closed form at the optimum
```

## Expensive and cheap evaluations

### Very expensive evaluations (minutes to hours each)

Use the breadth-first threaded search: each worker probes a handful of directions once from a frozen snapshot of
the best, then commits the whole budget to the single most promising direction. Set `GiveupBadDirStep` equal to
`InitialStep` so a failed direction costs at most two evaluations, and checkpoint everything:

```csharp
var config = /* … */
    .WithTraverse(new TraverseOptions { InitialStep = 0.05, GiveupBadDirStep = 0.05, GiveupGoodDirStep = 0.05 })
    .WithLog(SeekerLogLevel.Iterations)
    .WithCheckpoint("run-best.txt");                 // survives interruption; resumes automatically
var stop = config.OrthogonalTraverseBreadthFirstThreaded(threads: 2, broadIters: 5);
// … runs until:
var result = stop();                                 // cancel, wait for in-flight evals, collect the best
```

### Continuous parallel hill climbing

For evaluations in the seconds range, `OrthogonalTraverseThreaded` keeps all workers line-searching concurrently,
each direction starting from the live shared best; initial steps are randomized across a 50× range so coarse and
fine exploration interleave:

```csharp
var stop = config.OrthogonalTraverseThreaded(threads: 8);
Console.ReadLine();
var result = stop();
```

### Per-worker scratch state

If the evaluation needs preallocated buffers or other non-shareable state, pass a factory: each worker gets its own
evaluator instance, so nothing is shared and nothing locks on the hot path:

```csharp
var stop = config.OrthogonalTraverseThreaded(threads: 8,
    evalFactory: () => { var scratch = new Calc(); return v => scratch.Trade(prices, commission, v); });
```

Single-threaded algorithms never call the evaluation from more than one thread, so a closure over one scratch
object is already safe there.

### Memoising evaluations on an integer lattice

Hill climbing revisits integer points constantly; when an eval costs milliseconds+, cache by effective values:

```csharp
var seen = new Dictionary<(int w, int q), Result>();
.WithEval(v =>
{
    if (seen.TryGetValue(v, out var cached)) return cached;
    return seen[v] = ExpensiveEncode(v.w, v.q);
})
```

### Microsecond evaluations

The full typed pipeline (randomize + materialize + eval + compare + commit) costs on the order of 150 ns per
evaluation. To keep it there: leave `Log` off or at `Improvements` (all logging is guarded — no strings are built
when disabled); avoid allocation in the eval and the vector lambda (tuples are structs; reuse arrays); don't wrap
the comparison in anything that boxes. If several searches should run at once, run several independent seekers on
their own threads rather than sharing one.

## Reproducibility, logging, persistence

### Deterministic runs

```csharp
config.WithRandom(12349);                 // this search
// or share one seeded Random across an outer search and all its nested inner searches:
var rnd = new Random(12349);
outerConfig.WithRandom(rnd);  innerConfig.WithRandom(rnd);
```

The threaded algorithms derive per-worker seeds from the config's `Random`, so even parallel runs are reproducible
up to thread scheduling.

### Logging: destination, verbosity, sub-algorithms

```csharp
config.Log = SeekerLog.Console(SeekerLogLevel.Iterations);        // quick default
config.Log = SeekerLog.To(line => host.LogLine(line), SeekerLogLevel.Improvements);  // any sink

// inside a custom loop, fork a child logger for a wordy sub-phase, or silence it:
var quiet = s.Log.Sub("[polish] ", SeekerLogLevel.Off);
var loud = s.Log.Sub("[refine] ", SeekerLogLevel.Steps);
```

`SeekerLog.Num` formats values with adaptive precision, `SeekerLog.Vec` a whole vector — useful for artifact
filenames and improvement lines that must be paste-back-precise.

### Checkpointing and resume

```csharp
config.WithCheckpoint("search-best.txt");
```

Every global improvement saves the parameter values (throttled, default 15 s) and appends to
`search-best.txt.history`; packaged algorithms force a final save. On the next run the seeker starts from the saved
point automatically (`config.Checkpoint.Resume = false` to disable). The file is human-readable and hand-editable.

### Warm starts

Three equivalent ways to start from a known-good point: `initial:` values in the declarations, `startValues:` on
`HillClimb`/`CoordinateDescent`, or a checkpoint file. With any of them, `HillClimb` climbs the given point first,
then does its random restarts, then polishes the overall best.
