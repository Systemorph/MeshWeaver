using System.Reactive.Subjects;
using OpenSmc.Data;

namespace OpenSmc.Layout.Composition;

public record LayoutArea(LayoutAreaReference Reference)
{
    public static readonly string ControlsCollection = typeof(UiControl).FullName;

    public EntityStore Store { get; private set; } = new();
    public ReplaySubject<EntityStore> Subject { get; } = new(1);

    public void Commit()
    {
        Subject.OnNext(Store);
    }

    public void UpdateView(string area, UiControl control, bool deferUpdate = false)
    {
        Store = Store.UpdateCollection(ControlsCollection, i => i.SetItem(area, control));
        if (!deferUpdate)
            Commit();
    }

    private readonly Subject<ProgressControl> progressStream = new();

    public void UpdateProgress(object message, object progress)
        => progressStream.OnNext(new ProgressControl(message, progress));
}