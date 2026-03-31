---
NodeType: Markdown
Title: "Data Where It Belongs"
Icon: /static/storage/content/FutuRe/icon.svg
Tags:
  - "FutuRe"
  - "Data Mesh"
  - "Data Distribution"
---

In most insurance groups, consolidation means copying data from subsidiaries into a central warehouse — nightly ETL jobs, staging tables, transformation scripts, and stale snapshots. MeshWeaver takes a different approach: **data stays where it is**. The group view is assembled virtually through reactive stream composition.

---

## Data Mesh Architecture

Each business unit owns its data. The group hub doesn't store copies — it reads from each BU's data domain through virtual streams and applies mapping rules on the fly.

```mermaid
graph LR
    subgraph EuropeRe Domain
        EU_CSV[Datacube EUR amounts]
        EU_SVC[IContentService]
    end

    subgraph AmericasIns Domain
        AM_CSV[Datacube USD amounts]
        AM_SVC[IContentService]
    end

    subgraph AsiaRe Domain
        AS_CSV[Datacube JPY amounts]
        AS_SVC[IContentService]
    end

    EU_CSV --> EU_SVC
    AM_CSV --> AM_SVC
    AS_CSV --> AS_SVC

    subgraph Group Hub
        PART[PartitionedHubDataSource]
        MAP[TransactionMapping + FX Conversion]
        AGG[Consolidated View]
    end

    EU_SVC --> PART
    AM_SVC --> PART
    AS_SVC --> PART
    PART --> MAP --> AGG

    classDef eu fill:#e8f0fe,stroke:#4285f4,color:#333
    classDef am fill:#fce8e6,stroke:#ea4335,color:#333
    classDef asia fill:#fef7e0,stroke:#fbbc04,color:#333
    classDef hub fill:#e6f4ea,stroke:#34a853,color:#333
    class EU_CSV,EU_SVC eu
    class AM_CSV,AM_SVC am
    class AS_CSV,AS_SVC asia
    class PART,MAP,AGG hub
```

---

## Traditional ETL vs Virtual Streams

The traditional approach copies data through a multi-stage pipeline — extract, transform, load — before anyone can see a consolidated report. MeshWeaver composes reactive streams instead. No intermediate tables, no batch schedules.

```mermaid
graph TD
    subgraph Traditional Approach
        T_SRC[BU Databases] -->|Extract| T_STAGE[Staging Tables]
        T_STAGE -->|Transform| T_DW[Data Warehouse]
        T_DW -->|Load| T_REPORT[Reports]
    end

    subgraph MeshWeaver Approach
        M_SRC[BU Data] -->|Stream| M_HUB[Virtual Hub]
        M_HUB -->|Compose| M_REPORT[Live Reports]
    end

    classDef trad fill:#fee,stroke:#c33,color:#333
    classDef mesh fill:#efe,stroke:#3a3,color:#333
    class T_SRC,T_STAGE,T_DW,T_REPORT trad
    class M_SRC,M_HUB,M_REPORT mesh
```

| | Traditional ETL | MeshWeaver |
|---|---|---|
| **Data freshness** | Hours to days (batch) | Instant (reactive streams) |
| **Data copies** | 2-3 intermediate tables | Zero copies |
| **Storage cost** | Grows with each BU | BU-local only |
| **Pipeline maintenance** | Transform scripts, scheduling, monitoring | Declarative stream composition |
| **Adding a new BU** | New ETL job, new staging table, testing | Add partition address, done |

---

## The Reactive Pipeline

Here's what actually happens when the group dashboard loads. No manual orchestration — the reactive pipeline (`CombineLatest`) ensures that any change to local data, mapping rules, or exchange rates automatically propagates to the consolidated view.

```mermaid
graph TD
    CSV[BU Data]
    CONTENT[IContentService reads raw bytes]
    PARSE[LoadLocalDataCube parses data]
    ENRICH[CombineLatest enriches with LoB names]

    CSV --> CONTENT --> PARSE --> ENRICH

    ENRICH --> PHDS[PartitionedHubDataSource merges BU streams]

    MAPPINGS[TransactionMapping rules from mesh nodes]
    FX[ExchangeRate plan and actual rates]
    LOBS[LineOfBusiness group standard]

    PHDS --> COMBINE[CombineLatest all reference data]
    MAPPINGS --> COMBINE
    FX --> COMBINE
    LOBS --> COMBINE

    COMBINE --> AGG[AggregateToGroupLevel apply pct splits + FX]
    AGG --> CHARTS[Charts and Tables]

    classDef data fill:#e8f0fe,stroke:#4285f4,color:#333
    classDef process fill:#fff3e0,stroke:#f57c00,color:#333
    classDef ref fill:#f3e8fd,stroke:#9c27b0,color:#333
    classDef output fill:#e6f4ea,stroke:#34a853,color:#333
    class CSV,CONTENT data
    class PARSE,ENRICH,PHDS,COMBINE,AGG process
    class MAPPINGS,FX,LOBS ref
    class CHARTS output
```

---

## Key Design Decisions

**Domain ownership** — each BU manages its own data. EuropeRe's actuary updates their data directly; the group never touches it.

**Stream composition over data copying** — `PartitionedHubDataSource` reads from BU hubs as live `IObservable` streams. When EuropeRe's data changes, the group view updates automatically — no rebuild, no re-import.

**Declarative partitioning** — adding AsiaRe to the group is a single line of configuration:

```
.InitializingPartitions(
    (Address)"FutuRe/EuropeRe/Analysis",
    (Address)"FutuRe/AmericasIns/Analysis",
    (Address)"FutuRe/AsiaRe/Analysis"    // ← new BU
)
```

**No intermediate state** — there are no staging tables, no materialized views, no cache invalidation problems. The only persistent storage is the BU's own data and the mapping rule definitions.

---

## Why This Matters

- **Data mesh** principles (domain ownership, data as a product, self-serve platform, federated governance) are a natural fit for insurance groups where each BU has deep domain expertise
- Traditional ETL approaches create **stale copies** that diverge from the source, require reconciliation, and multiply storage costs
- MeshWeaver's virtual approach means changes to local data **appear instantly** in group views — no waiting for nightly batches
- The same architecture scales from 3 BUs to 30 — each new partition is one address, not a new pipeline
- Auditability is preserved: every group-level number can be traced back through the stream pipeline to a specific data set, mapping rule, and exchange rate

---

## Explore Further

- [Group Profitability Dashboard](@FutuRe/Analysis/AnnualReport) — the consolidated view in action
- [EuropeRe Analysis](@FutuRe/EuropeRe/Analysis) — a local BU hub with its own data
- [AmericasIns Analysis](@FutuRe/AmericasIns/Analysis) — another local data domain
- [Back to FutuRe overview](@FutuRe)
