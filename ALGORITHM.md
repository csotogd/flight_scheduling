# ACSP — Technical report: the full algorithm

This document describes the complete solution procedure implemented in this repository:
the exact branch-and-price-and-cut of Derigs & Friederichs (2013) at its core, plus the
two layers added on top of the paper — a **restricted-master MIP heuristic** inside the
solver and an **autonomous network-design loop** around it — and the supporting
engineering (dual LP backends, Excel round-trip, verification). Paper section numbers
refer to OR Spectrum 35:325–362.

## 1. Core: the paper's method (summary)

The ACSP integrates flight selection, fleet assignment, rotation planning (with
maintenance) and cargo routing over a periodic week (N = 10,080 minutes). The
implementation follows the paper faithfully; details are in the code, one line each here:

- **Model ACSP-T** (§3.4): path-flow formulation over a timeline network (§3.2). Columns
  are cargo *paths* (feasible O&D itineraries, §3.1.8) and *flight strings* (maintenance-
  feasible sequences of flights operated by one fleet, §3.1.9, after Barnhart et al. 1998).
  Rows (14)–(22): flight cover/selection, string-flight linking, fleet counts, leg weight
  and volume capacities, demand limits.
- **Column generation**: PRICE-P (§6.1) prices cargo paths as an RCSPP solved with A*
  (admissible heuristic h(v), heuristic dominance tests); PRICE-S (§6.2) prices flight
  strings over a multi-week DAG with resources, label limit σ = 20 and *bucket ordering*.
- **Cuts** (§8): implied bound cuts linking flight-selection variables and capacities.
- **Branching** (§7): problem-specific strategies (optional flight in/out, fleet
  assignment, follow-on), depth-first, 1-branch first.
- **Exactness**: without maintenance the string pricer is exact and the whole procedure is
  an exact algorithm; with maintenance the label limit makes it approximate (same as §9).
- **Independent verification**: a `FeasibilityChecker` re-validates every accepted
  incumbent against all constraints of §2.2.4; the solver throws if it would ever emit an
  infeasible solution.

## 2. Addition 1: restricted-master MIP heuristic

**Not in the paper.** The paper's algorithm only obtains incumbents when a node's LP
relaxation happens to be integral or when depth-first branching bottoms out. On
industry-scale instances (1,250+ flights, 9,000 O&Ds) that means running for 15+ minutes
with no feasible schedule in hand — sometimes finishing with none at all.

The remedy is the standard *restricted master heuristic* from the column-generation
literature (also called price-and-branch), wired into the node loop:

- **What it does**: take every column generated so far (paths + strings), restore the
  integrality requirements on the string/selection variables, and solve that restricted
  MIP with the LP backend under a time budget. Any feasible integer solution found becomes
  an incumbent (after passing the `FeasibilityChecker`). No new columns are generated —
  the heuristic only recombines what pricing has already produced.
- **When it runs**: every `MipHeuristicFrequency` branch-and-bound nodes (default 40) and
  once more when the global time limit expires (the "last chance" pass that produced the
  only incumbent on GI-III).
- **Budget**: adaptive, `max(20 s, flights/12)` — large instances need a longer budget for
  the restricted MIP to reach feasibility.
- **Guarantees**: none beyond feasibility; the dual bound still comes from column
  generation, so the reported gap is honest. The heuristic can only improve the incumbent.
- **Measured effect**: on GI-I the first incumbent arrives in ~2–4 min instead of never
  within the 15-min limit; the branch-and-price then improves it. Cost: minutes per call
  on 1,500+-flight models, which dominates round times in the design loop below.

## 3. Addition 2: autonomous network-design loop

**Not in the paper**, but built on its "pragmatic paradigm" (§2.1): the model chooses from
a candidate list, a planner module *proposes* the candidates. The paper assumes the
planner writes the optional-flight list by hand; this loop generates it automatically and
iterates, so the tool designs the network with no human in the loop
(`NetworkDesigner` + `FlightProposer`).

### 3.1 One round

```
round 0:   solve the base instance (own flights only)          -> baseline profit, shipped[od]
round r:   1. PROPOSE a batch of candidate flights aimed at the demand the
              round r-1 solution left on the ground
           2. SOLVE the extended instance (full B&P&C + MIP heuristic, time-boxed)
           3. SCORE  each live candidate: flown (in a selected string) or booked
              (external) this round?
           4. EVICT  candidates unused for `EvictAfterRounds` consecutive rounds
              (the instance is rebuilt with dense ids; O&Ds never change)
           5. STOP   when round-over-round profit improvement < threshold,
              rounds are exhausted, or no new proposals exist
```

The best solution over all rounds is returned. Cross-round machinery (added after the
pool-scaling experiments of §5.1, which exposed each need):

- **Column-pool warm start**: the RMP columns generated in round r−1 are re-injected into
  round r (remapped after evictions), so its column generation only prices the incremental
  batch — root convergence in ~15–40 iterations instead of ~200.
- **Deadline with a Farley bound**: column generation checks the round clock every
  iteration; on expiry it returns the tightest *valid* dual bound seen during convergence
  (LP + Σ d_od·rc⁺ + Σ n_k·rc⁺ from full pricing passes), so hard per-round budgets are
  mathematically legal.
- **Seeded incumbent**: round r−1's schedule is feasible as-is in round r's model (new
  candidates at zero) and is accepted as the initial incumbent — a round can never return
  less than the previous one — and passed to CPLEX as a partial MIP start so the
  improvement heuristic spends its budget improving it rather than rediscovering it.
- **Amnesty**: an evicted candidate may be re-proposed a few rounds later (default 4) —
  the network context changes as accepted flights accumulate.
- **Final rescue solve**: after the loop, base + every candidate that was flown in at
  least one round (including later-evicted ones) is solved once with a longer clock, so
  synergies split across batches get one joint chance.
- **Stop rule**: only after 3 *consecutive* rounds below the improvement threshold — with
  rotating batches a single flat round does not mean the candidate space is exhausted.

Defaults: batch 100, up to 8 rounds, flat threshold 0.3%, evict after 2 unused rounds,
amnesty 4, 180 s per round (base solve 2×, final rescue 900 s).

**Batch sizing.** The binding constraint is the restricted-master MIP heuristic: there is
a batch size beyond which it cannot find a single improvement within its budget (observed
at 1,000 novelties on a 1,250-flight instance), while a tiny batch wastes the fixed
per-round cost (~2–3 min of pricing/LP) on little progress. Rule of thumb from the §5.1
experiments: **batch ≈ 20–25% of the base cargo flights, capped at ~300–400** (RC with
100 base flights → 60; GI with 1,250 → 300). Both failure modes are observable per
round — a large batch closing with 0.00% improvement means "too big", an early finish
with comfortable improvement means "room to grow" — so the planned next step is an
auto-batch mode that adapts AIMD-style (grow ~25% while rounds improve, halve on a flat
large-batch round), making the parameter self-tuning.

### 3.2 Candidate generation (`FlightProposer`)

Targets are computed from the *previous* solution, which is what couples the rounds:

- **Unroutable demand**: O&Ds for which no feasible itinerary exists at all (probed with
  the path pricer under a −∞ dual).
- **Capacity-crowded demand**: O&Ds that have a route but were left (partially) unshipped
  — the capacity signal.

Pairs are ranked by revenue at risk (unshipped tonnes × rate) and served three kinds of
candidates, with a batch quota of roughly **80% hub / 15% direct / 5% external**:

1. **Hub round trips** (the workhorse): `hub → spoke → hub`, timed to the target demand
   (pickup legs arrive before the onward connection; delivery legs depart after the
   inbound transfer). If neither endpoint is a hub, two coordinated round trips are
   proposed (pickup to the best-detour hub + delivery from it). Cargo can connect onward,
   so one candidate serves many O&Ds beyond the targeted pair.
2. **Direct rotations** (lower priority): `origin → destination → origin` with no hub,
   proposed only when the pair's unshipped demand fills at least **half the smallest
   airplane** — the case where skipping the hub detour and transfer cost wins.
3. **External charter/interline** (last resort): a single bookable external leg for pairs
   that *no own-fleet proposal can reach* (out of range, no usable hub). Priced at ~2× the
   own-fleet per-tonne economics plus a fixed booking fee, using the §4.1 external-cost
   extension already in the RMP (binary booking column). The model books one only when the
   stranded demand still pays at that premium — expensive capacity as a safety valve, not
   a bargain.

Candidate costs are synthesized from the instance itself (median own fixed cost per km per
fleet, average variable cost per km), so proposals are priced consistently with the
airline's real cost structure. Near-duplicates are suppressed with 6-hour departure
buckets per route, and dedup keys persist across rounds so the same candidate is never
proposed twice, even after eviction.

### 3.3 Properties and limits

- The outer loop is **greedy**: no global optimality claim across rounds (the inner solves
  keep their own bounds/gaps). Complementary flights are proposed as pairs (round trips,
  pickup+delivery) to reduce the risk of rejecting flights that only pay off together.
- Eviction keeps the active model bounded (~300–3,000 flights depending on batch), which
  is what makes large candidate pools tractable — the pool is explored over time, not
  held in the master all at once.
- Per-round time boxes mean later rounds on grown models report larger gaps; the final
  best solution is always feasibility-checked independently.

### 3.4 Deliver-all, local-branching adoption, and the contracted final seed

With a service commitment (`Instance.DeliverAll`) demand rows become equalities and every
O&D carries an always-available contracted-delivery column priced at 3× own flying
economics (`ExternalRecourse`, derived from the mandatory schedule only so the tariff is
identical across design rounds). Revenue becomes constant, the model minimizes the
contracting bill, and every selection is completable — seeds, sub-MIPs and the final solve
are feasible by construction.

Two mechanisms close the gaps this exposed on RLA (29,819 O&Ds):

- **Local-branching adoption** (`BpcOptions.LocalBranching`, Fischetti–Lodi): the primal
  MIP heuristic runs inside a Hamming ball of at most k selection flips around the
  incumbent (k = 60, escalating ×2, ×4 while flat), so each round answers the tractable
  question "best move of ≤ k changes" instead of re-deciding the whole network in one
  time-boxed MIP. The incumbent stays feasible inside the ball (monotone), and the solver
  — not a hand-built neighborhood — picks which flips, so cross-network rotation rewirings
  stay reachable. An adoption at the root is re-monetized with one LP over the pool
  (`adopt+flows`). Measured on RLA round 0: cover seed 129.9M → flow loader 139.6M →
  ball +2.7M → re-monetization +1.1M = 143.45M, the first heuristic improvement this
  model ever produced (the unrestricted heuristic moved +0 at the same budget).
- **Contracted seed for the exact final solve**: when the rounds ran on consolidated
  demand, the final solve swaps the full O&D set back in, where the coarse schedule's
  flows don't translate — but its flight selection does. The final is seeded with "same
  flights, everything contracted" (feasible by construction) and the flow loader monetizes
  it over the freshly priced full-demand pool. Before: two final attempts, no incumbent,
  no full-demand deliverable. After: 142.8M feasible on the full demand at a 9.9% gap.

Each round logs `adoption: LP promise vs adopted` (bound delta vs incumbent delta of the
candidate batch) — the observable separating "the proposer is weak" from "adoption is the
bottleneck". On RLA the promise decays from ~+4M (rounds 1–2) to ~0 by round 3: with a
strong incumbent under deliver-all economics, extra candidates genuinely stop paying —
the flat rounds are honest economics, not a closed adoption funnel.

## 4. Engineering layer

- **LP backends**: `ILpSolver` P/Invoke wrappers over HiGHS (bundled/Homebrew) and IBM
  CPLEX (auto-detected under `CPLEX_Studio*`; `ACSP_LP_BACKEND` overrides). Both pass the
  same dual-convention contract tests; measured 1.5–3.4× faster solves with CPLEX on the
  GI set. Column generation benefits from the backends' warm-started dual simplex.
- **Excel round-trip**: the full instance (airports, fleets, flights one row per leg,
  O&D demand) exports to a single workbook and imports back with row-level validation
  (unknown airports, broken leg chains, malformed times, missing capacities...) — the
  planner workflow is export → edit → upload, into either a one-shot solve or the design
  loop. No external spreadsheet libraries (raw OpenXML).
- **Web UI**: live convergence, world map with the designed network, time-space diagram,
  rotation Gantt, O&D demand-at-risk report, P&L, the design-rounds panel (profit bars +
  proposal lifecycle table), Excel itinerary export. CLI: `generate / solve / design /
  template / benchmark / diag`.

## 5. Measured results (synthetic instances, seed 1)

Backend benchmark (same instance, same limits):

| Instance | HiGHS | CPLEX |
|---|---|---|
| MI-I (600 s cap) | gap 1.56%, 8,371 nodes | gap 1.32%, 20,950 nodes |
| GI-I (0.5% target) | reached in 232 s | reached in 157 s |
| GI-II (0.5% target) | time-limited at 907 s, 0.50% | reached in 268 s |
| GI-III | root colgen saturates both backends (pricing-bound, ~29k columns) | idem, ~11% faster |

Autonomous design (batch 100, 6 rounds). "Flights/week" counts own-fleet flights operated
in the base vs the designed schedule (externally booked capacity noted separately):

| Instance | Base profit | Designed profit | Flights/week | Accepted / tried | Notes |
|---|---|---|---|---|---|
| RC-I | $0.47M | $1.60M (+240%) | 82 → 135 | 58 / 297 | converged round 4 |
| MI-I | $1.51M | $2.53M (+67%) | 73 → 116 | 43 / 600 | converged round 6 |
| GI-I | $44.8M | $94.0M (+110%) | 1,057 → 1,294 (+21 external bookings) | 258 / 600 | 233 hub + 4 direct + 21 external; still improving at round 6 |

On GI-I the designed network operates ~1,300 own flights (3,129 legs) with 111 of 148
aircraft, serving 73.4% of 35,153 t of weekly demand; the 21 external bookings cost $0.47M
and capture demand no own-fleet candidate could reach.

### 5.1 Candidate-pool scaling (GI-I, ~10,000-candidate pool)

Three regimes for exploring a large candidate pool, same instance and machinery:

| Regime | Result | Verdict |
|---|---|---|
| batch 100 × 6 rounds (600 tried) | $94.0M in ~57 min | solid baseline |
| batch 1,000 × 9 rounds | $44.8M — **zero progress** | root colgen converges (warm start) but the integer improvement step cannot digest 1,000 novelties within any reasonable budget, even MIP-started |
| batch 300 × up to 30 rounds (1,800 tried before convergence) | **$94.1M in ~75 min**, converged at round 6 | best profit and best wall-clock per dollar |

Takeaways: per-round cost is set by the *batch* size, not the pool size (eviction keeps the
model bounded); the binding constraint at scale is the restricted-master MIP heuristic, so
the batch must stay within what it can digest (~300 here); and the final rescue solve
(+42 once-flown, later-evicted candidates re-admitted jointly) found no further
improvement — evidence that on this instance the batch-split synergy blind spot costs
about nothing, while the same mechanism did improve the small RC instance (+0.2%).
The 300×30 network: 1,311 own flights + 31 external bookings (3,168 legs), 75.7% of
demand served, 112/148 aircraft, profit +110.1% over the base schedule.

### 5.2 The Farley bound and upper-bounded columns (resolved)

Early versions of the deadline bound displayed absurd per-round gaps (~2,000%) on seeded
rounds. Root cause, established by a forensic test that recomputes every priced column's
exact reduced cost from its RMP coefficients: pricers legitimately return columns that are
already in the master **nonbasic at their upper bound** (e.g. mandatory-covering strings
at y=1), whose reduced cost is genuinely positive at LP optimality — the bound dual
absorbs it. Feeding those rc's into the Farley sum inflated it by n_k × rc.

The correct formula only counts columns **outside** the master: for in-master columns the
(rc)⁺·u terms are already inside z_RMP via strong duality, so
z* ≤ z_RMP + Σ_od d_od·(best missing-path rc)⁺ + Σ_k n_k·(best missing-string rc)⁺.
This is implemented (membership filter) and covered by `FarleyBoundTests`, which assert
(a) pricer-claimed rc equals the exact coefficient-based rc, (b) missing columns never
carry positive rc at convergence, and (c) in-master columns with positive rc sit at their
upper bound. Measured effect on a seeded GI round cut at 4 min: reported gap 8.8%
(vs 2,016% before). The pre-fix bounds were valid but uselessly loose; solutions,
decisions and feasibility were never affected.

## 6. Known limitations

- Airport operating hours (curfews) are not modeled anywhere — generated and proposed
  flights can depart/arrive at any hour. Relevant for real European airports.
- GI-III-scale root column generation is pricing-bound; a faster LP does not help.
  Warm-starting columns across design rounds and pricing parallelization are the obvious
  next steps.
- Design rounds re-solve from scratch; no cross-round warm start yet.
- Instances are synthetic (calibrated to the paper's Tables 1–2), not real airline data.
