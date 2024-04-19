using System.Reflection;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;

namespace OpenSmc.TestDomain
{
    public class StaticDataFieldReadOnlyWorkspace : IWorkspace
    {
        public IReadOnlyCollection<T> GetData<T>()
            where T : class
        {
            var dataProperty = typeof(T).GetField(
                "Data",
                BindingFlags.Public | BindingFlags.Static
            );
            if (dataProperty == null)
                return null;

            return ((IEnumerable<T>)dataProperty.GetValue(null))?.ToList().AsReadOnly();
        }

        // TODO V10: We might think about reintroducing IReadOnlyWorkspace and get rid of all not implemented members here (2024/03/15, Dmitry Kalabin)

        #region Not Implemented support for IWorkspace

        public ChangeStream<TReference> GetRemoteStream<TReference>(
            object address,
            WorkspaceReference<TReference> reference
        )
        {
            throw new NotImplementedException();
        }

        public IObservable<ChangeItem<WorkspaceState>> Stream =>
            throw new NotImplementedException();

        public IObservable<ChangeItem<WorkspaceState>> ChangeStream =>
            throw new NotImplementedException();

        public WorkspaceState State => throw new NotImplementedException();

        public Task Initialized => throw new NotImplementedException();

        public IEnumerable<Type> MappedTypes => throw new NotImplementedException();

        public void Commit()
        {
            throw new NotImplementedException();
        }

        public void Delete(IEnumerable<object> instances)
        {
            throw new NotImplementedException();
        }

        public EntityReference GetReference(object entity)
        {
            throw new NotImplementedException();
        }

        public void Rollback()
        {
            throw new NotImplementedException();
        }

        public IObservable<ChangeItem<TReference>> GetStream<TReference>(
            WorkspaceReference<TReference> reference
        )
        {
            throw new NotImplementedException();
        }

        public WorkspaceState CreateState(EntityStore deserialize)
        {
            throw new NotImplementedException();
        }

        public void Update(IEnumerable<object> instances, UpdateOptions updateOptions)
        {
            throw new NotImplementedException();
        }

        public void RequestChange(DataChangeRequest change, object changedBy)
        {
            throw new NotImplementedException();
        }

        public void Synchronize(DataChangedEvent message)
        {
            throw new NotImplementedException();
        }

        public void Initialize()
        {
            throw new NotImplementedException();
        }

        public void Subscribe(object sender, WorkspaceReference reference)
        {
            throw new NotImplementedException();
        }

        public void Unsubscribe(object sender, WorkspaceReference reference)
        {
            throw new NotImplementedException();
        }

        public ValueTask DisposeAsync()
        {
            throw new NotImplementedException();
        }

        #endregion Not Implemented support for IWorkspace
    }
}
