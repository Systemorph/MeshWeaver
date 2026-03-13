---
NodeType: "FutuRe/Report"
Title: "Why Data Mesh?"
Icon: /static/storage/content/FutuRe/icon.svg
Tags:
  - "FutuRe"
  - "Data Mesh"
  - "Strategy"
---

## Data Mesh Fundamentals

Each team owns and publishes its own data as a product — no central bottleneck, no waiting for the platform team.

<div style="display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; align-items: stretch;">
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    T1[Team A] -->|push| DW[(Central Warehouse)]
    T2[Team B] -->|push| DW
    T3[Team C] -->|push| DW
    DW -->|batch| R[Reports]

    classDef trad fill:#fce8e6,stroke:#ea4335,color:#333
    class T1,T2,T3,DW,R trad
```

</div>
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    D1[Team A<br/>owns its data] -->|publish| MESH[Mesh]
    D2[Team B<br/>owns its data] -->|publish| MESH
    D3[Team C<br/>owns its data] -->|publish| MESH
    MESH -->|live| V[Federated Views]

    classDef mesh fill:#e6f4ea,stroke:#34a853,color:#333
    class D1,D2,D3,MESH,V mesh
```

</div>
</div>

---

## From MBOs to SLOs

Each data product defines, monitors, and is accountable for its own quality guarantees — decentralised accountability replaces top-down cascade.

<div style="display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; align-items: stretch;">
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    CEO[CEO] -->|targets| VP1[VP Finance] -->|cascade| TEAM1[Team executes]
    CEO -->|targets| VP2[VP Ops] -->|cascade| TEAM2[Team executes]

    classDef mbo fill:#fce8e6,stroke:#ea4335,color:#333
    class CEO,VP1,VP2,TEAM1,TEAM2 mbo
```

</div>
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    P1[Claims Product] -->|owns| S1[Freshness < 1h<br/>Accuracy > 99%]
    P2[Pricing Product] -->|owns| S2[Availability 99.9%<br/>Latency < 5s]
    P3[Reporting Product] -->|owns| S3[Completeness 100%<br/>All BUs covered]

    classDef slo fill:#e6f4ea,stroke:#34a853,color:#333
    class P1,P2,P3,S1,S2,S3 slo
```

</div>
</div>

---

## Data is Addressed, Not Copied

Data stays where it lives. Consumers reference it by address — `@FutuRe/EuropeRe/Analysis` — zero copies, zero staleness, zero reconciliation.

<div style="display: grid; grid-template-columns: 1fr; gap: 1rem;">
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    SRC1[Source] -->|copy| STG[Staging] -->|copy| WH[Warehouse] -->|copy| MART[Data Mart] -->|copy| RPT1[Report]

    classDef copy fill:#fce8e6,stroke:#ea4335,color:#333
    class SRC1,STG,WH,MART,RPT1 copy
```

</div>
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    EU[EuropeRe<br/>Analysis] -.->|"@address"| RPT2[Report]
    AM[AmericasIns<br/>Analysis] -.->|"@address"| RPT2
    AS[AsiaRe<br/>Analysis] -.->|"@address"| RPT2

    classDef addr fill:#e6f4ea,stroke:#34a853,color:#333
    class EU,AM,AS,RPT2 addr
```

</div>
</div>

---

## Virtual Transformations

Transformations are virtual — computed on the fly as reactive streams. Source changes propagate instantly. Only materialise when absolutely necessary.

<div style="display: grid; grid-template-columns: 1fr; gap: 1rem;">
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    RAW1[Raw Data] --> T1[Transform] --> TBL1[(Table)] --> T2[Transform] --> TBL2[(Table)] --> T3[Transform] --> TBL3[(Table)]

    classDef mat fill:#fce8e6,stroke:#ea4335,color:#333
    class RAW1,T1,TBL1,T2,TBL2,T3,TBL3 mat
```

</div>
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    RAW2[Raw Data] --> V1[LoB Mapping] --> V2[FX Conversion] --> V3[Aggregation] --> OUT[Live Result]

    classDef virt fill:#e6f4ea,stroke:#34a853,color:#333
    class RAW2,V1,V2,V3,OUT virt
```

</div>
</div>

---

## Governance & Auditability

Every change passes through checkpoints. Hierarchical access control — who approved what, when — fully documented and auditable.

<div style="display: grid; grid-template-columns: 1fr; gap: 1rem;">
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    CHANGE[Data Change] --> VAL[Validation] -->|pass| APPROVAL[Approval Gate] -->|"approved by: Jane, 2025-03-01"| LOG[(Audit Log)] --> LIVE[Live Data]
    VAL -->|fail| REJ1[Rejected + reason]
    APPROVAL -->|denied| REJ2[Denied + reason]

    classDef flow fill:#e8f0fe,stroke:#4285f4,color:#333
    classDef gate fill:#fff3e0,stroke:#f57c00,color:#333
    classDef audit fill:#e6f4ea,stroke:#34a853,color:#333
    classDef reject fill:#fce8e6,stroke:#ea4335,color:#333
    class CHANGE,LIVE flow
    class VAL,APPROVAL gate
    class LOG audit
    class REJ1,REJ2 reject
```

</div>
</div>

---

## Types as Data

Data types are themselves data — stored, versioned, and queryable in the mesh. Change a schema or add a view without redeploying.

<div style="display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; align-items: stretch;">
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    CODE[App Code] --> SCHEMA[Hardcoded Schema] --> DEPLOY[Redeploy to Change]

    classDef trad fill:#fce8e6,stroke:#ea4335,color:#333
    class CODE,SCHEMA,DEPLOY trad
```

</div>
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    NT[NodeType<br/>stored as data] --> VIEWS[Views]
    NT --> VALID[Validation]
    NT --> HANDLERS[Handlers]
    NT --> HUB[Hub Config]

    classDef mesh fill:#e6f4ea,stroke:#34a853,color:#333
    class NT,VIEWS,VALID,HANDLERS,HUB mesh
```

</div>
</div>

---

## AI Agents as First-Class Citizens

AI agents discover schemas, query data, and execute operations through the same mesh APIs. External tools connect via MCP.

<div style="display: grid; grid-template-columns: 1fr; gap: 1rem;">
<div style="display: flex; flex-direction: column; justify-content: center;">

```mermaid
graph LR
    USER[User] -->|natural language| AGENT[AI Agent]
    AGENT -->|Get / Search| MESH[Mesh]
    AGENT -->|Create / Update| MESH
    MESH -->|schema + data| AGENT
    AGENT --> UI[Visual Result]
    EXT[GitHub Copilot<br/>Claude Code] -->|MCP| MESH

    classDef user fill:#e8f0fe,stroke:#4285f4,color:#333
    classDef agent fill:#f3e8fd,stroke:#9c27b0,color:#333
    classDef mesh fill:#e6f4ea,stroke:#34a853,color:#333
    classDef ext fill:#fff3e0,stroke:#f57c00,color:#333
    class USER user
    class AGENT agent
    class MESH,UI mesh
    class EXT ext
```

</div>
</div>

---

## Explore Further

- [FutuRe overview](@FutuRe) — see these principles in action
- [Data Distribution story](@FutuRe/DataDistribution) — how data stays where it belongs
- [FX Conversion story](@FutuRe/FxConversion) — SLOs applied to exchange rates
- [LoB Mapping story](@FutuRe/LobMapping) — governance in onboarding
- [Demo Roadmap](@FutuRe/DemoRoadmap) — what we'll cover today
