namespace MeshWeaver.Layout;

/// <summary>
/// Represents a toggle switch control (on/off).
/// Wraps FluentSwitch in the Blazor layer.
/// </summary>
/// <param name="Data">The boolean data binding for the switch state.</param>
public record SwitchControl(object Data)
    : FormControlBase<SwitchControl>(Data)
{
    /// <summary>
    /// Message shown when the switch is in the checked (on) state.
    /// </summary>
    public object? CheckedMessage { get; init; }

    /// <summary>
    /// Message shown when the switch is in the unchecked (off) state.
    /// </summary>
    public object? UncheckedMessage { get; init; }

    /// <summary>Returns a copy with <paramref name="message"/> as the checked-state label.</summary>
    /// <param name="message">The message to display when the switch is on.</param>
    /// <returns>A new <see cref="SwitchControl"/> with the updated checked message.</returns>
    public SwitchControl WithCheckedMessage(object message) => this with { CheckedMessage = message };
    /// <summary>Returns a copy with <paramref name="message"/> as the unchecked-state label.</summary>
    /// <param name="message">The message to display when the switch is off.</param>
    /// <returns>A new <see cref="SwitchControl"/> with the updated unchecked message.</returns>
    public SwitchControl WithUncheckedMessage(object message) => this with { UncheckedMessage = message };
}
