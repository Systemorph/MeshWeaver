using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;

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
    /// <param name="icon">The icon of the navigation group.</param>
    /// <param name="href">The href of the navigation group.</param>
    /// <returns>A new instance of <see cref="NavGroupControl"/>.</returns>
    public static NavGroupControl NavGroup(string title, object icon = null, object href = null) => new(title, icon, href);

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
    public static NavLinkControl NavLink(object title, Icon icon, object href) => new(title, icon, href);

    /// <summary>
    /// Gets a new instance of <see cref="StackControl"/>.
    /// </summary>
    public static StackControl Stack => new();

    /// <summary>
    /// Gets a new instance of <see cref="ToolbarControl"/>.
    /// </summary>
    public static ToolbarControl Toolbar => new ToolbarControl();

    /// <summary>
    /// Creates a new instance of <see cref="SelectControl"/> with the specified item.
    /// </summary>
    /// <param name="item">The item to select.</param>
    /// <returns>A new instance of <see cref="SelectControl"/>.</returns>
    public static SelectControl Select(object item) => new(item);

    /// <summary>
    /// Creates a new instance of <see cref="ListboxControl"/> with the specified item.
    /// </summary>
    /// <param name="item">The item to list.</param>
    /// <returns>A new instance of <see cref="ListboxControl"/>.</returns>
    public static ListboxControl Listbox(object item) => new(item);

    /// <summary>
    /// Creates a new instance of <see cref="ComboboxControl"/> with the specified item.
    /// </summary>
    /// <param name="item">The item to combobox.</param>
    /// <returns>A new instance of <see cref="ComboboxControl"/>.</returns>
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

    public static LayoutAreaControl LayoutArea(object address, LayoutAreaReference reference)
    {
        var (addressType, addressId) = MessageHubExtensions.GetAddressTypeAndId(address);
        return LayoutArea(addressType, addressId, reference);
    }

    public static LayoutAreaControl LayoutArea(object address, string area, object id = null)
        => LayoutArea(address, new(area) { Id = id });

    public static LayoutAreaControl LayoutArea(string addressType, string addressId, string area, object id = null)
        => LayoutArea(addressType, addressId, new(area) { Id = id });
    public static LayoutAreaControl LayoutArea(string addressType, string addressId, LayoutAreaReference reference)
        => new(addressType, addressId, reference);


}
