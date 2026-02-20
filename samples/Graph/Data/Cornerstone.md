---
Name: Cornerstone Insurance
Description: Reinsurance company managing property risk pricings
Icon: /static/storage/content/Cornerstone/icon.svg
Category: Case Studies
---

# Cornerstone Insurance

The Cornerstone Insurance use case demonstrates MeshWeaver's capabilities through a realistic insurance scenario: a reinsurance company managing property risk pricings for corporate clients.

## Overview

Cornerstone is organized as a namespace containing:

- **NodeType Definitions**: `Insured.json` and `Pricing.json` define the data models
- **Shared Code**: Reference data types (Country, Currency, LineOfBusiness, LegalEntity)
- **Insured Instances**: Microsoft, GlobalManufacturing, EuropeanLogistics, TechIndustries, Tesla, Nestle
- **Pricing Instances**: Each insured has pricings organized by underwriting year

### Key Concepts

1. **Insured → Pricing Hierarchy**: Clients have multiple pricings organized by underwriting year
2. **Shared NodeTypes**: The Pricing NodeType is reused across all insureds
3. **Property Risk Data**: Geocoded locations with TSI values imported from Excel
4. **Reinsurance Structure**: Multi-layer coverage with sections and financial terms
5. **AI Agent Integration**: Natural language interaction with pricing data via MeshPlugin

### Sample Insureds

| Insured | Industry | Year | Status |
|---------|----------|------|--------|
| Microsoft Corporation | Technology | 2026 | Bound |
| Global Manufacturing Corp | Manufacturing | 2024 | - |
| European Logistics Ltd | Logistics | 2024 | - |
| Tech Industries GmbH | Technology | 2024 | - |
| Tesla, Inc. | Automotive & Energy | - | - |
| Nestle S.A. | Food & Beverage | - | - |

## Documentation

- **[Getting Started](MeshWeaver/Documentation/Cornerstone/GettingStarted)**: Run the sample and explore the pricing interface
- **[Architecture](MeshWeaver/Documentation/Cornerstone/Architecture)**: Message hubs, data model, reactive design
- **[AI Agent Integration](MeshWeaver/Documentation/Cornerstone/AIAgentIntegration)**: How AI agents integrate with MeshWeaver
- **[Unified References](MeshWeaver/Documentation/Cornerstone/UnifiedReferences)**: Path syntax and reference patterns
