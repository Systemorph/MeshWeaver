namespace OpenSmc.Application.Layout;

public interface IObjectWithUiControl
{
    UiControl GetUiControl(IUiControlService uiControlService, IServiceProvider serviceProvider);
}

public interface IPropertyWithUiControl
{
    // TODO V10: add arguments: (object instance, object propertyValue) (2023/03/24, Alexander Yolokhov)
    UiControl GetUiControl(IUiControlService uiControlService, IServiceProvider serviceProvider);
}