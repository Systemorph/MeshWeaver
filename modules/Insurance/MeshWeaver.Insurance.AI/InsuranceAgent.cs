using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.Insurance.AI;

/// <summary>
/// Main Insurance agent that provides access to insurance pricing data and collections.
/// </summary>
[ExposedInDefaultAgent]
public class InsuranceAgent(IMessageHub hub) : IInitializableAgent, IAgentWithTools, IAgentWithContext, IAgentWithHandoffs
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private Dictionary<string, LayoutAreaDefinition>? layoutAreaMap;

    public string Name => "InsuranceAgent";

    public string Description =>
        "Handles all questions and actions related to insurance pricings, property risks, and dimensions. " +
        "Provides access to pricing data, allows creation and management of pricings and property risks. " +
        "Also manages submission documents and files for each pricing.";

    public IEnumerable<DelegationDescription> Delegations
    {
        get
        {
            yield return new DelegationDescription(
                nameof(RiskImportAgent),
                "Delegate to RiskImportAgent when the user wants to import property risks from Excel files. " +
                "When delegating, provide a clear message that includes: " +
                "(1) the exact file path with '/' prefix (e.g., '/Microsoft.xlsx'), " +
                "(2) mention it's for the 'current pricing context', " +
                "(3) any observed file structure details from the preview. " +
                "Example: 'Import property risks from the file \\'/Microsoft.xlsx\\' for the current pricing context'. " +
                "Handles files with columns like Location, TSI, Address, Country, Currency, Building Value, etc. " +
                "Common file names: risks.xlsx, exposure.xlsx, property schedule, location schedule."
            );

            yield return new DelegationDescription(
                nameof(SlipImportAgent),
                "Delegate to SlipImportAgent when the user wants to import insurance slips from PDF/Word documents. " +
                "When delegating, provide a clear message that includes: " +
                "(1) the exact file path with '/' prefix (e.g., '/slip.pdf'), " +
                "(2) mention it's for the 'current pricing context', " +
                "(3) any observed document structure from the preview. " +
                "Example: 'Import slip data from the file \\'/submission.pdf\\' for the current pricing context'. " +
                "Handles files with content about Insured, Coverage, Premium, Reinsurance Layers, Limits, Rates, etc. " +
                "Common file names: slip.pdf, submission.pdf, placement.pdf, quote.pdf."
            );
        }
    }

    public string Instructions =>
        $$$"""
        The agent is the InsuranceAgent, specialized in managing insurance pricings:

        ## Content Collection Context

        IMPORTANT: The current context is set to pricing/{pricingId} where pricingId follows the format {company}-{uwy}.
        - The ContentPlugin is already configured to use the current pricing context automatically
        - The submission files collection is named "Submissions-{pricingId}"
        - All file paths are relative to the root (/) of this collection (e.g., "/slip.pdf", "/risks.xlsx")

        ## Working with Submission Documents and Files

        CRITICAL: When users ask about submission files, documents, or content:
        - The ContentPlugin is already configured for the current pricing context
        - ALWAYS pass collectionName=null (or omit it entirely) - it will be inferred automatically from context
        - Simply call the ContentPlugin functions with just the file path
        - All file paths should start with "/" (e.g., "/slip.pdf", "/risks.xlsx" not just "slip.pdf")

        Available ContentPlugin functions (always pass collectionName=null):
        - {{{nameof(ContentPlugin.ListFiles)}}}(collectionName=null) - List all files in the current pricing's submissions
        - {{{nameof(ContentPlugin.GetContent)}}}(filePath, collectionName=null, numberOfRows) - Get file content preview
        - {{{nameof(ContentPlugin.SaveFile)}}}(filePath, content, collectionName=null) - Save a document
        - {{{nameof(ContentPlugin.DeleteFile)}}}(filePath, collectionName=null) - Delete a file

        Examples:
        - User: "Show me the submission files" → Call {{{nameof(ContentPlugin.ListFiles)}}}(collectionName=null)
        - User: "Preview Microsoft.xlsx" → Call {{{nameof(ContentPlugin.GetContent)}}}(filePath="/Microsoft.xlsx", collectionName=null, numberOfRows=20)

        ## Importing Files (Excel, PDF, Word)

        **CRITICAL: You (InsuranceAgent) should NEVER perform imports yourself. ALWAYS delegate to specialist agents.**

        When users want to import data from files (e.g., "import Microsoft.xlsx" or "import slip.pdf"):

        **Step 1: Determine file type and delegate immediately**
        - DO NOT call GetContent to preview the file - let the specialist agent handle that
        - DO NOT call Import function yourself - you are NOT an import specialist
        - Determine which agent to delegate to based on the file name and extension:
          - **Excel files (.xlsx, .xls, .csv)**: Files with names like "risks", "exposure", "property", "schedule", "Microsoft", etc.
            → Delegate to RiskImportAgent
          - **PDF/Word files (.pdf, .doc, .docx)**: Files with names like "slip", "submission", "placement", "quote", etc.
            → Delegate to SlipImportAgent
        - If the file type is unclear from the name, ask the user to clarify
        - CRITICAL: Pass the exact file path (with "/" prefix) and mention the current pricing context
        - Examples:
          - {{{nameof(RiskImportAgent)}}}("Import property risks from the file '/Microsoft.xlsx' for the current pricing context")
          - {{{nameof(SlipImportAgent)}}}("Import slip data from the file '/slip.pdf' for the current pricing context")
        - When delegating, explicitly mention:
          1. The exact file path with "/" prefix (e.g., "/Microsoft.xlsx")
          2. That it's for the "current pricing context" (the agent will see the context automatically)
          3. Keep the delegation message brief

        **Step 2: The specialist agent will handle everything**
        - The specialist agent (RiskImportAgent or SlipImportAgent) will:
          1. Get the file content sample
          2. Analyze the structure
          3. Create the import configuration or extract the data
          4. Perform the import automatically
        - You just delegate and let them work

        **Follow-up Requests and Corrections**
        - CRITICAL: If the user asks for ANY corrections, changes, or follow-up operations related to an import:
          - DO NOT analyze or plan the changes yourself
          - DO NOT ask the user if they want to proceed
          - DO NOT provide detailed explanations of how you will handle the request
          - IMMEDIATELY delegate to the SAME specialist agent that handled the original import
          - Simply pass the user's request directly to the specialist agent
          - The specialist agent will handle all analysis, planning, and execution
        - Examples of follow-up requests that should be delegated:
          - "map column F to TotalSumInsured instead of column E"
          - "for tsiBI, take Cell C3 and allocate with column Q"
          - "add column G to the tsiContent mapping"
          - "change the tableStartRow to 3"
          - "re-import with the corrected mapping"
        - Delegation format:
          - {{{nameof(RiskImportAgent)}}}("The user requests: [exact user request]")
          - Example: {{{nameof(RiskImportAgent)}}}("The user requests: for tsiBI, take Cell C3 and allocate with column Q")
        - The thread history will be available to the specialist agent, so they can see:
          - The file content preview
          - The previous import configuration
          - All previous import attempts and results

        CRITICAL Rules:
        - Always use file paths with "/" prefix (e.g., "/Microsoft.xlsx" not "Microsoft.xlsx")
        - DO NOT preview files yourself - let the specialist agents handle that
        - DO NOT import files yourself - you are a triage/routing agent, not an import specialist
        - DO NOT analyze import-related requests yourself - immediately delegate to the specialist agent
        - DO NOT provide detailed explanations or ask for confirmation - just delegate
        - Delegate based on file extension and name pattern for initial imports
        - For follow-up requests about imports, delegate to the same specialist agent that handled the original import
        - Your ONLY job is to route requests to the right specialist - nothing more

        ## Working with Pricing Data

        When users ask about pricing entities, risks, or dimensions (NOT files):
        1. Use the DataPlugin to get available data (function: {{{nameof(DataPlugin.GetData)}}}) with the appropriate type:
            {{{nameof(Pricing)}}}, {{{nameof(PropertyRisk)}}}, {{{nameof(LineOfBusiness)}}}, {{{nameof(Country)}}}, {{{nameof(LegalEntity)}}}, {{{nameof(Currency)}}}
        2. When asked to modify an entity, load it using {{{nameof(DataPlugin.GetData)}}} with the entityId parameter,
           modify it, and save it back using {{{nameof(DataPlugin.UpdateData)}}}.

        ## Displaying Layout Areas

        Use the LayoutAreaPlugin with {{{nameof(LayoutAreaPlugin.DisplayLayoutArea)}}} function.
        Available layout areas can be listed using {{{nameof(LayoutAreaPlugin.GetLayoutAreas)}}} function.
        """;

    IEnumerable<AITool> IAgentWithTools.GetTools(IAgentChat chat)
    {
        var dataPlugin = new DataPlugin(hub, chat, typeDefinitionMap);
        foreach (var tool in dataPlugin.CreateTools())
            yield return tool;

        var layoutPlugin = new LayoutAreaPlugin(hub, chat, layoutAreaMap);
        foreach (var tool in layoutPlugin.CreateTools())
            yield return tool;

        // Always provide ContentPlugin - it will use ContextToConfigMap to determine the collection
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

                var company = parts[0];
                var uwy = parts[1];
                var subPath = $"{company}/{uwy}";

                // Create Hub-based collection config pointing to the pricing address
                // This matches the logic in InsuranceApplicationExtensions
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
            typeDefinitionMap = typesResponse?.Message?.Types?.Select(t => t with { Address = null }).ToDictionary(x => x.Name!);
        }
        catch
        {
            typeDefinitionMap = null;
        }

        try
        {
            var layoutAreaResponse = await hub.AwaitResponse(
                new GetLayoutAreasRequest(),
                o => o.WithTarget(new PricingAddress("default")));
            layoutAreaMap = layoutAreaResponse?.Message?.Areas?.ToDictionary(x => x.Area);
        }
        catch
        {
            layoutAreaMap = null;
        }
    }

    /// <summary>
    /// Determines whether this agent should handle messages for contexts starting with "insurance-pricing".
    /// </summary>
    public bool Matches(AgentContext? context)
    {
        if (context?.Address == null)
            return false;

        return context.Address.Type == PricingAddress.TypeName;
    }
}
