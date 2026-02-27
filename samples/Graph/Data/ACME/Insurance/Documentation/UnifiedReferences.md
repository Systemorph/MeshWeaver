---
NodeType: "ACME/Insurance/Article"
Title: "Unified References in ACME Insurance"
Abstract: "Reference guide for namespace paths, data queries, and views in the ACME Insurance sample"
Icon: "Document"
Published: "2025-01-31"
Authors:
  - "MeshWeaver Team"
Tags:
  - "ACME Insurance"
  - "References"
---

This document demonstrates unified references in the ACME Insurance sample. For the complete Unified Path syntax reference, see [Unified Path](MeshWeaver/Documentation/DataMesh/UnifiedPath).

It covers namespace hierarchy, data queries, content paths, and views specific to the ACME Insurance reinsurance use case.

# Organization Structure

For the full namespace hierarchy, see [ACME Insurance Architecture](ACME/Insurance/Documentation/Architecture).

# Namespace Paths

## Root Level

| Path | Description |
|------|-------------|
| `ACME/Insurance` | The reinsurance company namespace |

## NodeType Definitions

| Path | Description |
|------|-------------|
| `ACME/Insurance/Insured` | Insured NodeType (client organizations) |
| `ACME/Insurance/Pricing` | Pricing NodeType (reinsurance pricings) |

## Insured Instances

| Path | Insured |
|------|---------|
| `ACME/Insurance/Microsoft` | Microsoft Corporation |
| `ACME/Insurance/GlobalManufacturing` | Global Manufacturing Corp |
| `ACME/Insurance/EuropeanLogistics` | European Logistics Ltd |
| `ACME/Insurance/TechIndustries` | Tech Industries GmbH |
| `ACME/Insurance/Tesla` | Tesla, Inc. |
| `ACME/Insurance/Nestle` | Nestle S.A. |

## Pricing Instances

| Path | Pricing |
|------|---------|
| `ACME/Insurance/Microsoft/2026` | Microsoft 2026 Property Reinsurance |
| `ACME/Insurance/GlobalManufacturing/2024` | Global Manufacturing 2024 Pricing |
| `ACME/Insurance/EuropeanLogistics/2024` | European Logistics 2024 Pricing |
| `ACME/Insurance/TechIndustries/2024` | Tech Industries 2024 Pricing |
| `ACME/Insurance/Tesla/2026` | Tesla 2026 Property Reinsurance |
| `ACME/Insurance/Nestle/2026` | Nestle 2026 Property Reinsurance |

# Query Syntax

MeshWeaver uses a GitHub-style query syntax for searching nodes. For complete query syntax reference, see [Query Syntax](MeshWeaver/Documentation/DataMesh/QuerySyntax).

## ACME Insurance Query Examples

**All Pricings**:
```
nodeType:ACME/Insurance/Pricing
```

**Pricings by Status** (Bound pricings):
```
nodeType:ACME/Insurance/Pricing status:Bound
```

**Pricings by Line of Business** (Property pricings):
```
nodeType:ACME/Insurance/Pricing lineOfBusiness:PROP
```

**Pricings for an Insured** (Microsoft pricings):
```
path:ACME/Insurance/Microsoft nodeType:ACME/Insurance/Pricing scope:subtree
```

**All Insureds**:
```
nodeType:ACME/Insurance/Insured
```

# Data References

See [Data Prefix](MeshWeaver/Documentation/DataMesh/UnifiedPath/DataPrefix) for the generic data reference syntax.

## Display Insured

```
@ACME/Insurance/Microsoft
```

@ACME/Insurance/Microsoft

## Display Pricing

```
@ACME/Insurance/Microsoft/2026
```

@ACME/Insurance/Microsoft/2026

## Display Property Risks

```
@@ACME/Insurance/Microsoft/2026/data:PropertyRisk
```

@@ACME/Insurance/Microsoft/2026/data:PropertyRisk

## Display Reinsurance Acceptances

```
@@ACME/Insurance/Microsoft/2026/data:ReinsuranceAcceptance
```

@@ACME/Insurance/Microsoft/2026/data:ReinsuranceAcceptance

## Display Reinsurance Sections

```
@@ACME/Insurance/Microsoft/2026/data:ReinsuranceSection
```

@@ACME/Insurance/Microsoft/2026/data:ReinsuranceSection

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
| CS-US | ACME Insurance US Inc. | United States |
| CS-UK | ACME Insurance UK Ltd. | United Kingdom |
| CS-EU | ACME Insurance Europe AG | Switzerland |
| CS-ASIA | ACME Insurance Asia Pte. Ltd. | Singapore |

# Content References

## Submission Files

Content collections allow file references:

```
@@ACME/Insurance/Microsoft/2026/content:Submissions/Slip.md
```

@@ACME/Insurance/Microsoft/2026/content:Submissions/Slip.md

```
@@ACME/Insurance/Microsoft/2026/content:Submissions/Microsoft.xlsx
```

# View References

Views are defined in `PricingViews.cs` and available for all pricings. See [Area Prefix](MeshWeaver/Documentation/DataMesh/UnifiedPath/AreaPrefix) for view syntax.

## Overview - Pricing Summary

Shows pricing header with insured details, dates, and financial summary:

```
@ACME/Insurance/Microsoft/2026/Overview
```

@ACME/Insurance/Microsoft/2026/Overview

## Property Risks - Risk Data Grid

DataGrid showing all property locations with TSI values:

```
@ACME/Insurance/Microsoft/2026/PropertyRisks
```

@ACME/Insurance/Microsoft/2026/PropertyRisks

## Risk Map - Geographic Visualization

Google Maps visualization of geocoded property locations:

```
@ACME/Insurance/Microsoft/2026/RiskMap
```

@ACME/Insurance/Microsoft/2026/RiskMap

## Structure - Reinsurance Layers

DataGrids showing reinsurance layers and sections:

```
@ACME/Insurance/Microsoft/2026/Structure
```

@ACME/Insurance/Microsoft/2026/Structure

## Submission - File Browser

File browser for uploaded submission documents:

```
@ACME/Insurance/Microsoft/2026/Submission
```

@ACME/Insurance/Microsoft/2026/Submission

## Import Configs - Import Settings

Excel import configuration settings:

```
@ACME/Insurance/Microsoft/2026/ImportConfigs
```

@ACME/Insurance/Microsoft/2026/ImportConfigs

## PricingCatalog - Insured's Pricings

All pricings for an insured grouped by status:

```
@ACME/Insurance/Microsoft/PricingCatalog
```

@ACME/Insurance/Microsoft/PricingCatalog

# Navigation Links

## Link to Insured

```markdown
[Microsoft Corporation](ACME/Insurance/Microsoft)
[Global Manufacturing](ACME/Insurance/GlobalManufacturing)
[European Logistics](ACME/Insurance/EuropeanLogistics)
[Tech Industries](ACME/Insurance/TechIndustries)
[Tesla](ACME/Insurance/Tesla)
[Nestle](ACME/Insurance/Nestle)
```

## Link to Pricing

```markdown
[Microsoft 2026](ACME/Insurance/Microsoft/2026)
[Global Manufacturing 2024](ACME/Insurance/GlobalManufacturing/2024)
[Tesla 2026](ACME/Insurance/Tesla/2026)
[Nestle 2026](ACME/Insurance/Nestle/2026)
```

## Link to View

```markdown
[Overview](ACME/Insurance/Microsoft/2026/Overview)
[Property Risks](ACME/Insurance/Microsoft/2026/PropertyRisks)
[Risk Map](ACME/Insurance/Microsoft/2026/RiskMap)
[Reinsurance Structure](ACME/Insurance/Microsoft/2026/Structure)
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

The ACME Insurance sample demonstrates MeshWeaver's unified path system for insurance:

- **Namespace paths** define the hierarchical Insured → Pricing structure
- **NodeType references** enable shared definitions across all insureds
- **Query syntax** allows flexible searching by status, line of business, etc.
- **Data references** embed live insurance data (PropertyRisk, Reinsurance)
- **Views** provide consistent displays (Overview, Property Risks, Risk Map, Structure)
- **Content paths** reference submission files and imported documents

All paths follow the pattern: `ACME/Insurance/[Insured]/[Pricing]/[View]`
