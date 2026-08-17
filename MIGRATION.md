# Migrating from RomanOptim

RomanOptim (removed in `a930119`) operated on raw `double[]` with multiplicative-only moves and maximize-only
comparison. Everything it did maps onto the current API; this is the correspondence.

## Member map

| RomanOptim | Now |
|---|---|
| `new RomanOptim()` + delegate fields | `Seeker.WithVector(…).WithEval(…).WithGoal(…)` → `SeekerConfig` |
| `Evaluate = v => -cost(v)` (maximize-only negation idiom) | `WithEval(v => cost(v))` + `WithGoal(SeekerGoal.Minimize)` |
| `GenerateRandomVector` | parameter declarations own randomization: bounded params sample their range; `MulDbl(initial, spread)` samples ×2^±spread around the initial (spread 2 ≈ the common `× U(0.25, 4)` generator) |
| `RenormalizeVector` | per-parameter bounds in the declarations; `config.Renormalize` for cross-parameter constraints |
| `Log = s => Console.WriteLine(s)` | `config.Log = SeekerLog.Console(level)` (or any sink via `SeekerLog.To`); levels replace commenting log lines in and out |
| `ItersRandomGuess` | `HillClimb(restarts: n)` |
| `CanGrowIfEqual` | `TraverseOptions.CanGrowIfEqual` (still defaults to false) |
| `initialStep, stepGrowShrink, giveupBadDirStep, giveupGoodDirStep` args | `TraverseOptions` (same names, same defaults incl. 1.62 and the `MaxStepFactor` guard), per config via `WithTraverse` or per call |
| `OptimizeOnce(ref eval, ref vector)` | `config.HillClimb(restarts, options, startValues)`; seeds also via `initial:` in declarations or a checkpoint |
| `OrthogonalTraverse(ref, ref)` | `seeker.OrthogonalTraverse(options)` on `config.CreateSeeker()` |
| `TraverseDirection(dir, ref, ref)` / `TraverseDirectionForward` | same names on the seeker; state lives in the seeker, not in `ref` locals |
| `OrthogonalTraverseThreaded(...)` → stop-func | `config.OrthogonalTraverseThreaded(threads, options, startValues, evalFactory)` → stop-func returning `SeekerResult` |
| `OrthogonalTraverseBreadthFirstThreaded(...)` (blocked forever) | same-named config extension; now returns a stop-func like the other threaded entry |
| `OptimiseThreaded` (empty stub) | gone |
| `CreateRandomOrthogonalMatrix(dims)` | `Seeker.CreateRandomOrthogonalMatrix(dims, rnd)` (seedable) |
| `parstr(v)` | `SeekerLog.Num(v)`, plus `SeekerLog.Vec(values)` |
| private static unseedable `Rnd` | `config.Random` / `.WithRandom(seed)` |
| best vector pasted back into source | `.WithCheckpoint(path)` — throttled save + history + automatic resume |

## Step semantics

RomanOptim's `moveVector` was multiplicative for every component: `dir·step = 1` meant "double", `-1` meant
"halve", zeros were frozen forever and signs could never flip. Now each parameter owns its stepping:

- `MulDbl` / `RatioDbl` keep the old convention exactly (amount 1 doubles), so old step values carry over unchanged
  for pure scale-factor vectors.
- `LogarithmicDbl/Int` are multiplicative but scale the step to the log-range (amount 1 spans it).
- `LinearDbl/Int` are additive, scale the step to the range, and can reach and cross zero — the case RomanOptim
  could not express. If a parameter legitimately sits at or crosses 0, it must be declared linear.

Bounds that used to live as `.Clip(...)` calls inside evals or in `RenormalizeVector` belong in the declarations.

## Behavioural differences to be aware of

- Comparison is tri-state and generic: `TEval` need not be `double`, and exact equality drives the plateau logic
  that used to be the `eval == bestEval` special cases.
- Threaded searches now synchronize the shared best properly (RomanOptim's breadth-first phase read and wrote it
  unlocked) and derive per-worker seeded RNGs; evaluators with private scratch state use `evalFactory`.
- The incumbent is engine state (chain vs global best) rather than `ref` locals; a random restart is
  `ResetChain()` + `RandomizeAll()` + `Evaluate()`, and `OptimizeOnce`'s explore-a-worse-start-locally behaviour is
  preserved by the chain/global split.
- Logging is fully guarded: with the level off, no strings are built (RomanOptim formatted some lines
  unconditionally).

## Before / after

```csharp
// RomanOptim
var opt = new RomanOptim();
opt.Log = s => Console.WriteLine(s);
opt.Evaluate = v => -Cost(v);                       // maximize-only, so negate
opt.GenerateRandomVector = v => v.Select(x => x * Rnd.NextDouble(0.25, 4)).ToArray();
var bestVector = seed;
var bestEval = opt.Evaluate(bestVector);
opt.OptimizeOnce(ref bestEval, ref bestVector);

// Now
var result = Seeker.WithVector(ctx => seed.Select(x => ctx.MulDbl(x, randomizeSpread: 2)).ToArray())
    .WithEval(v => Cost(v))
    .WithGoal(SeekerGoal.Minimize)
    .WithLog(SeekerLogLevel.Improvements)
    .HillClimb(restarts: 10);
// result.BestVector, result.BestEval
```
