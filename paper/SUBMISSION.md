# Optimization Online — submission metadata

Copy-paste ready. The form wants **plain text** (no LaTeX markup) and a **PDF** upload.
File to upload: `paper/acsp-matheuristic.pdf`

---

## Title

Towards Autonomous Air Cargo Network Design: A Measured Matheuristic over
Branch-and-Price at Integrator Scale

## Authors

Carlos Soto (Independent researcher)
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

- [ ] Read the PDF end to end yourself — it goes out under your name.
- [x] ORCID: 0009-0004-0176-427X (already in the paper's author footnote).
- [ ] Confirm you are comfortable with the AI-assistance disclosure as written
      (last section of the paper).
- [ ] Optional: archive the repository on Zenodo for a code DOI, and cite that
      DOI in the paper's reproducibility section.
