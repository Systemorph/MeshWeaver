using MeshWeaver.Layout;

namespace MeshWeaver.Kernel;

public record NotebookControl(string FileName) : UiControl<NotebookControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{

}
