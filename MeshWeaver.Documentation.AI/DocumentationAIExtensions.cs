using System.ComponentModel;
using MeshWeaver.AI;
using Microsoft.Extensions.AI;

namespace MeshWeaver.Documentation.AI;

public static class DocumentationAIExtensions
{
    public static AIConfiguration AddDocumentationAI(this AIConfiguration config)
    {
        return config.WithChatOptionEnrichment((options,_) =>
        {
            options.Tools = GetTools().ToArray();
        });
    }

    private static IEnumerable<AITool> GetTools()
    {
        yield return AIFunctionFactory.Create(new DocumentationAIPlugin().Moments);
    }
}


public class DocumentationAIPlugin
{
    [Description("Is triggered when saying 'Give me Moments'.")]
    public async IAsyncEnumerable<string> Moments()
    {
        yield return "Starting";
        for (int i = 1; i < 10; i++)
        {
            await Task.Delay(1000);
            yield return "Moment " + i;
        }
        yield return "Finished";

    }
}
