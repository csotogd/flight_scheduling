#!/usr/bin/env python3
"""Builds results/RESULTS.md (Table-3-style report) from the benchmark CSVs."""
import csv
import sys
from pathlib import Path

results = Path(sys.argv[1] if len(sys.argv) > 1 else "results")


def read(path):
    if not path.exists():
        return []
    with open(path) as f:
        return list(csv.DictReader(f))


def table(rows):
    out = [
        "| Set | 1st integer: t (s) | 1st integer: objective | Best objective | Bound | Gap | Nodes | Time (s) | Stop |",
        "|---|---|---|---|---|---|---|---|---|",
    ]

    def num(v):
        f = float(v)
        return "—" if f != f or f in (float("inf"), float("-inf")) else f"{int(f):,}"

    for r in rows:
        gap = float(r["gap"])
        first_t = float(r["firstIncTime"])
        out.append(
            f"| {r['set']} | {'—' if first_t != first_t else f'{first_t:.1f}'} | {num(r['firstIncObj'])} "
            f"| {num(r['bestObj'])} | {num(r['bound'])} | {'—' if gap != gap or gap > 1e6 else f'{gap:.2%}'} "
            f"| {r['nodes']} | {float(r['time']):.1f} | {r['stop']} |")
    return "\n".join(out)


plain = read(results / "benchmark.csv")
mnt = read(results / "benchmark-mnt.csv")

md = ["""# Computational results

Format analogous to Table 3 of the paper. Synthetic instances (seed 1) generated with
`acsp generate`; LP solver HiGHS 1.15; gap target 0.5%; hardware: Apple Silicon (arm64).
Absolute objective values are not comparable with the paper (different instances); the
structure of the experiment is.

## ACSP-T without maintenance constraints (exact branch-and-price-and-cut)
"""]
md.append(table(plain) if plain else "_pending_")
md.append("""
## ACSP-T + MC with maintenance (approximate variant, as in §9)
""")
md.append(table(mnt) if mnt else "_pending_")
md.append("""
Notes:
- "tree exhausted" = optimality proven within the gap target.
- With maintenance, string pricing uses the label limit σ=20 with *bucket ordering* (§6.2),
  so the bound is not a theoretical guarantee — same as the approximate variant in the paper.
  The `FeasibilityChecker` validates every solution against all constraints.
- EX with maintenance is not attempted by default: the paper did not find integer solutions
  for EX+MC within 16–32h either (§9.3).
""")

(results / "RESULTS.md").write_text("\n".join(md))
print(f"wrote {results / 'RESULTS.md'}")
