using System.Collections.Immutable;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Kernel;

/// <summary>
/// A container control that provides Jupyter-like notebook functionality with code and markdown cells.
/// Cells are added as child views using WithView.
/// </summary>
public record NotebookControl()
    : ContainerControl<NotebookControl, NotebookSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    /// <summary>
    /// Gets or sets the cell controls in this notebook.
    /// This property is serialized to JSON so that cells are available on the client.
    /// </summary>
    public ImmutableList<NotebookCellControl> Cells { get; init; } = ImmutableList<NotebookCellControl>.Empty;

    /// <summary>
    /// Adds a cell to the notebook.
    /// </summary>
    public NotebookControl WithCell(NotebookCellControl cell) =>
        (this with { Cells = Cells.Add(cell) }).WithView(cell);

    /// <summary>
    /// Adds multiple cells to the notebook.
    /// </summary>
    public NotebookControl WithCells(IEnumerable<NotebookCellControl> cells)
    {
        var result = this;
        foreach (var cell in cells)
        {
            result = result.WithCell(cell);
        }
        return result;
    }

    /// <summary>
    /// Adds multiple cells to the notebook from params.
    /// </summary>
    public NotebookControl WithCells(params NotebookCellControl[] cells) =>
        WithCells((IEnumerable<NotebookCellControl>)cells);

    /// <summary>
    /// Adds a code cell to the notebook.
    /// </summary>
    public NotebookControl AddCodeCell(string content, string? language = null) =>
        WithCell(NotebookCellControl.Code(content, language ?? "csharp"));

    /// <summary>
    /// Adds a markdown cell to the notebook.
    /// </summary>
    public NotebookControl AddMarkdownCell(string content) =>
        WithCell(NotebookCellControl.Markdown(content));
}

/// <summary>
/// Skin for configuring the NotebookControl appearance and behavior.
/// </summary>
public record NotebookSkin : Skin<NotebookSkin>
{
    /// <summary>
    /// The address of the kernel hub for code execution.
    /// </summary>
    public object? KernelAddress { get; init; }

    /// <summary>
    /// The default language for new code cells.
    /// </summary>
    public object? DefaultLanguage { get; init; }

    /// <summary>
    /// Available languages for code cells.
    /// </summary>
    public object? AvailableLanguages { get; init; }

    /// <summary>
    /// Whether to show line numbers in code cells.
    /// </summary>
    public object? ShowLineNumbers { get; init; }

    /// <summary>
    /// The height of the notebook container.
    /// </summary>
    public object? Height { get; init; }

    /// <summary>
    /// Sets the kernel address for code execution.
    /// </summary>
    public NotebookSkin WithKernelAddress(Address address) =>
        this with { KernelAddress = address };

    /// <summary>
    /// Sets the default language for new code cells.
    /// </summary>
    public NotebookSkin WithDefaultLanguage(string language) =>
        this with { DefaultLanguage = language };

    /// <summary>
    /// Sets the available languages.
    /// </summary>
    public NotebookSkin WithAvailableLanguages(params string[] languages) =>
        this with { AvailableLanguages = languages };

    /// <summary>
    /// Sets whether to show line numbers.
    /// </summary>
    public NotebookSkin WithShowLineNumbers(bool show) =>
        this with { ShowLineNumbers = show };

    /// <summary>
    /// Sets the height of the notebook container.
    /// </summary>
    public NotebookSkin WithHeight(string height) =>
        this with { Height = height };
}

/// <summary>
/// Extension methods for NotebookControl.
/// </summary>
public static class NotebookControlExtensions
{
    /// <summary>
    /// Sets the kernel address for code execution.
    /// </summary>
    public static NotebookControl WithKernelAddress(this NotebookControl control, Address address) =>
        control.WithSkin(s => s.WithKernelAddress(address));

    /// <summary>
    /// Sets the default language for new code cells.
    /// </summary>
    public static NotebookControl WithDefaultLanguage(this NotebookControl control, string language) =>
        control.WithSkin(s => s.WithDefaultLanguage(language));

    /// <summary>
    /// Sets the available languages.
    /// </summary>
    public static NotebookControl WithAvailableLanguages(this NotebookControl control, params string[] languages) =>
        control.WithSkin(s => s.WithAvailableLanguages(languages));

    /// <summary>
    /// Sets whether to show line numbers.
    /// </summary>
    public static NotebookControl WithShowLineNumbers(this NotebookControl control, bool show) =>
        control.WithSkin(s => s.WithShowLineNumbers(show));

    /// <summary>
    /// Sets the height of the notebook container.
    /// </summary>
    public static NotebookControl WithHeight(this NotebookControl control, string height) =>
        control.WithSkin(s => s.WithHeight(height));
}
