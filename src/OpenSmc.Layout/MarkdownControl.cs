
namespace OpenSmc.Layout;
public record MarkdownControl(object Data)
    : UiControl<MarkdownControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);
