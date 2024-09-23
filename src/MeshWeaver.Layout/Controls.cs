using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.DataGrid;

namespace MeshWeaver.Layout;

public static class Controls
{
    public static SplitterControl Splitter => new();
    public static LayoutControl Layout => new();
    public static LayoutGridControl LayoutGrid => new();

    public static NavGroupControl NavGroup(string title, object icon=null, object href=null) => new(title, icon, href);
    public static TabsControl Tabs => new();
    public static NavMenuControl NavMenu => new();

    public static NavLinkControl NavLink(object title, object href) => new(title, null, href);
    public static NavLinkControl NavLink(object title, Icon icon, object href) => new(title, icon, href);

    public static LayoutStackControl Stack => new();

    public static ToolbarControl Toolbar => new ToolbarControl();

    public static SelectControl Select(object item) => new(item);
    public static ListboxControl Listbox(object item) => new(item);
    public static ComboboxControl Combobox(object item) => new(item);

    public static TextFieldControl Text(object data) => new(data);

    public static NumberFieldControl Number(object data, string type) => new(data, type);

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

    public static NamedAreaControl NamedArea(object area) => new(area);

    // TODO V10: factor out into separate static class Labels (25.07.2024, Roland Bürgi)
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

    public static BadgeControl Badge(object data) => new(data);


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


    public static SpacerControl Spacer => new();
    public static EditFormControl EditForm => new();

    public static MarkdownControl Markdown(object data) => new MarkdownControl(data);

    public static DataGridControl DataGrid(object elements)
    => new(elements);

    public static DateTimeControl DateTime(object data)
    => new(data);

    public static IconControl Icon(object data) => new(data);
}
