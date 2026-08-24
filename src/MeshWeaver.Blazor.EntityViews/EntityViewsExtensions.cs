using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using static MeshWeaver.Layout.Client.LayoutClientConfiguration;

namespace MeshWeaver.Blazor.EntityViews;

/// <summary>
/// The entity-views pack's single hub-side entry point: Blazor renderers for the standard
/// entity form/edit controls — the text/number/date/choice inputs plus the three entity-editing
/// skins (<see cref="EditorSkin"/> / <see cref="EditFormSkin"/> / <see cref="PropertySkin"/>).
/// A plain view-pack class library — component types plus this registration; the control records
/// and skins stay in <c>MeshWeaver.Layout</c>. Register before or after <c>AddBlazor()</c> —
/// order stopped being load-bearing when the escaped-HTML fallback moved to its own slot and the
/// base registry's skin dispatch learned to DECLINE (return null) instead of throwing.
/// </summary>
public static class EntityViewsExtensions
{
    /// <summary>
    /// Registers the entity form-control views and the entity-editing skin views on the hub
    /// configuration.
    /// </summary>
    public static MessageHubConfiguration AddEntityViews(this MessageHubConfiguration config) =>
        config.AddViews(layout => layout.WithView(EntityViewsMap(layout.Hub)));

    /// <summary>
    /// Builds the pack's single <see cref="LayoutClientConfiguration.ViewMap"/>. One map rather
    /// than a dozen <c>WithView&lt;,&gt;()</c> registrations, and DELIBERATELY the same shape as
    /// the base registry's <c>DefaultFormatting</c>: pop the skin FIRST, and let the typed
    /// control arms match only skin-free controls. A per-control-type map registered naively
    /// would claim e.g. a <c>TextFieldControl</c> that still carries a <c>PropertySkin</c> (the
    /// standard editor shape: the skin view renders the label chrome, then re-dispatches the
    /// popped control) or a framework skin like <c>LayoutStackSkin</c> — rendering it bare and
    /// silently dropping the skin. Popping first preserves the exact dispatch semantics the
    /// arms had inside the base registry, wherever in the map chain this pack lands.
    /// </summary>
    private static ViewMap EntityViewsMap(IMessageHub hub)
    {
        // Resolved once at registration: the reflection-built generics (NumberFieldView<T> /
        // RadioGroupView<T>) close over the control's declared value type via the type registry.
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        return (instance, stream, area) =>
        {
            if (instance is not UiControl control)
                return null;

            control = control.PopSkin(out var skin);
            if (skin != null)
                return skin switch
                {
                    EditorSkin editor => StandardSkinnedView<EditorView>(editor, stream, area, control),
                    EditFormSkin edit => StandardSkinnedView<EditFormView>(edit, stream, area, control),
                    PropertySkin property => StandardSkinnedView<PropertyView>(property, stream, area, control),
                    // Not our skin — decline so the base registry / other packs get their turn
                    // (the control is re-popped by whichever map claims it; PopSkin is pure).
                    _ => null,
                };

            return control switch
            {
                // The two reflection-constructed generics: no static constraint can reach them, so
                // ReflectionRegisteredViews_AreStillBlazorViewsForTheirControl (Hosting.Blazor.Test)
                // pins at runtime that they stay BlazorViews for their control (#1333).
                NumberFieldControl number => StandardView(number, typeof(NumberFieldView<>).MakeGenericType(typeRegistry.GetType(number.Type.ToString()!) ?? throw new InvalidOperationException($"Type not found: {number.Type}")), stream, area),
                RadioGroupControl radioGroup => StandardView(radioGroup, typeof(RadioGroupView<>).MakeGenericType(typeRegistry.GetType(radioGroup.Type?.ToString() ?? throw new ArgumentException($"Cannot find type {radioGroup.Type} for radio group.")) ?? throw new InvalidOperationException($"Type not found: {radioGroup.Type}")), stream, area),
                TextFieldControl textbox => StandardView<TextFieldControl, TextFieldView>(textbox, stream, area),
                TextAreaControl textArea => StandardView<TextAreaControl, TextAreaView>(textArea, stream, area),
                DateTimeControl dateTime => StandardView<DateTimeControl, DateTimeView>(dateTime, stream, area),
                ComboboxControl combobox => StandardView<ComboboxControl, Combobox>(combobox, stream, area),
                ListboxControl listbox => StandardView<ListboxControl, Listbox>(listbox, stream, area),
                SelectControl select => StandardView<SelectControl, SelectView>(select, stream, area),
                CheckBoxControl checkbox => StandardView<CheckBoxControl, Checkbox>(checkbox, stream, area),
                SwitchControl switchCtrl => StandardView<SwitchControl, Switch>(switchCtrl, stream, area),
                _ => null,
            };
        };
    }
}
