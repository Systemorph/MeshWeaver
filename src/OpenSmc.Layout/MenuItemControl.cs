using System.Reflection;
using OpenSmc.Reflection;

namespace OpenSmc.Layout;

public record MenuItemControl(object Title, object Icon)
    : UiControl<MenuItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public string Description { get; init; }

    public string Color { get; init; }

    public MenuItemControl WithColor(string color) => this with { Color = color };

    public MenuItemControl WithTitle(object title) => this with { Title = title };

    public MenuItemControl WithIcon(object icon) => this with { Icon = icon };

    public MenuItemControl WithDescription(string description) =>
        this with
        {
            Description = description
        };


    /*public MenuItem WithSubMenu(object payload, Func<object, IAsyncEnumerable<MenuItem>> subMenu)
    {
        return WithExpand(payload, async p => await ExpandSubMenu(p, subMenu));
    }*/


#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    private static readonly MethodInfo ExpandSubMenuMethod =
        ReflectionHelper.GetMethodGeneric<MenuItemControl>(x =>
            x.ExpandSubMenu<object>(null, null)
        );
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    private async Task<UiControl> ExpandSubMenu<TPayload>(
        TPayload payload,
        Func<TPayload, IAsyncEnumerable<MenuItemControl>> subMenu
    )
    {
        return ParseToUiControl(await subMenu(payload).ToArrayAsync());
    }

    private async Task<UiControl> ExpandSubMenu(object payload, object subMenu)
    {
        if (subMenu is Func<IAsyncEnumerable<MenuItemControl>> simple)
            return ParseToUiControl(await simple().ToArrayAsync());
        var subMenuType = subMenu.GetType();
        if (subMenuType.IsGenericType && subMenuType.GetGenericTypeDefinition() == typeof(Func<,>))
        {
            var task =
                (Task<UiControl>)
                    ExpandSubMenuMethod
                        .MakeGenericMethod(subMenuType.GetGenericArguments().First())
                        .Invoke(this, new[] { payload, subMenu });
            if (task == null)
                return null;
            return await task;
        }

        throw new NotSupportedException();
    }


    public UiControl ParseToUiControl(IReadOnlyCollection<MenuItemControl> children)
    {
        if (children.Count == 1)
            return children.First();

        var ret = new LayoutStackControl();
        foreach (var menuItem in children)
            ret = ret.WithView(menuItem);
        return ret;
    }
}
