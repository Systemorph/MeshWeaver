namespace OpenSmc.Layout.Api;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class SliderAttribute : Attribute, IPropertyWithUiControl
{
    public int Min { get; }
    public int Max { get; }
    public int Step { get; }

    public SliderAttribute(int min, int max, int step = 1)
    {
        Min = min;
        Max = max;
        Step = step;
    }

    UiControl IPropertyWithUiControl.GetUiControl(UiControlsManager uiControlsManager)
    {
        UiControl control = Controls.Slider(Min, Max, Step);
        
        return control;
    }
}