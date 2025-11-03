using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.SemanticKernel;

namespace MeshWeaver.Insurance.AI;

/// <summary>
/// Main Insurance agent that provides access to insurance pricing data and collections.
/// </summary>
[ExposedInNavigator]
public class InsuranceAgent(IMessageHub hub) : IInitializableAgent, IAgentWithPlugins, IAgentWithContext, IAgentWithDelegations
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private Dictionary<string, LayoutAreaDefinition>? layoutAreaMap;

    public string Name => "InsuranceAgent";

    public string Description =>
        "Handles all questions and actions related to insurance pricings, property risks, and dimensions. " +
        "Provides access to pricing data, allows creation and management of pricings and property risks. " +
        "Also manages submission documents and files for each pricing. " +
        "Can delegate to specialized import agents for processing risk data files and slip documents.";

    public IEnumerable<DelegationDescription> Delegations
    {
        get
        {
            yield return new DelegationDescription(
                nameof(RiskImportAgent),
                "Delegate to RiskImportAgent when the user wants to import property risks from Excel files, " +
                "or when working with risk data files (.xlsx, .xls) that contain property information like " +
                "location, TSI (Total Sum Insured), address, country, currency, building values, etc. " +
                "Common file names include: risks.xlsx, exposure.xlsx, property schedule, location schedule, etc."
            );

            yield return new DelegationDescription(
                nameof(SlipImportAgent),
                "Delegate to SlipImportAgent when the user wants to import insurance slips from PDF documents, " +
                "or when working with slip files (.pdf) that contain insurance submission information like " +
                "insured details, coverage terms, premium information, reinsurance structure layers, limits, rates, etc. " +
                "Common file names include: slip.pdf, submission.pdf, placement.pdf, quote.pdf, etc."
            );
        }
    }

    public string Instructions =>
        $$$"""
        The agent is the InsuranceAgent, specialized in managing insurance pricings:

        ## Content Collection Context

        IMPORTANT: The current context is set to pricing/{pricingId} where pricingId follows the format {company}-{uwy}.
        - The submission files collection is named "Submissions-{pricingId}"
        - All file paths are relative to the root (/) of this collection
        - Example: For pricing "AXA-2024", the collection is "Submissions-AXA-2024" and files are at paths like "/slip.pdf", "/risks.xlsx"

        ## Working with Submission Documents and Files

        CRITICAL: When users ask about submission files, documents, or content:
        - DO NOT call {{{nameof(DataPlugin.GetData)}}} for Pricing or any other data first
        - DO NOT try to verify the pricing exists before accessing files
        - The ContentPlugin is already configured for the current pricing context
        - Simply call the ContentPlugin functions directly
        - All file paths should start with "/" (e.g., "/slip.pdf", "/risks.xlsx")

        Available ContentPlugin functions (all collectionName parameters are optional):
        - {{{nameof(ContentPlugin.ListFiles)}}}() - List all files in the current pricing's submissions
        - {{{nameof(ContentPlugin.ListFolders)}}}() - List all folders
        - {{{nameof(ContentPlugin.ListCollectionItems)}}}() - List both files and folders
        - {{{nameof(ContentPlugin.GetDocument)}}}(documentPath) - Get document content (use path like "/Slip.md")
        - {{{nameof(ContentPlugin.SaveFile)}}}(documentPath, content) - Save a document
        - {{{nameof(ContentPlugin.DeleteFile)}}}(filePath) - Delete a file
        - {{{nameof(ContentPlugin.CreateFolder)}}}(folderPath) - Create a folder
        - {{{nameof(ContentPlugin.DeleteFolder)}}}(folderPath) - Delete a folder

        Examples:
        - User: "Show me the submission files" → You: Call {{{nameof(ContentPlugin.ListFiles)}}}()
        - User: "What files are in the submissions?" → You: Call {{{nameof(ContentPlugin.ListFiles)}}}()
        - User: "Read the slip document" → You: Call {{{nameof(ContentPlugin.GetDocument)}}}("/Slip.md")

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

    IEnumerable<KernelPlugin> IAgentWithPlugins.GetPlugins(IAgentChat chat)
    {
        yield return new DataPlugin(hub, chat, typeDefinitionMap).CreateKernelPlugin();
        yield return new LayoutAreaPlugin(hub, chat, layoutAreaMap).CreateKernelPlugin();

        // Always provide ContentPlugin - it will use ContextToConfigMap to determine the collection
        var submissionPluginConfig = CreateSubmissionPluginConfig();
        yield return new ContentPlugin(hub, submissionPluginConfig, chat).CreateKernelPlugin();
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
