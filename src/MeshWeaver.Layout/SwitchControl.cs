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

    public SwitchControl WithCheckedMessage(object message) => this with { CheckedMessage = message };
    public SwitchControl WithUncheckedMessage(object message) => this with { UncheckedMessage = message };
}
