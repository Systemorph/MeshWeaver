using System.Collections.Immutable;
using OpenSmc.ShortGuid;

namespace OpenSmc.Layout.Composition;


public record LayoutStackControl() : UiControl<LayoutStackControl, LayoutPlugin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), IUiControlWithSubAreas
{
    public record AreaChangedOptions(string InsertAfter = null);
    private readonly ImmutableList<LayoutArea> areasImpl = ImmutableList<LayoutArea>.Empty;
    internal const string Root = "";

    internal ImmutableList<ViewElement> ViewElements { get; init; } = ImmutableList<ViewElement>.Empty; //definition of views

    public IReadOnlyCollection<LayoutArea> Areas
    {
        get => AreasImpl;
        init => AreasImpl = value?.ToImmutableList();
    }

    internal ImmutableList<LayoutArea> AreasImpl
    {
        get => areasImpl;
        init => areasImpl = value?.Where(x => x is { View: not null }).ToImmutableList();
    }

    IReadOnlyCollection<LayoutArea> IUiControlWithSubAreas.SubAreas => Areas;
    IUiControlWithSubAreas IUiControlWithSubAreas.SetArea(LayoutArea areaChanged)
    {
        return SetAreaToState(areaChanged);
    }


    internal LayoutStackControl SetAreaToState(LayoutArea area)
    {
        var insertAfter = (area.Options as AreaChangedOptions)?.InsertAfter;
        var toBeReplaced = AreasImpl.FirstOrDefault(x => x.Area == area.Area);
        var areas = toBeReplaced != null
                        ? AreasImpl.Replace(toBeReplaced, area)
                        : AreasImpl.Insert(string.IsNullOrEmpty(insertAfter) ? 0 : AreasImpl.FindIndex(x => x.Area == insertAfter) + 1, area);
        return this with
               {
                   AreasImpl = areas,
               };
    }


public LayoutStackControl WithView(object value) => WithView(value, null);

    public LayoutStackControl WithView(object value, Func<SetAreaOptions, SetAreaOptions> options)
    {
        var o = GetOptions(options);
        return this with { ViewElements = ViewElements.Add(new ViewElementWithView(value, o)) };
    }

    private SetAreaOptions GetOptions(Func<SetAreaOptions, SetAreaOptions> options)
    {
        var ret = new SetAreaOptions(GetAreaName());
        if (options != null)
            ret = options.Invoke(ret);
        return ret;
    }

    public LayoutStackControl WithView<T>(Func<T> view, Func<SetAreaOptions, SetAreaOptions> options = null) => WithView(() => Task.FromResult(new ViewElementWithView(view(), GetOptions(options))));
    public LayoutStackControl WithView<T>(Func<Task<T>> viewDefinition, Func<SetAreaOptions, SetAreaOptions> options = null) => WithView(ConvertToViewDefinition(viewDefinition), options);

    private ViewDefinition ConvertToViewDefinition<T>(Func<Task<T>> viewDefinition)
    {
        if (typeof(ViewElementWithView).IsAssignableFrom(typeof(T)))
            return async _ => (ViewElementWithView)(object)(await viewDefinition());
        return async opt => new ViewElementWithView(await viewDefinition(), opt);
    }

    public LayoutStackControl WithView(ViewDefinition viewDefinition, Func<SetAreaOptions, SetAreaOptions> options = null)
    {
        var o = GetOptions(options);
        return this with { ViewElements = ViewElements.Add(new ViewElementWithViewDefinition(viewDefinition, o)) };
    }

    private string GetAreaName()
    {
        return $"{ViewElements.Count + 1}";
    }


    public LayoutStackControl WithColumns(int columns)
    {
        return this with { ColumnCount = columns };
    }

    public int ColumnCount { get; init; }

    public LayoutStackControl UpdateArea(LayoutArea areaChangedEvent)
    {
        var old = AreasImpl.Find(x => x.Area == areaChangedEvent.Area);
        var areas = areaChangedEvent.View == null ? AreasImpl.RemoveAll(x => x.Area == areaChangedEvent.Area) : old != null ? AreasImpl.Replace(old, areaChangedEvent) : AreasImpl.Add(areaChangedEvent);
        return this with { AreasImpl = areas };
    }



    public override bool IsUpToDate(object other)
    {
        if (other is not LayoutStackControl stack)
            return false;


        if (Areas == null)
            return base.IsUpToDate(other);

        return (this with { Areas = null }).IsUpToDate(stack with { Areas = null })
               && Areas.Count == stack.Areas.Count
               && Areas.Zip(stack.Areas, IsUpToDate).All(x => x);

    }

    public void Update(LayoutStackControl updatedView, int maxAreas)
    {
        if (Hub == null)
            throw new InvalidOperationException("Cannot update detached control");

        var newElements = updatedView.ViewElements.OfType<ViewElementWithView>().Reverse().ToArray();
        foreach (var newArea in newElements)
        {
            var area = Guid.NewGuid().AsString();
            Hub.Post(new SetAreaRequest(new SetAreaOptions(area)
                                        {
                                            AreaViewOptions = new AreaChangedOptions(Areas.LastOrDefault()?.Area)
                                        }, newArea.View));
        }

        foreach (var area in newElements.Select(x => x.Area).Concat(Areas.Select(x => x.Area)).Skip(maxAreas))
            Hub.Post(new SetAreaRequest(new SetAreaOptions(area), null));
    }

    public bool HighlightNewAreas { get; init; }
    public LayoutStackControl WithHighlightNewAreas()
    {
        return this with { HighlightNewAreas = true };
    }
}

