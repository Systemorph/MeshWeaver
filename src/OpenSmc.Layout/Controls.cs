using System.Linq.Expressions;
using OpenSmc.Application.Styles;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataBinding;
using OpenSmc.Layout.Views;

namespace OpenSmc.Layout;

public static class Controls
{
    public static NavMenuControl NavMenu() => new();
    public static NavGroupControl NavGroup(string title) => new(title);
    public static NavLinkControl NavLink(string title, string href) => new(title, href);

    public static LayoutStackControl Stack() => new();

    public static ToolbarControl Toolbar() => new();

    public static SelectControl Select() => new();

    public static SpinnerControl Spinner() => new();

    public static TextBoxControl TextBox(object data) => new(data);

    public static NumberControl Number(object data) => new(data);

    public static DateControl Date(object data) => new(data);

    public static MenuItemControl Menu(object title) => new(title, null);

    public static MenuItemControl Menu(Icon icon) => new(null, icon);

    public static MenuItemControl Menu(object title, object icon) => new(title, icon);

    public static MenuItemControl Button(object title, object icon) =>
        new(title, icon) { Style = "button" };

    public static ExpandControl Expand(object collapsed) => new(collapsed);

    public static ExceptionControl Exception(Exception ex) => new(ex.Message, ex.GetType().Name);

    public static CodeSampleControl CodeSample(object data) => new(data);

    public static HtmlControl Html(object data) => new(data);

    public static HtmlControl Title(object text, int headerSize) =>
        Html($"<h{headerSize}>{text}</h{headerSize}>");

    public static BadgeControl Badge(object title, object subTitle = null, object color = null) =>
        new(title, subTitle, color);

    public static IconControl Icon(Icon icon, string color) => new(icon, color);

    public static CheckBoxControl CheckBox(object label, object isChecked) =>
        new(isChecked) { Label = label };

    public static SliderControl Slider(int min, int max, int step) => new(min, max, step);

    /// <summary>
    /// Control representing a progress bar
    /// </summary>
    /// <param name="message"></param>
    /// <param name="progress">Percentage between 0 and 100</param>
    /// <returns></returns>
    public static ProgressControl Progress(object message, object progress) =>
        new(message, progress);

    public static RedirectControl Redirect(object message, object address, object area) =>
        new(message, address, area);

    public static RedirectControl Redirect(LayoutAreaReference message, object address) =>
        new(message, address, message.Area);

    public record ExpandControl(object Data)
        : ExpandableUiControl<ExpandControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data);

    #region DataBinding


    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    public static UiControl Bind<T, TView>(this LayoutArea area, T data, string id, Expression<Func<T, TView>> dataTemplate)
        where TView : UiControl => BindObject(area, data, id, dataTemplate);

    internal static UiControl BindObject<T, TView>(
        this LayoutArea area,
        object data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var topLevel = UpdateData(area, data, id);
        var view = dataTemplate.Build(topLevel, out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");
        view = view with { DataContext = data };
        return view;
    }

    private static string UpdateData(LayoutArea area, object data, string id)
    {
        if (data is JsonPointerReference reference)
            return reference.Pointer;

        var topLevel = LayoutAreaReference.GetDataPointer(id);
        area.UpdateData(topLevel, data);
        return topLevel;
    }

    public static ItemTemplateControl Bind<T, TView>(
        this LayoutArea area,
        IEnumerable<T> data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl => BindEnumerable(area, data, id, dataTemplate);

    internal static ItemTemplateControl BindEnumerable<T, TView>(
        this LayoutArea area,
        object data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var topLevel = UpdateData(area, data, id);

        var view = dataTemplate.Build(topLevel, out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        return new ItemTemplateControl(view, new JsonPointerReference(topLevel));
    }

    #endregion
}
