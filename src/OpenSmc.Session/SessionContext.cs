using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using OpenSmc.Disposables;
using OpenSmc.ShortGuid;

namespace OpenSmc.Session
{
    public class SessionContext : ISessionContext, IDisposable
    {
        private CancellationTokenSource cancellation = new();

        public SessionContext(IOptions<SessionOptions> sessionOptions)
        {
            SessionId = sessionOptions.Value?.SessionId ?? Guid.NewGuid().AsString();
        }
        
        public virtual string SessionId { get; }

        public virtual void Cancel()
        {
            cancellation.Cancel();
        }

        public virtual void SetCancellationTokenSource(CancellationTokenSource cancellationTokenSource)
        {
            cancellation = cancellationTokenSource;
        }

        public virtual CancellationToken CancellationToken => cancellation.Token;

        public void Dispose()
        {
            cancellation?.Dispose();
        }


        public IDisposable SetMessageId(string id)
        {
            messageId = id;
            return new AnonymousDisposable(() => id = null);
        }
        private readonly ConcurrentDictionary<string, VariableDescriptor> variables = new();

        public VariableDescriptor[] Variables => variables.Values.ToArray();

        private string messageId;


        public delegate void VariableSet(Type senderType, VariableDescriptor variable);
        public event VariableSet OnVariableSet;

        public VariableDescriptor GetVariable(string name)
        {
            if (variables.TryGetValue(name, out var result))
                return result;
            return null;
        }

        public void SetVariable(Type senderType, string name, object instance, Type type = null)
        {
            type ??= instance.GetType();
            var variable = new VariableDescriptor(name, type, instance);
            variables[name] = variable;
            OnVariableSet?.Invoke(senderType, new VariableDescriptor(name, type, instance));
        }

        void ISessionContext.SetVariable(string name, object instance, Type type)
        {
        }

        public delegate void TypeDeclare(Type senderType, TypeInfo typeInfo);
        public event TypeDeclare OnDeclareType;

        public void DeclareType(Type sender, TypeInfo typeInfo)
        {
            OnDeclareType?.Invoke(sender, typeInfo);
        }
    }
}