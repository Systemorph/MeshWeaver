using System.Reactive.Linq;
using System.Reactive.Subjects;
using OpenSmc.Data;

namespace OpenSmc.Layout.Composition;

public record LayoutArea
{
    public static readonly string ControlsCollection = typeof(UiControl).FullName;

    public IObservable<EntityStore> Stream { get; }
    public LayoutAreaReference Reference { get; init; }


    private readonly Subject<Func<EntityStore, EntityStore>> updateStream = new();
    public void UpdateView(string area, UiControl control)
    {
        updateStream.OnNext(store => store.UpdateCollection(ControlsCollection, i => i.SetItem(area, control)));
    }


    public LayoutArea(LayoutAreaReference Reference)
    {
        this.Reference = Reference;
        Stream = updateStream
            .Scan(new EntityStore(), (currentState, updateFunc) => updateFunc(currentState))
            .Sample(TimeSpan.FromMilliseconds(100))
            .Replay(1)
            .RefCount();
    }

}