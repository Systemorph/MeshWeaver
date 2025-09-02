using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.DataGrid;

namespace MeshWeaver.Layout;

/// <summary>
/// Provides static methods to create various UI controls.
/// </summary>
public static class Controls
{
    /// <summary>
    /// Gets a new instance of <see cref="SplitterControl"/>.
    /// </summary>
    public static SplitterControl Splitter => new();

    /// <summary>
    /// Gets a new instance of <see cref="LayoutControl"/>.
    /// </summary>
    public static LayoutControl Layout => new();

    /// <summary>
    /// Gets a new instance of <see cref="LayoutGridControl"/>.
    /// </summary>
    public static LayoutGridControl LayoutGrid => new();

    /// <summary>
    /// Creates a new instance of <see cref="NavGroupControl"/> with the specified title, icon, and href.
    /// </summary>
    /// <param name="title">The title of the navigation group.</param>
    /// <returns>A new instance of <see cref="NavGroupControl"/>.</returns>
    public static NavGroupControl NavGroup(string title) => new(title);

    /// <summary>
    /// Gets a new instance of <see cref="TabsControl"/>.
    /// </summary>
    public static TabsControl Tabs => new();

    /// <summary>
    /// Gets a new instance of <see cref="NavMenuControl"/>.
    /// </summary>
    public static NavMenuControl NavMenu => new();

    /// <summary>
    /// Creates a new instance of <see cref="NavLinkControl"/> with the specified title and href.
    /// </summary>
    /// <param name="title">The title of the navigation link.</param>
    /// <param name="href">The href of the navigation link.</param>
    /// <returns>A new instance of <see cref="NavLinkControl"/>.</returns>
    public static NavLinkControl NavLink(object title, object href) => new(title, null, href);

    /// <summary>
    /// Creates a new instance of <see cref="NavLinkControl"/> with the specified title, icon, and href.
    /// </summary>
    /// <param name="title">The title of the navigation link.</param>
    /// <param name="icon">The icon of the navigation link.</param>
    /// <param name="href">The href of the navigation link.</param>
    /// <returns>A new instance of <see cref="NavLinkControl"/>.</returns>
    public static NavLinkControl NavLink(object title, Icon? icon, object href) => new(title, icon, href);

    /// <summary>
    /// Gets a new instance of <see cref="StackControl"/>.
    /// </summary>
    public static StackControl Stack => new();

    /// <summary>
    /// Gets a new instance of <see cref="ToolbarControl"/>.
    /// </summary>
    public static ToolbarControl Toolbar => new();

    /// <summary>
    /// Creates a new instance of <see cref="SelectControl"/> with the specified item.
    /// </summary>
    /// <param name="item">The item to select.</param>
    /// <param name="options">Options to select from.</param>
    /// <returns>A new instance of <see cref="SelectControl"/>.</returns>
    public static SelectControl Select(object item, object options) => new(item, options);

    /// <summary>
    /// Creates a new instance of <see cref="ListboxControl"/> with the specified item.
    /// </summary>
    /// <param name="item">The item to list.</param>
    /// <param name="options">Options to select from.</param>
    /// <returns>A new instance of <see cref="ListboxControl"/>.</returns>
    public static ListboxControl Listbox(object item, object options) => new(item, options);

    /// <summary>
    /// Creates a new instance of <see cref="ComboboxControl"/> with the specified item.
    /// </summary>
    /// <param name="item">The item to combobox.</param>
    /// <param name="options">Options to select from.</param>
    /// <returns>A new instance of <see cref="ComboboxControl"/>.</returns>
    public static ComboboxControl Combobox(object item, object options) => new(item, options);
    public static TextFieldControl Text(object data) => new(data);

    public static NumberFieldControl Number(object data, object type) => new(data, type);

    public static DateControl Date(object data) => new(data);

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


    public static CheckBoxControl CheckBox(object isChecked) =>
        new(isChecked);

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

    public static MarkdownControl Markdown(object data) => new MarkdownControl(data);

    public static DataGridControl DataGrid(object elements)
    => new(elements);

    public static DateTimeControl DateTime(object data)
    => new(data);

    public static IconControl Icon(object data) => new(data);

    /// <summary>
    /// Creates a new instance of <see cref="MenuItemControl"/> with the specified title and icon.
    /// </summary>
    /// <param name="title">The title of the menu item.</param>
    /// <param name="icon">The icon of the menu item.</param>
    /// <returns>A new instance of <see cref="MenuItemControl"/>.</returns>
    public static MenuItemControl MenuItem(object title, object? icon = null) => new(title, icon ?? (object)"");

    /// <summary>
    /// Creates a new instance of <see cref="DialogControl"/> with the specified content and title.
    /// </summary>
    /// <param name="content">The content to display in the dialog.</param>
    /// <param name="title">The title of the dialog.</param>
    /// <returns>A new instance of <see cref="DialogControl"/>.</returns>
    public static DialogControl Dialog(object content, object? title = null) => new(content) { Title = title! };

    public static LayoutAreaControl LayoutArea(object address, string area, object? id = null)
        => LayoutArea(address, new LayoutAreaReference(area) { Id = id! });
    public static LayoutAreaControl LayoutArea(object address, LayoutAreaReference reference)
        => new(address, reference);


    public static RadioGroupControl RadioGroup(object data, object options, object type)
        => new(data, options, type);

    public static FileBrowserControl FileBrowser(object collection, object? path = null)
        => new(collection) { Path = path! };
}
