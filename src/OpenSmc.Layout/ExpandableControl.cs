using OpenSmc.Application.Styles;

namespace OpenSmc.Layout;

public record TextBoxControl(object Data) : UiControl<TextBoxControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);
public record NumberControl(object Data) : UiControl<NumberControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data); // TODO V10: Add formatter somehow (2023.09.07, Armen Sirotenko)
public record DateControl(object Data) : UiControl<DateControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data); // TODO V10: Add date formatter (2023.09.07, Armen Sirotenko)

public record ExceptionControl(string Message, string Type) : UiControl<ExceptionControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public string StackTrace { get; init; }
}

//not in scope of MVP
public record CodeSampleControl(object Data) : UiControl<CodeSampleControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);
public record HtmlControl(object Data) : UiControl<HtmlControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);
public record TitleControl(object Data) : UiControl<TitleControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);
public record CheckBoxControl(object Data) : UiControl<CheckBoxControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);
public record GridControl(object Data) : UiControl<GridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);

// TODO V10: fix build (2023.09.07, Armen Sirotenko)
public record BadgeControl(object Title, object SubTitle, object Color) : UiControl<BadgeControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);

/// <summary>
/// </summary>
/// <param name="Message">String message</param>
/// <param name="Progress">Between 0 and 100</param>
public record ProgressControl(object Message, object Progress) : UiControl<BadgeControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);

public record IconControl(Icon Icon, string Color) : UiControl<IconControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public string Size { get; init; }
};

public record SliderControl(int Min, int Max, int Step) : UiControl<SliderControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);

public record RedirectControl(object Message, object RedirectAddress, object RedirectArea)
    : UiControl<RedirectControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);
