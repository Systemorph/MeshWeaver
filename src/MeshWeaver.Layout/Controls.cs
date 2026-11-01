using System.Collections.Immutable;
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

    /// <summary>
    /// Creates a hand-drawn signature input bound to <paramref name="data"/> — a canvas signed with
    /// a finger, a pen or the mouse; the value is the signature as a PNG data URL.
    /// </summary>
    /// <param name="data">The bound value (a PNG data URL) or data-binding expression.</param>
    /// <returns>A new <see cref="SignaturePadControl"/>.</returns>
    public static SignaturePadControl SignaturePad(object data) => new(data);

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
    /// <returns>A new <see cref="ButtonControl"/>.</returns>
    /// <remarks>
    /// 🚨 This factory used to seed <c>Style = "button"</c> — a CSS CLASS token parked in the inline
    /// STYLE slot. Nothing ever read it (no <c>.button</c> rule ships, no client branches on it) and it
    /// was invisible only for as long as <c>ButtonView</c> dropped <see cref="UiControl.Style"/>
    /// altogether. The moment the view started forwarding the style, every un-styled button emitted
    /// <c>style="button"</c> — invalid CSS the browser discards. The style slot belongs to the caller;
    /// it is left empty here.
    /// </remarks>
    public static ButtonControl Button(object title) => new(title);

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

    /// <summary>
    /// Wraps content in the select-to-comment affordance so a reader can select text in it and
    /// anchor a comment to that text — the same anchored comments markdown nodes have, available
    /// to ANY content (a rendered post, an HTML block, a composed stack).
    /// </summary>
    /// <param name="nodePath">The node the comment is anchored to (the satellite's MainNode).</param>
    /// <param name="anchorText">
    /// The node's plain SOURCE text the selection is captured against — NOT the rendered markup.
    /// </param>
    /// <returns>
    /// A new <see cref="CommentableControl"/>; add the content with <c>.WithView(...)</c>.
    /// </returns>
    public static CommentableControl Commentable(string nodePath, string anchorText) =>
        new() { NodePath = nodePath, AnchorText = anchorText };

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

    /// <summary>The theme's muted foreground — the colour of a <see cref="Hint"/>.</summary>
    public const string HintColor = "var(--neutral-foreground-hint)";

    /// <summary>
    /// Creates a muted body label — the explanatory line under a heading, an empty state
    /// ("No sources yet."), a loading placeholder. Body typography in the theme's
    /// <see cref="HintColor"/>, so it follows light and dark mode; use it instead of a
    /// <c>&lt;p style="color: var(--neutral-foreground-hint)"&gt;</c> in <see cref="Html"/>.
    /// </summary>
    /// <param name="data">The text or data-binding expression. User-facing: pass a localized string.</param>
    /// <returns>A new <see cref="LabelControl"/> with Body typography and the hint colour.</returns>
    public static LabelControl Hint(object data) => Body(data).WithColor(HintColor);
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

    /// <summary>
    /// A horizontal, wrapping bar of navigating chips separated by →, optionally led by a
    /// <paramref name="prefix"/> breadcrumb — a journey, a pipeline, a workflow's steps. Each chip is a
    /// badge with <c>Href</c> (<see cref="Chip.ToControl"/>), never a button; colours are theme-aware
    /// mixes (<see cref="ThemePalette"/>). Pure composition over Stack, Badge and Label.
    /// </summary>
    /// <param name="chips">The chips, in order; null or empty draws an empty bar.</param>
    /// <param name="prefix">An optional breadcrumb before the first chip; skipped when it has no visible segment.</param>
    /// <returns>A <see cref="StackControl"/>.</returns>
    public static StackControl ChipBar(IEnumerable<Chip>? chips, Breadcrumb? prefix = null) =>
        ChipBarLayout.Bar(chips, prefix);

    /// <summary>A chip bar led by a plain caption ("Platform rollout:") instead of a breadcrumb —
    /// one group of chips among several on a page.</summary>
    /// <param name="caption">The caption. User-facing: pass a localized string.</param>
    /// <param name="chips">The chips, in order.</param>
    /// <returns>A <see cref="StackControl"/>.</returns>
    public static StackControl ChipBar(string caption, IEnumerable<Chip>? chips) =>
        ChipBarLayout.CaptionedBar(caption, chips);

    /// <summary>
    /// A small uppercase trail of linked segments separated by › — a page's way back up, or a chip
    /// bar's lead. Linked segments are navigating badges in the hint colour; an unlinked segment (the
    /// current page) is a label in the foreground colour; blank segments are skipped.
    /// </summary>
    /// <param name="segments">(label, href) pairs, outermost first; a null href is an unlinked segment.
    /// User-facing: pass localized labels.</param>
    /// <returns>A <see cref="StackControl"/>.</returns>
    public static StackControl Breadcrumb(params (string Label, string? Href)[] segments) =>
        global::MeshWeaver.Layout.Breadcrumb.Of(segments).ToControl();

    /// <summary>
    /// A compact one-line "label value · label value" strip for a card body. Fields with a blank value
    /// are skipped; built from Label controls — no markdown table, no markup.
    /// </summary>
    /// <param name="fields">(label, value) pairs, in order. User-facing: pass localized labels.</param>
    /// <returns>A <see cref="StackControl"/>.</returns>
    public static StackControl FieldStrip(params (string Label, string? Value)[] fields) =>
        ChipBarLayout.Strip(fields);


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

    /// <summary>Creates an icon toggle button bound to <paramref name="isPressed"/>.</summary>
    /// <param name="isPressed">The bound boolean value or data-binding expression controlling the pressed state.</param>
    /// <returns>A new <see cref="ToggleButtonControl"/>.</returns>
    public static ToggleButtonControl ToggleButton(object isPressed) =>
        new(isPressed);

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

    /// <summary>Creates an in-place audio player control for the given source.</summary>
    /// <param name="src">The audio source URL (a media file), or a data-binding expression.</param>
    /// <returns>A new <see cref="AudioControl"/>.</returns>
    public static AudioControl Audio(object src) => new(src);

    /// <summary>Creates an info / success / warning / error banner (<see cref="MessageBarControl"/>); <see cref="MessageBarIntent.Info"/> until <see cref="MessageBarControl.WithIntent"/> says otherwise.</summary>
    /// <param name="body">The message text, or a data-binding expression. User-facing: pass a localized string.</param>
    /// <returns>A new <see cref="MessageBarControl"/>.</returns>
    public static MessageBarControl MessageBar(object body) => new(body);

    /// <summary>Creates an inline text link (<see cref="LinkControl"/>); renderers refuse an unsafe scheme and mark an external address <c>rel="noopener noreferrer"</c> (<see cref="LinkHrefs"/>).</summary>
    /// <param name="text">The link text, or a data-binding expression. User-facing: pass a localized string.</param>
    /// <param name="href">The address, or a data-binding expression.</param>
    /// <returns>A new <see cref="LinkControl"/>.</returns>
    public static LinkControl Link(object text, object href) => new(text, href);

    /// <summary>Creates a QR code drawn from a module matrix the server computed (<see cref="QrCodes.Encode"/>).</summary>
    /// <param name="modules">The matrix, or a data-binding expression pointing at one.</param>
    /// <returns>A new <see cref="QrCodeControl"/>.</returns>
    public static QrCodeControl QrCode(object modules) => new(modules);

    /// <summary>Creates a QR code of <paramref name="text"/>, encoded here on the server (black on white, with the standard quiet zone).</summary>
    /// <param name="text">The text to encode — a public address, never a credential.</param>
    /// <param name="errorCorrection">One of <see cref="QrErrorCorrection"/>; <see cref="QrErrorCorrection.Medium"/> by default.</param>
    /// <returns>A new <see cref="QrCodeControl"/>.</returns>
    public static QrCodeControl QrCodeOf(string text, string errorCorrection = QrErrorCorrection.Medium) =>
        new(QrCodes.Encode(text, errorCorrection));

    /// <summary>Creates the Copy link and Share actions for <paramref name="url"/>.</summary>
    /// <param name="url">The absolute address, or a data-binding expression.</param>
    /// <returns>A new <see cref="ShareLinkControl"/>.</returns>
    public static ShareLinkControl ShareLink(object url) => new(url);

    /// <summary>Creates the Install on iPhone / Android entry point for the page it is on.</summary>
    /// <param name="manifestHref">The page's own web-app manifest URL, or a data-binding expression.</param>
    /// <returns>A new <see cref="InstallAppControl"/>.</returns>
    public static InstallAppControl InstallApp(object manifestHref) => new(manifestHref);

    /// <summary>
    /// Creates the embedded payment-method form for one open payment setup. It carries no secret:
    /// the renderer obtains the provider's one-time client secret in the buyer's own circuit.
    /// </summary>
    /// <param name="setupId">The provider's setup id, or a data-binding expression.</param>
    /// <returns>A new <see cref="PaymentSetupFormControl"/>.</returns>
    public static PaymentSetupFormControl PaymentSetupForm(object setupId) => new(setupId);

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
    /// Creates an image control — a picture, glyph or icon, sized and shaped: an image URL or
    /// <c>data:image/…</c> URI (a node's <c>content:</c> reference with <see cref="ImageControl.WithNodePath"/>),
    /// raw SVG markup (drawn only as an image, never injected), an emoji, or a Fluent icon name — the
    /// forms a node icon holds. Unsafe sources are skipped in favour of the fallback.
    /// </summary>
    /// <param name="source">The picture, glyph or icon, or a data-binding expression.</param>
    /// <param name="alt">The localized accessible name; <c>""</c> marks the image decorative.</param>
    /// <returns>A new <see cref="ImageControl"/>.</returns>
    public static ImageControl Image(object source, object alt) => new(source, alt);

    /// <summary>Creates a thin themed rule between two regions of a page (<see cref="DividerControl"/>).</summary>
    /// <returns>A new horizontal <see cref="DividerControl"/>.</returns>
    public static DividerControl Divider => new();

    /// <summary>
    /// Creates a framed document or app (<see cref="EmbedControl"/>): a PDF this portal serves by
    /// default (<see cref="EmbedKind.Document"/>), or an external page with
    /// <see cref="EmbedControl.WithKind"/> <see cref="EmbedKind.App"/>, which is always sandboxed.
    /// </summary>
    /// <param name="src">The address, or a data-binding expression.</param>
    /// <param name="title">The localized accessible name of the frame.</param>
    /// <returns>A new <see cref="EmbedControl"/>.</returns>
    public static EmbedControl Embed(object src, object title) => new(src, title);

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

    /// <summary>
    /// A <b>catalog</b>: the standard way to show one set of nodes — a search bar over the set, one
    /// hit per row rendered through the hit's OWN <paramref name="itemArea"/>, <paramref name="pageSize"/>
    /// at a time with "Load more". It is nothing but <see cref="MeshSearch"/> with the set as its
    /// hidden query (<paramref name="filter"/> plus the default <paramref name="sort"/>). There is no
    /// sort dropdown and no view-options bar: a reader re-orders or narrows by TYPING the query
    /// language into the same bar (<c>sort:name</c>), and the placeholder says so by example — the
    /// typed text follows the hidden query, so its <c>sort:</c> wins. Refine it with any
    /// <c>With…</c> of <see cref="MeshSearchControl"/> (create, columns, render mode).
    /// </summary>
    /// <param name="title">The catalog's title; also names the search placeholder.</param>
    /// <param name="filter">The set, as a query WITHOUT <c>sort:</c> (e.g. <c>nodeType:X scope:subtree partitions:all</c>).</param>
    /// <param name="itemArea">The area each hit renders through (e.g. <c>Card</c>); null for the default card.</param>
    /// <param name="sort">The default order; null means <see cref="CatalogSort.LastActivity"/>.</param>
    /// <param name="pageSize">Hits per page before "Load more".</param>
    public static MeshSearchControl Catalog(
        string title, string filter, string? itemArea = null, CatalogSort? sort = null, int pageSize = 20)
    {
        sort ??= CatalogSort.LastActivity;
        var control = MeshSearch
            .WithTitle(title)
            .WithHiddenQuery(CatalogSort.Sorted(filter, sort.Sort))
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithMaxColumns(1)
            .WithPageSize(pageSize)
            .WithPlaceholder(CatalogSort.Placeholder(title, sort))
            .WithShowSearchBox(true)
            .WithReactiveMode(true)
            .WithShowEmptyMessage(true)
            .WithShowLoadingIndicator(true);
        return itemArea is null ? control : control.WithItemArea(itemArea);
    }

    /// <summary>Creates a mesh-node picker control bound to <paramref name="data"/>.</summary>
    /// <param name="data">The bound node path value or data-binding expression.</param>
    /// <returns>A new <see cref="MeshNodePickerControl"/>.</returns>
    public static MeshNodePickerControl MeshNodePicker(object data) => new(data);

    #region Analysis views

    /// <summary>
    /// Creates a KPI strip — a wrapping row of tiles carrying a view's headline figures.
    /// A plain leaf control: compose it inside a <see cref="Stack"/> or <see cref="LayoutGrid"/>.
    /// </summary>
    /// <param name="items">The <see cref="KpiItem"/> tiles.</param>
    /// <returns>A new <see cref="KpiStripControl"/>.</returns>
    public static KpiStripControl KpiStrip(params KpiItem[] items) =>
        new(items.ToImmutableList());

    /// <summary>
    /// Creates a KPI strip over <paramref name="items"/> — a concrete
    /// <c>IEnumerable&lt;KpiItem&gt;</c>, or a data-binding reference resolving to one.
    /// </summary>
    /// <param name="items">The tiles, or a data-binding reference resolving to them.</param>
    /// <returns>A new <see cref="KpiStripControl"/>.</returns>
    public static KpiStripControl KpiStrip(object items) => new(items);

    /// <summary>
    /// Creates the vertical excess-of-loss tower over <paramref name="bands"/>: the axis is the
    /// amount, consecutive bands touch, and the retention is the base the tower stands on.
    /// </summary>
    /// <param name="bands">The <see cref="TowerBand"/> bands, or a data-binding reference resolving to them.</param>
    /// <returns>A new <see cref="TowerControl"/>.</returns>
    public static TowerControl Tower(object bands) => new(bands);

    /// <summary>
    /// Creates the vertical excess-of-loss tower over <paramref name="bands"/>, denominated in
    /// <paramref name="currency"/>.
    /// </summary>
    /// <param name="bands">The <see cref="TowerBand"/> bands, or a data-binding reference resolving to them.</param>
    /// <param name="currency">The unit the axis is denominated in.</param>
    /// <returns>A new <see cref="TowerControl"/>.</returns>
    public static TowerControl Tower(object bands, object currency) =>
        new(bands) { Currency = currency };

    /// <summary>
    /// Creates paired comparison bars — two series per measure on one shared scale, with an absent
    /// side rendered as words rather than as a zero-length bar.
    /// </summary>
    /// <param name="pairs">The <see cref="ComparisonPair"/> measures.</param>
    /// <returns>A new <see cref="ComparisonBarsControl"/>.</returns>
    public static ComparisonBarsControl ComparisonBars(params ComparisonPair[] pairs) =>
        new(pairs.ToImmutableList());

    /// <summary>
    /// Creates paired comparison bars over <paramref name="pairs"/> — a concrete
    /// <c>IEnumerable&lt;ComparisonPair&gt;</c>, or a data-binding reference resolving to one.
    /// </summary>
    /// <param name="pairs">The measures, or a data-binding reference resolving to them.</param>
    /// <returns>A new <see cref="ComparisonBarsControl"/>.</returns>
    public static ComparisonBarsControl ComparisonBars(object pairs) => new(pairs);

    #endregion
}
