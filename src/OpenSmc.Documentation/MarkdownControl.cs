using OpenSmc.Documentation;
using OpenSmc.Layout;

public static class ModuleSetup
{
    public const string Version = "1.0";
    public const string ModuleName = "OpenSmc.Documentation";
}

public record MarkdownControl(object Data)
    : UiControl<MarkdownControl>(ModuleSetup.ModuleName, ModuleSetup.Version, Data);
