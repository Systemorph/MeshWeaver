---
NodeType: Markdown
Title: "Demo Roadmap"
Icon: /static/storage/content/FutuRe/icon.svg
Tags:
  - "FutuRe"
  - "Demo"
  - "Roadmap"
---

What we'll cover in the next ~30 minutes — following the journey from three siloed business units to a unified, governed data mesh.

```mermaid
%%{init: {'theme':'neutral'}}%%
graph TD
    SILOS["1️⃣ The Silos\nThree BUs, three worlds"]

    SILOS --> LOCAL["2️⃣ Local Data Cubes\nEach BU owns its data"]
    LOCAL --> EU_HUB["EuropeRe\nAnalysis Hub"]
    LOCAL --> AM_HUB["AmericasIns\nAnalysis Hub"]

    LOCAL --> COMBINE["3️⃣ Combining\nTwo transformations"]
    COMBINE --> LOB["LoB Mapping\nLocal → Group LoBs"]
    COMBINE --> FX["FX Conversion\n3 currencies → CHF"]

    LOB --> GROUP["4️⃣ Group Dashboard\nConsolidated P&L"]
    FX --> GROUP

    GROUP --> ONBOARD["5️⃣ AI Onboarding\nAsiaRe integration"]

    click EU_HUB "FutuRe/EuropeRe/Analysis/AnnualReport"
    click AM_HUB "FutuRe/AmericasIns/Analysis/AnnualReport"
    click LOB "FutuRe/LobMapping"
    click FX "FutuRe/FxConversion"
    click GROUP "FutuRe/Analysis/AnnualReport"

    classDef silo fill:#fce8e6,stroke:#ea4335,color:#333
    classDef local fill:#e8f0fe,stroke:#4285f4,color:#333
    classDef transform fill:#fff3e0,stroke:#f57c00,color:#333
    classDef group fill:#e6f4ea,stroke:#34a853,color:#333
    classDef ai fill:#f3e8fd,stroke:#9c27b0,color:#333

    class SILOS silo
    class LOCAL,EU_HUB,AM_HUB local
    class COMBINE,LOB,FX transform
    class GROUP group
    class ONBOARD ai
```

---

## 1. The Silos

- **Show:** [FutuRe Home](@FutuRe) — three disparate BUs with different systems, databases, spreadsheets
- **Key Takeaway:** This is every insurance group's reality — fragmented data, no single source of truth

---

## 2. Local Data Cubes

- **Stops:** [EuropeRe Analysis](@FutuRe/EuropeRe/Analysis/AnnualReport) → [EuropeRe LoBs](@FutuRe/EuropeRe/LineOfBusiness/Search)
- **Show:** Navigate a BU, explore its local P&L structure, 8 Lines of Business, charts & KPIs
- **Key Takeaway:** Each BU owns its data as a local data product — domain ownership in practice

---

## 3. Combining: LoB Mapping + FX Conversion

- **Stops:** [LoB Mapping](@FutuRe/LobMapping) → [EuropeRe Mapping Rules](@FutuRe/EuropeRe/TransactionMapping/MappingRules) → [FX Conversion](@FutuRe/FxConversion)
- **Show:** How local product lines map to 10 group categories with percentage splits. How three currencies convert to CHF with Plan vs. Actuals modes
- **Key Takeaway:** Virtual aggregation — no copies, instant recalculation. Both transformations are governed data products with SLOs

---

## 4. Group Dashboard

- **Stops:** [Group Report](@FutuRe/Analysis/AnnualReport)
- **Show:** Consolidated P&L across all BUs. Switch currency modes. Drill into Lines of Business. Compare Estimate vs. Actual
- **Key Takeaway:** One view, assembled live from three independent data domains — change a number in EuropeRe, see it instantly in the group

---

## 5. AI Onboarding

- **Show:** AI agent reads an email thread between actuaries, extracts LoB mapping percentages, generates governance document, and integrates AsiaRe into the group
- **Key Takeaway:** Onboarding a new BU drops from months to minutes with AI-assisted data extraction. The result is governed, versioned, and auditable — not a spreadsheet attachment

---

## Governance Stops (if time permits)

- [Group Lines of Business](@FutuRe/LineOfBusiness/Search) — SLOs, change request process, approval authority
- [Exchange Rate Hub](@FutuRe/ExchangeRate) — Publication schedule, rate sources, freeze policy
- [Why Data Mesh?](@FutuRe/WhyDataMesh) — The principles behind this architecture
