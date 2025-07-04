using Json.Patch;
using MeshWeaver.Data;

namespace MeshWeaver.Layout.Client;

public abstract class ModelParameter
{
    public abstract void Confirm();
    public abstract object? GetValueFromModel(JsonPointerReference reference);
}
public class ModelParameter<TModel> : ModelParameter
{
    private readonly Func<ModelParameter<TModel>,JsonPointerReference, object>? getReference;
    public event EventHandler<TModel>? ElementChanged; 
    public ModelParameter(TModel model, Func<ModelParameter<TModel>, JsonPointerReference, object>? getReference)
    {
        this.getReference = getReference;
        Element = model;
        LastSubmitted = Element;
    }

    public TModel Element { get; set; }

    private readonly Queue<TModel> toBePersisted = new(); 
    private TModel LastSubmitted { get; set; }

    public void Update(JsonPatch patch)
    {
        Element = patch.Apply(Element)!;
        ElementChanged?.Invoke(this, Element);
    }

    public void Update(Func<TModel, TModel> update)
    {
        Element = update(Element);
        ElementChanged?.Invoke(this, Element);
    }


    public TModel Submit()
    {
        toBePersisted.Enqueue(LastSubmitted);
        LastSubmitted = Element;
        return Element;
    }

    public bool IsUpToDate => Element?.Equals(LastSubmitted) ?? false;

    public override void Confirm()
    {
        toBePersisted.Dequeue();
    }

    public override object? GetValueFromModel(JsonPointerReference reference)
        => getReference?.Invoke(this, reference);

    public void Reset()
    {
        Element = LastSubmitted;
        if (toBePersisted.TryDequeue(out var lastPersisted))
            LastSubmitted = lastPersisted;
    }

}
