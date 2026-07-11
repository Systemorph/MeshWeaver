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
    /// <summary>Creates a single-line text input control bound to <paramref name="data"/>.</summary>
    /// <param name="data">The bound value or data-binding expression for the text field.</param>
    /// <returns>A new <see cref="TextFieldControl"/>.</returns>
    public static TextFieldControl Text(object data) => new(data);

    /// <summary>Creates a numeric input control bound to <paramref name="data"/>.</summary>
    /// <param name="data">The bound numeric value or data-binding expression.</param>
    /// <param name="type">The numeric type (e.g. integer, decimal) that constrains input.</param>
    /// <returns>A new <see cref="NumberFieldControl"/>.</returns>
    public static NumberFieldControl Number(object data, object type) => new(data, type);

    /// <summary>Creates a date picker control bound to <paramref name="data"/>.</summary>
    /// <param name="data">The bound date value or data-binding expression.</param>
    /// <returns>A new <see cref="DateControl"/>.</returns>
    public static DateControl Date(object data) => new(data);

    /// <summary>Creates a clickable button labelled with <paramref name="title"/>.</summary>
    /// <param name="title">The button label text or content.</param>
    /// <returns>A new <see cref="ButtonControl"/> with the "button" style applied.</returns>
    public static ButtonControl Button(object title) => new(title) { Style = "button" };

    /// <summary>Creates a draggable wrapper around <paramref name="content"/> carrying <paramref name="payload"/>.</summary>
    /// <param name="content">The control to make draggable.</param>
    /// <param name="payload">The payload delivered to the drop target when this element is dropped.</param>
    /// <returns>A new <see cref="DraggableControl"/>.</returns>
    public static DraggableControl Draggable(UiControl content, object payload) => new(content, payload);

    /// <summary>Creates a drop target that renders <paramref name="content"/> as its drop zone.</summary>
    /// <param name="content">The control rendered inside the drop zone.</param>
    /// <returns>A new <see cref="DropTargetControl"/>; attach a handler with <see cref="DropTargetControl.WithDropAction(System.Action{DropContext})"/>.</returns>
    public static DropTargetControl DropTarget(UiControl content) => new(content);

    /// <summary>Creates a control that displays the message and type of <paramref name="ex"/>.</summary>
    /// <param name="ex">The exception whose message and type name are rendered.</param>
    /// <returns>A new <see cref="ExceptionControl"/>.</returns>
    public static ExceptionControl Exception(Exception ex) => new(ex.Message, ex.GetType().Name);

    /// <summary>Creates a syntax-highlighted code sample control.</summary>
    /// <param name="data">The source code content or data-binding expression.</param>
    /// <returns>A new <see cref="CodeSampleControl"/>.</returns>
    public static CodeSampleControl CodeSample(object data) => new(data);

    /// <summary>Creates a control that renders pre-built HTML content verbatim. Use only for genuinely pre-rendered markup; use layout controls for structured data.</summary>
    /// <param name="data">The raw HTML string or data-binding expression.</param>
    /// <returns>A new <see cref="HtmlControl"/>.</returns>
    public static HtmlControl Html(object data) => new(data);

    /// <summary>Creates an HTML heading element at the specified level wrapping <paramref name="text"/>.</summary>
    /// <param name="text">The heading content.</param>
    /// <param name="headerSize">The heading level (1–6), producing &lt;h1&gt;…&lt;h6&gt;.</param>
    /// <returns>A new <see cref="HtmlControl"/> containing the heading markup.</returns>
    public static HtmlControl Title(object text, int headerSize) =>
        Html($"<h{headerSize}>{text}</h{headerSize}>");

    /// <summary>Creates a control that renders a named layout area identified by <paramref name="area"/>.</summary>
    /// <param name="area">The area name or data-binding expression resolving to an area name.</param>
    /// <returns>A new <see cref="NamedAreaControl"/>.</returns>
    public static NamedAreaControl NamedArea(object area) => new(area);

    // TODO V10: factor out into separate static class Labels (25.07.2024, Roland Bürgi)
    /// <summary>Creates a label control displaying <paramref name="text"/>.</summary>
    /// <param name="text">The label content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/>.</returns>
    public static LabelControl Label(object text) => new(text);

    /// <summary>Creates a label styled as body text.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with Body typography.</returns>
    public static LabelControl Body(object data) => Label(data).WithTypo(Typography.Body);
    /// <summary>Creates a label styled as a subject line.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with Subject typography.</returns>
    public static LabelControl Subject(object data) => Label(data).WithTypo(Typography.Subject);
    /// <summary>Creates a label styled as a section header.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with Header typography.</returns>
    public static LabelControl Header(object data) => Label(data).WithTypo(Typography.Header);
    /// <summary>Creates a label styled as an email header.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with EmailHeader typography.</returns>
    public static LabelControl EmailHeader(object data) => Label(data).WithTypo(Typography.EmailHeader);
    /// <summary>Creates a label styled as a pane header.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with PaneHeader typography.</returns>
    public static LabelControl PaneHeader(object data) => Label(data).WithTypo(Typography.PaneHeader);

    /// <summary>Creates a label styled as a page title.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with PageTitle typography.</returns>
    public static LabelControl PageTitle(object data) => Label(data).WithTypo(Typography.PageTitle);
    /// <summary>Creates a label styled as a hero (large display) title.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with HeroTitle typography.</returns>
    public static LabelControl HeroTitle(object data) => Label(data).WithTypo(Typography.HeroTitle);
    /// <summary>Creates a label styled as an H1 heading.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with H1 typography.</returns>
    public static LabelControl H1(object data) => Label(data).WithTypo(Typography.H1);
    /// <summary>Creates a label styled as an H2 heading.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with H2 typography.</returns>
    public static LabelControl H2(object data) => Label(data).WithTypo(Typography.H2);
    /// <summary>Creates a label styled as an H3 heading.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with H3 typography.</returns>
    public static LabelControl H3(object data) => Label(data).WithTypo(Typography.H3);
    /// <summary>Creates a label styled as an H4 heading.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with H4 typography.</returns>
    public static LabelControl H4(object data) => Label(data).WithTypo(Typography.H4);
    /// <summary>Creates a label styled as an H5 heading.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with H5 typography.</returns>
    public static LabelControl H5(object data) => Label(data).WithTypo(Typography.H5);
    /// <summary>Creates a label styled as an H6 heading.</summary>
    /// <param name="data">The content or data-binding expression.</param>
    /// <returns>A new <see cref="LabelControl"/> with H6 typography.</returns>
    public static LabelControl H6(object data) => Label(data).WithTypo(Typography.H6);

    /// <summary>Creates a badge control displaying <paramref name="data"/> as a small status indicator.</summary>
    /// <param name="data">The badge content or data-binding expression.</param>
    /// <returns>A new <see cref="BadgeControl"/>.</returns>
    public static BadgeControl Badge(object data) => new(data);


    /// <summary>Creates a checkbox input control bound to <paramref name="isChecked"/>.</summary>
    /// <param name="isChecked">The bound boolean value or data-binding expression controlling checked state.</param>
    /// <returns>A new <see cref="CheckBoxControl"/>.</returns>
    public static CheckBoxControl CheckBox(object isChecked) =>
        new(isChecked);

    /// <summary>Creates a toggle switch control bound to <paramref name="isChecked"/>.</summary>
    /// <param name="isChecked">The bound boolean value or data-binding expression controlling on/off state.</param>
    /// <returns>A new <see cref="SwitchControl"/>.</returns>
    public static SwitchControl Switch(object isChecked) =>
        new(isChecked);

    /// <summary>Creates a range slider control with the given bounds and step.</summary>
    /// <param name="min">The minimum selectable value.</param>
    /// <param name="max">The maximum selectable value.</param>
    /// <param name="step">The increment between selectable values.</param>
    /// <returns>A new <see cref="SliderControl"/>.</returns>
    public static SliderControl Slider(int min, int max, int step) => new(min, max, step);

    /// <summary>
    /// Control representing a progress bar
    /// </summary>
    /// <param name="message"></param>
    /// <param name="progress">Percentage between 0 and 100</param>
    /// <returns></returns>
    public static ProgressControl Progress(object message, object progress) =>
        new(message, progress);


    /// <summary>Gets a new spacer control that fills available space in a flex or stack layout.</summary>
    public static SpacerControl Spacer => new();

    /// <summary>Creates a Markdown rendering control for the given content.</summary>
    /// <param name="data">The Markdown text or data-binding expression to render.</param>
    /// <returns>A new <see cref="MarkdownControl"/>.</returns>
    public static MarkdownControl Markdown(object data) => new(data);

    /// <summary>Creates a video player control for the given source.</summary>
    /// <param name="src">The video source URL (a media file; or an embed URL combined with <see cref="VideoControl.WithKind"/> <see cref="VideoKind.Embed"/>).</param>
    /// <returns>A new <see cref="VideoControl"/>.</returns>
    public static VideoControl Video(object src) => new(src);

    /// <summary>Creates a data grid control that renders <paramref name="elements"/> as a tabular view.</summary>
    /// <param name="elements">A collection or data-binding expression providing the rows.</param>
    /// <returns>A new <see cref="DataGridControl"/>.</returns>
    public static DataGridControl DataGrid(object elements)
    => new(elements);

    /// <summary>Creates a date-and-time picker control bound to <paramref name="data"/>.</summary>
    /// <param name="data">The bound date-time value or data-binding expression.</param>
    /// <returns>A new <see cref="DateTimeControl"/>.</returns>
    public static DateTimeControl DateTime(object data)
    => new(data);

    /// <summary>Creates an icon control displaying the icon described by <paramref name="data"/>.</summary>
    /// <param name="data">The icon name, URL, or data-binding expression.</param>
    /// <returns>A new <see cref="IconControl"/>.</returns>
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

    /// <summary>Creates a layout area control that embeds the layout area identified by <paramref name="area"/> from the hub at <paramref name="address"/>.</summary>
    /// <param name="address">The hub address that owns the layout area.</param>
    /// <param name="area">The area name within the hub.</param>
    /// <param name="id">An optional identifier passed to the area reference.</param>
    /// <returns>A new <see cref="LayoutAreaControl"/>.</returns>
    public static LayoutAreaControl LayoutArea(object address, string area, object? id = null)
        => LayoutArea(address, new LayoutAreaReference(area) { Id = id! });
    /// <summary>Creates a layout area control using an explicit <paramref name="reference"/> from the hub at <paramref name="address"/>.</summary>
    /// <param name="address">The hub address that owns the layout area.</param>
    /// <param name="reference">The fully specified layout area reference.</param>
    /// <returns>A new <see cref="LayoutAreaControl"/>.</returns>
    public static LayoutAreaControl LayoutArea(object address, LayoutAreaReference reference)
        => new(address, reference);


    /// <summary>Creates a radio-button group control bound to <paramref name="data"/>.</summary>
    /// <param name="data">The bound selected-value or data-binding expression.</param>
    /// <param name="options">The collection or data-binding expression providing the available options.</param>
    /// <param name="type">The option type descriptor used for rendering.</param>
    /// <returns>A new <see cref="RadioGroupControl"/>.</returns>
    public static RadioGroupControl RadioGroup(object data, object options, object type)
        => new(data, options, type);

    /// <summary>Creates a file browser control for navigating <paramref name="collection"/>.</summary>
    /// <param name="collection">The storage collection or data-binding expression to browse.</param>
    /// <param name="path">An optional initial path within the collection.</param>
    /// <returns>A new <see cref="FileBrowserControl"/>.</returns>
    public static FileBrowserControl FileBrowser(object collection, object? path = null)
        => new(collection) { Path = path! };

    /// <summary>Creates a redirect control that navigates the client to <paramref name="href"/>.</summary>
    /// <param name="href">The target URL or data-binding expression resolving to a URL.</param>
    /// <returns>A new <see cref="RedirectControl"/>.</returns>
    public static RedirectControl Redirect(object href) => new(href);

    /// <summary>Gets a new free-text search box control.</summary>
    public static SearchBoxControl SearchBox => new();

    /// <summary>Gets a new mesh-node search control that searches across the mesh.</summary>
    public static MeshSearchControl MeshSearch => new();

    /// <summary>Creates a mesh-node picker control bound to <paramref name="data"/>.</summary>
    /// <param name="data">The bound node path value or data-binding expression.</param>
    /// <returns>A new <see cref="MeshNodePickerControl"/>.</returns>
    public static MeshNodePickerControl MeshNodePicker(object data) => new(data);
}
