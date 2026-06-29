---
NodeType: Markdown
Name: PensionFund
Category: Pension & Retirement
Description: Swiss pension fund balance sheet modelled as a data cube — dimensions, facts, and computed positions, everything a mesh node
Icon: <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3v18M5 7l7-4 7 4M5 7v4a7 7 0 0 0 14 0V7"/><path d="M3 21h18"/></svg>
---

Welcome to **Helvetia Vorsorge** — a fictional Swiss pension fund whose balance sheet is modelled end-to-end as a MeshWeaver data cube. It is the worked example behind [Data Cubes](/Doc/DataMesh/DataCubes).

# Everything Is a Mesh Node

The model has no databases, no foreign keys, no surrogate ids — only mesh nodes referencing each other by **path**:

| Layer | NodeType | What it holds |
|---|---|---|
| Dimensions | [Position](/PensionFund/Position), [Year](/PensionFund/Year), [Currency](/PensionFund/Currency) | What a value *means*, when, and in which currency |
| Facts | [BalanceSheetEntry](/PensionFund/BalanceSheetEntry) | One atomic amount per Position × Year |
| Formulas | Computed [Position](/PensionFund/Position) nodes | *Total Assets*, *Pension Capital*, *Balance Sheet Sum*, *Funding Ratio* — modelled **out of** other positions |
| Reports | [Statement](/PensionFund/Statement) | Scope-evaluated statement, key figures, asset allocation |

A fact has **no Id property** — its identity is its node path. Its dimension columns store the *paths* of dimension nodes, declared with `[MeshNode("nodeType:PensionFund/Position")]`, which is also what renders mesh-node pickers in every edit form.

# Formulas Live on the Dimension

Computed positions carry their formula as data:

- **Total Assets** = Σ of the six atomic asset positions
- **Pension Capital** = Active Members' Capital + Pensioners' Capital + Technical Provisions
- **Available Assets** = Total Assets − short-term obligations (a Sum with **negative weights**)
- **Funding Ratio** = Available Assets ÷ Pension Capital (BVV2 Art. 44)

The `PositionValue` business-rules scope evaluates any position — atomic, sum, or ratio — by composing other `PositionValue` scopes, cached per (position, year).

# Explore

- The balance sheet statement, key figures, and asset-allocation chart: [Statement](/PensionFund/Statement)
- The full walk-through with executable code: [Doc/DataMesh/DataCubes](/Doc/DataMesh/DataCubes)
