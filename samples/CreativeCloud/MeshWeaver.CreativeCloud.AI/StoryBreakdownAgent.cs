using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.CreativeCloud.Domain;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.CreativeCloud.AI;

/// <summary>
/// Agent that breaks down stories into LinkedIn posts, video scripts, and other content formats
/// based on the author's content archetype.
/// </summary>
[ExposedInDefaultAgent]
public class StoryBreakdownAgent(IMessageHub hub) : IInitializableAgent, IAgentWithTools, IAgentWithContext
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private Dictionary<string, LayoutAreaDefinition>? layoutAreaMap;

    /// <inheritdoc/>
    public string Name => "StoryBreakdownAgent";

    /// <inheritdoc/>
    public string? GroupName => "CreativeCloud";

    /// <inheritdoc/>
    public int DisplayOrder => 0;

    /// <inheritdoc/>
    public string? IconName => "DocumentSplit";

    /// <inheritdoc/>
    public string Description =>
        "Breaks down stories into LinkedIn posts, video scripts, and other content formats based on the author's content archetype.";

    /// <inheritdoc/>
    public string Instructions =>
        $$$"""
        You are the StoryBreakdownAgent, specialized in helping content creators break down their stories into multiple content pieces.

        ## Your Capabilities

        Given a story, you will:
        1. Analyze the story content and identify key messages and themes
        2. Reference the author's content archetype for tone, style, and content pillars
        3. Generate LinkedIn posts aligned with the content pillars (Tactical, Aspirational, Insightful, Personal)
        4. Create video script outlines
        5. Suggest event/talk topics

        ## Content Pillars

        When creating content, align each piece with one of the four content pillars:
        - **Tactical**: Actionable, how-to content with immediate value
        - **Aspirational**: Success stories, case studies, and transformation narratives
        - **Insightful**: Thought leadership, analysis, and industry perspectives
        - **Personal**: Authentic stories, reflections, and behind-the-scenes content

        ## Working with Data

        Use the DataPlugin to:
        - Get stories: {{{nameof(DataPlugin.GetData)}}}(type: "Story", entityId: null)
        - Get a specific story: {{{nameof(DataPlugin.GetData)}}}(type: "Story", entityId: "story-id")
        - Get content archetypes: {{{nameof(DataPlugin.GetData)}}}(type: "ContentArchetype", entityId: null)
        - Get content lenses: {{{nameof(DataPlugin.GetData)}}}(type: "ContentLens", entityId: null)
        - Get persons: {{{nameof(DataPlugin.GetData)}}}(type: "Person", entityId: null)
        - Create posts: {{{nameof(DataPlugin.UpdateData)}}}(type: "Post", entities: [...])
        - Create videos: {{{nameof(DataPlugin.UpdateData)}}}(type: "Video", entities: [...])
        - Create events: {{{nameof(DataPlugin.UpdateData)}}}(type: "Event", entities: [...])

        ## Workflow

        1. When a user asks to break down a story:
           - First, get the story content using GetData
           - Get the author's content archetype and content lenses
           - Analyze the story themes and identify which content lenses apply
           - Generate content pieces for each relevant pillar

        2. For LinkedIn posts:
           - Create a hook that grabs attention
           - Include actionable insights or emotional connection
           - End with engagement (question or call-to-action)
           - Keep under 3000 characters

        3. For video scripts:
           - Create an outline with introduction, main points, and conclusion
           - Suggest talking points for each section
           - Recommend video length based on platform (YouTube: 8-15 min, TikTok/Reels: 30-60 sec)

        4. For events:
           - Suggest event type (Webinar, Workshop, Meetup, Conference talk)
           - Create a compelling title and description
           - Outline key topics to cover
        """;

    /// <inheritdoc/>
    IEnumerable<AITool> IAgentWithTools.GetTools(IAgentChat chat)
    {
        var dataPlugin = new DataPlugin(hub, chat, typeDefinitionMap);
        foreach (var tool in dataPlugin.CreateTools())
            yield return tool;

        var layoutPlugin = new LayoutAreaPlugin(hub, chat, layoutAreaMap);
        foreach (var tool in layoutPlugin.CreateTools())
            yield return tool;
    }

    /// <inheritdoc/>
    async Task IInitializableAgent.InitializeAsync()
    {
        try
        {
            var typesResponse = await hub.AwaitResponse(
                new GetDomainTypesRequest(),
                o => o.WithTarget(CreativeCloudApplicationAttribute.Address));
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
                o => o.WithTarget(CreativeCloudApplicationAttribute.Address));
            layoutAreaMap = layoutAreaResponse?.Message?.Areas?.ToDictionary(x => x.Area);
        }
        catch
        {
            layoutAreaMap = null;
        }
    }

    /// <inheritdoc/>
    public bool Matches(AgentContext? context)
    {
        if (context?.Address == null)
            return false;

        return context.Address.ToString().Contains("CreativeCloud");
    }
}
