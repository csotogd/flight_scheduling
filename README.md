# ACSP — Air Cargo Scheduling

Implementación en C#/.NET 8 del paper:

> Ulrich Derigs, Stefan Friederichs — **"Air cargo scheduling: integrated models and solution
> procedures"**, OR Spectrum (2013) 35:325–362, DOI 10.1007/s00291-012-0299-y.

El *Air Cargo Scheduling Problem* (ACSP) integra cuatro subproblemas de una aerolínea de carga
sobre un horario semanal periódico (N = 10.080 min):

1. **Selección de vuelos** — qué vuelos opcionales añadir al horario (paradigma pragmático:
   se parte de un horario existente con vuelos *mandatory* y candidatos *optional*).
2. **Fleet assignment** — qué tipo de flota opera cada vuelo.
3. **Rotation planning** — rotaciones cíclicas factibles por flota, con restricciones de
   mantenimiento (A-checks) vía *flight strings* (Barnhart et al. 1998).
4. **Cargo routing** — qué demanda O&D transportar por qué itinerarios (modelo path-flow).

Todo se resuelve de forma integrada con el modelo **ACSP-T** (§3.4) mediante
**branch-and-price-and-cut** (§5–§8).

## Estructura

| Proyecto | Contenido |
|---|---|
| `src/Acsp.Core` | Dominio: tiempo periódico, aeropuertos/flotas/legs/vuelos/O&Ds, factibilidad de paths (§3.1.8) y flight strings (§3.1.9), `Solution` + `FeasibilityChecker` independiente (FA-*/RP-*/CR-*) |
| `src/Acsp.Data` | Generador de instancias sintéticas (§9.1–9.2, Tablas 1–2): aerolíneas RC/IC/MI/EX × sets I/II/III; IO JSON |
| `src/Acsp.Solver` | `TimelineNetwork` (§3.2), `Rmp` (filas (14)–(22) + cortes), `PathPricer` (PRICE-P: RCSPP con A*, §6.1), `StringPricer` (PRICE-S: DAG multi-semana con recursos, σ=20 y *bucket ordering*, §6.2), `ImpliedBoundCuts` (§8), `Branching` (§7), `BranchAndPrice` (Fig. 3), `SolutionAssembler`, `DirectMipSolver` (baseline), backends LP (`HighsSolver`, hueco `CplexSolver`) |
| `src/Acsp.Cli` | `generate` / `solve` / `benchmark` / `diag` |
| `src/Acsp.Web` | Web app local: API + SSE + dashboard (mapa de red, Gantt de rotaciones, O&Ds, P&L, convergencia en vivo) |
| `tests/Acsp.Tests` | 88 tests xUnit (ver Verificación) |

## Requisitos

- .NET 8 SDK (`~/.dotnet` vía `dotnet-install.sh` o Homebrew)
- HiGHS: `brew install highs` (se carga `libhighs.dylib` por P/Invoke; ruta configurable con
  `ACSP_LIBHIGHS`)
- Opcional: IBM CPLEX Studio ≥ 22.1.1 — el backend se selecciona con
  `ACSP_LP_BACKEND=cplex` cuando se cablee `CplexSolver` (pendiente de instalación local).

## Uso

```bash
# tests
dotnet test

# generar las 60 instancias del paper (4 aerolíneas x 3 sets x 5 semillas)
dotnet run --project src/Acsp.Cli -c Release -- generate --airline all --set all --seeds 5

# resolver una instancia (sin mantenimiento; añadir --maintenance para FARP-TS)
dotnet run --project src/Acsp.Cli -c Release -- solve instances/RC-I-s1.json --time-limit 600

# benchmark tipo Tabla 3
dotnet run --project src/Acsp.Cli -c Release -- benchmark --airlines RC,IC,MI,EX --sets 1,2,3 \
  --seeds 1 --time-limit 600 --out results

# web app en http://localhost:5170
dotnet run --project src/Acsp.Web
```

## Verificación

- **Pricers vs fuerza bruta**: PRICE-P y PRICE-S (en modo exacto) reproducen el mejor reduced
  cost de la enumeración exhaustiva sobre instancias pequeñas con duales aleatorios y cortes.
- **Column generation exacta**: el LP por generación de columnas coincide con el LP con *todas*
  las columnas pre-generadas para CRP-P aislado, FARP-T/FARP-TS aislado y el modelo integrado.
- **B&P&C vs MIP directo**: el branch-and-price-and-cut alcanza el óptimo del MIP con columnas
  pre-generadas (bloque 1 de la Tabla 3) en instancias artesanales y generadas, con y sin
  mantenimiento.
- **`FeasibilityChecker`**: verificador independiente de todas las restricciones del §2.2.4
  que se ejecuta sobre cada incumbente aceptado (el solver lanza excepción si emite una
  solución infactible).

Como en el paper, el procedimiento es **exacto sin restricciones de mantenimiento** y
**aproximado con ellas** (el límite de labels σ del pricing de strings hace que la cota del
LP no sea válida en teoría; §9 usa la misma variante aproximada).

## Diferencias con el paper

- Solver LP: HiGHS (open source) en lugar de CPLEX 12.1; la interfaz `ILpSolver` permite
  enchufar CPLEX como backend alternativo.
- Heurística primal adicional: MIP sobre las columnas generadas (incumbentes tempranos).
  Los mecanismos del paper (branching específico, cortes, DFS 1-branch primero) están
  implementados tal cual.
- Las instancias son sintéticas pero calibradas a la Tabla 1/2 (tamaños de red, flotas,
  demanda en TKM); los valores objetivo absolutos no son comparables con la Tabla 3.

## Resultados

Ver [results/RESULTS.md](results/RESULTS.md) (generado con el benchmark; CSVs en `results/`).
