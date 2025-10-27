using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Messaging;
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
                2) If no import configuration was returned in 1, get a sample of the worksheet using CollectionPlugin's GetFile function with the collection name "Submissions-{pricingId}", the filename, and numberOfRows=20. Extract the table start row as well as the mapping as in the schema provided below.
                   Consider any input from the user to modify the configuration. Use the {{{nameof(RiskImportPlugin.UpdateRiskImportConfiguration)}}} function to save the configuration.
                3) Call Import with the filename and the configuration you have updated or created.

                # Updating Risk Import Configuration
                When the user asks you to update the risk import configuration, you should:
                1) Get the existing risk mapping configuration for the specified file using the function {{{nameof(RiskImportPlugin.GetRiskImportConfiguration)}}} with the filename.
                2) Modify it according to the user's input, ensuring it follows the schema provided below.
                3) Upload the new configuration using the function {{{nameof(RiskImportPlugin.UpdateRiskImportConfiguration)}}} with the filename and the updated mapping.

                # Automatic Risk Import Configuration
                - Use CollectionPlugin's GetFile with numberOfRows=20 to get a sample of the file. It returns a markdown table with:
                  - First column: Row numbers (1-based)
                  - Remaining columns: Labeled A, B, C, D, etc. (Excel column letters)
                  - Empty cells appear as empty values in the table (not "null")
                - Column letters start with A (first data column after Row number). Empty columns are still shown with their letters.
                - Row numbers are 1-based. When specifying tableStartRow, use the row number from the Row column (e.g., if headers are on row 1 and data starts on row 2, set tableStartRow=2).
                - Look for the header row in the markdown table and map column letters (A, B, C, etc.) to PropertyRisk properties.
                - Map to the properties of the PropertyRisk type (see schema below). Only these names are allowed for mappings. Read the descriptions contained in the schema to get guidance on which field to map where.
                - IMPORTANT: Each TargetProperty should appear ONLY ONCE in the configuration. If a property maps to multiple columns, use the SourceColumns list (e.g., "sourceColumns": ["A", "B"]) instead of creating multiple entries with the same TargetProperty.
                - IMPORTANT: Each column (A, B, C, etc.) should be mapped ONLY ONCE across all mappings. Do not include the same column in multiple targetProperty mappings or sourceColumns lists.
                - Columns you cannot map ==> ignore (don't include them in the configuration).
                - Empty columns at the beginning still get column letters (A, B, C...). You can see which columns are empty by looking at the markdown table.

                # TsiContent Mapping
                - MOST COLUMNS will be mapped to the 'tsiContent' property (Total Sum Insured content breakdown).
                - Common column headers for tsiContent include: Stock, Fixtures, Fittings, IT Equipment, Land, Leasehold Improv., Leasehold Improvements, Plant & Equipment, Tooling, Workshop Equipment, Rent Forecast.
                - These columns typically represent different categories of insured content and should be mapped to tsiContent using the SourceColumns list.
                - Example: If you see columns for "Stock", "Fixtures", "IT Equipment", map them as: "targetProperty": "tsiContent", "sourceColumns": ["E", "F", "G"]

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
        var submissionPluginConfig = CreateSubmissionPluginConfig();
        yield return new ContentCollectionPlugin(hub, submissionPluginConfig, chat).CreateKernelPlugin();

        // Add CollectionPlugin for import functionality
        var collectionPlugin = new CollectionPlugin(hub);
        yield return KernelPluginFactory.CreateFromObject(collectionPlugin);

        // Add risk import specific plugin
        var plugin = new RiskImportPlugin(hub, chat);
        yield return KernelPluginFactory.CreateFromObject(plugin);
    }

    private static ContentCollectionPluginConfig CreateSubmissionPluginConfig()
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
            var types = typesResponse?.Message?.Types;
            typeDefinitionMap = types?.Select(t => t with { Address = null }).ToDictionary(x => x.Name!);
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

            // Hard-code TypeName to "PropertyRisk" in the schema
            var schema = resp?.Message?.Schema;
            if (!string.IsNullOrEmpty(schema))
            {
                // Parse the schema as JSON to modify it
                try
                {
                    var schemaJson = JsonNode.Parse(schema) as JsonObject;
                    if (schemaJson?["anyOf"] is JsonArray array && array.First() is JsonObject obj && obj["properties"] is JsonObject properties)
                    {
                        // Set TypeName property to have a constant value of "PropertyRisk"
                        properties["typeName"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["const"] = "PropertyRisk",
                            ["description"] = "The fully qualified type name of the entity to import. This is hard-coded to 'PropertyRisk' for risk imports."
                        };
                        schema = schemaJson.ToJsonString();
                    }
                }
                catch
                {
                    // If parsing fails, use original schema
                }
            }
            excelImportConfigSchema = schema;
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
            propertyRiskSchema = resp?.Message?.Schema;
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
            var json = JsonSerializer.Serialize(response?.Message?.Data, hub.JsonSerializerOptions);

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
            return JsonSerializer.Serialize(response?.Message, hub.JsonSerializerOptions);
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

    private string ExtractJson(string json)
    {
        return json.Replace("```json", "")
            .Replace("```", "")
            .Trim();
    }
}
