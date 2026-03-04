---
NodeType: Organization
Name: FutuRe
Category: Insurance & Reinsurance
Description: Insurance group with two business units, local-to-group LoB mapping, and profitability analysis
Icon: /static/storage/content/FutuRe/icon.svg
---

**FutuRe Insurance & Reinsurance** is a fictional group with two business units — **EuropeRe** (EUR) and **AmericasIns** (USD). Each unit writes business under its own local Lines of Business. The group defines a standard LoB classification and a **TransactionMapping** with percentage splits to aggregate local data into group-level profitability.

## Organization

```mermaid
graph TD
    G["FutuRe Group"]

    G --> EUR["EuropeRe<br/>EMEA · EUR"]
    G --> AME["AmericasIns<br/>Americas · USD"]

    EUR --> EUR_LOB["Line of Business<br/>8 local LoBs"]
    EUR --> EUR_MAP["Transaction Mapping<br/>13 split rules"]

    AME --> AME_LOB["Line of Business<br/>8 local LoBs"]
    AME --> AME_MAP["Transaction Mapping<br/>14 split rules"]

    G --> G_LOB["Group Line of Business<br/>10 standard LoBs"]
    G --> G_ANALYSIS["Profitability Analysis<br/>Data Cube · 6 Amount Types"]

    click EUR "FutuRe/EuropeRe"
    click AME "FutuRe/AmericasIns"
    click EUR_LOB "FutuRe/EuropeRe/LineOfBusiness/Search"
    click EUR_MAP "FutuRe/EuropeRe/TransactionMapping/Search"
    click AME_LOB "FutuRe/AmericasIns/LineOfBusiness/Search"
    click AME_MAP "FutuRe/AmericasIns/TransactionMapping/Search"
    click G_LOB "FutuRe/LineOfBusiness/Search"
    click G_ANALYSIS "FutuRe/Profitability"
```

## Virtual Transformation

Local transactions stay in their business unit databases. The group view is **virtual** — TransactionMapping applies percentage splits on the fly without copying data.

```mermaid
graph TB
    subgraph BU1["EuropeRe — Physical Storage"]
        direction LR
        E1[("Household")]
        E2[("Motor")]
        E3[("Comm. Fire")]
        E4[("Liability")]
        E5[("Transport")]
        E6[("Tech Risk")]
        E7[("Life & Health")]
        E8[("Specialty")]
    end

    subgraph BU2["AmericasIns — Physical Storage"]
        direction LR
        A1[("Homeowners")]
        A2[("Workers Comp")]
        A3[("Commercial")]
        A4[("Energy")]
        A5[("Life & Ann.")]
        A6[("Cyber")]
        A7[("Specialty")]
        A8[("Agriculture")]
    end

    BU1 -->|"TransactionMapping · virtual % splits"| VL
    BU2 -->|"TransactionMapping · virtual % splits"| VL

    subgraph VL["Group View — Virtual · No Physical Storage"]
        direction LR
        V1["Property"]
        V2["Casualty"]
        V3["Marine"]
        V4["Energy"]
        V5["Life & Health"]
        V6["Cyber"]
        V7["Prof. Liability"]
        V8["Specialty"]
        V9["Aviation"]
        V10["Agriculture"]
    end

    style BU1 fill:#1a3a5c,stroke:#3b82f6,color:#fff
    style BU2 fill:#1a3a5c,stroke:#3b82f6,color:#fff
    style VL fill:none,stroke:#f59e0b,stroke-dasharray:5 5
```

**Example**: EuropeRe's *Household* line maps **90%** to group *Property* and **10%** to group *Casualty*. The original data never leaves the EuropeRe database — the group profitability cube reads it through a virtual transformation layer.

## Reports

- [Annual Profitability Report](@FutuRe/Profitability/AnnualReport) — KPIs, charts, and LoB breakdown with embedded live views from the profitability data cube

## Governance

Each business unit maintains its own mapping rules document with an inline governance discussion — actuarial rationale for split percentages, validation requirements, and the annual review cycle — captured as comments from the team.

- [EuropeRe Mapping Rules](@FutuRe/EuropeRe/TransactionMapping/MappingRules) — 13 split rules across 8 local LoBs
- [AmericasIns Mapping Rules](@FutuRe/AmericasIns/TransactionMapping/MappingRules) — 14 split rules across 8 local LoBs
