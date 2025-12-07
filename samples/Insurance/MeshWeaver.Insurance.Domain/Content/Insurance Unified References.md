---
Title: "Insurance Unified References"
Abstract: >
  This document demonstrates all addressable items in the Insurance application using MeshWeaver's
  unified content reference notation. It covers reference data, pricing entities, and layout areas
  specific to the Insurance domain.
Thumbnail: "images/UnifiedReferences.svg"
Published: "2025-12-06"
Authors:
  - "Roland Bürgi"
Tags:
  - "Insurance"
  - "Reference Data"
  - "Pricing"
---

This document showcases all addressable items in the Insurance application.

## Reference Data

### Country Collection

Display all countries:

```
@data:app/Insurance/Country
```

@data:app/Insurance/Country

### Single Country

Display a specific country:

```
@data:app/Insurance/Country/US
```

@data:app/Insurance/Country/US

### Currency Collection

Display all currencies:

```
@data:app/Insurance/Currency
```

@data:app/Insurance/Currency

### Single Currency

Display a specific currency:

```
@data:app/Insurance/Currency/USD
```

@data:app/Insurance/Currency/USD

### Line of Business Collection

Display all lines of business:

```
@data:app/Insurance/LineOfBusiness
```

@data:app/Insurance/LineOfBusiness

### Single Line of Business

Display a specific line of business:

```
@data:app/Insurance/LineOfBusiness/PROP
```

@data:app/Insurance/LineOfBusiness/PROP

### Legal Entity Collection

Display all legal entities:

```
@data:app/Insurance/LegalEntity
```

@data:app/Insurance/LegalEntity

### Single Legal Entity

Display a specific legal entity:

```
@data:app/Insurance/LegalEntity/MW-US
```

@data:app/Insurance/LegalEntity/MW-US

## Pricing Data

### Pricing Catalog

Display all pricings in the catalog:

```
@data:app/Insurance/Pricing
```

@data:app/Insurance/Pricing

## Layout Area References

### Pricing Catalog View

The pricing catalog showing all available pricings:

```
@app/Insurance/Pricings
```

@app/Insurance/Pricings

## Individual Pricing Views

### Pricing Overview

The overview for a specific pricing (e.g., Microsoft-2026):

```
@pricing/Microsoft-2026/Overview
```

@pricing/Microsoft-2026/Overview

### Property Risks

Property risks for a pricing:

```
@pricing/Microsoft-2026/PropertyRisks
```

@pricing/Microsoft-2026/PropertyRisks

### Risk Map

Geographic visualization of property risks:

```
@pricing/Microsoft-2026/RiskMap
```

@pricing/Microsoft-2026/RiskMap

### Reinsurance Structure

Reinsurance acceptance structure:

```
@pricing/Microsoft-2026/Structure
```

@pricing/Microsoft-2026/Structure

### Submission Files

File browser for submission documents:

```
@pricing/Microsoft-2026/Submission
```

@pricing/Microsoft-2026/Submission

### Import Configurations

Excel import configurations:

```
@pricing/Microsoft-2026/ImportConfigs
```

@pricing/Microsoft-2026/ImportConfigs
