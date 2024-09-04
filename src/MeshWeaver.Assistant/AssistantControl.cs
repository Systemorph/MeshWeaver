using MeshWeaver.Layout;

namespace MeshWeaver.Assistant;

public record AssistantControl()
    : UiControl<AssistantControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
}
