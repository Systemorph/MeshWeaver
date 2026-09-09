namespace MeshWeaver.Layout;

/// <summary>
/// Represents a form component with customizable properties.
/// </summary>
public interface IFormControl : IUiControl
{
    /// <summary>
    /// Gets or initializes the data associated with the form component.
    /// </summary>
    object Data { get; init; }

    /// <summary>
    /// Label of the form component.
    /// </summary>
    object? Label { get; init; }

    /// <summary>
    /// Sets the label of the form component.
    /// </summary>
    /// <param name="label">The label to set.</param>
    /// <returns>A new instance of the form control with the specified label.</returns>
    IFormControl WithLabel(object label);

    /// <summary>
    /// Accessible name for the control, rendered as <c>aria-label</c> on the underlying input.
    ///
    /// <para>
    /// 🚨 This is NOT a second visible label — it exists because the visible one frequently lives
    /// OUTSIDE the control. A generated editor field renders its caption in the surrounding
    /// <see cref="PropertySkin"/> (a <c>&lt;dt&gt;</c>) and deliberately leaves <see cref="Label"/>
    /// null so the caption is not painted twice; the input is then left with no accessible name at
    /// all. An HTML <c>&lt;label for&gt;</c> cannot rescue it either: the Fluent web components put
    /// their real input inside a SHADOW ROOT, and <c>for</c>/<c>id</c> do not associate across that
    /// boundary — which is why <c>getByRole('textbox', { name })</c> found nothing (MeshWeaver#3863).
    /// <c>aria-label</c> is set on the host element and survives the shadow boundary.
    /// </para>
    /// </summary>
    object? AriaLabel => null;

    /// <summary>
    /// Returns a copy of the control carrying <paramref name="ariaLabel"/> as its accessible name.
    /// </summary>
    /// <param name="ariaLabel">The accessible name, or a binding expression resolving to one.</param>
    /// <returns>A new instance of the form control with the specified accessible name.</returns>
    /// <remarks>
    /// The default implementation returns the control unchanged — a form control that carries no
    /// accessible name of its own. <c>FormControlBase</c>, which every control in this repository
    /// derives from, overrides both this and <see cref="AriaLabel"/> with the real record copy.
    /// The default exists so that adding this member cannot oblige an out-of-repo implementer to
    /// write code before it can compile (see <c>scripts/check-interface-addition.py</c>).
    /// </remarks>
    IFormControl WithAriaLabel(object ariaLabel) => this;

    /// <summary>
    /// Whether the form control is disabled.
    /// </summary>
    object? Disabled { get; init; }

    /// <summary>
    /// Whether the form control is required.
    /// </summary>
    object? Required { get; init; }

    /// <summary>
    /// Whether the form control should auto-focus.
    /// </summary>
    object? AutoFocus { get; init; }

    /// <summary>
    /// Gets or initializes the immediate update state of the input control.
    /// </summary>
    object? Immediate { get; init; }

    /// <summary>
    /// Gets or initializes the delay for immediate updates of the input control.
    /// </summary>
    object? ImmediateDelay { get; init; }

    /// <summary>
    /// Gets or initializes the start icon of the text field control.
    /// </summary>
    object? IconStart { get; init; }

    /// <summary>
    /// Gets or initializes the end icon of the text field control.
    /// </summary>
    object? IconEnd { get; init; }

    /// <summary>
    /// Placeholder to be put in the control.
    /// </summary>
    object? Placeholder { get; init; }

    /// <summary>
    /// Data-bindable width for the control (CSS value).
    /// </summary>
    object? Width { get; init; }

    /// <summary>
    /// Data-bindable height for the control (CSS value).
    /// </summary>
    object? Height { get; init; }

    /// <summary>
    /// Whether the control has a blur action.
    /// </summary>
    bool IsBlurable { get; }

    /// <summary>
    /// Callback invoked when the control loses focus.
    /// </summary>
    object? OnBlur { get; }
}
