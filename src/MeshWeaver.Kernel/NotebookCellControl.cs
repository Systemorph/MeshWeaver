using MeshWeaver.Layout;

namespace MeshWeaver.Kernel;

/// <summary>
/// Represents the type of a notebook cell.
/// </summary>
public enum NotebookCellType
{
    /// <summary>
    /// A code cell that can be executed.
    /// </summary>
    Code,

    /// <summary>
    /// A markdown cell for documentation.
    /// </summary>
    Markdown
}

/// <summary>
/// Represents the output of a code cell execution.
/// </summary>
public record NotebookCellOutput
{
    /// <summary>
    /// The result value from execution.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Whether the execution was successful.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Standard output from execution.
    /// </summary>
    public string? StandardOutput { get; init; }
}

/// <summary>
/// A control representing a single cell in a notebook.
/// Contains code or markdown content with optional execution output.
/// </summary>
public record NotebookCellControl()
    : UiControl<NotebookCellControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The content/source code of the cell.
    /// </summary>
    public object? Content { get; init; }

    /// <summary>
    /// The output from the last execution.
    /// </summary>
    public object? Output { get; init; }

    /// <summary>
    /// Creates a new code cell with the specified content.
    /// </summary>
    public static NotebookCellControl Code(string content, string language = "csharp") => new NotebookCellControl()
        .WithContent(content)
        .WithCellType(NotebookCellType.Code)
        .WithLanguage(language);

    /// <summary>
    /// Creates a new markdown cell with the specified content.
    /// </summary>
    public static NotebookCellControl Markdown(string content) => new NotebookCellControl()
        .WithContent(content)
        .WithCellType(NotebookCellType.Markdown)
        .WithLanguage("markdown");

    /// <summary>
    /// Sets the content of the cell.
    /// </summary>
    public NotebookCellControl WithContent(string content) =>
        this with { Content = content };

    /// <summary>
    /// Sets the output of the cell.
    /// </summary>
    public NotebookCellControl WithOutput(NotebookCellOutput? output) =>
        this with { Output = output };

    /// <summary>
    /// Gets or sets the NotebookCellSkin, ensuring only one exists.
    /// </summary>
    private NotebookCellControl WithCellSkin(Func<NotebookCellSkin, NotebookCellSkin> config)
    {
        var existingSkin = Skins.OfType<NotebookCellSkin>().FirstOrDefault() ?? new NotebookCellSkin();
        var newSkin = config(existingSkin);
        var newSkins = Skins.RemoveAll(s => s is NotebookCellSkin).Add(newSkin);
        return this with { Skins = newSkins };
    }

    /// <summary>
    /// Sets the cell ID.
    /// </summary>
    public NotebookCellControl WithCellId(string id) =>
        WithCellSkin(s => s.WithCellId(id));

    /// <summary>
    /// Sets the cell type.
    /// </summary>
    public NotebookCellControl WithCellType(NotebookCellType cellType) =>
        WithCellSkin(s => s.WithCellType(cellType));

    /// <summary>
    /// Sets the language.
    /// </summary>
    public NotebookCellControl WithLanguage(string language) =>
        WithCellSkin(s => s.WithLanguage(language));

    /// <summary>
    /// Sets whether the cell is executing.
    /// </summary>
    public NotebookCellControl WithIsExecuting(bool isExecuting) =>
        WithCellSkin(s => s.WithIsExecuting(isExecuting));

    /// <summary>
    /// Sets the execution count.
    /// </summary>
    public NotebookCellControl WithExecutionCount(int? count) =>
        WithCellSkin(s => s.WithExecutionCount(count));
}

/// <summary>
/// Skin for configuring the NotebookCellControl appearance and behavior.
/// </summary>
public record NotebookCellSkin : Skin<NotebookCellSkin>
{
    /// <summary>
    /// Unique identifier for the cell.
    /// </summary>
    public object? CellId { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The type of cell (Code or Markdown).
    /// </summary>
    public object? CellType { get; init; } = NotebookCellType.Code;

    /// <summary>
    /// The programming language for code cells (e.g., "csharp", "python").
    /// </summary>
    public object? Language { get; init; } = "csharp";

    /// <summary>
    /// Whether the cell is currently executing.
    /// </summary>
    public object? IsExecuting { get; init; }

    /// <summary>
    /// Execution order number (for display purposes).
    /// </summary>
    public object? ExecutionCount { get; init; }

    /// <summary>
    /// Sets the cell ID.
    /// </summary>
    public NotebookCellSkin WithCellId(string id) =>
        this with { CellId = id };

    /// <summary>
    /// Sets the cell type.
    /// </summary>
    public NotebookCellSkin WithCellType(NotebookCellType cellType) =>
        this with { CellType = cellType };

    /// <summary>
    /// Sets the language.
    /// </summary>
    public NotebookCellSkin WithLanguage(string language) =>
        this with { Language = language };

    /// <summary>
    /// Sets whether the cell is executing.
    /// </summary>
    public NotebookCellSkin WithIsExecuting(bool isExecuting) =>
        this with { IsExecuting = isExecuting };

    /// <summary>
    /// Sets the execution count.
    /// </summary>
    public NotebookCellSkin WithExecutionCount(int? count) =>
        this with { ExecutionCount = count };
}
