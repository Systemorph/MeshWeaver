using MeshWeaver.Domain;

namespace MeshWeaver.Layout;

public record DateControl(object Data)
    : UiControl<DateControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion); // TODO V10: Add date formatter (2023.09.07, Armen Sirotenko)

public record ExceptionControl(string Message, string Type)
    : UiControl<ExceptionControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public string StackTrace { get; init; }
}

public record CodeSampleControl(object Data)
    : UiControl<CodeSampleControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);

public record CheckBoxControl(object Data)
    : UiControl<CheckBoxControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);

public record SliderControl(int Min, int Max, int Step)
    : UiControl<SliderControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);

