---
NodeType: "Demos/Cornerstone/Article"
Title: "Getting Started with Cornerstone"
Abstract: "Run the sample and explore the pricing interface"
Icon: "Document"
Published: "2025-01-31"
Authors:
  - "MeshWeaver Team"
Tags:
  - "Cornerstone"
  - "Getting Started"
---

# Getting Started with Cornerstone

This guide walks you through running the Cornerstone sample and exploring the pricing interface.

## Running the Sample

### Clone and Build

```bash
git clone https://github.com/MeshWeaver/MeshWeaver.git
cd MeshWeaver
dotnet build
```

### Start the Portal

```bash
cd memex/Memex.Portal.Monolith
dotnet run
```

Navigate to `http://localhost:7122` in your browser.

### Navigate to Cornerstone

1. Navigate to **Cornerstone** namespace
2. Explore **Microsoft**, **GlobalManufacturing**, **Tesla**, **Nestle**, or other insureds
3. Select a pricing (e.g., 2026) to view details

## Exploring the Pricing Interface

### Insured Views

Each insured (Microsoft, GlobalManufacturing, etc.) has these views:

| View | Description |
|------|-------------|
| **PricingCatalog** | All pricings grouped by status (Draft, Quoted, Bound, etc.) |

### Pricing Views

Each pricing instance has these views:

| View | Description |
|------|-------------|
| **Overview** | Pricing header with insured details, dates, and financial summary |
| **Property Risks** | DataGrid showing all property locations with TSI values |
| **Risk Map** | Google Maps visualization of geocoded property locations |
| **Structure** | DataGrids showing reinsurance layers and sections |
| **Thumbnail** | Compact card display for catalogs |
| **Submission** | File browser for uploaded submission documents |
| **Import Configs** | Excel import configuration settings |

### Pricing Details View

The Overview displays:
- Insured name and description
- Inception and expiration dates
- Line of business and currency
- Broker and primary insurer information
- Current pricing status with promotion options

### Property Risks DataGrid

The Property Risks view shows:
- Location details (address, city, state, country, ZIP)
- TSI values (Building, Content, Business Interruption)
- Construction details (build year, occupancy type)
- Geocoded coordinates for mapping

### Reinsurance Structure

The Structure view visualizes:
- Multiple coverage layers with attachment points and limits
- Financial terms (EPI, rates, commissions)
- Sections per layer (Fire, Natural Catastrophe, Business Interruption)

## Using the AI Agent

The AI chat agent understands natural language and can help manage pricings.

### Querying Pricings

```
"Show me the Microsoft 2026 pricing"
→ Displays the Overview view

"What property risks are in the Microsoft pricing?"
→ Displays the Property Risks DataGrid

"Show me the reinsurance structure"
→ Displays the Structure view
```

### Creating Pricings

```
"Create a new pricing for Microsoft with inception January 1, 2027"
→ Creates draft pricing with specified dates

"Add a property pricing for Global Manufacturing, 2025 underwriting year"
→ Creates new pricing in GlobalManufacturing namespace
```

### Navigating Views

```
"Show the risk map for Microsoft 2026"
→ Displays geographic visualization of property locations

"Open the submission files"
→ Shows the file browser for uploaded documents
```

### Understanding Insurance Data

```
"What lines of business are available?"
→ Lists: Property, Casualty, Marine, Aviation, Energy

"Show available pricing statuses"
→ Lists: Draft, Quoted, Bound, Declined, Expired

"What countries are supported?"
→ Lists supported country codes with regions
```

## Understanding the Code

### Pricing Data Model (`Pricing.cs`)

```csharp
public record Pricing : IContentInitializable
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string InsuredName { get; init; } = string.Empty;

    [Markdown]
    public string? Description { get; init; }

    public DateTime InceptionDate { get; init; }
    public DateTime ExpirationDate { get; init; }
    public int UnderwritingYear { get; init; }

    [Dimension<LineOfBusiness>]
    public string LineOfBusiness { get; init; } = "PROP";

    [Dimension<Country>]
    public string Country { get; init; } = "US";

    [Dimension<Currency>]
    public string Currency { get; init; } = "USD";

    [Dimension<PricingStatus>]
    public string Status { get; init; } = "Draft";

    public string? BrokerName { get; init; }
    public string? PrimaryInsurance { get; init; }
}
```

Key features:
- `[Key]` marks the primary identifier
- `[Dimension<T>]` links to dimension types for filtering
- `[Markdown]` enables markdown rendering for descriptions

### NodeType Configuration (`Pricing.json`)

```json
{
  "id": "Pricing",
  "namespace": "Cornerstone",
  "name": "Pricing",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "configuration": "config => config
        .WithContentType<Pricing>()
        .AddContentCollection(...)
        .AddData(data => data.AddSource(...))
        .AddDefaultLayoutAreas()
        .AddLayout(layout => layout.WithDefaultArea(\"Overview\").AddPricingViews())"
  }
}
```

### Pricing Instance (`Microsoft/2026.json`)

```json
{
  "id": "2026",
  "namespace": "Cornerstone/Microsoft",
  "name": "Microsoft 2026",
  "nodeType": "Demos/Cornerstone/Pricing",
  "content": {
    "$type": "Pricing",
    "id": "2026",
    "insuredName": "Microsoft Corporation",
    "inceptionDate": "2026-01-01T00:00:00Z",
    "expirationDate": "2026-12-31T00:00:00Z",
    "lineOfBusiness": "PROP",
    "country": "US",
    "currency": "USD",
    "status": "Bound"
  }
}
```

## Working with Submissions

### Uploading Files

Pricings support file uploads for submission documents:
1. Navigate to a pricing (e.g., Cornerstone/Microsoft/2026)
2. Select the **Submission** view
3. Upload files such as slip documents, Excel data, or PDFs

### Excel Import

Property risk data can be imported from Excel:
1. Upload an Excel file to Submissions
2. Configure import settings in **Import Configs**
3. Map columns to PropertyRisk fields
4. Execute import to populate the Property Risks DataGrid

## Next Steps

1. **Explore the Data**: Navigate through Cornerstone insureds and examine pricing data
2. **Use the Agent**: Try natural language commands in the chat interface
3. **View Property Risks**: Explore the Property Risks and Risk Map views
4. **Examine the Structure**: Review reinsurance layers in the Structure view
5. **Upload Submissions**: Try the file upload workflow
