# Optimization Online — submission metadata

Copy-paste ready. The form wants **plain text** (no LaTeX markup) and a **PDF** upload.
File to upload: `paper/acsp-matheuristic.pdf`

---

## Title

Towards Autonomous Air Cargo Network Design: A Measured Matheuristic over
Branch-and-Price at Integrator Scale

## Authors

Carlos Soto García Delgado (Independent researcher)
ORCID: 0009-0004-0176-427X

## Abstract (plain text)

Integrated airline schedule design — which flights to operate, which fleet flies
each, how aircraft rotate, how cargo routes — remains planner-in-the-loop:
state-of-the-art exact methods optimize over a candidate flight list that a human
expert must write, and stall entirely at the scale of a global express integrator.
Both facts are bottlenecks to automation. We present an approach that addresses
them together, and a working system that measures it. Building on the
branch-and-price-and-cut of Derigs and Friederichs (OR Spectrum 35:325-362, 2013),
an autonomous design loop generates its own candidate flights round by round —
propose, re-optimize, keep what pays, evict what does not — while a matheuristic
layer keeps the optimization moving at scale: a deliver-all service model that
prices contracted delivery as recourse and makes every subproblem feasible by
construction; greedy cover seeding monetized by one warm LP over freshly priced
columns; an escalating local-branching ball for integer adoption; and a geographic
fix-and-optimize decomposition whose cross-boundary shipments carry exact
connection windows read from the frozen timetable, so regional improvements splice
into a globally verified schedule and the cycle is monotone by construction. On a
synthetic integrator-scale instance (29,819 shipments, 1,250 flights, 154
aircraft), the whole-network integer search adopts nothing in ninety minutes while
the regional cycle gains 4-5M of weekly profit (synthetic units) in minutes; every
component is toggleable and measured by ablation, including three negative results
reported with equal prominence. Results are single-seed and exploratory; we state
this, and the road to a full battery, explicitly.

## Keywords

air cargo scheduling; branch-and-price; column generation; matheuristics;
local branching; fix-and-optimize; network design; large-scale optimization

## Category

Pick from the live classification tree. Best fit, in order of preference:

1. Applications — OR and Management Sciences → Transportation
2. Integer Programming (if a decomposition/branch-and-price subcategory exists)
3. Optimization Software and Modeling Systems (secondary, if multiple allowed)

## Link to accompany the entry

https://github.com/csotogd/flight_scheduling

---

## Rules worth remembering (from the site's own terms)

- An account/registration is required; PDF only.
- A volunteer coordinator checks that the metadata is correct and the paper fits
  the chosen category — not quality or correctness. Their decision is final.
- You warrant the work is yours and that you hold any needed permissions.
- **If this is later accepted by a journal, you must update or remove the entry**
  and point to the published reference. Put a reminder somewhere.

## Before you press submit

- [ ] **BLOCKER (2026-09-05 audit): re-run the campaign on the fixed solver.** Two
      defects were found and fixed after the campaign: (1) design-round instance
      rebuilds dropped `CargoHandlingMinutes` (designed networks ran with easier
      connections than the baseline → all design uplifts are optimistic); (2) the
      dual bound could undershoot (label-capped PRICE-P path term; χ=0 strings
      escape the Σ n_k string aggregation) → pre-fix gaps, including RLA's 9.9%,
      are estimates, not certificates. The solver now emits `boundCertified` in
      every solution JSON, and the suite grew 120 → 124 tests. Every number in
      the paper/README produced by pre-fix runs (design uplifts, tree-table gaps,
      142.8M @ 9.9%, regional +4–5M) must be re-measured or stay explicitly
      marked as pre-fix estimates (current wording does the latter).
- [ ] **BLOCKER reinforced (2026-09-16 audit): four more solver defects fixed;
      the re-run must happen on this second-audit build.** (1) Exhausted trees
      reported Bound = incumbent / Gap = 0 although subtrees were pruned within
      the gap target — every "0.00% / tree exhausted" row overstates its
      certificate (the honest claim is optimum ≤ incumbent·(1+gapTarget));
      (2) the maintenance string pricer's week window missed connections two
      slots back (ExactMode was not exact → maintenance certificates could be
      invalid); (3) unconstrained elapsed-time limits (int.MaxValue) overflowed
      the week count and silently disabled maintenance string pricing;
      (4) the feasibility checker rejected week-wrapping rotation connections
      the master legally selects (and Rotation.AircraftNeeded undercounted them
      by one aircraft, so recomputed solution costs disagreed with the RMP
      objective). Paper propositions and pseudocode were also corrected to match
      the implementation (Prop. 1 freeze criterion and flow classification,
      Prop. 2 base case, eq:ibc restricted to own optional flights, Algorithm 1
      round cap / flat threshold / final-solve semantics and I*/σ* snapshot,
      fleet-slice seed-rejection caveat stated honestly — a seed-count clamp was
      tried and reverted: it silently disables the shave learning whenever the
      fleet is fully used). README/ALGORITHM design uplifts are now transcribed
      from retained artifacts (+222%/+204%/+110%, replacing the unsupported
      +240%/+67%; the GI row cites the batch-100 artifact, with the batch-300
      variant noted separately). An adversarial review of the fix diff itself
      then found and closed three more bound-reporting holes (adopted integral
      nodes under truncated colgen, the gap-target stop testing a different
      bound than the one reported, and a missing incumbent floor on non-empty-
      stack exits). Suite 124 → 130 tests (`AuditRegression2Tests`).
- [ ] Triage the audit's remaining unverified minors before v2: notation-table
      symbols (W double use), RLA pipeline figures vs artifacts, design-layer
      objective-evaluator mismatches (coarse vs full demand under
      ConsolidateTinyFar; regional recourse pricing vs acceptance test).
- [x] Closest concurrent work read and positioned against: Zhu, Belieres, Hewitt
      and Wu, Transportation Research Part B 209:103469 (2026). Cited, and the
      three differences (horizon, candidate space, scale/evidence) are stated in
      the related-work section; the absence of a head-to-head is a limitation.
- [ ] Spot-check three references taken from that paper's bibliography rather
      than from the sources themselves: Derigs et al. (2009), Xiao et al. (2022),
      Yildiz and Savelsbergh (2022).
- [ ] Read the PDF end to end yourself — it goes out under your name.
- [x] ORCID: 0009-0004-0176-427X (already in the paper's author footnote).
- [ ] Confirm you are comfortable with the AI-assistance disclosure as written
      (last section of the paper).
- [ ] Optional: archive the repository on Zenodo for a code DOI, and cite that
      DOI in the paper's reproducibility section.

## After it is posted

- Put a reminder to update or withdraw the entry if a journal accepts it.
- Planned v2 content, in order of value: the controlled scaling series within
  one instance family (one night of compute; it would turn "the breakdown is
  between 9k and 30k shipments, cause unattributed" into a measured curve);
  the multi-seed battery with component ablations; an arc-flow MIP baseline;
  mechanism figures and a managerial-insight section.
