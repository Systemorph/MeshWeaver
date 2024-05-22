using OpenSmc.Layout.Conventions;

namespace OpenSmc.Layout;

public interface IObjectWithUiControl
{
    UiControl GetUiControl(UiControlsManager uiControlsManager);
}

public interface IPropertyWithUiControl
{
    // TODO V10: add arguments: (object instance, object propertyValue) (2023/03/24, Alexander Yolokhov)
    UiControl GetUiControl(UiControlsManager uiControlsManager);
}
