using System.Reflection;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Reflection;

namespace MeshWeaver.Layout;

public record ComboboxControl(object Data) : ListControlBase<ComboboxControl>(Data), IListControl
{
    public object Autofocus { get; init; }
    public object Autocomplete { get; init; }
    public object Placeholder { get; init; }
    public object Position { get; init; }
    public object Disabled { get; init; }

    [ReplaceMethods]
    public ComboboxControl WithAutofocus(bool autofocus) => this with {Autofocus = autofocus};
    public ComboboxControl WithAutocomplete(object autocomplete) => this with {Autocomplete = autocomplete};
    public ComboboxControl WithPlaceholder(object placeholder) => this with {Placeholder = placeholder};
    public ComboboxControl WithPosition(object position) => this with {Position = position};
    public ComboboxControl WithDisabled(object disabled) => this with {Disabled = disabled};

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
