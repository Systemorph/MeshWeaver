using System.Text.Json.Nodes;
using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.Insurance.AI;

public class RiskImportAgent(IMessageHub hub) : IInitializableAgent, IAgentWithTools, IAgentWithContext, IAgentWithModelPreference
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private string? propertyRiskSchema;
    private string? excelImportConfigSchema;
    private ContentPlugin? contentPlugin;

    public string Name => nameof(RiskImportAgent);

    public string? GroupName => "Insurance";
    public int DisplayOrder => 1;
    public string? IconName => "DocumentTable";

    public string Description => "Runs risk imports for a pricing. Creates mappings and imports property risk data from Excel files.";

    public string? GetPreferredModel(IReadOnlyList<string> availableModels)
        => availableModels.FirstOrDefault(m => m.Contains("claude-sonnet-4-5", StringComparison.OrdinalIgnoreCase));

    public string Instructions
    {
        get
        {
            var baseText =
                $$$"""
                You control risk imports for a specific pricing. Use the provided tools.

                ## Content Collection Context

                Files are referenced using the fully qualified 'collection:filename' format:
                - Format: "Submissions@{pricingId}:filename" (e.g., "Submissions@Microsoft-2026:Microsoft.xlsx")
                - Pass this full string directly to ContentPlugin functions - it will parse collection and path automatically
                - When the user or InsuranceAgent mentions a file like "Submissions@Microsoft-2026:Microsoft.xlsx",
                  use it directly in ContentPlugin calls (e.g., GetContent(filePath="Submissions@Microsoft-2026:Microsoft.xlsx"))
                - DO NOT split the collection:filename format - pass it as-is to ContentPlugin

                # Importing Risks - MANDATORY WORKFLOW

                **CRITICAL: You MUST always follow ALL 5 steps below. Never skip any step. Never call Import without a configuration.**

                When the user asks you to import risks, follow these steps IN ORDER:

                **Step 1: Get Existing Configuration**
                - Call DataPlugin's GetData with type="ExcelImportConfiguration" and entityId=<fully-qualified-path>
                - Example: GetData(type="ExcelImportConfiguration", entityId="Submissions@Microsoft-2026:Microsoft.xlsx")
                - The entityId MUST be the fully qualified path (collection:filename format)

                **Step 2: Load Content Sample**
                - ALWAYS call ContentPlugin's GetContent with the fully qualified path and numberOfRows=20
                - Example: GetContent(filePath="Submissions@Microsoft-2026:Microsoft.xlsx", numberOfRows=20)
                - This gives you the Excel structure to create/verify the mapping

                **Step 3: Create or Update Configuration**
                - If Step 1 returned no configuration: Create a new configuration from scratch based on the content sample
                - If Step 1 returned an existing configuration: Review it against the content sample and update if needed
                - Apply any user-provided modifications
                - Ensure the configuration includes:
                  - "name" field set to the fully qualified path (e.g., "Submissions@Microsoft-2026:Microsoft.xlsx")
                  - "typeName" field set to "PropertyRisk"
                  - Correct "dataStartRow" based on the content sample
                  - Proper column mappings based on the content sample headers

                **Step 4: Save Configuration**
                - Call DataPlugin's UpdateData with type="ExcelImportConfiguration" and the configuration JSON
                - Example: UpdateData(type="ExcelImportConfiguration", data=<configuration-json>)

                **Step 5: Execute Import WITH Configuration**
                - Call ImportRisks with the fully qualified path, address, AND the configuration
                - Example: ImportRisks(path="Submissions@Microsoft-2026:Microsoft.xlsx", address="pricing/Microsoft-2026", configuration=<configuration-json>)
                - The configuration parameter is REQUIRED - ImportRisks will reject calls without it

                # Updating Risk Import Configuration
                When the user asks you to update the risk import configuration:
                1) Get the existing configuration using DataPlugin's GetData with type="ExcelImportConfiguration" and entityId=<fully-qualified-path>
                2) Load content sample using ContentPlugin's GetContent with numberOfRows=20
                3) Modify the configuration according to the user's input
                4) Save using DataPlugin's UpdateData with type="ExcelImportConfiguration"
                5) If user wants to re-import, call ImportRisks WITH the updated configuration

                # Automatic Risk Import Configuration
                - Use ContentPlugin's GetContent with numberOfRows=20 to get a sample of the file. It returns a markdown table with:
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
                - When the user asks you to import, your job is not finished by creating the risk import configuration. You MUST call ImportRisks with the configuration.
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
        return context?.Address?.Type == InsuranceApplicationAttribute.PricingType;
    }

    IEnumerable<AITool> IAgentWithTools.GetTools(IAgentChat chat)
    {
        var submissionPluginConfig = CreateSubmissionPluginConfig();
        contentPlugin = new ContentPlugin(hub, submissionPluginConfig, chat);

        // Return DataPlugin tools
        foreach (var tool in new DataPlugin(hub, chat, typeDefinitionMap).CreateTools())
            yield return tool;

        // Return ContentPlugin tools EXCEPT Import (we provide our own ImportRisks)
        foreach (var tool in contentPlugin.CreateTools().Where(t => t.Name != "Import"))
            yield return tool;

        // Add custom ImportRisks that enforces configuration requirement and null format
        yield return AIFunctionFactory.Create(ImportRisks);
    }

    /// <summary>
    /// Imports property risks from an Excel file. REQUIRES a configuration parameter.
    /// Format is always null - the configuration contains all import settings.
    /// </summary>
    [System.ComponentModel.Description(
        "Imports property risks from an Excel file. " +
        "REQUIRES a configuration parameter - calls without configuration will be rejected. " +
        "The configuration must be a valid ExcelImportConfiguration JSON. " +
        "Format is not used - all settings come from the configuration.")]
    private async Task<string> ImportRisks(
        [System.ComponentModel.Description("The fully qualified path in 'collection:filename' format (e.g., 'Submissions@Microsoft-2026:Microsoft.xlsx')")]
        string path,
        [System.ComponentModel.Description("The target address for the import (e.g., 'pricing/Microsoft-2026')")]
        string address,
        [System.ComponentModel.Description("REQUIRED: The ExcelImportConfiguration JSON. Must include typeName, columnMappings, etc.")]
        string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return "ERROR: Configuration is REQUIRED for ImportRisks. " +
                   "You MUST follow the 5-step workflow: " +
                   "1) Get existing configuration, 2) Load content sample, 3) Create/update configuration, " +
                   "4) Save configuration, 5) Call ImportRisks WITH the configuration JSON.";
        }

        if (contentPlugin == null)
        {
            return "ERROR: ContentPlugin not initialized.";
        }

        // Always pass format as null - configuration contains all settings
        return await contentPlugin.Import(path, address, format: null, configuration: configuration);
    }

    private static ContentPluginConfig CreateSubmissionPluginConfig()
    {
        return new ContentPluginConfig
        {
            Collections = [],
            ContextToConfigMap = context =>
            {
                // Only handle pricing contexts
                if (context?.Address?.Type != InsuranceApplicationAttribute.PricingType)
                    return null!;

                var pricingId = context.Address.Id;

                // Pricing format: pricing/company/year (segments already validated)

                // Use Hub-based collection config pointing to the pricing address
                // This allows the ContentPlugin to query the pricing hub for the actual collection configuration
                return new ContentCollectionConfig
                {
                    SourceType = HubStreamProviderFactory.SourceType,
                    Name = $"Submissions@{pricingId}",
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
                o => o.WithTarget(new Address(InsuranceApplicationAttribute.PricingType, "default", "2024")));
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
                o => o.WithTarget(new Address(InsuranceApplicationAttribute.PricingType, "default", "2024")));

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
                o => o.WithTarget(new Address(InsuranceApplicationAttribute.PricingType, "default", "2024")));
            propertyRiskSchema = resp?.Message?.Schema;
        }
        catch
        {
            propertyRiskSchema = null;
        }
    }
}
