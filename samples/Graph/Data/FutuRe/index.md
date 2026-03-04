---
NodeType: Organization
Name: FutuRe
Category: Insurance & Reinsurance
Description: Insurance group with three business units, local-to-group LoB mapping, and profitability analysis
Icon: /static/storage/content/FutuRe/icon.svg
---

**FutuRe Insurance & Reinsurance** is a fictional group with three business units — **EuropeRe** (EUR), **AmericasIns** (USD), and **AsiaRe** (JPY). Each unit writes business under its own local Lines of Business. The group defines a standard LoB classification and uses **TransactionMapping** percentage splits to aggregate local data into group-level profitability.

## Organization

```mermaid
graph TD
    G[FutuRe Group]

    G --> EUR[EuropeRe]
    G --> AME[AmericasIns]
    G --> ASIA[AsiaRe]

    EUR --> EUR_LOB[8 local LoBs]
    EUR --> EUR_MAP[13 mapping rules]

    AME --> AME_LOB[8 local LoBs]
    AME --> AME_MAP[14 mapping rules]

    ASIA --> ASIA_LOB[LoBs pending]
    ASIA --> ASIA_MAP[Mapping pending]

    G --> G_LOB[10 group LoBs]

    click EUR "FutuRe/EuropeRe"
    click AME "FutuRe/AmericasIns"
    click ASIA "FutuRe/AsiaRe"
    click EUR_LOB "FutuRe/EuropeRe/LineOfBusiness/Search"
    click EUR_MAP "FutuRe/EuropeRe/TransactionMapping/MappingRules"
    click AME_LOB "FutuRe/AmericasIns/LineOfBusiness/Search"
    click AME_MAP "FutuRe/AmericasIns/TransactionMapping/MappingRules"
    click G_LOB "FutuRe/LineOfBusiness/Search"
```

## Analysis

Each business unit has a **local analysis hub** that loads its own CSV data cube. The **group analysis hub** aggregates data from all local hubs via PartitionedHubDataSource, applying TransactionMapping percentage splits to map local LoBs to group LoBs. No data is physically copied — the group view is virtual.

```mermaid
graph LR
    subgraph Local
        EUR_A[EuropeRe Analysis]
        AME_A[AmericasIns Analysis]
        ASIA_A[AsiaRe Analysis]
    end

    EUR_A -->|% splits| GROUP[Group Profitability]
    AME_A -->|% splits| GROUP
    ASIA_A -.->|pending| GROUP

    GROUP --> REPORT[Annual Report]

    click EUR_A "FutuRe/EuropeRe/Analysis"
    click AME_A "FutuRe/AmericasIns/Analysis"
    click ASIA_A "FutuRe/AsiaRe/Analysis"
    click GROUP "FutuRe/Analysis"
    click REPORT "FutuRe/Analysis/AnnualReport"
```

**Example**: EuropeRe's *Household* line maps **90 %** to group *Property* and **10 %** to group *Casualty*. The original data never leaves the EuropeRe hub — the group profitability cube reads it through a virtual transformation layer.

## Report

@@("FutuRe/Analysis/AnnualReport")

## Governance

Each business unit maintains its own mapping rules document with an inline governance discussion — actuarial rationale for split percentages, validation requirements, and the annual review cycle — captured as comments from the team.

- [EuropeRe Mapping Rules](@FutuRe/EuropeRe/TransactionMapping/MappingRules) — 13 split rules across 8 local LoBs
- [AmericasIns Mapping Rules](@FutuRe/AmericasIns/TransactionMapping/MappingRules) — 14 split rules across 8 local LoBs
