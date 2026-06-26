using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.DataBinding;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Base class for data-bound form components that bind a single typed value
/// to a JSON-pointer data source via the mesh stream, with debounced write-back
/// and stale-echo filtering to keep local edits stable during round-trips.
/// </summary>
/// <typeparam name="TViewModel">The layout control model type that carries the form field configuration.</typeparam>
/// <typeparam name="TView">The concrete Blazor view component that derives from this base.</typeparam>
/// <typeparam name="TValue">The CLR type of the value this field edits.</typeparam>
public abstract class FormComponentBase<TViewModel, TView, TValue> : BlazorView<TViewModel, TView>
    where TViewModel : UiControl, IFormControl
    where TView : FormComponentBase<TViewModel, TView, TValue>
{
    private TValue? data;

    /// <summary>Blazor area key used to identify the edit-mode variant of a form view.</summary>
    public const string Edit = nameof(Edit);
    /// <summary>Optional label text rendered above or beside the input control.</summary>
    protected string? Label { get; set; }
    /// <summary>Debounce window in milliseconds applied when <c>Immediate</c> mode is active.</summary>
    protected int ImmediateDelay { get; set; }
    private JsonPointerReference? DataPointer { get; set; }

    /// <summary>When <c>true</c>, the control receives focus automatically when rendered.</summary>
    protected bool AutoFocus { get; set; }
    /// <summary>When <c>true</c>, value changes are pushed to the stream on every keystroke (subject to <c>ImmediateDelay</c>).</summary>
    protected bool Immediate { get; set; }
    /// <summary>Icon displayed on the leading (start) side of the input control.</summary>
    protected Icon? IconStart { get; set; }
    /// <summary>Icon displayed on the trailing (end) side of the input control.</summary>
    protected Icon? IconEnd { get; set; }
    /// <summary>Ghost text shown inside the control when no value is entered.</summary>
    protected string? Placeholder { get; set; }
    /// <summary>When <c>true</c>, the control is rendered in a disabled state and user interaction is blocked.</summary>
    protected bool Disabled { get; set; }
    /// <summary>When <c>true</c>, the control is rendered as read-only — value is visible but cannot be changed.</summary>
    protected bool Readonly { get; set; }
    /// <summary>When <c>true</c>, the field is marked as required and validation will flag an empty value.</summary>
    protected bool Required { get; set; }
    /// <summary>CSS width value (e.g. <c>"200px"</c> or <c>"50%"</c>) applied to the control via <c>ComputedStyle</c>.</summary>
    protected string? Width { get; set; }
    /// <summary>CSS height value applied to the control via <c>ComputedStyle</c>.</summary>
    protected string? Height { get; set; }

    /// <summary>
    /// Combines the data-bound <see cref="Width"/> and <see cref="Height"/> with the
    /// control's Style. Views set Style="@ComputedStyle" on the underlying Fluent component.
    /// If a Fluent component exposes its own Width parameter, the view may pass
    /// <see cref="Width"/> directly and then exclude width from the style composition.
    /// </summary>
    protected string? ComputedStyle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Style)) parts.Add(Style.TrimEnd(';'));
            if (!string.IsNullOrWhiteSpace(Width)) parts.Add($"width: {Width}");
            if (!string.IsNullOrWhiteSpace(Height)) parts.Add($"height: {Height}");
            return parts.Count > 0 ? string.Join("; ", parts) + ";" : null;
        }
    }

    // Sync state tracking to prevent race conditions between local edits and stream feedback
    private TValue? lastSyncedValue;
    private TValue? currentLocalValue;
    private bool hasPendingLocalChanges;

    private Subject<TValue>? valueUpdateSubject;

    /// <summary>
    /// The current locally-held value for the field. Setting this property queues a
    /// debounced write-back to the JSON-pointer data source via <c>UpdatePointer</c>.
    /// </summary>
    protected TValue? Value
    {
        get => data;
        set
        {
            var needsUpdate = !EqualityComparer<TValue>.Default.Equals(this.data, value);
            this.data = value;
            if (needsUpdate)
            {
                // Track that we have local changes pending sync
                currentLocalValue = value;
                hasPendingLocalChanges = true;

                // NOTE: We no longer call UpdatePointer immediately here.
                // The pointer update is now handled via the debounced valueUpdateSubject.
                // This prevents the race condition where:
                // 1. User types "H", UpdatePointer("H") sends to stream
                // 2. User types "e", UpdatePointer("He") sends to stream
                // 3. Stream echoes "H" back, overwriting "He" and losing characters

                // Push value to debounce subject (which will call UpdatePointer after debounce)
                valueUpdateSubject?.OnNext(value!);
            }
        }
    }

    /// <summary>
    /// Directly sets the backing field without triggering a write-back to the stream.
    /// Used internally when initialising the value from an options list before the binding pipeline runs.
    /// </summary>
    /// <param name="v">The value to store.</param>
    protected void SetValue(TValue? v)
        => this.data = v;

    private const int DebounceWindow = 20;
    /// <summary>
    /// Binds all form-control parameters (Label, Placeholder, Disabled, AutoFocus, Immediate,
    /// ImmediateDelay, icons, Readonly, Required, Width, Height) from the view-model, sets up
    /// the debounced write-back pipeline through <c>valueUpdateSubject</c>, and wires the data
    /// pointer binding with stale-echo filtering via <c>ConversionToValueWithFilter</c>.
    /// </summary>
    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Label, x => x.Label);
        DataBind(ViewModel.Placeholder, x => x.Placeholder);
        DataBind(ViewModel.Disabled, x => x.Disabled);
        DataBind(ViewModel.AutoFocus, x => x.AutoFocus);
        DataBind(ViewModel.Immediate, x => x.Immediate);
        DataBind(ViewModel.ImmediateDelay, x => x.ImmediateDelay);
        DataBind(ViewModel.IconStart, x => x.IconStart);
        DataBind(ViewModel.IconEnd, x => x.IconEnd);
        DataBind(ViewModel.Readonly, x => x.Readonly);
        DataBind(ViewModel.Required, x => x.Required);
        DataBind(ViewModel.Width, x => x.Width);
        DataBind(ViewModel.Height, x => x.Height);

        DataPointer = ViewModel.Data as JsonPointerReference;
        valueUpdateSubject = new();

        // No .Skip(1) — the first value change must propagate immediately
        // so that editing a field (e.g. TransactionMapping percentage) triggers
        // reactive updates on the first keystroke, not only from the second one.
        AddBinding(valueUpdateSubject
            .ThrottleImmediate(TimeSpan.FromMilliseconds(DebounceWindow))
            .DistinctUntilChanged()
            .Subscribe(x =>
            {
                if (DataPointer is not null)
                {
                    // Skip if value hasn't changed since last sync
                    if (EqualityComparer<TValue>.Default.Equals(x, lastSyncedValue))
                        return;

                    lastSyncedValue = x;
                    // Keep hasPendingLocalChanges=true — only cleared when the echo
                    // of this sync arrives (in ShouldApplyExternalUpdate). This prevents
                    // stale echoes from older syncs from overwriting the current value.
                    UpdatePointer(ConvertToData(x)!, DataPointer);
                }
            })
        );
        DataBind(ViewModel.Data, x => x.data, ConversionToValueWithFilter!);
    }

    /// <summary>
    /// Wraps ConversionToValue with filtering to prevent stale stream feedback from overwriting local changes.
    /// </summary>
    private TValue? ConversionToValueWithFilter(object v, TValue defaultValue)
    {
        var convertedValue = ConversionToValue(v, defaultValue);

        // Check if we should apply this external update
        if (!ShouldApplyExternalUpdate(convertedValue))
        {
            // Return current local value to prevent overwriting
            return data;
        }

        // Update sync state when external update is applied
        lastSyncedValue = convertedValue;
        currentLocalValue = convertedValue;
        hasPendingLocalChanges = false;

        return convertedValue;
    }

    /// <summary>
    /// Determines whether an external update (from stream) should be applied.
    /// Returns false if the update is an echo of our own sync or if we have pending local changes.
    /// When the echo of our last sync arrives, clears the pending flag so subsequent
    /// genuine external updates can be applied.
    /// </summary>
    private bool ShouldApplyExternalUpdate(TValue? value)
    {
        // Echo of what we last synced — don't apply (duplicate) but confirm the sync
        if (EqualityComparer<TValue>.Default.Equals(value, lastSyncedValue))
        {
            hasPendingLocalChanges = false;
            return false;
        }

        // Still waiting for our sync echo — reject stale external values
        if (hasPendingLocalChanges)
            return false;

        return true;
    }

    /// <summary>
    /// Converts a raw stream value (typically a <c>JsonElement</c> or boxed CLR value) to
    /// <typeparamref name="TValue"/>. Handles JSON, common numeric types, booleans, enums,
    /// dates, and GUIDs. Derived types may override for domain-specific conversions.
    /// </summary>
    /// <param name="v">The raw value received from the data binding source.</param>
    /// <param name="defaultValue">Value to return when conversion fails or the source is null/undefined.</param>
    /// <returns>The converted value, or <paramref name="defaultValue"/> on failure.</returns>
    protected virtual TValue? ConversionToValue(object v, TValue defaultValue)
    {
        if (v is JsonElement je)
        {
            // Handle undefined and null JSON values - return default (null for nullable types)
            if (je.ValueKind == JsonValueKind.Undefined || je.ValueKind == JsonValueKind.Null)
                return default;
            return je.Deserialize<TValue>(Stream!.Hub.JsonSerializerOptions);
        }

        // Handle specific conversions
        if (v is string stringValue && typeof(TValue) != typeof(string))
        {
            var targetType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);

            try
            {
                // Handle common numeric types
                if (targetType == typeof(int))
                    return (TValue)(object)int.Parse(stringValue);
                if (targetType == typeof(double))
                    return (TValue)(object)double.Parse(stringValue);
                if (targetType == typeof(decimal))
                    return (TValue)(object)decimal.Parse(stringValue);
                if (targetType == typeof(float))
                    return (TValue)(object)float.Parse(stringValue);
                if (targetType == typeof(long))
                    return (TValue)(object)long.Parse(stringValue);

                // Handle boolean
                if (targetType == typeof(bool))
                    return (TValue)(object)bool.Parse(stringValue);

                // Handle date/time
                if (targetType == typeof(DateTime))
                    return (TValue)(object)DateTime.Parse(stringValue);

                // Handle enums
                if (targetType.IsEnum)
                    return (TValue)Enum.Parse(targetType, stringValue);

                // Handle GUID
                if (targetType == typeof(Guid))
                    return (TValue)(object)Guid.Parse(stringValue);
            }
            catch
            {
                // If conversion fails, return default value for the type
                return defaultValue;
            }
        }
        // Check if direct type assignment is possible
        if (v is TValue typedValue)
        {
            return typedValue;
        }

        // Handle null values - return default for nullable types
        if (v is null)
        {
            return default;
        }

        try
        {
            // Handle nullable types - Convert.ChangeType doesn't support them directly
            var targetType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
            var converted = Convert.ChangeType(v, targetType);
            return (TValue)converted;
        }
        catch
        {
            // If conversion fails, return default value
            return defaultValue;
        }

    }

    /// <summary>
    /// Converts the typed field value back to a form suitable for writing to the JSON-pointer
    /// data source. The default implementation returns the value as-is; derived types override
    /// to unwrap wrapper types (e.g. an <c>Option</c> record to its underlying item).
    /// </summary>
    /// <param name="v">The current typed value to convert.</param>
    /// <returns>The value to write to the data pointer, or <c>null</c> to clear it.</returns>
    protected virtual object? ConvertToData(TValue v) => v;

    /// <summary>
    /// Returns <c>true</c> when the incoming stream value differs from the current field value
    /// and the component should re-render. Override to implement type-specific equality (e.g.
    /// option matching by string key rather than reference).
    /// </summary>
    /// <param name="v">The candidate new value received from the data stream.</param>
    /// <returns><c>true</c> if the component state needs to be updated; otherwise <c>false</c>.</returns>
    protected virtual bool NeedsUpdate(TValue v)
    {
        return !Equals(data, v);
    }

    /// <summary>
    /// Called when the form field loses focus. Flushes any pending local changes that the
    /// debounce timer has not yet emitted, then posts a <c>BlurEvent</c> to the stream owner
    /// so server-side validation and field-exit logic can react.
    /// </summary>
    protected virtual void OnBlur()
    {
        if (Stream is null || ViewModel is not { IsBlurable: true })
            return;

        // Flush any pending value that the debounce cooldown hasn't emitted yet.
        // Without this, a user could edit a value and click away before the
        // debounce timer fires, causing the change to be silently lost.
        if (hasPendingLocalChanges && DataPointer is not null)
        {
            lastSyncedValue = currentLocalValue;
            hasPendingLocalChanges = false;
            UpdatePointer(ConvertToData(currentLocalValue!)!, DataPointer);
        }

        Stream.Hub.Post(new BlurEvent(Area, Stream.StreamId), o => o.WithTarget(Stream.Owner));
    }
}
