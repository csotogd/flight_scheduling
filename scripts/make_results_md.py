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
        "| Set | 1ª entera: t (s) | 1ª entera: objetivo | Mejor objetivo | Cota | Gap | Nodos | Tiempo (s) | Parada |",
        "|---|---|---|---|---|---|---|---|---|",
    ]
    for r in rows:
        gap = float(r["gap"])
        out.append(
            f"| {r['set']} | {float(r['firstIncTime']):.1f} | {int(float(r['firstIncObj'])):,} "
            f"| {int(float(r['bestObj'])):,} | {int(float(r['bound'])):,} | {gap:.2%} "
            f"| {r['nodes']} | {float(r['time']):.1f} | {r['stop']} |")
    return "\n".join(out)


plain = read(results / "benchmark.csv")
mnt = read(results / "benchmark-mnt.csv")

md = ["""# Resultados computacionales

Formato análogo a la Tabla 3 del paper. Instancias sintéticas (semilla 1) generadas con
`acsp generate`; solver LP HiGHS 1.15; gap objetivo 0,5 %; hardware: Apple Silicon (arm64).
Los valores absolutos no son comparables con el paper (instancias distintas), la estructura
del experimento sí.

## ACSP-T sin restricciones de mantenimiento (branch-and-price-and-cut exacto)
"""]
md.append(table(plain) if plain else "_pendiente_")
md.append("""
## ACSP-T + MC con mantenimiento (variante aproximada, como §9)
""")
md.append(table(mnt) if mnt else "_pendiente_")
md.append("""
Notas:
- "tree exhausted" = óptimo demostrado dentro del gap objetivo.
- Con mantenimiento el pricing de strings usa límite de labels σ=20 con *bucket ordering*
  (§6.2), por lo que la cota no es una garantía teórica — igual que la variante aproximada
  del paper. El `FeasibilityChecker` valida cada solución contra todas las restricciones.
- EX con mantenimiento no se intenta por defecto: el paper tampoco encontró soluciones
  enteras para EX+MC en 16–32 h (§9.3).
""")

(results / "RESULTS.md").write_text("\n".join(md))
print(f"wrote {results / 'RESULTS.md'}")
