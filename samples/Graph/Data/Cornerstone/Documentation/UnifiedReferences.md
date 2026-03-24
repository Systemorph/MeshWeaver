---
NodeType: "Cornerstone/Article"
Title: "Unified References in Cornerstone"
Abstract: "Reference guide for namespace paths, data queries, and views in the Cornerstone sample"
Icon: "Document"
Published: "2025-01-31"
Authors:
  - "MeshWeaver Team"
Tags:
  - "Cornerstone"
  - "References"
---

This document demonstrates unified references in the Cornerstone sample. For the complete Unified Path syntax reference, see [Unified Path](/Doc/DataMesh/UnifiedPath).

It covers namespace hierarchy, data queries, content paths, and views specific to the Cornerstone reinsurance use case.

# Organization Structure

For the full namespace hierarchy, see [Cornerstone Architecture](Cornerstone/Documentation/Architecture).

# Namespace Paths

## Root Level

| Path | Description |
|------|-------------|
| `Cornerstone` | The reinsurance company namespace |

## NodeType Definitions

| Path | Description |
|------|-------------|
| `Cornerstone/Insured` | Insured NodeType (client organizations) |
| `Cornerstone/Pricing` | Pricing NodeType (reinsurance pricings) |

## Insured Instances

| Path | Insured |
|------|---------|
| `Cornerstone/Microsoft` | Microsoft Corporation |
| `Cornerstone/GlobalManufacturing` | Global Manufacturing Corp |
| `Cornerstone/EuropeanLogistics` | European Logistics Ltd |
| `Cornerstone/TechIndustries` | Tech Industries GmbH |
| `Cornerstone/Tesla` | Tesla, Inc. |
| `Cornerstone/Nestle` | Nestle S.A. |

## Pricing Instances

| Path | Pricing |
|------|---------|
| `Cornerstone/Microsoft/2026` | Microsoft 2026 Property Reinsurance |
| `Cornerstone/GlobalManufacturing/2024` | Global Manufacturing 2024 Pricing |
| `Cornerstone/EuropeanLogistics/2024` | European Logistics 2024 Pricing |
| `Cornerstone/TechIndustries/2024` | Tech Industries 2024 Pricing |
| `Cornerstone/Tesla/2026` | Tesla 2026 Property Reinsurance |
| `Cornerstone/Nestle/2026` | Nestle 2026 Property Reinsurance |

# Query Syntax

MeshWeaver uses a GitHub-style query syntax for searching nodes. For complete query syntax reference, see [Query Syntax](/Doc/DataMesh/QuerySyntax).

## Cornerstone Query Examples

**All Pricings**:
```
nodeType:Cornerstone/Pricing
```

**Pricings by Status** (Bound pricings):
```
nodeType:Cornerstone/Pricing status:Bound
```

**Pricings by Line of Business** (Property pricings):
```
nodeType:Cornerstone/Pricing lineOfBusiness:PROP
```

**Pricings for an Insured** (Microsoft pricings):
```
path:Cornerstone/Microsoft nodeType:Cornerstone/Pricing scope:subtree
```

**All Insureds**:
```
nodeType:Cornerstone/Insured
```

# Data References

See [Data Prefix](/Doc/DataMesh/UnifiedPath/DataPrefix) for the generic data reference syntax.

## Display Insured

```
@Cornerstone/Microsoft
```

@Cornerstone/Microsoft

## Display Pricing

```
@Cornerstone/Microsoft/2026
```

@Cornerstone/Microsoft/2026

## Display Property Risks

```
@@Cornerstone/Microsoft/2026/data:PropertyRisk
```

@@Cornerstone/Microsoft/2026/data:PropertyRisk

## Display Reinsurance Acceptances

```
@@Cornerstone/Microsoft/2026/data:ReinsuranceAcceptance
```

@@Cornerstone/Microsoft/2026/data:ReinsuranceAcceptance

## Display Reinsurance Sections

```
@@Cornerstone/Microsoft/2026/data:ReinsuranceSection
```

@@Cornerstone/Microsoft/2026/data:ReinsuranceSection

# Dimension References

Dimensions are shared across all pricings using the same NodeType.

## PricingStatus Dimension

| Status | Description | Order |
|--------|-------------|-------|
| Draft | Initial pricing draft, not yet submitted for quote | 0 |
| Quoted | Quote has been issued to the client | 1 |
| Bound | Policy has been bound and coverage is in effect | 2 |
| Declined | Pricing was declined by client or underwriter | 3 |
| Expired | Quote or policy has expired | 4 |

## LineOfBusiness Dimension

| Code | Name | Description |
|------|------|-------------|
| PROP | Property | Property insurance covering buildings, contents, and business interruption |
| CAS | Casualty | Casualty insurance covering liability and workers compensation |
| MARINE | Marine | Marine and cargo insurance |
| AVIATION | Aviation | Aviation and aerospace insurance |
| ENERGY | Energy | Energy sector insurance including oil & gas |

## Country Dimension

| Code | Name | Region |
|------|------|--------|
| US | United States | North America |
| GB | United Kingdom | Europe |
| DE | Germany | Europe |
| FR | France | Europe |
| JP | Japan | Asia |
| CN | China | Asia |
| AU | Australia | Oceania |
| CA | Canada | North America |
| CH | Switzerland | Europe |
| SG | Singapore | Asia |

## Currency Dimension

| Code | Name | Symbol |
|------|------|--------|
| USD | US Dollar | $ |
| EUR | Euro | € |
| GBP | British Pound | £ |
| JPY | Japanese Yen | ¥ |
| CHF | Swiss Franc | CHF |
| AUD | Australian Dollar | A$ |
| CAD | Canadian Dollar | C$ |

## LegalEntity Dimension

| Code | Name | Country |
|------|------|---------|
| CS-US | Cornerstone US Inc. | United States |
| CS-UK | Cornerstone UK Ltd. | United Kingdom |
| CS-EU | Cornerstone Europe AG | Switzerland |
| CS-ASIA | Cornerstone Asia Pte. Ltd. | Singapore |

# Content References

## Submission Files

Content collections allow file references:

```
@@Cornerstone/Microsoft/2026/content:Submissions/Slip.md
```

@@Cornerstone/Microsoft/2026/content:Submissions/Slip.md

```
@@Cornerstone/Microsoft/2026/content:Submissions/Microsoft.xlsx
```

# View References

Views are defined in `PricingViews.cs` and available for all pricings. See [Area Prefix](/Doc/DataMesh/UnifiedPath/AreaPrefix) for view syntax.

## Overview - Pricing Summary

Shows pricing header with insured details, dates, and financial summary:

```
@Cornerstone/Microsoft/2026/Overview
```

@Cornerstone/Microsoft/2026/Overview

## Property Risks - Risk Data Grid

DataGrid showing all property locations with TSI values:

```
@Cornerstone/Microsoft/2026/PropertyRisks
```

@Cornerstone/Microsoft/2026/PropertyRisks

## Risk Map - Geographic Visualization

Google Maps visualization of geocoded property locations:

```
@Cornerstone/Microsoft/2026/RiskMap
```

@Cornerstone/Microsoft/2026/RiskMap

## Structure - Reinsurance Layers

DataGrids showing reinsurance layers and sections:

```
@Cornerstone/Microsoft/2026/Structure
```

@Cornerstone/Microsoft/2026/Structure

## Submission - File Browser

File browser for uploaded submission documents:

```
@Cornerstone/Microsoft/2026/Submission
```

@Cornerstone/Microsoft/2026/Submission

## Import Configs - Import Settings

Excel import configuration settings:

```
@Cornerstone/Microsoft/2026/ImportConfigs
```

@Cornerstone/Microsoft/2026/ImportConfigs

## PricingCatalog - Insured's Pricings

All pricings for an insured grouped by status:

```
@Cornerstone/Microsoft/PricingCatalog
```

@Cornerstone/Microsoft/PricingCatalog

# Navigation Links

## Link to Insured

```markdown
[Microsoft Corporation](Cornerstone/Microsoft)
[Global Manufacturing](Cornerstone/GlobalManufacturing)
[European Logistics](Cornerstone/EuropeanLogistics)
[Tech Industries](Cornerstone/TechIndustries)
[Tesla](Cornerstone/Tesla)
[Nestle](Cornerstone/Nestle)
```

## Link to Pricing

```markdown
[Microsoft 2026](Cornerstone/Microsoft/2026)
[Global Manufacturing 2024](Cornerstone/GlobalManufacturing/2024)
[Tesla 2026](Cornerstone/Tesla/2026)
[Nestle 2026](Cornerstone/Nestle/2026)
```

## Link to View

```markdown
[Overview](Cornerstone/Microsoft/2026/Overview)
[Property Risks](Cornerstone/Microsoft/2026/PropertyRisks)
[Risk Map](Cornerstone/Microsoft/2026/RiskMap)
[Reinsurance Structure](Cornerstone/Microsoft/2026/Structure)
```

# Sample Insureds

| Insured | Industry | Location |
|---------|----------|----------|
| Microsoft Corporation | Technology | United States |
| Global Manufacturing Corp | Manufacturing | United States |
| European Logistics Ltd | Logistics & Transportation | United Kingdom |
| Tech Industries GmbH | Technology Manufacturing | Germany |
| Tesla, Inc. | Automotive & Energy | United States |
| Nestle S.A. | Food & Beverage | Switzerland |

# Summary

The Cornerstone sample demonstrates MeshWeaver's unified path system for insurance:

- **Namespace paths** define the hierarchical Insured → Pricing structure
- **NodeType references** enable shared definitions across all insureds
- **Query syntax** allows flexible searching by status, line of business, etc.
- **Data references** embed live insurance data (PropertyRisk, Reinsurance)
- **Views** provide consistent displays (Overview, Property Risks, Risk Map, Structure)
- **Content paths** reference submission files and imported documents

All paths follow the pattern: `Cornerstone/[Insured]/[Pricing]/[View]`
