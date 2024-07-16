using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Application.Styles;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataBinding;
using OpenSmc.Reflection;

namespace OpenSmc.Layout;

public static class Controls
{
    public static NavMenuControl NavMenu() => new();

    public static NavGroupControl NavGroup(string title) => new(title);

    public static NavLinkControl NavLink(string title, string href) => new(title, href);

    public static LayoutStackControl Stack() => new();

    public static LayoutStackControl Toolbar() => new LayoutStackControl().WithSkin(Skins.Toolbar);

    public static SelectControl Select(object item) => new (item);
    public static ListboxControl Listbox(object item) => new (item);
    public static ComboboxControl Combobox(object item) => new (item);

    public static SpinnerControl Spinner() => new();

    public static TextBoxControl TextBox(object data) => new(data);

    public static NumberControl Number(object data) => new(data);

    public static DateControl Date(object data) => new(data);

    public static MenuItemControl Menu(object title) => new(title, null);

    public static MenuItemControl Menu(Icon icon) => new(null, icon);

    public static MenuItemControl Menu(object title, object icon) => new(title, icon);

    public static ButtonControl Button(object title) => new(title) { Style = "button" };

    public static ExceptionControl Exception(Exception ex) => new(ex.Message, ex.GetType().Name);

    public static CodeSampleControl CodeSample(object data) => new(data);

    public static HtmlControl Html(object data) => new(data);

    public static HtmlControl Title(object text, int headerSize) =>
        Html($"<h{headerSize}>{text}</h{headerSize}>");

    public static LabelControl Label(object text) => new(text);

    public static LabelControl Body(object data) => Label(data).WithTypo(Typography.Body);
    public static LabelControl Subject(object data) => Label(data).WithTypo(Typography.Subject);
    public static LabelControl Header(object data) => Label(data).WithTypo(Typography.Header);
    public static LabelControl EmailHeader(object data) => Label(data).WithTypo(Typography.EmailHeader);
    public static LabelControl PaneHeader(object data) => Label(data).WithTypo(Typography.PaneHeader);

    public static LabelControl PageTitle(object data) => Label(data).WithTypo(Typography.PageTitle);
    public static LabelControl HeroTitle(object data) => Label(data).WithTypo(Typography.HeroTitle);
    public static LabelControl H1(object data) => Label(data).WithTypo(Typography.H1);
    public static LabelControl H2(object data) => Label(data).WithTypo(Typography.H2);
    public static LabelControl H3(object data) => Label(data).WithTypo(Typography.H3);
    public static LabelControl H4(object data) => Label(data).WithTypo(Typography.H4);
    public static LabelControl H5(object data) => Label(data).WithTypo(Typography.H5);
    public static LabelControl H6(object data) => Label(data).WithTypo(Typography.H6);

    public static BadgeControl Badge(object title, object subTitle = null, object color = null) =>
        new(title, subTitle, color);

    public static IconControl Icon(Icon icon, string color) => new(icon, color);

    public static CheckBoxControl CheckBox(object label, object isChecked) =>
        new(isChecked) { Label = label };

    public static SliderControl Slider(int min, int max, int step) => new(min, max, step);

    public static LayoutGridItemControl ToLayoutGridItem(
        this IUiControl control,
        Func<LayoutGridItemControl, LayoutGridItemControl> builder
    ) => builder.Invoke(new LayoutGridItemControl(control));
    public static BodyContentControl ToBodyContent(
        this IUiControl control,
        Func<BodyContentControl, BodyContentControl> builder
    ) => builder.Invoke(new BodyContentControl(control));
    public static BodyContentControl ToBodyContent(
        this IUiControl control
    ) => new BodyContentControl(control);

    public static LayoutGridItemControl ToLayoutGridItem(this IUiControl control) => new(control);

    public static SplitterPaneControl ToSplitterPane(
        this IUiControl control,
        Func<SplitterPaneControl, SplitterPaneControl> builder
    ) => builder.Invoke(new SplitterPaneControl(control));

    public static SplitterPaneControl ToSplitterPane(this IUiControl control) => new(control);

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

    #region DataBinding


    /// <summary>
    /// Takes expression tree of data template and replaces all property getters by binding instances and sets data context property
    /// </summary>
    [ReplaceBindMethod]
    public static UiControl Bind<T, TView>(
        this LayoutAreaHost area,
        T data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl => BindObject(area, data, id, dataTemplate);

    private class ReplaceBindMethodAttribute : ReplaceMethodInTemplateAttribute
    {
        public override MethodInfo Replace(MethodInfo method) => RelaceBindObjects(method);
    }

    private static MethodInfo RelaceBindObjects(MethodInfo method) =>
        (
            method.GetGenericMethodDefinition() switch
            {
                { } m when m == BindObjectMethod => BindObjectMethod,
                { } m when m == BindEnumerableMethod => BindEnumerableMethod,
                _ => throw new ArgumentException("Method is not supported.")
            }
        ).MakeGenericMethod(method.GetGenericArguments());

    private static readonly MethodInfo BindObjectMethod = ReflectionHelper.GetStaticMethodGeneric(
        () => BindObject<object, UiControl>(null, default, null, default)
    );

    internal static UiControl BindObject<T, TView>(
        this LayoutAreaHost area,
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
        return view;
    }

    private static string UpdateData(LayoutAreaHost area, object data, string id)
    {
        if (data is JsonPointerReference reference)
            return reference.Pointer;

        area.UpdateData(id, data);
        return LayoutAreaReference.GetDataPointer(id);
    }

    [ReplaceBindMethod]
    public static ItemTemplateControl Bind<T, TView>(
        this LayoutAreaHost area,
        IEnumerable<T> data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl => BindEnumerable(area, data, id, dataTemplate);

    public static TView Bind<T, TView>(
        this LayoutAreaHost area,
        string pointer,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl 
    {
        var view = dataTemplate.Build(pointer, out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");
        return view;
    }

    public static ItemTemplateControl BindEnumerable<T, TView>(
        this LayoutAreaHost area,
        string pointer,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var view = dataTemplate.Build("/", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        return new ItemTemplateControl(view, pointer);
    }

    private static readonly MethodInfo BindEnumerableMethod =
        ReflectionHelper.GetStaticMethodGeneric(
            () => BindEnumerable<object, UiControl>(null, default, null, default)
        );

    internal static ItemTemplateControl BindEnumerable<T, TView>(
        this LayoutAreaHost area,
        object data,
        string id,
        Expression<Func<T, TView>> dataTemplate
    )
        where TView : UiControl
    {
        var view = dataTemplate.Build("/", out var _);
        if (view == null)
            throw new ArgumentException("Data template was not specified.");

        return new ItemTemplateControl(view, UpdateData(area, data, id));
    }

    #endregion
}
