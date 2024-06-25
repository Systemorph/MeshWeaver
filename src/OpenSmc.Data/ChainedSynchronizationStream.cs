using System.Reactive.Linq;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Data
{
    internal record ChainedSynchronizationStream<TStream, TReference, TReduced>
        : SynchronizationStream<TReduced, TReference>
        where TReference : WorkspaceReference
    {
        private readonly ISynchronizationStream<TStream> parent;
        private readonly PatchFunction<TStream, TReduced> backTransform;

        public ChainedSynchronizationStream(
            ISynchronizationStream<TStream> parent,
            object owner,
            object subscriber,
            TReference reference
        )
            : base(
                owner,
                subscriber,
                parent.Hub,
                reference,
                parent.ReduceManager.ReduceTo<TReduced>(),
                InitializationMode.Automatic
            )
        {
            this.parent = parent;
            backTransform = parent.ReduceManager.GetPatchFunction<TReduced>(parent, reference);

            if (backTransform != null)
                    AddDisposable(
                        this.Where(value => RemoteAddress.Equals(value.ChangedBy))
                            .Subscribe(UpdateParent));
        }

        public override DataChangeResponse RequestChange(
            Func<TReduced, ChangeItem<TReduced>> update
        )
        {
            var ret = base.RequestChange(update);
            if (backTransform == null || !RemoteAddress.Equals(Current.ChangedBy))
                return ret;

            if (!parent.Initialized.IsCompleted)
                throw new InvalidOperationException("Parent is not initialized yet");

            return parent.RequestChange(state =>
                backTransform(state, parent, update(Current.Value))
            );
        }

        private void UpdateParent(ChangeItem<TReduced> value)
        {
            // if the parent is initialized, we will update the parent
            if (parent.Initialized.IsCompleted)
                parent.Update(state => backTransform(state, parent, value));
            // if we are in automatic mode, we will initialize the parent
            else if (parent.InitializationMode == InitializationMode.Automatic)
                parent.Initialize(
                    backTransform(Activator.CreateInstance<TStream>(), parent, value)
                );
        }
    }
}
