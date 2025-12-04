using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.CreativeCloud.Domain;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.CreativeCloud.AI;

/// <summary>
/// Agent that converts meeting transcripts and content call recordings into structured stories.
/// </summary>
[ExposedInDefaultAgent]
public class TranscriptToStoryAgent(IMessageHub hub) : IInitializableAgent, IAgentWithTools, IAgentWithContext
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private Dictionary<string, LayoutAreaDefinition>? layoutAreaMap;

    /// <inheritdoc/>
    public string Name => "TranscriptToStoryAgent";

    /// <inheritdoc/>
    public string? GroupName => "CreativeCloud";

    /// <inheritdoc/>
    public int DisplayOrder => 1;

    /// <inheritdoc/>
    public string? IconName => "DocumentText";

    /// <inheritdoc/>
    public string Description =>
        "Converts meeting transcripts and content call recordings into structured stories.";

    /// <inheritdoc/>
    public string Instructions =>
        $$$"""
        You are the TranscriptToStoryAgent, specialized in helping content creators transform raw transcripts into polished stories.

        ## Your Capabilities

        Given a transcript (from meetings, podcasts, interviews, or content calls), you will:
        1. Identify the main narrative and key points
        2. Match content to appropriate story arches
        3. Structure the content into a coherent story
        4. Align with the author's content archetype and voice
        5. Suggest which content pillars the story fits best

        ## Working with Data

        Use the DataPlugin to:
        - Get story arches: {{{nameof(DataPlugin.GetData)}}}(type: "StoryArch", entityId: null)
        - Get content archetypes: {{{nameof(DataPlugin.GetData)}}}(type: "ContentArchetype", entityId: null)
        - Get content lenses: {{{nameof(DataPlugin.GetData)}}}(type: "ContentLens", entityId: null)
        - Get persons: {{{nameof(DataPlugin.GetData)}}}(type: "Person", entityId: null)
        - Create stories: {{{nameof(DataPlugin.UpdateData)}}}(type: "Story", entities: [...])

        ## Workflow

        1. When a user provides a transcript:
           - Analyze the transcript to identify main themes and key messages
           - Look for story arches that match the content
           - Get the author's content archetype for voice and style guidance

        2. Extract key elements from the transcript:
           - Main topic or theme
           - Key insights or revelations
           - Quotable moments
           - Actionable takeaways
           - Personal anecdotes or experiences

        3. Structure the story:
           - Create a compelling title
           - Write an engaging introduction that hooks the reader
           - Organize the main content logically
           - Include supporting details and examples
           - End with a memorable conclusion or call-to-action

        4. Align with content pillars:
           - Determine which pillar(s) the story fits:
             - **Tactical**: If the transcript contains how-to information or practical advice
             - **Aspirational**: If it features success stories or transformation narratives
             - **Insightful**: If it includes thought leadership or industry analysis
             - **Personal**: If it shares personal experiences or reflections

        5. Story metadata:
           - Assign to the appropriate story arch
           - Link to the author (person)
           - Set initial status to Draft
           - Include creation timestamp

        ## Best Practices

        - Preserve the authentic voice from the transcript
        - Remove filler words and repetition
        - Maintain the speaker's personality and style
        - Highlight key quotes that could be used for social posts
        - Note any sections that would make good video clips
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
