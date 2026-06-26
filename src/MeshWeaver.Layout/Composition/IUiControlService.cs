using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// Converts arbitrary objects to <see cref="UiControl"/> instances using the registered
/// conversion rules collected in <see cref="LayoutDefinition"/>. Used by the layout engine
/// to turn data objects, strings, and renderable objects into displayable controls.
/// </summary>
public interface IUiControlService
{
    /// <summary>
    /// Converts <paramref name="o"/> to a <see cref="UiControl"/> by trying registered
    /// conversion rules in order. Returns <c>null</c> when no rule produces a result.
    /// </summary>
    /// <param name="o">The object to convert.</param>
    /// <returns>A <see cref="UiControl"/> or <c>null</c>.</returns>
    UiControl? Convert(object o);

    /// <summary>The layout definition that supplied the conversion rules used by this service.</summary>
    LayoutDefinition LayoutDefinition { get; }
}

/// <summary>
/// Default <see cref="IUiControlService"/> implementation. Rules are fixed at construction time
/// (config-time rules from <see cref="LayoutDefinition"/> plus a default fallback); no
/// runtime rule addition is supported.
/// </summary>
public class UiControlService : IUiControlService
{
    /// <summary>The layout definition from which this service's conversion rules were drawn.</summary>
    public LayoutDefinition LayoutDefinition { get; }

    // Rules are immutable: config-time rules from LayoutDefinition + default fallback.
    // No AddRule() — all rules are known at construction time.
    private readonly ImmutableList<Func<object, UiControl?>> rules;

    /// <summary>
    /// Initialises the service by resolving the hub's <see cref="LayoutDefinition"/> and
    /// appending the default conversion fallback to its rules.
    /// </summary>
    /// <param name="hub">The message hub whose layout definition provides conversion rules.</param>
    public UiControlService(IMessageHub hub)
    {
        LayoutDefinition = hub.GetLayoutDefinition();
        rules = LayoutDefinition.ConversionRules.Add(o => o as UiControl ?? DefaultConversion(o));
    }

    /// <summary>
    /// Tries each registered conversion rule in order and returns the first non-null result.
    /// Falls back to the default conversion (HTML-encoded string) when no rule matches.
    /// </summary>
    /// <param name="o">The object to convert.</param>
    /// <returns>A <see cref="UiControl"/>, never <c>null</c> for a non-null input.</returns>
    public UiControl? Convert(object o) =>
        rules.Select(r => r.Invoke(o))
            .FirstOrDefault(x => x is not null);



    private static UiControl? DefaultConversion(object instance)
    {
        if (instance is UiControl control)
            return control;

        if (instance is IRenderableObject ro)
            return ro.ToControl();

        return Controls.Html(System.Net.WebUtility.HtmlEncode(instance?.ToString() ?? string.Empty));
    }
}
