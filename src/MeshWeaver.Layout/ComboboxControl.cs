using System.Reflection;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Reflection;

namespace MeshWeaver.Layout;

/// <summary>
/// Represents a combobox control with customizable properties.
/// </summary>
/// <remarks>
/// For more information, visit the
/// <a href="https://www.fluentui-blazor.net/combobox">Fluent UI Blazor Combobox documentation</a>.
/// </remarks>
/// <param name="Data">The data associated with the combobox control.</param>
/// <param name="Options">The options to choose from.</param>
public record ComboboxControl(object Data, object Options) : ListControlBase<ComboboxControl>(Data, Options)
{
    /// <summary>
    /// Gets or initializes the autofocus state of the combobox.
    /// </summary>
    public object Autofocus { get; init; }

    /// <summary>
    /// Gets or initializes the autocomplete state of the combobox.
    /// </summary>
    public object Autocomplete { get; init; }


    /// <summary>
    /// Gets or initializes the position of the combobox.
    /// </summary>
    public object Position { get; init; }

    /// <summary>
    /// Sets the autofocus state of the combobox.
    /// </summary>
    /// <param name="autofocus">The autofocus state to set.</param>
    /// <returns>A new <see cref="ComboboxControl"/> instance with the specified autofocus state.</returns>

    [ReplaceMethods]
    public ComboboxControl WithAutofocus(bool autofocus) => this with { Autofocus = autofocus };

    /// <summary>
    /// Sets the autocomplete state of the combobox.
    /// </summary>
    /// <param name="autocomplete">The autocomplete state to set.</param>
    /// <returns>A new <see cref="ComboboxControl"/> instance with the specified autocomplete state.</returns>
    public ComboboxControl WithAutocomplete(object autocomplete) => this with { Autocomplete = autocomplete };

    /// <summary>
    /// Sets the position of the combobox.
    /// </summary>
    /// <param name="position">The position to set.</param>
    /// <returns>A new <see cref="ComboboxControl"/> instance with the specified position.</returns>
    public ComboboxControl WithPosition(object position) => this with { Position = position };

    /// <summary>
    /// Sets the autofocus state of the combobox.
    /// </summary>
    /// <param name="autofocus">The autofocus state to set.</param>
    /// <returns>A new <see cref="ComboboxControl"/> instance with the specified autofocus state.</returns>

    internal ComboboxControl WithAutofocus(object autofocus) => this with { Autofocus = autofocus };

    private class ReplaceMethodsAttribute : ReplaceMethodInTemplateAttribute
    {
        private static readonly Dictionary<MethodInfo, MethodInfo> MethodMap = new()
        {
            {
                ReflectionHelper.GetMethod<ComboboxControl>(x => x.WithAutofocus(default(bool))),
                ReflectionHelper.GetMethod<ComboboxControl>(x => x.WithAutofocus(default(object)))
            }
        };

        public override MethodInfo Replace(MethodInfo expression)
        {
            return MethodMap[expression];
        }
    }
}

public enum ComboboxAutocomplete
{
    Inline,
    List,
    Both
}
