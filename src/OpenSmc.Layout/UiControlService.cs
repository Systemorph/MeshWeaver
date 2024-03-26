using System.ComponentModel.DataAnnotations;
using System.Reflection;
using OpenSmc.Layout.DataBinding;
using OpenSmc.TypeRelevance;
using OpenSmc.Utils;

namespace OpenSmc.Layout;

public record UiControlsManager() 
{
    private readonly SortByTypeRelevanceRegistry<Func<object, UiControl>> rules = new ();
    private Func<object, UiControl> fallbackRule;

    public void Register<T>(Func<T, UiControl> factory)
    {
        Register(typeof(T), instance => factory((T)instance));
    }

    public void Register(Type type, Func<object, UiControl> factory)
    {
        if (type == null)
            throw new ArgumentNullException(nameof(type));

        rules.Register(type, factory);
    }

    public void RegisterFallback(Func<object, UiControl> factory)
    {
        fallbackRule = factory;
    }
        
    public UiControl Get(object instance, Type type = null)
    {
        try
        {
            return GetUiControlInternal(instance, type);
        }
        catch (Exception ex)
        {
            return Controls.Exception(ex);
        }
    }

    public UiControl GetUiControlInternal(object instance, Type type)
    {
        try
        {
            if (instance is IObjectWithUiControl poa)
                return poa.GetUiControl(this);

            if (instance is UiControl control)
                return control;

            type ??= instance?.GetType() ?? typeof(object);

            var factory = rules.Get(type) ?? fallbackRule;
            if (factory == null)
                throw new ApplicationException("No conversion from instance to UIControl was found.");

            var ret = factory(instance);
            return ret;
        }
        catch (Exception e)
        {
            throw new ApplicationException($"Error while trying to retrieve presenter. {e.Message}", e);
        }
    }


    // TODO V10: Change to some other service where we ll be able to get property editor if property has setter or property viewer if it does not (2023.08.18, Armen Sirotenko)
    public PropertyLayout GetPropertyLayout(PropertyInfo propertyInfo)
    {
        var dn = propertyInfo.GetCustomAttribute<DisplayAttribute>();
        var systemName = propertyInfo.Name.ToCamelCase();
        var displayName = string.IsNullOrEmpty(dn?.Name) ? propertyInfo.Name.Wordify() : dn.Name;
        var attributes = propertyInfo.GetCustomAttributes().OfType<IPropertyWithUiControl>().ToList();

        var viewer = GetUiControlProperty(propertyInfo.PropertyType, attributes) with { Data = new Binding(propertyInfo.Name.ToCamelCase()) };
        return new PropertyLayout(systemName, displayName, viewer);
    }

    private UiControl GetUiControlProperty(Type type, IEnumerable<IPropertyWithUiControl> attributes)
    {
        //we ll get first or in it is empty we ll call fallback
        foreach (var propertyWithUiControl in attributes)
            return propertyWithUiControl.GetUiControl(this);

        // HACK V10: THis is hack, which will go when we ll eliminate this functionality.
        // For now all properties which has custom registry, which depends on instance will have potential null reference exception(for example nullables) (2023.08.29, Armen Sirotenko)
        return GetUiControlInternal(null, type);
    }
}