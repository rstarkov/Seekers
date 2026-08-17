# Possible future improvements

Ideas that surfaced while building the library and migrating real optimization problems onto it, but which did not
yet earn their place. Each is known to have at least one real use case. None are speculative API sugar; the risk with
all of them is over-generalizing, so each should be added only when a concrete problem demands it.

## Noisy / stochastic evaluations

Some evaluations are measurements, not computations: the same vector returns different values on different runs.
Greedy acceptance then locks in lucky noise, and the incumbent's recorded score drifts away from its true score.
Support would mean: evaluate a candidate k times and aggregate (arithmetic or geometric mean — the latter matters
when the metric is a product of factors); optionally re-evaluate a candidate before accepting it as the new best;
and periodically re-evaluate the incumbent itself, since a stale lucky score silently blocks all progress. This
should be a config-level wrapper around the evaluation so every algorithm gets it for free.

## Diverse-best archive (niching)

Some problems want *several* good answers, not one — e.g. the best solution for each structural variant, so the
final choice can weigh simplicity against score. Support would mean an archive of up to N bests with a user-supplied
"too similar" predicate: a new entry displaces only the entries it is similar to (keeping the better of the two),
and eviction removes the worst. The archive doubles as a restart pool and as the data behind "best with at most k
components"-style reporting. This is the main missing piece for population-flavoured searches.

## 1-D solvers

At least three hand-rolled one-dimensional solvers exist in the problems studied: bracket-and-grow followed by
bisection, ternary search on a unimodal function, and binary search on a monotone predicate. They are typically
called *inside* an evaluation (solve a constraint exactly rather than making it a search dimension), which is the
right structure and should stay — but the solvers themselves should be one library call each, with well-defined
behaviour on degenerate brackets. Hand-rolled versions accumulate defensive `throw`s where the author wasn't sure of
the edge cases.

## Magnitude-proportional multiplicative parameter

One problem used a step operator that multiplies the *logarithm* of the value, so parameters far from 1 take
proportionally larger multiplicative steps than parameters near 1 — a useful self-scaling property when one shared
step size must serve parameters spanning several orders of magnitude. The ad-hoc version had serious defects (a
fixed point at exactly 1, the intervals (0,1) and (1,∞) mutually unreachable, direction sense inverting below 1). A
proper parameter type could keep the self-scaling while handling the 1-boundary explicitly. Low priority: per-group
direction scaling already covers most of the need.

## Cyclic parameters

Angles and phases wrap: 359 is adjacent to 0. The current workaround — declare wide linear bounds and rely on the
evaluation being periodic, wrapping the value only for presentation — works but leaks into user code and breaks
`Randomize` uniformity subtly. A `CyclicDbl(period)` parameter would wrap on move, randomize over one period, and
report values in canonical range.

## Built-in evaluation memoisation

Hill climbing over integer parameters revisits lattice points constantly, and when an evaluation costs milliseconds
or more, caching by effective values is the single highest-leverage optimization — currently done by hand with a
dictionary inside the eval. A config flag could do this centrally, keyed on the effective value tuple, with a size
cap. It must remain opt-in: for stateful or impure evaluations a cache is wrong, and for microsecond evaluations the
dictionary costs more than it saves.

## Graded infeasibility

Infeasible candidates are currently expressed as sentinel evaluations ("worse than anything"), which makes the
infeasible region a flat wall the search cannot gradient across. A first-class result carrying a *degree* of
violation (e.g. bytes over budget) would let the comparison rank infeasible candidates among themselves, so the
search walks back toward feasibility instead of stalling. This composes naturally with custom `TEval` comparisons
but deserves a helper so users don't reinvent the ordering (all feasible beat all infeasible; infeasible sorted by
violation).

## Comparator builders

Raw `Comparison<TEval>` lambdas are error-prone: one real comparator studied has an inverted branch that ranks more
defects as better, invisible because the sign convention lives entirely in the author's head. A small builder —
`SeekerCompare.By(x => …).ThenByDescending(…).GateOn(x => feasible)` with explicit per-key direction and an epsilon
band option — would make lexicographic orderings, feasibility gates and banded equality declarative and reviewable.

## Mutable bounds

One algorithm family shrinks a parameter's range around accumulating evidence (a bounding box of tied bests) and
expands it again on stagnation. Bounds today are fixed at declaration. Parameters could expose live re-bounding
(with well-defined clamping of the current position), which also serves adaptive "zoom" schedules.

## Block-structured / growing vectors

Some models grow during optimization: the vector is a sequence of blocks (one per model component), new blocks are
appended over time, and refinement sweeps blocks one at a time against an incrementally-maintained evaluation cache
(subtract the block, evaluate candidates against the cached remainder, re-add). Today this is done by constructing a
small per-block seeker inside the outer loop, which works well; first-class support would mean declaring blocks
once, per-block enter/leave hooks for cache maintenance, and an explicit "revalidate the objective" step to purge
floating-point drift after each sweep.

## Evaluation-history retention and repulsion-weighted restarts

One restart strategy biases new starting points away from everywhere already sampled: a candidate restart is
rejection-sampled against a soft repulsion field built from the entire evaluation history, with the repulsion radius
shrinking as the history grows. This needs the library to (optionally) retain sampled positions, which it currently
discards. The history is also useful for post-hoc analysis of the landscape.

## Vector simplification pass

After a search converges, a distinct secondary search can look for the *simplest* vector that performs equally
within an epsilon band: round each parameter to fewer significant digits, nudge values toward their minimums, accept
any change that doesn't lose (banded) score. This "prefer the simplest answer" polish exists hand-rolled in one
problem and generalizes well: it needs per-parameter precision state and an epsilon-banded acceptance rule, neither
of which the current parameter model carries.

## Zooming lattice sweep

A packaged exhaustive alternative to hill climbing: evaluate a coarse lattice over the full box, then repeatedly
re-centre a finer lattice on the best point until the step is small. Wasteful per evaluation but immune to
deceptive local structure at coarse scales, and some users reach for it deliberately. Worth packaging mainly so the
zoom bookkeeping (re-centred bounds, ordering constraints between parameters) is written once, correctly.

## Cancellation tokens

Long-running searches are currently stopped by the threaded algorithms' stop-and-collect handles or by throwing
`SeekerBreakException` from the evaluation. Accepting a `CancellationToken` in the packaged single-threaded
algorithms would let hosts cancel without cooperating evaluations, with the same best-so-far result semantics.
