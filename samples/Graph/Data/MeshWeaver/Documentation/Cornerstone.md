---
Name: Cornerstone Case Studies
Category: Documentation
Description: Learn MeshWeaver through the Cornerstone Insurance reinsurance pricing sample
Icon: /static/storage/content/MeshWeaver/Documentation/ACME/icon.svg
---

# Cornerstone Case Studies

The Cornerstone Insurance sample demonstrates MeshWeaver capabilities through a realistic reinsurance pricing scenario.

---

## What do you want to learn?

| Topic | Go here |
|-------|---------|
| Get up and running | [Getting Started](MeshWeaver/Documentation/Cornerstone/GettingStarted) - Setup, navigation, first steps |
| Understand the architecture | [Architecture](MeshWeaver/Documentation/Cornerstone/Architecture) - MeshNodes, namespaces, data model |
| Add AI to your app | [AI Agent Integration](MeshWeaver/Documentation/Cornerstone/AIAgentIntegration) - PricingAgent, NLP |
| Reference paths and queries | [Unified References](MeshWeaver/Documentation/Cornerstone/UnifiedReferences) - Paths, queries, layout areas |

---

## The Cornerstone Organization

Cornerstone is a reinsurance company managing property risk pricings for corporate clients:

```
Cornerstone/                           # Reinsurance company
├── Insured.json                       # Insured NodeType
├── Pricing.json                       # Pricing NodeType
├── Pricing/PricingAgent.md            # AI agent
├── Microsoft/                         # Insured: Microsoft Corporation
│   └── 2026/                          # Pricing instance
│       └── Submissions/               # Uploaded documents
├── GlobalManufacturing/               # Insured: Global Manufacturing Corp
├── EuropeanLogistics/                 # Insured: European Logistics Ltd
└── TechIndustries/                    # Insured: Tech Industries GmbH
```

---

## Key Concepts Demonstrated

### Namespace Hierarchy

Data is organized in a hierarchical namespace:
- **Reinsurer** → **Insured** → **Pricing**
- Each level has its own context and permissions
- Shared NodeTypes enable code reuse across all insureds

### NodeType Reuse

The Pricing NodeType (`Cornerstone/Pricing`) is defined once and used by all insureds:
- Same data model, views, and behavior
- Insured-specific pricings with relevant dates and terms
- Shared AI agent for pricing management

### AI Agent Integration

The PricingAgent demonstrates:
- Natural language pricing queries and creation
- Insurance domain awareness (TSI, coverage, layers)
- Layout area integration for visual responses

---

## Sample Data

### Sample Insureds

| Insured | Industry | Location |
|---------|----------|----------|
| Microsoft Corporation | Technology | United States |
| Global Manufacturing Corp | Manufacturing | United States |
| European Logistics Ltd | Logistics & Transportation | United Kingdom |
| Tech Industries GmbH | Technology Manufacturing | Germany |

### Microsoft 2026 Pricing

| Field | Value |
|-------|-------|
| Line of Business | Property (PROP) |
| Status | Bound |
| Inception | 2026-01-01 |
| Expiration | 2026-12-31 |
| Broker | Orion Risk Partners Ltd. |
| Primary Insurer | Sentinel Global Insurance SE |

---

## Explore Further

Navigate to `Cornerstone` in the portal to explore:
- Insured organizations and their pricings
- Different views: Overview, PropertyRisks, RiskMap, Structure
- The AI chat agent for natural language interactions
- File uploads and Excel import workflows
