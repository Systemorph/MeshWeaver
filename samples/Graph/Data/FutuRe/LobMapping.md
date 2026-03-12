---
NodeType: "FutuRe/Report"
Title: "Onboarding a New Business Unit"
Icon: /static/storage/content/FutuRe/icon.svg
Tags:
  - "FutuRe"
  - "LoB Mapping"
  - "Onboarding"
---

When an insurance group acquires a new subsidiary, one of the hardest tasks isn't signing the deal — it's integrating data. The new unit writes business under its own product classification, but the group needs a single consolidated view.

At FutuRe, **EuropeRe** and **AmericasIns** already map their local Lines of Business to 10 group-level categories. Now **AsiaRe** needs the same treatment. Here's how MeshWeaver makes that possible.

---

## The Challenge

Every business unit has its own product taxonomy. EuropeRe calls it "Household", AmericasIns calls it "Homeowners" — both map to the group's "Property" line, but at different percentages. Traditionally, building these mappings means months of spreadsheet work between actuaries, product owners, and IT teams.

<div style="display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; align-items: stretch;">
<div style="display: flex; flex-direction: column; justify-content: center;">

**Old Approach**

```mermaid
graph TD
    EMAIL[Email discussion between teams]
    SHEET[Spreadsheets back and forth]
    MANUAL[Manual JSON editing]

    EMAIL --> SHEET --> MANUAL

    classDef old fill:#fee,stroke:#c33,color:#333
    class EMAIL,SHEET,MANUAL old
```

</div>
<div style="display: flex; flex-direction: column; justify-content: center;">

**With MeshWeaver**

```mermaid
graph TD
    DISC[Email discussion]
    AGENT[MeshWeaver Agent extracts rules]
    RULES[Structured mapping rules]

    DISC --> AGENT --> RULES

    classDef new fill:#efe,stroke:#3a3,color:#333
    class DISC,AGENT,RULES new
```

</div>
</div>

---

## How the Mapping Works

Each local Line of Business splits into one or more group categories by percentage. The percentages always sum to 100% per local LoB — ensuring no premium is lost or double-counted.

```mermaid
graph LR
    subgraph AsiaRe Local LoBs
        FIRE[Fire and Allied]
        AUTO[Auto]
        MAR_JP[Marine Cargo]
        LIAB_JP[Liability]
        PA[Personal Accident]
        TECH[Tech and Cyber]
        MISC[Miscellaneous]
        LIFE[Life and Medical]
    end

    subgraph Group Standard
        PROP[Property]
        CAS[Casualty]
        MARINE[Marine]
        PROF[Professional Liability]
        LH[Life & Health]
        CYBER[Cyber]
        SPEC[Specialty]
        ENRG[Energy]
        AVTN[Aviation]
        AGRI[Agriculture]
    end

    FIRE -->|"75%"| PROP
    FIRE -->|"25%"| CAS
    AUTO -->|"100%"| CAS
    MAR_JP -->|"85%"| MARINE
    MAR_JP -->|"15%"| SPEC
    LIAB_JP -->|"60%"| CAS
    LIAB_JP -->|"40%"| PROF
    PA -->|"100%"| LH
    TECH -->|"70%"| CYBER
    TECH -->|"30%"| PROF
    MISC -->|"40%"| SPEC
    MISC -->|"30%"| AVTN
    MISC -->|"30%"| AGRI
    LIFE -->|"100%"| LH

    classDef local fill:#e8f0fe,stroke:#4285f4,color:#333
    classDef group fill:#e6f4ea,stroke:#34a853,color:#333
    class FIRE,AUTO,MAR_JP,LIAB_JP,PA,TECH,MISC,LIFE local
    class PROP,CAS,MARINE,PROF,LH,CYBER,SPEC,ENRG,AVTN,AGRI group
```

---

## Three BUs, One Pattern

EuropeRe and AmericasIns already follow this exact pattern. AsiaRe is the third — different local names, same structural approach.

```mermaid
graph TD
    subgraph EuropeRe - EUR
        EU1[8 local LoBs] -->|13 rules| EU2[10 group LoBs]
    end

    subgraph AmericasIns - USD
        AM1[8 local LoBs] -->|14 rules| AM2[10 group LoBs]
    end

    subgraph AsiaRe - JPY
        AS1[8 local LoBs] -->|12 rules| AS2[10 group LoBs]
    end

    EU2 --> GROUP[Consolidated Group PnL]
    AM2 --> GROUP
    AS2 --> GROUP

    classDef eu fill:#e8f0fe,stroke:#4285f4,color:#333
    classDef am fill:#fce8e6,stroke:#ea4335,color:#333
    classDef asia fill:#fef7e0,stroke:#fbbc04,color:#333
    classDef group fill:#e6f4ea,stroke:#34a853,color:#333
    class EU1,EU2 eu
    class AM1,AM2 am
    class AS1,AS2 asia
    class GROUP group
```

---

## Why This Matters

- **Data standardization** is consistently ranked as a top challenge in reinsurance onboarding — mapping local products to a group taxonomy is where months of effort go
- MeshWeaver agents can read an unstructured email thread and propose structured percentage splits, reducing manual effort from weeks to hours
- The resulting rules are **auditable** — every split percentage is versioned and reviewable directly in the platform
- Mappings are applied **virtually at query time** — no data is physically copied from the BU to the group level
- Adding a fourth BU tomorrow follows the same pattern: define local LoBs, create mapping rules, and the group view updates automatically

---

## Explore Further

- [EuropeRe Mapping Rules](@FutuRe/EuropeRe/TransactionMapping/MappingRules) — 13 rules across 8 LoBs
- [AmericasIns Mapping Rules](@FutuRe/AmericasIns/TransactionMapping/MappingRules) — 14 rules across 8 LoBs
- [AsiaRe Mapping Rules](@FutuRe/AsiaRe/TransactionMapping/MappingRules) — the newest addition
- [Group Lines of Business](@FutuRe/LineOfBusiness/Search) — the 10 standard categories
