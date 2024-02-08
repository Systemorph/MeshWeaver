using OpenSmc.Application.Styles;
using OpenSmc.Layout.Api;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Views;

namespace OpenSmc.Layout;

public static class Controls
{
    public static Composition.LayoutStackControl Stack() => new();
    public static SpinnerControl Spinner() => new();   
    public static TextBoxControl TextBox(object data) => new(data);
    public static NumberControl Number(object data) => new(data);
    public static DateControl Date(object data) => new(data);
    public static MenuItemControl Menu(object title) => new(title, null);
    public static MenuItemControl Menu(Icon icon) => new(null, icon);
    public static MenuItemControl Menu(object title, object icon) => new(title, icon);
    public static MenuItemControl Button(object title, object icon) => new(title, icon) { Style = "button" };

    public static ExpandControl Expand(object collapsed) => new(collapsed);

    public static ExceptionControl Exception(Exception ex) => new(ex.Message, ex.GetType().Name);
    public static CodeSampleControl CodeSample(object data) => new(data);
    public static HtmlControl HtmlView(object data) => new(data);

    public static HtmlControl Title(object text, int headerSize) => HtmlView($"<h{headerSize}>{text}</h{headerSize}>");
    public static BadgeControl Badge(object title, object subTitle = null, object color = null) => new(title, subTitle, color);
    public static IconControl Icon(Icon icon, string color) => new(icon, color);
    public static CheckBoxControl CheckBox(object label, object isChecked) => new(isChecked) { Label = label };
    public static SliderControl Slider(int min, int max, int step) => new(min, max, step);

    /// <summary>
    /// </summary>
    /// <param name="message"></param>
    /// <param name="progress">Percentage between 0 and 100</param>
    /// <returns></returns>
    public static ProgressControl Progress(object message, object progress) => new(message, progress);

    public static RedirectControl Redirect(object message, object address, object area) => new(message, address, area);
    public static RedirectControl Redirect(RefreshRequest message, object address) => new(message, address, message.Area);
    public static RemoteViewControl RemoteView(object message, object address, string area) => new(message, address, area);
    public static RemoteViewControl RemoteView(RefreshRequest message, object address) => new(message, address, message.Area);
    public static RemoteViewControl RemoteView(ViewDefinition viewDefinition, SetAreaOptions options) => new(viewDefinition, options);
    public static Composition.LayoutStackControl ApplicationWindow() => Stack().WithSkin(Skin.MainWindow); 

    public static Composition.LayoutStackControl SideMenu() => Stack().WithSkin(Skin.SideMenu);
}