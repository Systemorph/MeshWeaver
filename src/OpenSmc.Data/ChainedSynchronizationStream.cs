using OpenSmc.Data.Serialization;

namespace OpenSmc.Data
{
    internal record ChainedSynchronizationStream<TStream, TReference, TReduced> :
        SynchronizationStream<TReduced, TReference> where TReference : WorkspaceReference
    {
        private readonly ISynchronizationStream<TStream> parent;
        private readonly PatchFunction<TStream, TReduced> backTransform;

        public ChainedSynchronizationStream(ISynchronizationStream<TStream> parent, object owner, object subscriber, TReference reference) : base( owner, subscriber, parent.Hub, reference, parent.ReduceManager.ReduceTo<TReduced>(), InitializationMode.Automatic)
        {
            this.parent = parent;
            backTransform = parent.ReduceManager.GetPatchFunction<TReduced>();
        }

        public override DataChangeResponse RequestChange(Func<TReduced, ChangeItem<TReduced>> update)
        {
            var ret = base.RequestChange(update);
            if (backTransform == null || !RemoteAddress.Equals(Current.ChangedBy))
                return ret;

            if(!parent.Initialized.IsCompleted)
                throw new InvalidOperationException("Parent is not initialized yet");

            return parent.RequestChange(state =>
                backTransform(state, Reference, update(Current.Value)));

        }

        public override void NotifyChange(Func<TReduced, ChangeItem<TReduced>> update)
        {
            base.NotifyChange(update);
            UpdateParent(Current);
        }

        public override void Initialize(ChangeItem<TReduced> initial)
        {
            base.Initialize(initial);
            UpdateParent(Current);
        }

        private void UpdateParent(ChangeItem<TReduced> value)
        {
            // if we cannot back transform or if the change was not made by the remote party, we do not need to notify the parent
            if (backTransform == null || !RemoteAddress.Equals(value.ChangedBy))
                return;

            // if the parent is initialized, we will update the parent
            if(parent.Initialized.IsCompleted)
                parent.Update(state => backTransform(state, Reference, value));

            // if we are in automatic mode, we will initialize the parent
            else if (parent.InitializationMode == InitializationMode.Automatic)
                parent.Initialize(backTransform(Activator.CreateInstance<TStream>(), Reference, value));
        }
    }
}
