using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace MeshWeaver.Insurance.AI;

public class RiskImportAgent(IMessageHub hub) : IInitializableAgent, IAgentWithPlugins, IAgentWithContext
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private string? propertyRiskSchema;
    private string? excelImportConfigSchema;

    public string Name => nameof(RiskImportAgent);

    public string Description => "Runs risk imports for a pricing. Creates mappings and imports property risk data from Excel files.";

    public string Instructions
    {
        get
        {
            var baseText =
                $$$"""
                You control risk imports for a specific pricing. Use the provided tool:

                ## Content Collection Context

                IMPORTANT: The current context is set to pricing/{pricingId} where pricingId follows the format {company}-{uwy}.
                - The submission files collection is named "Submissions-{pricingId}"
                - All file paths are relative to the root (/) of this collection
                - When listing files, you'll see paths like "/risks.xlsx", "/exposure.xlsx"
                - When accessing files, use paths starting with "/" (e.g., "/risks.xlsx")

                # Importing Risks
                When the user asks you to import risks, you should:
                1) Get the existing risk mapping configuration for the specified file using the function {{{nameof(RiskImportPlugin.GetRiskImportConfiguration)}}} with the filename.
                2) If no import configuration was returned in 1, get a sample of the worksheet using {{{nameof(RiskImportPlugin.GetWorksheetSample)}}} with the filename and extract the table start row as well as the mapping as in the schema provided below.
                   Consider any input from the user to modify the configuration. Use the {{{nameof(RiskImportPlugin.UpdateRiskImportConfiguration)}}} function to save the configuration.
                3) Call Import with the filename. The Import function will automatically use the saved configuration.

                # Updating Risk Import Configuration
                When the user asks you to update the risk import configuration, you should:
                1) Get the existing risk mapping configuration for the specified file using the function {{{nameof(RiskImportPlugin.GetRiskImportConfiguration)}}} with the filename.
                2) Modify it according to the user's input, ensuring it follows the schema provided below.
                3) Upload the new configuration using the function {{{nameof(RiskImportPlugin.UpdateRiskImportConfiguration)}}} with the filename and the updated mapping.

                # Automatic Risk Import Configuration
                - Read the column header from the row which you determine to be the first of the Data Table and map to column numbers.
                - Map to the properties of the PropertyRisk type (see schema below). Only these names are allowed for mappings. Read the descriptions contained in the schema to get guidance on which field to map where
                - Columns you cannot map ==> ignore.
                - Watch out for empty columns at the beginning of the table. In this case, see that you get the column index right.

                Notes:
                - The agent defaults to ignoring rows where Id or Address is missing (adds "Id == null" and "Address == null" to ignoreRowExpressions).
                - Provide only the file name (e.g., "risks.xlsx"); it is resolved relative to the pricing's content collection.

                IMPORTANT OUTPUT RULES:
                - do not output JSON to the user.
                - When the user asks you to import, your job is not finished by creating the risk import configuration. You will actually have to call import.
                """;

            if (excelImportConfigSchema is not null)
                baseText += $"\n\n# Schema for ExcelImportConfiguration\n{excelImportConfigSchema}";
            if (propertyRiskSchema is not null)
                baseText += $"\n\n# Schema for PropertyRisk (Target for Mapping)\n{propertyRiskSchema}";

            return baseText;
        }
    }

    public bool Matches(AgentContext? context)
    {
        return context?.Address?.Type == PricingAddress.TypeName;
    }

    IEnumerable<KernelPlugin> IAgentWithPlugins.GetPlugins(IAgentChat chat)
    {
        yield return new DataPlugin(hub, chat, typeDefinitionMap).CreateKernelPlugin();

        // Add ContentCollectionPlugin for submissions
        var submissionPluginConfig = CreateSubmissionPluginConfig(chat);
        yield return new ContentCollectionPlugin(hub, submissionPluginConfig, chat).CreateKernelPlugin();

        // Add CollectionPlugin for import functionality
        var collectionPlugin = new CollectionPlugin(hub);
        yield return KernelPluginFactory.CreateFromObject(collectionPlugin);

        // Add risk import specific plugin
        var plugin = new RiskImportPlugin(hub, chat);
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
        try
        {
            var typesResponse = await hub.AwaitResponse(
                new GetDomainTypesRequest(),
                o => o.WithTarget(new PricingAddress("default")));
            typeDefinitionMap = typesResponse.Message.Types.Select(t => t with { Address = null }).ToDictionary(x => x.Name);
        }
        catch
        {
            typeDefinitionMap = null;
        }

        try
        {
            var resp = await hub.AwaitResponse(
                new GetSchemaRequest("ExcelImportConfiguration"),
                o => o.WithTarget(new PricingAddress("default")));
            excelImportConfigSchema = resp.Message.Schema;
        }
        catch
        {
            excelImportConfigSchema = null;
        }

        try
        {
            var resp = await hub.AwaitResponse(
                new GetSchemaRequest(nameof(PropertyRisk)),
                o => o.WithTarget(new PricingAddress("default")));
            propertyRiskSchema = resp.Message.Schema;
        }
        catch
        {
            propertyRiskSchema = null;
        }
    }
}

public class RiskImportPlugin(IMessageHub hub, IAgentChat chat)
{
    private JsonSerializerOptions GetJsonOptions()
    {
        return hub.JsonSerializerOptions;
    }

    [KernelFunction]
    [Description("Imports a file with filename")]
    public async Task<string> Import(string filename)
    {
        if (chat.Context?.Address?.Type != PricingAddress.TypeName)
            return "Please navigate to the pricing for which you want to import risks.";

        var pricingId = chat.Context.Address.Id;
        var collectionName = $"Submissions-{pricingId}";
        var address = new PricingAddress(pricingId);

        // Try to get the saved configuration for this file
        string? configuration = null;
        try
        {
            var configJson = await GetRiskImportConfiguration(filename);
            // Check if we got a valid configuration (not an error message)
            if (!configJson.StartsWith("Error") && !configJson.StartsWith("Please navigate"))
            {
                configuration = configJson;
            }
        }
        catch
        {
            // If we can't get the configuration, fall back to format-based import
        }

        // Delegate to CollectionPlugin's Import method
        var collectionPlugin = new CollectionPlugin(hub);
        return await collectionPlugin.Import(
            path: filename,
            collection: collectionName,
            address: address,
            format: configuration != null ? null : "PropertyRiskImport", // Use format only if no configuration
            configuration: configuration // Pass configuration if available
        );
    }

    [KernelFunction]
    [Description("Gets the risk configuration for a particular file")]
    public async Task<string> GetRiskImportConfiguration(string filename)
    {
        if (chat.Context?.Address?.Type != PricingAddress.TypeName)
            return "Please navigate to the pricing for which you want to create a risk import mapping.";

        try
        {
            var response = await hub.AwaitResponse(
                new GetDataRequest(new EntityReference("ExcelImportConfiguration", filename)),
                o => o.WithTarget(new PricingAddress(chat.Context.Address.Id))
            );

            // Serialize the data
            var json = JsonSerializer.Serialize(response.Message.Data, hub.JsonSerializerOptions);

            // Parse and ensure $type is set to ExcelImportConfiguration
            var jsonObject = JsonNode.Parse(json) as JsonObject;
            if (jsonObject != null)
            {
                var withType = EnsureTypeFirst(jsonObject, "ExcelImportConfiguration");
                return JsonSerializer.Serialize(withType, hub.JsonSerializerOptions);
            }

            return json;
        }
        catch (Exception e)
        {
            return $"Error processing file '{filename}': {e.Message}";
        }
    }

    [KernelFunction]
    [Description("Updates the mapping configuration for risks import")]
    public async Task<string> UpdateRiskImportConfiguration(
        string filename,
        [Description("Needs to follow the schema provided in the system prompt")] string mappingJson)
    {
        if (chat.Context?.Address?.Type != PricingAddress.TypeName)
            return "Please navigate to the pricing for which you want to update the risk import configuration.";

        var pa = new PricingAddress(chat.Context.Address.Id);
        if (string.IsNullOrWhiteSpace(mappingJson))
            return "Json mapping is empty. Please provide valid JSON.";

        try
        {
            var parsed = EnsureTypeFirst((JsonObject)JsonNode.Parse(ExtractJson(mappingJson))!, "ExcelImportConfiguration");
            parsed["entityId"] = pa.Id;
            parsed["name"] = filename;
            var response = await hub.AwaitResponse(new DataChangeRequest() { Updates = [parsed] }, o => o.WithTarget(pa));
            return JsonSerializer.Serialize(response.Message, hub.JsonSerializerOptions);
        }
        catch (Exception e)
        {
            return $"Mapping JSON is invalid. Please provide valid JSON. Exception: {e.Message}";
        }
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

    [KernelFunction]
    [Description("Gets the first 20 rows for each worksheet in the workbook to help determine the mapping")]
    public async Task<string> GetWorksheetSample(string filename)
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
                using var wb = new XLWorkbook(stream);
                var sb = new StringBuilder();

                foreach (var ws in wb.Worksheets)
                {
                    var used = ws.RangeUsed();
                    sb.AppendLine($"Sheet: {ws.Name}");
                    if (used is null)
                    {
                        sb.AppendLine("(No data)");
                        sb.AppendLine();
                        continue;
                    }

                    var firstRow = used.FirstRow().RowNumber();
                    var lastRow = Math.Min(used.FirstRow().RowNumber() + 19, used.LastRow().RowNumber());
                    var firstCol = 1;
                    var lastCol = used.LastColumn().ColumnNumber();

                    for (var r = firstRow; r <= lastRow; r++)
                    {
                        var rowVals = new List<string>();
                        for (var c = firstCol; c <= lastCol; c++)
                        {
                            var raw = ws.Cell(r, c).GetValue<string>();
                            var val = raw?.Replace('\n', ' ').Replace('\r', ' ').Trim();
                            rowVals.Add(string.IsNullOrEmpty(val) ? "null" : val);
                        }

                        sb.AppendLine(string.Join('\t', rowVals));
                    }

                    sb.AppendLine();
                }

                return sb.ToString();
            }
        }
        catch (Exception e)
        {
            return $"Error reading sample: {e.Message}";
        }
    }


    private static async Task<Stream?> OpenContentReadStreamAsync(
        IContentService contentService,
        string pricingId,
        string filename)
    {
        try
        {
            var collectionName = $"Submissions-{pricingId}";

            var collection = await contentService.GetCollectionAsync(collectionName, CancellationToken.None);
            if (collection is null)
                return null;

            return await collection.GetContentAsync(filename);
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
