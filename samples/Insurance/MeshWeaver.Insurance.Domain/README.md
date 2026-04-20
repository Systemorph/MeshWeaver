# MeshWeaver.Insurance.Domain

## Overview
MeshWeaver.Insurance.Domain defines the domain model, data configuration, layout areas, and services for a sample insurance pricing application. It demonstrates how to build a full business domain on the MeshWeaver framework with dimension-based data, reactive UI layouts, and geocoding integration.

## Features
- Domain entities: `Pricing`, `PropertyRisk`, `ReinsuranceAcceptance`, `ReinsuranceSection`
- Reference data dimensions: `LineOfBusiness`, `Country`, `LegalEntity`, `Currency`
- Layout areas for pricing catalog, pricing overview, property risks, risk map, submissions, reinsurance structure, and import configuration
- In-memory pricing service with geocoding support (Google Maps)
- Excel import configuration for ingesting submission data
- Content collection support for file-based submission attachments

## Usage
```csharp
hub.ConfigureInsuranceApplication();       // Root hub with catalog + dimensions
hub.ConfigureSinglePricingApplication();   // Per-pricing hub (address: pricing/company/year)
```

## Related Projects
- [MeshWeaver.Insurance.SampleData](../MeshWeaver.Insurance.SampleData/) -- Excel sample data generator
- [MeshWeaver.Layout](../../../src/MeshWeaver.Layout/README.md) -- Layout framework
- [MeshWeaver.Import](../../../src/MeshWeaver.Import/README.md) -- Data import
