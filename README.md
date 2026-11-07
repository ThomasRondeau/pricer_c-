# Equity Pricer

An equity derivatives pricing library in C# / .NET 9, with a WPF desktop front end and a
browser demo that runs the same library in WebAssembly.

**Live demo: https://thomasrondeau.github.io/Pricer_Equity/** — no install. Every price is computed
in your browser; there is no server.

## What it does

- **Products**: stocks, forwards, European and American options, digitals, barriers, Asians,
  reverse convertibles, autocalls, Phoenix notes and worst-of Phoenix.
- **Models**: Black-Scholes, Dupire local vol built from an arbitrage-checked SVI surface, Heston.
- **Engines**: closed forms, Crank-Nicolson PDE with Rannacher start-up, Monte Carlo with Sobol
  sequences and a Brownian bridge (SIMD, deterministic seeds, standard error always reported).
- **Greeks by the best method available** — closed form, semi-analytic, PDE grid, pathwise,
  likelihood ratio — each one labelled with its method and convention and checked against a
  bump-and-reprice control.
- **Book and runs** (desktop): positions from IBKR plus local trades, cached runs, run diffs down
  to the parameter, and a full-revaluation **PnL explain** waterfall cross-checked with a greeks
  explain.

## Design

A pricing call is a pure function of immutable, hashed inputs:

```
Price(trade, market snapshot, model config, engine config, valuation date, measures) -> result
```

Bumped greeks, sliders, stress scenarios, run comparisons and the PnL explain are all this one
function called with modified inputs, so every run can be replayed exactly. Models and engines
describe their parameters with metadata, from which both user interfaces generate their forms.

```
src/
  Pricer.Core          day counts, content hashing, parameter metadata
  Pricer.Market        snapshots, curves, SVI surfaces, market bumps
  Pricer.Instruments   term sheets and a payoff language compiled to dated events
  Pricer.Models        Black-Scholes, local vol, Heston, SVI calibration
  Pricer.Engines       analytic, PDE and Monte Carlo engines
  Pricer.Risk          greeks, model comparison
  Pricer.Book          positions, runs, PnL explain
  Pricer.Data          SQLite storage, IBKR and Yahoo capture
  Pricer.Presentation  view models shared by both front ends
  Pricer.App           WPF desktop application
  Pricer.Web           Blazor WebAssembly demo (Pricer and Model Lab pages)
  Pricer.Capture       daily market capture and web data export
tests/Pricer.Tests     reference tests: closed forms, convergence, greek consistency, PnL explain
```

## Running it

```powershell
dotnet test tests/Pricer.Tests -c Release
dotnet run --project src/Pricer.App -c Release      # desktop (Windows)
dotnet run --project src/Pricer.Web                 # web demo on http://localhost:5145
```

Publishing the web demo in Release compiles it ahead of time to WebAssembly and needs
`dotnet workload install wasm-tools`. The demo's market data is regenerated with
`dotnet run --project src/Pricer.Capture -- --export-web src/Pricer.Web/wwwroot/data --ids <capture ids>`.

## Data

The demo ships a synthetic SVI market and two Yahoo Finance option chain captures from
2026-10-30, recalibrated by the library. They are for demonstration only.
