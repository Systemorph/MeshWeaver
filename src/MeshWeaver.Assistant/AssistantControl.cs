using MeshWeaver.Layout;

namespace MeshWeaver.Assistant;

public record AssistantControl()
    : UiControl<AssistantControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object SystemMessage { get; init; }

    public IReadOnlyCollection<string> Suggestions { get; init; }

    public AssistantControl WithSystemMessage(object message)
        => this with {SystemMessage = message};

    public AssistantControl WithSuggestions(IReadOnlyCollection<string> suggestions)
        => this with {Suggestions = suggestions};
}
