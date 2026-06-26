using Json.Patch;
using MeshWeaver.Data;

namespace MeshWeaver.Layout.Client;

/// <summary>
/// Non-generic base for a model parameter that tracks a view-model value and its submission state.
/// </summary>
public abstract class ModelParameter
{
    /// <summary>Acknowledges receipt of the last submitted value, removing it from the pending queue.</summary>
    public abstract void Confirm();
    /// <summary>
    /// Resolves the value pointed to by <paramref name="reference"/> from the current model.
    /// </summary>
    /// <param name="reference">A JSON Pointer reference identifying the field to read.</param>
    /// <returns>The resolved value, or null if the reference cannot be resolved.</returns>
    public abstract object? GetValueFromModel(JsonPointerReference reference);
}
/// <summary>
/// Tracks a typed view-model value, supports JSON-Patch updates, and manages a submission queue
/// so in-flight changes can be confirmed or rolled back without data loss.
/// </summary>
/// <typeparam name="TModel">The view-model type being tracked.</typeparam>
public class ModelParameter<TModel> : ModelParameter
{
    private readonly Func<ModelParameter<TModel>,JsonPointerReference, object?> getReference;
    /// <summary>Raised after every update (patch or functional) with the new element value.</summary>
    public event EventHandler<TModel>? ElementChanged; 
    /// <summary>
    /// Initializes the parameter with an initial model value and a reference resolver.
    /// </summary>
    /// <param name="model">The initial model value.</param>
    /// <param name="getReference">Function that resolves a JSON Pointer reference against this parameter's model.</param>
    public ModelParameter(TModel model, Func<ModelParameter<TModel>, JsonPointerReference, object?> getReference)
    {
        this.getReference = getReference;
        Element = model;
        LastSubmitted = Element;
    }

    /// <summary>The current (possibly unsaved) model value.</summary>
    public TModel Element { get; set; }

    private readonly Queue<TModel> toBePersisted = new(); 
    private TModel LastSubmitted { get; set; }

    /// <summary>
    /// Applies a JSON Patch to the current element and raises <see cref="ElementChanged"/>.
    /// </summary>
    /// <param name="patch">The JSON Patch to apply.</param>
    public void Update(JsonPatch patch)
    {
        Element = patch.Apply(Element) ?? Element;
        ElementChanged?.Invoke(this, Element!);
    }

    /// <summary>
    /// Applies a functional update to the current element and raises <see cref="ElementChanged"/>.
    /// </summary>
    /// <param name="update">A function that produces the new model value from the current one.</param>
    public void Update(Func<TModel, TModel> update)
    {
        Element = update(Element);
        ElementChanged?.Invoke(this, Element!);
    }


    /// <summary>
    /// Marks the current element as submitted, enqueuing the previous last-submitted value for rollback.
    /// </summary>
    /// <returns>The current element value that was submitted.</returns>
    public TModel Submit()
    {
        toBePersisted.Enqueue(LastSubmitted);
        LastSubmitted = Element;
        return Element;
    }

    /// <summary>True when the current element value equals the last submitted value (no pending local changes).</summary>
    public bool IsUpToDate => Element!.Equals(LastSubmitted);

    /// <inheritdoc/>
    public override void Confirm()
    {
        toBePersisted.Dequeue();
    }

    /// <inheritdoc/>
    public override object? GetValueFromModel(JsonPointerReference reference)
        => getReference.Invoke(this, reference);

    /// <summary>
    /// Reverts the current element to the last submitted value and restores the previous pending value if present.
    /// </summary>
    public void Reset()
    {
        Element = LastSubmitted;
        if (toBePersisted.TryDequeue(out var lastPersisted))
            LastSubmitted = lastPersisted;
    }

}
