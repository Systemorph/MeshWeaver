using MeshWeaver.Assistant;
using MeshWeaver.Layout.Client;

namespace MeshWeaver.Blazor.Assistant;

public static class BlazorAssistantExtensions
{
    public static LayoutClientConfiguration AddAssistant(this LayoutClientConfiguration config)
        => config.WithView<AssistantControl, AssistantView>();
}
