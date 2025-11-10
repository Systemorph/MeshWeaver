using System.Text.Json.Nodes;
using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.Insurance.AI;

public class RiskImportAgent(IMessageHub hub) : IInitializableAgent, IAgentWithTools
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
                1) Get the file content sample by calling ContentPlugin's GetContent function with path=filename, collectionName=null, numberOfRows=20
                   - For Excel files: numberOfRows=20 gives you the first 20 rows to see column structure
                   - Show the file preview to the user so they can see what you're working with
                2) Get the existing risk mapping configuration for the specified file using DataPlugin's GetData function with type="ExcelImportConfiguration" and entityId=filename.
                3) Analyze the configuration and user request:
                   - **If no configuration exists**: Create a new mapping configuration from scratch
                   - **If configuration exists and user is making corrections/changes**:
                     - CRITICAL: Load the existing configuration and MODIFY only the parts the user wants to change
                     - DO NOT recreate the entire configuration from scratch
                     - Keep all existing mappings that the user hasn't asked to change
                     - Only update the specific mappings, columns, or settings mentioned in the user's request
                   - The file content preview shows a markdown table with row numbers and column letters (A, B, C, etc.)
                   - Identify the header row and extract the table start row
                   - Map column letters to PropertyRisk properties according to the schema provided below
                   - Ensure the JSON includes "name" field set to the filename and "typeName" set to "PropertyRisk"
                4) Proceed with the import immediately - DO NOT ask for user confirmation
                   - Call ContentPlugin's Import function with: path=filename, collection=null, address=PricingAddress, format=null, configuration=the JSON configuration, snapshot=true
                   - IMPORTANT: Always pass collection=null (it will be inferred from context)
                   - IMPORTANT: Always pass format=null since the configuration parameter contains all necessary import settings
                   - IMPORTANT: Always pass snapshot=true to create a snapshot of the imported data
                   - The Import function will automatically save the configuration to the hub if it's valid, so you don't need to call UpdateData separately.
                   - After the import completes, provide a summary of what was imported (number of risks, any issues encountered)

                # Handling User Corrections and Iterations

                When the user asks for corrections or changes to an existing import:
                - ALWAYS load the existing configuration first using DataPlugin's GetData
                - Identify what the user wants to change (e.g., "map column F to TotalSumInsured", "change tableStartRow to 3", "add column G to tsiContent")
                - Make ONLY the requested changes to the existing configuration
                - Keep all other mappings and settings unchanged
                - Example scenarios:
                  - User: "map column F to TotalSumInsured instead of column E"
                    → Load existing config, find the TotalSumInsured mapping, change sourceColumn from "E" to "F", keep everything else
                  - User: "add column G to tsiContent"
                    → Load existing config, find the tsiContent mapping, add "G" to the sourceColumns list, keep everything else
                  - User: "remove the Id mapping"
                    → Load existing config, remove the mapping with targetProperty="Id", keep everything else

                # Creating Risk Import Configuration from File Preview

                The file content preview in the chat history is a markdown table with:
                - First column: Row numbers (1-based)
                - Remaining columns: Labeled A, B, C, D, etc. (Excel column letters)
                - Empty cells appear as empty values in the table (not "null")

                When creating a new import configuration:
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
                - When the user asks you to import, your job is not finished by creating the risk import configuration. You will actually have to call ContentPlugin's Import function.
                - DO NOT ask for user confirmation before importing - proceed directly with the import after creating the configuration.
                """;

            if (excelImportConfigSchema is not null)
                baseText += $"\n\n# Schema for ExcelImportConfiguration\n{excelImportConfigSchema}";
            if (propertyRiskSchema is not null)
                baseText += $"\n\n# Schema for PropertyRisk (Target for Mapping)\n{propertyRiskSchema}";

            return baseText;
        }
    }

    IEnumerable<AITool> IAgentWithTools.GetTools(IAgentChat chat)
    {
        var dataPlugin = new DataPlugin(hub, chat, typeDefinitionMap);
        foreach (var tool in dataPlugin.CreateTools())
            yield return tool;

        // Add ContentPlugin for submissions and import functionality
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
