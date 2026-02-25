---
NodeType: "Demos/Cornerstone/Article"
Title: "Cornerstone Case Studies"
Abstract: "Learn MeshWeaver through practical examples with the Cornerstone reinsurance pricing sample"
Icon: "Document"
Published: "2025-01-31"
Authors:
  - "MeshWeaver Team"
Tags:
  - "Cornerstone"
  - "Getting Started"
---

# Cornerstone Case Studies

The Cornerstone sample organization demonstrates MeshWeaver capabilities through a realistic reinsurance pricing scenario, including property risk management, geographic visualization, and document-based workflows.

---

## What do you want to learn?

| Topic | Go here |
|-------|---------|
| Get up and running | [Getting Started](Cornerstone/Documentation/GettingStarted) - Setup, navigation, first steps |
| Understand the architecture | [Architecture](Cornerstone/Documentation/Architecture) - Data model, NodeTypes, pricing pipeline |
| Add AI to your app | [AI Agent Integration](Cornerstone/Documentation/AIAgentIntegration) - Pricing assistant, risk queries |
| Reference paths and queries | [Unified References](Cornerstone/Documentation/UnifiedReferences) - Paths, queries, layout areas |

---

## The Cornerstone Organization

Cornerstone is a reinsurance company with corporate clients organized by underwriting year:

```
Cornerstone/                          # Organization
├── Insured/                          # NodeType definition
│   └── Code/
│       ├── Insured.cs                # Insured data model
│       └── CornerstoneViews.cs       # Insured-level views
├── Pricing/                          # NodeType definition
│   └── Code/
│       ├── Pricing.cs                # Pricing data model
│       ├── PricingViews.cs           # 7 views (Overview, PropertyRisks, ...)
│       ├── PropertyRisk.cs           # Property risk entity
│       ├── ReinsuranceAcceptance.cs  # Layer definitions
│       ├── ReinsuranceSection.cs     # Section details
│       ├── MicrosoftDataLoader.cs    # Sample data loading
│       └── SlipParser.cs             # Document parsing
├── Microsoft/                        # Sample insured
│   └── 2026/                         # Pricing year
│       ├── PropertyRisks.json        # 50+ property locations
│       └── Submissions/              # Uploaded documents
├── Tesla/                            # Sample insured
│   └── 2026/                         # Pricing year
└── Nestle/                           # Sample insured
    └── 2026/                         # Pricing year
```

---

## Key Concepts Demonstrated

### Hierarchical NodeTypes

The `Insured` → `Pricing` hierarchy models the natural structure of reinsurance:
- **Insured**: Corporate clients with multiple underwriting years
- **Pricing**: Year-specific pricing instances with risks, structure, and documents

Each level inherits configuration while allowing specialization:
```
Insured (NodeType)
  └── Pricing (NodeType)
        ├── PropertyRisk (data)
        ├── ReinsuranceAcceptance (data)
        └── ReinsuranceSection (data)
```

### Property Risk Data Model

The `PropertyRisk` entity captures insurance-specific property details:

| Field | Description |
|-------|-------------|
| LocationName | Site or facility name |
| Address, City, State, Country | Location details |
| TsiBuilding | Total Sum Insured for buildings |
| TsiContent | Total Sum Insured for contents |
| TsiBi | Business Interruption coverage |
| GeocodedLocation | Resolved coordinates for mapping |
| OccupancyCode, ConstructionCode | Classification codes |
| BuildYear, NumberOfStories, Sprinklers | Physical attributes |

### Reinsurance Structure

Multi-layer programs are modeled with:
- **ReinsuranceAcceptance**: Layer definitions with EPI, rate, brokerage
- **ReinsuranceSection**: Sections within layers with attachment/limit points

### Content Collections

File uploads are managed via content collections:
- Excel submissions for property schedules
- Email chains for broker correspondence
- Slip documents for coverage terms

---

## Views Architecture

Each pricing includes 7 views for different perspectives:

| View | Purpose | Key Features |
|------|---------|--------------|
| Overview | Summary dashboard | Status badge, coverage period, classification |
| PropertyRisks | Property schedule | DataGrid with TSI columns, source tracking |
| RiskMap | Geographic visualization | Google Maps integration, marker clustering |
| Structure | Reinsurance layers | Acceptance and section DataGrids |
| Submission | File management | FileBrowser with upload capability |
| ImportConfigs | Import mappings | Excel column configurations |
| Thumbnail | Catalog card | Styled card for catalog display |

### View Implementation Pattern

Views follow a reactive pattern using `IObservable`:

```csharp
public static IObservable<UiControl?> PropertyRisks(LayoutAreaHost host, RenderingContext _)
{
    return host.Workspace.GetStream<PropertyRisk>()
        .Select(risks => BuildDataGrid(risks));
}
```

---

## Sample Data

### Microsoft Pricing (Full Sample)

The Microsoft 2026 pricing includes complete sample data:

| Entity | Count | Description |
|--------|-------|-------------|
| PropertyRisk | 50+ | Locations across US, Europe, Asia |
| ReinsuranceAcceptance | 4 | Multi-layer program |
| ReinsuranceSection | 8 | Sections per layer |
| Submission Files | 4 | Excel schedules, email chains |

### Other Insureds

| Insured | Year | Status |
|---------|------|--------|
| Tesla | 2026 | Sample pricing structure |
| Nestle | 2026 | Sample pricing structure |
| GlobalManufacturing | 2024 | Historical reference |
| EuropeanLogistics | 2024 | Historical reference |
| TechIndustries | 2024 | Historical reference |

---

## Dimension System

Pricings use standardized dimensions for filtering:

| Dimension | Values |
|-----------|--------|
| LineOfBusiness | Property, Liability, Specialty, Marine, Aviation |
| Country | US, DE, UK, CH, FR, JP, CN, ... |
| Currency | USD, EUR, CHF, GBP, JPY, CNY |
| PricingStatus | Draft, InReview, Quoted, Bound, Declined, Expired |
| LegalEntity | Primary insurers and reinsurers |

Dimensions enable:
- Consistent filtering across views
- Aggregation in analytics
- Validation in data entry

---

## Explore Further

Navigate to `Cornerstone` in the portal to explore:
- The Microsoft pricing with full sample data
- Property risk visualization on the RiskMap
- Reinsurance structure in the Structure view
- File upload and import workflows
- The AI chat agent for pricing queries
