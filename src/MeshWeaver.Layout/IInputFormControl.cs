namespace MeshWeaver.Layout;

/// <summary>
/// Interface for input controls.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/input">Fluent UI Blazor Input documentation</a>.
/// </remarks>
public interface IInputFormControl : IFormControl
{
    /// <summary>
    /// Maximum length for the form control input.
    /// </summary>
    object MaxLength { get; init; }

    /// <summary>
    /// Minimum length for the form control input.
    /// </summary>
    object MinLength { get; init; }

    /// <summary>
    /// Size of the form control.
    /// </summary>
    object Size { get; init; }

}
