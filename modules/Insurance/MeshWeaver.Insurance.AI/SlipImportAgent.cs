using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace MeshWeaver.Insurance.AI;

public class SlipImportAgent(IMessageHub hub) : IInitializableAgent, IAgentWithPlugins, IAgentWithContext
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private string? pricingSchema;
    private string? acceptanceSchema;
    private string? sectionSchema;

    public string Name => nameof(SlipImportAgent);

    public string Description => "Imports insurance slip documents from PDF or Markdown files and structures them into Pricing and ReinsuranceAcceptance data models using LLM-based extraction.";

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
                1) First, use {{{nameof(ContentCollectionPlugin.ListFiles)}}}() to see available files in the submissions collection
                2) Use {{{nameof(SlipImportPlugin.ExtractCompleteText)}}} to extract the document content from PDF or Markdown files
                   - Simply pass the filename (e.g., "Slip.pdf" or "Slip.md")
                   - The collection name will be automatically resolved to "Submissions-{pricingId}"
                3) Review the extracted text and identify data that matches the domain schemas
                4) Use {{{nameof(SlipImportPlugin.ImportSlipData)}}} to save the structured data as JSON
                5) Provide feedback on what data was successfully imported or if any issues were encountered

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
                - When listing files, you may see paths with "/" prefix (e.g., "/Slip.pdf", "/Slip.md")
                - When calling ExtractCompleteText, provide only the filename (e.g., "Slip.pdf" or "Slip.md")
                - The collection name is automatically determined from the pricing context
                - Both PDF and Markdown (.md) files are supported
                """;

            if (pricingSchema is not null)
                baseText += $"\n\n# Pricing Schema\n```json\n{pricingSchema}\n```";
            if (acceptanceSchema is not null)
                baseText += $"\n\n# ReinsuranceAcceptance Schema\n```json\n{acceptanceSchema}\n```";
            if (sectionSchema is not null)
                baseText += $"\n\n# ReinsuranceSection Schema\n```json\n{sectionSchema}\n```";

            return baseText;
        }
    }

    IEnumerable<KernelPlugin> IAgentWithPlugins.GetPlugins(IAgentChat chat)
    {
        yield return new DataPlugin(hub, chat, typeDefinitionMap).CreateKernelPlugin();

        // Add ContentCollectionPlugin for submissions
        var submissionPluginConfig = CreateSubmissionPluginConfig(chat);
        yield return new ContentCollectionPlugin(hub, submissionPluginConfig, chat).CreateKernelPlugin();

        // Add slip import specific plugin
        var plugin = new SlipImportPlugin(hub, chat);
        yield return KernelPluginFactory.CreateFromObject(plugin);
    }

    private static ContentCollectionPluginConfig CreateSubmissionPluginConfig(IAgentChat chat)
    {
        return new ContentCollectionPluginConfig
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

                var company = parts[0];
                var uwy = parts[1];
                var subPath = $"{company}/{uwy}";

                // Create Hub-based collection config pointing to the pricing address
                return new ContentCollectionConfig
                {
                    SourceType = HubStreamProviderFactory.SourceType,
                    Name = $"Submissions-{pricingId}",
                    Address = context.Address,
                    BasePath = subPath
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
            typeDefinitionMap = typesResponse?.Message?.Types?.Select(t => t with { Address = null }).ToDictionary(x => x.Name!);
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
    }

    public bool Matches(AgentContext? context)
    {
        return context?.Address?.Type == PricingAddress.TypeName;
    }
}

public class SlipImportPlugin(IMessageHub hub, IAgentChat chat)
{
    private JsonSerializerOptions GetJsonOptions()
    {
        return hub.JsonSerializerOptions;
    }

    [KernelFunction]
    [Description("Extracts the complete text from a slip document (PDF or Markdown) and returns it for LLM processing")]
    public async Task<string> ExtractCompleteText(
        [Description("The filename to extract (e.g., 'Slip.pdf' or 'Slip.md')")] string filename,
        [Description("The collection name (optional, defaults to context-based resolution)")] string? collectionName = null)
    {
        if (chat.Context?.Address?.Type != PricingAddress.TypeName)
            return "Please navigate to the pricing first.";

        try
        {
            var pricingId = chat.Context.Address.Id;
            var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

            // Get collection name using the same pattern as ContentCollectionPlugin
            var resolvedCollectionName = collectionName ?? $"Submissions-{pricingId}";

            // Use ContentService directly with the correct collection name and simple path
            var stream = await contentService.GetContentAsync(resolvedCollectionName, filename, CancellationToken.None);

            if (stream is null)
                return $"Content not found: {filename} in collection {resolvedCollectionName}";

            await using (stream)
            {
                string completeText;

                // Determine file type by extension
                if (filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    // Read markdown file directly as text
                    using var reader = new StreamReader(stream);
                    completeText = await reader.ReadToEndAsync();
                }
                else if (filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract text from PDF
                    completeText = await ExtractCompletePdfText(stream);
                }
                else
                {
                    // Try to read as text for unknown file types
                    using var reader = new StreamReader(stream);
                    completeText = await reader.ReadToEndAsync();
                }

                var sb = new StringBuilder();
                sb.AppendLine("=== INSURANCE SLIP DOCUMENT TEXT ===");
                sb.AppendLine();
                sb.AppendLine(completeText);

                return sb.ToString();
            }
        }
        catch (Exception e)
        {
            return $"Error extracting document text: {e.Message}";
        }
    }

    [KernelFunction]
    [Description("Imports the structured slip data as JSON into the pricing")]
    public async Task<string> ImportSlipData(
        [Description("Pricing data as JSON (optional if updating existing)")] string? pricingJson,
        [Description("Array of ReinsuranceAcceptance data as JSON (can contain multiple acceptances)")] string? acceptancesJson,
        [Description("Array of ReinsuranceSection data as JSON (sections/layers within acceptances)")] string? sectionsJson)
    {
        if (chat.Context?.Address?.Type != PricingAddress.TypeName)
            return "Please navigate to the pricing first.";

        var pricingId = chat.Context.Address.Id;
        var pricingAddress = new PricingAddress(pricingId);

        try
        {
            // Step 1: Retrieve existing Pricing data
            var existingPricing = await GetExistingPricingAsync(pricingAddress, pricingId);

            var updates = new List<JsonObject>();

            // Step 2: Update Pricing if provided
            if (!string.IsNullOrWhiteSpace(pricingJson))
            {
                var newPricingData = JsonNode.Parse(ExtractJson(pricingJson));
                if (newPricingData is JsonObject newPricingObj)
                {
                    var mergedPricing = MergeWithExistingPricing(existingPricing, newPricingObj, pricingId);
                    RemoveNullProperties(mergedPricing);
                    updates.Add(mergedPricing);
                }
            }

            // Step 3: Process ReinsuranceAcceptance records (can be multiple)
            if (!string.IsNullOrWhiteSpace(acceptancesJson))
            {
                var acceptancesData = JsonNode.Parse(ExtractJson(acceptancesJson));

                // Handle both array and single object
                var acceptanceArray = acceptancesData is JsonArray arr ? arr : new JsonArray { acceptancesData };

                foreach (var acceptanceData in acceptanceArray)
                {
                    if (acceptanceData is JsonObject acceptanceObj)
                    {
                        var processedAcceptance = EnsureTypeFirst(acceptanceObj, nameof(ReinsuranceAcceptance));
                        processedAcceptance["pricingId"] = pricingId;
                        RemoveNullProperties(processedAcceptance);
                        updates.Add(processedAcceptance);
                    }
                }
            }

            // Step 4: Process ReinsuranceSection records (can be multiple)
            if (!string.IsNullOrWhiteSpace(sectionsJson))
            {
                var sectionsData = JsonNode.Parse(ExtractJson(sectionsJson));

                // Handle both array and single object
                var sectionArray = sectionsData is JsonArray arr ? arr : new JsonArray { sectionsData };

                foreach (var sectionData in sectionArray)
                {
                    if (sectionData is JsonObject sectionObj)
                    {
                        var processedSection = EnsureTypeFirst(sectionObj, nameof(ReinsuranceSection));
                        RemoveNullProperties(processedSection);
                        updates.Add(processedSection);
                    }
                }
            }

            if (updates.Count == 0)
                return "No valid data provided for import.";

            // Step 5: Post DataChangeRequest
            var updateRequest = new DataChangeRequest { Updates = updates };
            var response = await hub.AwaitResponse(updateRequest, o => o.WithTarget(pricingAddress));

            return response.Message.Status switch
            {
                DataChangeStatus.Committed => $"Slip data imported successfully. Updated {updates.Count} entities.",
                _ => $"Data update failed:\n{string.Join('\n', response.Message.Log.Messages?.Select(l => l.LogLevel + ": " + l.Message) ?? Array.Empty<string>())}"
            };
        }
        catch (Exception e)
        {
            return $"Import failed: {e.Message}";
        }
    }

    private async Task<Pricing?> GetExistingPricingAsync(Address pricingAddress, string pricingId)
    {
        try
        {
            var response = await hub.AwaitResponse(
                new GetDataRequest(new EntityReference(nameof(Pricing), pricingId)),
                o => o.WithTarget(pricingAddress));

            return response?.Message?.Data as Pricing;
        }
        catch
        {
            return null;
        }
    }

    private JsonObject MergeWithExistingPricing(Pricing? existing, JsonObject newData, string pricingId)
    {
        JsonObject baseData;
        if (existing != null)
        {
            var existingJson = JsonSerializer.Serialize(existing, GetJsonOptions());
            baseData = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
        }
        else
        {
            baseData = new JsonObject();
        }

        var merged = MergeJsonObjects(baseData, newData);
        merged = EnsureTypeFirst(merged, nameof(Pricing));
        merged["id"] = pricingId;
        return merged;
    }

    private static JsonObject MergeJsonObjects(JsonObject? existing, JsonObject? newData)
    {
        if (existing == null)
            return newData?.DeepClone() as JsonObject ?? new JsonObject();

        if (newData == null)
            return existing.DeepClone() as JsonObject ?? new JsonObject();

        var merged = existing.DeepClone() as JsonObject ?? new JsonObject();

        foreach (var kvp in newData)
        {
            var isNullValue = kvp.Value == null ||
                             (kvp.Value?.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                              kvp.Value.GetValue<string>() == "null");

            if (!isNullValue)
            {
                if (merged.ContainsKey(kvp.Key) &&
                    merged[kvp.Key] is JsonObject existingObj &&
                    kvp.Value is JsonObject newObj)
                {
                    merged[kvp.Key] = MergeJsonObjects(existingObj, newObj);
                }
                else
                {
                    merged[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
        }

        return merged;
    }

    private static JsonObject EnsureTypeFirst(JsonObject source, string typeName)
    {
        var ordered = new JsonObject
        {
            ["$type"] = typeName
        };
        foreach (var kv in source)
        {
            if (string.Equals(kv.Key, "$type", StringComparison.Ordinal)) continue;
            ordered[kv.Key] = kv.Value?.DeepClone();
        }
        return ordered;
    }

    private static void RemoveNullProperties(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kvp in obj.ToList())
            {
                var value = kvp.Value;
                if (value is null)
                {
                    obj.Remove(kvp.Key);
                }
                else
                {
                    RemoveNullProperties(value);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                RemoveNullProperties(item);
            }
        }
    }

    private async Task<string> ExtractCompletePdfText(Stream stream)
    {
        var completeText = new StringBuilder();

        try
        {
            using var pdfReader = new PdfReader(stream);
            using var pdfDocument = new PdfDocument(pdfReader);

            for (int pageNum = 1; pageNum <= pdfDocument.GetNumberOfPages(); pageNum++)
            {
                var page = pdfDocument.GetPage(pageNum);
                var strategy = new SimpleTextExtractionStrategy();
                var pageText = PdfTextExtractor.GetTextFromPage(page, strategy);

                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    completeText.AppendLine($"=== PAGE {pageNum} ===");
                    completeText.AppendLine(pageText.Trim());
                    completeText.AppendLine();
                }
            }
        }
        catch (Exception ex)
        {
            completeText.AppendLine($"Error extracting PDF: {ex.Message}");
        }

        return completeText.ToString();
    }

    private string ExtractJson(string json)
    {
        return json.Replace("```json", "")
            .Replace("```", "")
            .Trim();
    }
}
