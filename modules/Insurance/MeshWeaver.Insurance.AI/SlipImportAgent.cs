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
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Insurance.AI;

public class SlipImportAgent(IMessageHub hub) : IInitializableAgent, IAgentWithPlugins, IAgentWithContext
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private string? pricingSchema;
    private string? structureSchema;

    public string Name => nameof(SlipImportAgent);

    public string Description => "Imports insurance slip documents from PDF files and structures them into Pricing and Structure data models using LLM-based extraction.";

    public string Instructions
    {
        get
        {
            var baseText =
                $$$"""
                You are a slip import agent that processes PDF documents containing insurance submission slips.
                Your task is to extract structured data and map it to the insurance domain models using the provided schemas.

                ## Content Collection Context

                IMPORTANT: The current context is set to pricing/{pricingId} where pricingId follows the format {company}-{uwy}.
                - The submission files collection is named "Submissions-{pricingId}"
                - All file paths are relative to the root (/) of this collection
                - When listing files, you'll see paths like "/slip.pdf", "/submission.pdf"
                - When accessing files, use paths starting with "/" (e.g., "/slip.pdf")

                # Importing Slips
                When the user asks you to import a slip:
                1) First, use {{{nameof(ContentCollectionPlugin.ListFiles)}}}() to see available files in the submissions collection
                2) Use {{{nameof(SlipImportPlugin.ExtractCompleteText)}}} to extract the PDF content (e.g., "slip.pdf" without the leading /)
                3) Review the extracted text and identify data that matches the domain schemas
                4) Use {{{nameof(SlipImportPlugin.ImportSlipData)}}} to save the structured data as JSON
                5) Provide feedback on what data was successfully imported or if any issues were encountered

                # Data Mapping Guidelines
                Based on the extracted PDF text, create JSON objects that match the schemas provided below:
                - **Pricing**: Basic pricing information (insured name, broker, dates, premium, country, legal entity)
                - **Structure**: Reinsurance layer structure and financial terms (cession, limits, rates, commissions)

                # Important Rules
                - Only extract data that is explicitly present in the PDF text
                - Use null or default values for missing data points
                - Ensure all monetary values are properly formatted as numbers
                - Convert percentages to decimal format (e.g., 25% → 0.25)
                - Provide clear feedback on what data was successfully extracted
                - If data is ambiguous or unclear, note it in your response
                - For Structure records, generate appropriate LayerId values (e.g., "Layer1", "Layer2")
                - Multiple layers can be imported if the slip contains multiple layer structures

                # PDF Section Processing
                Look for common sections in insurance slips:
                - Insured information (name, location, industry)
                - Coverage details (inception/expiration dates, policy terms)
                - Premium and financial information
                - Layer structures (limits, attachments, rates)
                - Reinsurance terms (commission, brokerage, taxes)

                Notes:
                - When listing files, paths will include "/" prefix (e.g., "/slip.pdf")
                - When calling import functions, provide only the filename without "/" (e.g., "slip.pdf")
                """;

            if (pricingSchema is not null)
                baseText += $"\n\n# Pricing Schema\n```json\n{pricingSchema}\n```";
            if (structureSchema is not null)
                baseText += $"\n\n# Structure Schema\n```json\n{structureSchema}\n```";

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
            typeDefinitionMap = typesResponse.Message.Types.Select(t => t with { Address = null }).ToDictionary(x => x.Name);
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
            pricingSchema = resp.Message.Schema;
        }
        catch
        {
            pricingSchema = null;
        }

        try
        {
            var resp = await hub.AwaitResponse(
                new GetSchemaRequest(nameof(Structure)),
                o => o.WithTarget(pricingAddress));
            structureSchema = resp.Message.Schema;
        }
        catch
        {
            structureSchema = null;
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
    [Description("Extracts the complete text from a PDF slip document and returns it for LLM processing")]
    public async Task<string> ExtractCompleteText(string filename)
    {
        if (chat.Context?.Address?.Type != PricingAddress.TypeName)
            return "Please navigate to the pricing first.";

        try
        {
            var pricingId = chat.Context.Address.Id;
            var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();
            var stream = await OpenContentReadStreamAsync(contentService, pricingId, filename);

            if (stream is null)
                return $"Content not found: {filename}";

            await using (stream)
            {
                var completeText = await ExtractCompletePdfText(stream);

                var sb = new StringBuilder();
                sb.AppendLine("=== INSURANCE SLIP DOCUMENT TEXT ===");
                sb.AppendLine();
                sb.AppendLine(completeText);

                return sb.ToString();
            }
        }
        catch (Exception e)
        {
            return $"Error extracting PDF text: {e.Message}";
        }
    }

    [KernelFunction]
    [Description("Imports the structured slip data as JSON into the pricing")]
    public async Task<string> ImportSlipData(
        [Description("Pricing data as JSON (optional if updating existing)")] string? pricingJson,
        [Description("Array of Structure layer data as JSON (can contain multiple layers)")] string? structuresJson)
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

            // Step 3: Process Structure layers (can be multiple)
            if (!string.IsNullOrWhiteSpace(structuresJson))
            {
                var structuresData = JsonNode.Parse(ExtractJson(structuresJson));

                // Handle both array and single object
                var structureArray = structuresData is JsonArray arr ? arr : new JsonArray { structuresData };

                foreach (var structureData in structureArray)
                {
                    if (structureData is JsonObject structureObj)
                    {
                        var processedStructure = EnsureTypeFirst(structureObj, nameof(Structure));
                        processedStructure["pricingId"] = pricingId;
                        RemoveNullProperties(processedStructure);
                        updates.Add(processedStructure);
                    }
                }
            }

            if (updates.Count == 0)
                return "No valid data provided for import.";

            // Step 4: Post DataChangeRequest
            var updateRequest = new DataChangeRequest { Updates = updates };
            var response = await hub.AwaitResponse<DataChangeResponse>(updateRequest, o => o.WithTarget(pricingAddress));

            return response.Message.Status switch
            {
                DataChangeStatus.Committed => $"Slip data imported successfully. Updated {updates.Count} entities.",
                _ => $"Data update failed:\n{string.Join('\n', response.Message.Log.Messages.Select(l => l.LogLevel + ": " + l.Message))}"
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

            return response.Message.Data as Pricing;
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

    private static async Task<Stream?> OpenContentReadStreamAsync(
        IContentService contentService,
        string pricingId,
        string filename)
    {
        try
        {
            // Parse pricingId in format {company}-{uwy}
            var parts = pricingId.Split('-');
            if (parts.Length != 2)
                return null;

            var company = parts[0];
            var uwy = parts[1];
            var contentPath = $"{company}/{uwy}/{filename}";

            var collection = await contentService.GetCollectionAsync("Submissions", CancellationToken.None);
            if (collection is null)
                return null;

            return await collection.GetContentAsync(contentPath);
        }
        catch
        {
            return null;
        }
    }

    private string ExtractJson(string json)
    {
        return json.Replace("```json", "")
            .Replace("```", "")
            .Trim();
    }
}
