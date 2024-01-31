using System.Collections.Immutable;
using System.Reflection;
using OpenSmc.Layout.Composition;
using OpenSmc.Reflection;

namespace OpenSmc.Layout.Views;

/// <summary>
/// 
/// </summary>
/// <param name="User">UserInfo, for now populated with Email, DisplayName and Photo</param>
/// <param name="Title">Text</param>
/// <param name="Summary">Control</param>
/// <param name="View">Control of main body</param>
/// <param name="Menu">Control</param>
/// <param name="Options">Control</param>
public record ActivityControl(object User, object Title, object Summary, object View, string Color, DateTime Date, ImmutableList<MenuItemControl> Menu, ImmutableList<MenuItemControl> Options) : UiControl<ActivityControl, GenericUiControlPlugin<ActivityControl>>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null);

public record MenuItemControl(object Title, object Icon) : ExpandableUiControl<MenuItemControl, ExpandableUiPlugin<MenuItemControl>>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public string Description { get; init; }

    public string Color { get; init; }
    public MenuItemControl WithColor(string color) => this with { Color = color };
    public MenuItemControl WithTitle(object title) => this with { Title = title };

    public MenuItemControl WithIcon(object icon) => this with { Icon = icon };
    public MenuItemControl WithDescription(string description) => this with { Description = description };

    public MenuItemControl WithSubMenu(Func<IAsyncEnumerable<MenuItemControl>> subMenu)
    {
        return WithExpand(async context => ParseToUiControl(await subMenu().ToArrayAsync()));
    }

    /*public MenuItem WithSubMenu(object payload, Func<object, IAsyncEnumerable<MenuItem>> subMenu)
    {
        return WithExpand(payload, async p => await ExpandSubMenu(p, subMenu));
    }*/

    public MenuItemControl WithSubMenu(object payload, Func<object, IAsyncEnumerable<MenuItemControl>> subMenu)
    {
        // TODO V10: redirect to non generic method (2023.08.28, Armen Sirotenko)
        return WithExpand(payload, async p => await ExpandSubMenu(p, subMenu));
    }


#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    private static readonly MethodInfo ExpandSubMenuMethod = ReflectionHelper.GetMethodGeneric<MenuItemControl>(x => x.ExpandSubMenu<object>(null, null));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    private async Task<object> ExpandSubMenu<TPayload>(TPayload payload, Func<TPayload, IAsyncEnumerable<MenuItemControl>> subMenu)
    {
        return ParseToUiControl(await subMenu(payload).ToArrayAsync());
    }
    private async Task<object> ExpandSubMenu(object payload, object subMenu)
    {
        if (subMenu is Func<IAsyncEnumerable<MenuItemControl>> simple)
            return await simple().ToArrayAsync();
        var subMenuType = subMenu.GetType();
        if (subMenuType.IsGenericType && subMenuType.GetGenericTypeDefinition() == typeof(Func<,>))
        {
            var task = (Task<object>)ExpandSubMenuMethod.MakeGenericMethod(subMenuType.GetGenericArguments().First()).Invoke(this, new[] { payload, subMenu });
            if (task == null)
                return null;
            return await task;
        }

        throw new NotSupportedException();
    }


    public MenuItemControl WithSubMenu(object payload, object view)
    {
        return this with
               {
                   ExpandFunc = p => ExpandSubMenu(p, view),
                   ExpandMessage = new(new ExpandRequest(Expand){Payload = payload}, Address, Expand)
               };
    }

    public MenuItemControl WithSubMenu(object view)
    {
        return WithSubMenu(null, view);
    }

    public MenuItemControl WithSubMenu(params MenuItemControl[] children)
    {

        if(children.Length == 0) return this;

        return WithExpand(context => Task.FromResult(ParseToUiControl(children)));
    }



    public object ParseToUiControl(IReadOnlyCollection<MenuItemControl> children)
    {
        if(children.Count == 1)
            return children.First();

        var ret = new Composition.Layout();
        foreach (var menuItem in children)
            ret = ret.WithView(menuItem);
        return ret;
    }


}