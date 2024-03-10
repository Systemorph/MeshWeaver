namespace OpenSmc.Layout.Views;

//not relevant for MVP
public record ExpandControl(object Data) : ExpandableUiControl<ExpandControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);

