---
NodeType: Organization
Name: Cornerstone
Category: Insurance
Description: Reinsurance pricing demo showcasing property risk management, geographic visualization, and Excel data import
Icon: /static/storage/content/Cornerstone/icon.svg
---

Welcome to Cornerstone, a reinsurance pricing demo showcasing MeshWeaver's capabilities for managing complex insurance data. Explore property risk pricings, geographic visualizations, and document-based workflows for corporate insurance clients.

# Quick Start

| Resource | Description |
|----------|-------------|
| [Microsoft](Cornerstone/Microsoft) | Technology sector pricing with full sample data |
| [Tesla](Cornerstone/Tesla) | Industrial and specialty lines |
| [Nestle](Cornerstone/Nestle) | Consumer and agricultural coverage |
| [Getting Started](Cornerstone/Documentation/GettingStarted) | Setup and first steps |
| [Documentation](Cornerstone/Documentation) | Complete guides and references |

---

# What's Inside

## Organization Structure

Cornerstone demonstrates a hierarchical structure for insurance pricing:

```
Cornerstone (Organization)
  └── Insured (NodeType)
        └── Pricing (NodeType)
```

Each insured company contains yearly pricing instances with property risks, reinsurance layers, and submission documents.

---

## Sample Insureds

| Insured | Sector | Status |
|---------|--------|--------|
| [Microsoft](Cornerstone/Microsoft) | Technology | Full sample data (2026) |
| [Tesla](Cornerstone/Tesla) | Industrial | Sample pricing (2026) |
| [Nestle](Cornerstone/Nestle) | Consumer/Agriculture | Sample pricing (2026) |
| [GlobalManufacturing](Cornerstone/GlobalManufacturing) | Manufacturing | Historical (2024) |
| [EuropeanLogistics](Cornerstone/EuropeanLogistics) | Logistics | Historical (2024) |
| [TechIndustries](Cornerstone/TechIndustries) | Technology | Historical (2024) |

---

## Key Features Demonstrated

**Property Risk Management**
Track individual property locations with values, construction types, and protection classes. Microsoft pricing includes 50+ property risks across multiple countries.

**Geographic Visualization**
View property risks on an interactive map with risk aggregation by region. Identify concentration risks and natural catastrophe exposures.

**Excel Import**
Upload broker submissions in Excel format. Configure import mappings to parse property schedules, reinsurance structures, and coverage terms.

**Reinsurance Structure**
Model multi-layer reinsurance programs with sections, participation percentages, and acceptance tracking.

---

# Pricing Views

Each pricing includes multiple analysis perspectives:

| View | Purpose |
|------|---------|
| Overview | Summary metrics and key figures |
| PropertyRisks | Detailed property schedule |
| RiskMap | Geographic distribution of risks |
| Structure | Reinsurance layers and sections |
| Submission | Uploaded documents and files |
| ImportConfigs | Excel import configurations |

---

# Dimensions

Pricings use standardized dimensions for filtering and aggregation:

| Dimension | Purpose |
|-----------|---------|
| LineOfBusiness | Property, Liability, Specialty |
| Country | Geographic classification |
| Currency | USD, EUR, CHF, etc. |
| PricingStatus | Draft, In Review, Quoted, Bound |

---

# Learn More

| Topic | Link |
|-------|------|
| Architecture | [How Cornerstone is built](Cornerstone/Documentation/Architecture) |
| AI Integration | [Using the pricing assistant](Cornerstone/Documentation/AIAgentIntegration) |
| References | [Paths, queries, and areas](Cornerstone/Documentation/UnifiedReferences) |
