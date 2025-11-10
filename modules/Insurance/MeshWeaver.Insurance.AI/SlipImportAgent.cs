using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.Insurance.AI;

public class SlipImportAgent(IMessageHub hub) : IInitializableAgent, IAgentWithTools
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private string? pricingSchema;
    private string? acceptanceSchema;
    private string? sectionSchema;
    private string? referenceDataInfo;

    public string Name => nameof(SlipImportAgent);

    public string Description =>
        "Imports insurance slip documents from PDF or Markdown files and structures them into Pricing and ReinsuranceAcceptance data models using LLM-based extraction.";

    public string Instructions
    {
        get
        {
            var baseText =
                $$$"""
                   You are a slip import agent that processes insurance submission slip documents in PDF or Markdown format.
                   Your task is to extract structured data and map it to the insurance domain models using the provided schemas.

                   ## Content Collection Context

                   IMPORTANT: The current context is set to pricing/{pricingId} where pricingId follows the format {company}-{uwy}.
                   - The submission files collection is automatically named "Submissions-{pricingId}"
                   - Files are stored at the root level of this collection
                   - When listing files, you'll see filenames like "Slip.pdf", "Slip.md", etc.
                   - When accessing files with ExtractCompleteText, use just the filename (e.g., "Slip.pdf" or "Slip.md")

                   # Importing Slips

                   When the user asks you to import a slip:
                   1) Get the slip document content by calling ContentPlugin's GetContent function with path=filename, collectionName=null
                      - For PDF/Word files: Omit numberOfRows to get the full content
                      - Show a brief summary of the document to the user so they know what you're working with
                   2) Review the extracted document text and identify data that matches the domain schemas
                   3) Create JSON objects for each entity type following the schemas below
                   4) Proceed with the import immediately - DO NOT ask for user confirmation
                      - Import the data using DataPlugin's UpdateData function:
                      - First, retrieve existing Pricing data using DataPlugin's GetData with type="Pricing" and entityId=pricingId
                      - Merge new pricing fields with existing data and call DataPlugin's UpdateData with type="Pricing"
                      - For each ReinsuranceAcceptance (layer), create JSON and call DataPlugin's UpdateData with type="ReinsuranceAcceptance"
                      - For each ReinsuranceSection (coverage within layer), create JSON and call DataPlugin's UpdateData with type="ReinsuranceSection"
                   5) After the import completes, provide a summary of what data was successfully imported (pricing details, number of layers/sections, any issues encountered)

                   # Data Mapping Guidelines
                   Based on the extracted document text, create JSON objects that match the schemas provided below:
                   - **Pricing**: Basic pricing information including:
                     - Insured name (e.g., "Microsoft Corporation")
                     - Primary insurance company (labeled as "Primary Insurer" or similar in slip header) - populate the PrimaryInsurance field
                     - Broker name (labeled as "Broker" in slip header) - populate the BrokerName field
                     - Dates (inception, expiration), premium, country, legal entity
                   - **ReinsuranceAcceptance**: Represents a reinsurance layer (Layer 1, Layer 2, Layer 3) with financial terms
                   - **ReinsuranceSection**: Represents a coverage type within a layer (Fire Damage, Natural Catastrophe, Business Interruption)

                   # Structure Hierarchy
                   The data structure follows this hierarchy:
                   1. **Pricing** (the main insurance program)
                   2. **ReinsuranceAcceptance** (the layers: Layer 1, Layer 2, Layer 3, etc.)
                   3. **ReinsuranceSection** (the coverage types within each layer: Fire Damage, Natural Catastrophe, Business Interruption, etc.)

                   # Important Rules
                   - Only extract data that is explicitly present in the document text
                   - Use null or default values for missing data points
                   - Ensure all monetary values are properly formatted as numbers
                   - Convert percentages to decimal format (e.g., 25% → 0.25)
                   - **Reference Data Matching**: When you extract Country, LineOfBusiness, or LegalEntity from the document:
                     - Match the extracted text to the reference data provided in the # Reference Data section below
                     - Use the exact Id value from the reference data (e.g., if document says "United States", use the matching Country Id like "US")
                     - If no exact match exists, choose the closest match or leave the field null
                   - Provide clear feedback on what data was successfully extracted
                   - If data is ambiguous or unclear, note it in your response

                   # Creating ReinsuranceAcceptance Records (Layers)
                   - First, create ReinsuranceAcceptance records for each layer (Layer 1, Layer 2, Layer 3)
                   - Use IDs like "Layer1", "Layer2", "Layer3"
                   - Set the Name property to "Layer 1", "Layer 2", "Layer 3"
                   - Include financial terms like share, cession, rate, commission on the acceptance
                   - If there is a "Reinsurance Terms" section in the header with properties like EPI and Brokerage, apply these values to ALL ReinsuranceAcceptance records (all layers get the same EPI and Brokerage)
                   - Convert percentage values to decimals (e.g., 10% → 0.10, 100% → 1.0)

                   # Creating ReinsuranceSection Records (Coverage Types)
                   - Then, create ReinsuranceSection records for each coverage type within each layer
                   - Use IDs like "Layer1-Fire", "Layer1-NatCat", "Layer1-BI", "Layer2-Fire", etc.
                   - Set the AcceptanceId to link the section to its parent layer (e.g., "Layer1")
                   - Set the LineOfBusiness to the coverage type (e.g., "Fire Damage", "Natural Catastrophe", "Business Interruption")
                   - Set the Name to a descriptive name (e.g., "Fire Damage - Layer 1")
                   - Include the attachment point (Attach), limit, aggregate deductible (AggAttach), and aggregate limit (AggLimit)

                   # Example from a Slip
                   If the slip shows:
                   - Fire Damage → Layer 1: Attach 5M, Limit 100M, AAD 25M, AAL 300M
                   - Fire Damage → Layer 2: Attach 100M, Limit 150M, AAL 450M
                   - Natural Catastrophe → Layer 1: Attach 10M, Limit 75M, AAD 30M, AAL 225M

                   Create:
                   1. ReinsuranceAcceptance: Id="Layer1", Name="Layer 1"
                   2. ReinsuranceAcceptance: Id="Layer2", Name="Layer 2"
                   3. ReinsuranceSection: Id="Layer1-Fire", AcceptanceId="Layer1", LineOfBusiness="Fire Damage", Attach=5000000, Limit=100000000, AggAttach=25000000, AggLimit=300000000
                   4. ReinsuranceSection: Id="Layer2-Fire", AcceptanceId="Layer2", LineOfBusiness="Fire Damage", Attach=100000000, Limit=150000000, AggLimit=450000000
                   5. ReinsuranceSection: Id="Layer1-NatCat", AcceptanceId="Layer1", LineOfBusiness="Natural Catastrophe", Attach=10000000, Limit=75000000, AggAttach=30000000, AggLimit=225000000

                   # Document Section Processing
                   Look for common sections in insurance slips:
                   - **Header section**: Insured name, Primary Insurer, Broker, dates
                   - **Insured information**: Name, location, industry
                   - **Coverage details**: Inception/expiration dates, policy terms
                   - **Premium and financial information**: Premium amounts, currency
                   - **Reinsurance terms section**: EPI (Estimated Premium Income), Brokerage percentage, Commission, Taxes
                   - **Layer structures**: Layer 1, Layer 2, Layer 3 with limits, attachments, rates
                   - **Coverage types within layers**: Fire Damage, Natural Catastrophe, Business Interruption, etc.

                   Notes:
                   - Both PDF and Markdown (.md) file content are supported
                   - When updating data, ensure each JSON object has the correct $type field and required ID fields (id, pricingId, acceptanceId, etc.)
                   - Remove null-valued properties from JSON before calling UpdateData
                   - DO NOT ask for user confirmation before importing - proceed directly with the import

                   """;

            if (referenceDataInfo is not null)
                baseText +=
                    $"\n\n# Reference Data\nWhen populating LineOfBusiness, Country, or LegalEntity fields in the Pricing data, you MUST use the Id values from the lists below. Match the extracted text from the document to the appropriate reference data Id.\n\n{referenceDataInfo}";

            if (pricingSchema is not null)
                baseText += $"\n\n# Pricing Schema\n```json\n{pricingSchema}\n```";
            if (acceptanceSchema is not null)
                baseText += $"\n\n# ReinsuranceAcceptance Schema\n```json\n{acceptanceSchema}\n```";
            if (sectionSchema is not null)
                baseText += $"\n\n# ReinsuranceSection Schema\n```json\n{sectionSchema}\n```";

            return baseText;
        }
    }

    IEnumerable<AITool> IAgentWithTools.GetTools(IAgentChat chat)
    {
        var dataPlugin = new DataPlugin(hub, chat, typeDefinitionMap);
        foreach (var tool in dataPlugin.CreateTools())
            yield return tool;

        // Add ContentPlugin for submissions and file reading functionality
        var submissionPluginConfig = CreateSubmissionPluginConfig();
        var contentPlugin = new ContentPlugin(hub, submissionPluginConfig, chat);
        foreach (var tool in contentPlugin.CreateTools())
            yield return tool;
    }

    private static ContentPluginConfig CreateSubmissionPluginConfig()
    {
        return new ContentPluginConfig
        {
            Collections = [],
            ContextToConfigMap = context =>
            {
                // Only handle pricing contexts
                if (context?.Address?.Type != PricingAddress.TypeName)
                    return null!;

                var pricingId = context.Address.Id;

                // Parse pricingId in format {company}-{uwy}
                var parts = pricingId.Split('-');
                if (parts.Length != 2)
                    return null!;

                // Use Hub-based collection config pointing to the pricing address
                // This allows the ContentPlugin to query the pricing hub for the actual collection configuration
                return new ContentCollectionConfig
                {
                    SourceType = HubStreamProviderFactory.SourceType,
                    Name = $"Submissions-{pricingId}",
                    Address = context.Address
                };
            }
        };
    }

    async Task IInitializableAgent.InitializeAsync()
    {
        var pricingAddress = new PricingAddress("default");

        try
        {
            var typesResponse = await hub.AwaitResponse(
                new GetDomainTypesRequest(),
                o => o.WithTarget(pricingAddress));
            typeDefinitionMap = typesResponse?.Message?.Types?.Select(t => t with { Address = null })
                .ToDictionary(x => x.Name!);
        }
        catch
        {
            typeDefinitionMap = null;
        }

        try
        {
            var resp = await hub.AwaitResponse(
                new GetSchemaRequest(nameof(Pricing)),
                o => o.WithTarget(pricingAddress));
            pricingSchema = resp?.Message?.Schema;
        }
        catch
        {
            pricingSchema = null;
        }

        try
        {
            var resp = await hub.AwaitResponse(
                new GetSchemaRequest(nameof(ReinsuranceAcceptance)),
                o => o.WithTarget(pricingAddress));
            acceptanceSchema = resp?.Message?.Schema;
        }
        catch
        {
            acceptanceSchema = null;
        }

        try
        {
            var resp = await hub.AwaitResponse(
                new GetSchemaRequest(nameof(ReinsuranceSection)),
                o => o.WithTarget(pricingAddress));
            sectionSchema = resp?.Message?.Schema;
        }
        catch
        {
            sectionSchema = null;
        }

        // Load reference data
        try
        {
            var refDataText = new System.Text.StringBuilder();

            // Load LineOfBusiness data
            var lobRequest = new GetDataRequest(new CollectionReference(nameof(LineOfBusiness)));
            var lobResponse = await hub.AwaitResponse(lobRequest, o => o.WithTarget(pricingAddress));
            if (lobResponse?.Message?.Data is InstanceCollection lobCollection)
            {
                var lobs = lobCollection.Get<LineOfBusiness>().ToList();
                if (lobs.Any())
                {
                    refDataText.AppendLine("## Available Lines of Business:");
                    foreach (var lob in lobs)
                    {
                        refDataText.AppendLine($"- **{lob.Id}**: {lob.Name ?? lob.Id}");
                        if (!string.IsNullOrEmpty(lob.Description))
                            refDataText.AppendLine($"  {lob.Description}");
                    }

                    refDataText.AppendLine();
                }
            }

            // Load Country data
            var countryRequest = new GetDataRequest(new CollectionReference(nameof(Country)));
            var countryResponse = await hub.AwaitResponse(countryRequest, o => o.WithTarget(pricingAddress));
            if (countryResponse?.Message?.Data is InstanceCollection countryCollection)
            {
                var countries = countryCollection.Get<Country>().ToList();
                if (countries.Any())
                {
                    refDataText.AppendLine("## Available Countries:");
                    foreach (var country in countries)
                    {
                        refDataText.AppendLine($"- **{country.Id}**: {country.Name ?? country.Id}");
                        if (!string.IsNullOrEmpty(country.Region))
                            refDataText.AppendLine($"  Region: {country.Region}");
                    }

                    refDataText.AppendLine();
                }
            }

            // Load LegalEntity data
            var entityRequest = new GetDataRequest(new CollectionReference(nameof(LegalEntity)));
            var entityResponse = await hub.AwaitResponse(entityRequest, o => o.WithTarget(pricingAddress));
            if (entityResponse?.Message?.Data is InstanceCollection entityCollection)
            {
                var entities = entityCollection.Get<LegalEntity>().ToList();
                if (entities.Any())
                {
                    refDataText.AppendLine("## Available Legal Entities:");
                    foreach (var entity in entities)
                    {
                        refDataText.AppendLine($"- **{entity.Id}**: {entity.Name ?? entity.Id}");
                        if (!string.IsNullOrEmpty(entity.EntityType))
                            refDataText.AppendLine($"  Type: {entity.EntityType}");
                        if (!string.IsNullOrEmpty(entity.CountryOfIncorporation))
                            refDataText.AppendLine($"  Country: {entity.CountryOfIncorporation}");
                    }
                }
            }

            referenceDataInfo = refDataText.Length > 0 ? refDataText.ToString() : null;
        }
        catch
        {
            referenceDataInfo = null;
        }
    }

}
