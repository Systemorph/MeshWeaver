---
NodeType: "FutuRe/Report"
Title: "Demo Roadmap"
Icon: /static/storage/content/FutuRe/icon.svg
Tags:
  - "FutuRe"
  - "Demo"
  - "Roadmap"
---

# Demo Roadmap

What we'll cover in the next ~30 minutes.

```mermaid
graph TD
    HOME["FutuRe Group<br/>Home Page"]

    HOME --> EU_BU["EuropeRe<br/>Business Unit"]
    EU_BU --> EU_LOB["EuropeRe<br/>Lines of Business"]
    EU_LOB --> EU_HUB["EuropeRe<br/>Analysis Hub<br/>Charts & KPIs"]

    HOME --> GROUP["Group Report<br/>Consolidated View"]
    GROUP --> MAPPING["LoB Mapping<br/>Live Editing"]
    MAPPING --> IMPACT["Numbers Change<br/>Instantly"]

    HOME --> FX["FX Conversion<br/>3 Currency Modes"]
    FX --> SLO["SLOs in Practice<br/>Frozen Rates & Sources"]

    HOME --> ASIA["Onboarding<br/>AsiaRe"]
    ASIA --> EMAIL["AI Agent Reads<br/>Email Discussion"]
    EMAIL --> SPLITS["Agent Generates<br/>Mapping Splits"]
    SPLITS --> LIVE["AsiaRe Integrated<br/>into Group P&L"]

    click HOME "FutuRe"
    click EU_BU "FutuRe/EuropeRe"
    click EU_LOB "FutuRe/EuropeRe/LineOfBusiness/Search"
    click EU_HUB "FutuRe/EuropeRe/Analysis/AnnualReport"
    click GROUP "FutuRe/Analysis/AnnualReport"
    click MAPPING "FutuRe/LobMapping"
    click FX "FutuRe/FxConversion"

    classDef home fill:#e8f0fe,stroke:#4285f4,color:#333,font-size:16px
    classDef explore fill:#e6f4ea,stroke:#34a853,color:#333
    classDef data fill:#fff3e0,stroke:#f57c00,color:#333
    classDef ai fill:#f3e8fd,stroke:#9c27b0,color:#333
    classDef result fill:#e6f4ea,stroke:#34a853,color:#333,stroke-width:2px

    class HOME home
    class EU_BU,EU_LOB,EU_HUB explore
    class GROUP,MAPPING,IMPACT,FX,SLO data
    class ASIA,EMAIL,SPLITS ai
    class LIVE result
```

---

| Arm | Stops | What We Show | Key Takeaway |
|-----|-------|-------------|-------------|
| **Explore** | [FutuRe Home](@FutuRe) → [EuropeRe](@FutuRe/EuropeRe) → [LoBs](@FutuRe/EuropeRe/LineOfBusiness/Search) → [Analysis Hub](@FutuRe/EuropeRe/Analysis/AnnualReport) | Navigate a BU, its local product lines, and profitability charts | Each BU owns its data and analytics — domain ownership in practice |
| **Consolidation** | [Group Report](@FutuRe/Analysis/AnnualReport) → [LoB Mapping](@FutuRe/LobMapping) | View consolidated P&L, then edit a mapping rule live — watch numbers update | Virtual aggregation — no copies, instant recalculation |
| **FX & SLOs** | [FX Conversion](@FutuRe/FxConversion) | Switch between Plan CHF, Actuals CHF, and Original Currency modes | Exchange rates are frozen monthly with clear ownership — SLOs guarantee data quality |
| **AI Onboarding** | AsiaRe → AI Agent → Mapping Splits | AI agent reads an email thread between actuaries, extracts LoB mapping percentages, and integrates AsiaRe into the group | Onboarding a new BU drops from months to minutes with AI-assisted data extraction |
