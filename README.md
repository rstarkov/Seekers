# Seekers

See also: [EXAMPLES.md](EXAMPLES.md) — a cookbook covering the odd scenarios (nested searches, plateaus,
constraints, custom loops, huge/tiny evaluations); [FUTURE.md](FUTURE.md) — possible future improvements.

A .NET library of utilities for multi-parameter optimization. It targets a broad range of problems with very
different performance characteristics — from evaluations costing a microsecond (where per-eval library overhead
matters) to evaluations costing half an hour of compute (where every single evaluation must count).

Two ways to use it:

- **Packaged algorithms** for straightforward problems: declare the parameters, the evaluation, the goal — run.
- **Engine primitives** for everything else: the target program implements its own search loop out of the same
  building blocks the packaged algorithms are made of.

## Declaring a problem

The fluent chain declares the typed vector, the evaluation, and the comparison; the algorithm is chosen last, as an
extension method on the resulting config:

```csharp
var result = Seeker.WithVector(ctx =>
    (
        roadWidth: ctx.LinearInt(1, 100),
        threshold: (byte) ctx.LinearInt(1, 254),
        minPixels: ctx.LogarithmicInt(100, 10_000),
        gain: ctx.LinearDbl(-5, 5, initial: 0.3)
    ))
    .WithEval(v => Measure(v.roadWidth, v.threshold, v.minPixels, v.gain))
    .WithGoal(SeekerGoal.Minimize)
    .HillClimb(restarts: 10);

// result.BestVector is the same named tuple; result.BestEval the best evaluation
```

The lambda serves two purposes: on a one-time configuration pass each `ctx.…` call registers a parameter, and on
every later pass the same calls return current values, materializing a fully typed vector. Parameters are referenced
by name with no strings and no `double[]` index bookkeeping — but `TVector` can just as well *be* a `double[]` when
that is the natural shape.

### Parameter types

Every parameter owns its own step semantics. Algorithms move parameters by abstract amounts; the parameter decides
what an amount means on its scale. This is how additive and multiplicative stepping coexist in one search:

| Declaration | Stepping | Notes |
|---|---|---|
| `LinearInt(min, max)` | additive; amount 1 spans the range | integer, internally continuous so small steps accumulate; can cross zero |
| `LinearDbl(min, max)` | additive; amount 1 spans the range | can reach and cross zero |
| `LogarithmicInt(min, max)` | multiplicative; amount 1 spans the log-range | min ≥ 1; equal relative resolution across the range |
| `LogarithmicDbl(min, max)` | multiplicative; amount 1 spans the log-range | min > 0; can never reach zero |
| `RatioDbl(min, max)` | multiplicative; amount 1 doubles the value | min > 0; absolute step scale |
| `MulDbl(initial)` | multiplicative; amount 1 doubles the value | unbounded positive; randomizes around the initial value |

All declarations accept an optional `initial:` value; parameters without one start at a random point. Cross-parameter
constraints go in `config.Renormalize`, invoked after every move.

### Evaluations and comparisons

`TEval` is any type: a `double`, a tuple, a domain object, even a whole `SeekerResult` of a nested search. Supply a
custom `Comparison<TEval>`/`IComparer<TEval>` for lexicographic orderings, feasibility gates, epsilon bands and
tie-breaks. The comparison is tri-state — exact equality is meaningful and drives plateau handling
(`TraverseOptions.CanGrowIfEqual`), which is essential for integer-valued or quantized objectives.

- NaN evaluations (for `double`/`float` `TEval`, plain or nullable) and null evaluations (for reference-type or
  nullable `TEval`) are never accepted as the incumbent and are never shown to the comparison functions.
  `config.IsViable` customizes this filter for any `TEval` (or disables it with `_ => true`).
- Throw `SeekerBreakException` from an evaluation to abandon the search gracefully; the best-so-far is returned.
- `WithEval((v, best) => …, worstEval)` declares an incumbent-aware evaluation that can abort early once it provably
  cannot beat the best so far (e.g. a tournament that cannot win any more).
- Evaluations are never assumed pure, cheap, or thread-safe. Single-threaded algorithms call the evaluation from
  exactly one thread; threaded algorithms accept a per-worker evaluator factory for evals with private scratch state.

## Algorithms

- `FullyRandomVectors(n)` — pure random search.
- `HillClimb(restarts)` — the core algorithm: hill climbing along random orthogonal directions with adaptive step
  sizes, from the initial point (if given) plus random restarts, with a final polish of the global best.
- `CoordinateDescent(...)` — axis-aligned per-parameter line searches; best when rotated directions round away to
  nothing (many integer parameters).
- `OrthogonalTraverseThreaded(threads)` — continuous parallel hill climbing; returns a stop-and-collect handle.
- `OrthogonalTraverseBreadthFirstThreaded(threads, broadIters)` — for extremely expensive evaluations: probe N
  directions once from a frozen best, then ride only the most promising one.

## Custom search loops

`config.CreateSeeker()` returns the engine — current position, chain-local and global incumbents, and traversal
primitives (`Evaluate`, `RandomizeAll`, `Move`, `TraverseDirection`, `OrthogonalTraverse`, `CoordinateSweep`,
`ResetChain`, `RestoreGlobalBest`, `SeedChain`, raw state save/restore). A "chain" is one climb; resetting it starts
a fresh exploration without forgetting the global best. Compose these into whatever schedule the problem demands —
bespoke restart policies, subspace searches over sparse directions, alternating phases, searches pumped
incrementally from a UI timer.

## Logging

`SeekerLog` is destination-agnostic (`Action<string>` sink, Console by default) with verbosity levels
(`Off`, `Improvements`, `Iterations`, `Steps`). `log.Sub(prefix, level)` forks a child logger for a sub-algorithm so
nested noise is controlled per parent. All logging is guarded: no strings are built when the level is off — safe
even for sub-microsecond evaluations.

```csharp
config.Log = SeekerLog.Console(SeekerLogLevel.Iterations);
```

## Checkpointing

```csharp
config.WithCheckpoint("mysearch-best.txt");
```

Saves the best values (throttled) to a small human-readable file plus an append-only history, and automatically
resumes from it on the next run — replacing the venerable practice of pasting best vectors back into source code.
