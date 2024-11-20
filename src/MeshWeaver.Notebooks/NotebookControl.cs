using MeshWeaver.Layout;

namespace MeshWeaver.Notebooks;

public record NotebookControl(string FileName) : UiControl<NotebookControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{

}
