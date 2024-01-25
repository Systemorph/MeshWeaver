using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenSmc.Scopes.Synchronization;

namespace OpenSmc.Scopes.Proxy
{
    public class MutableScopeInterceptor : CachingInterceptor, IHasAdditionalInterfaces
    {
        private readonly ILogger<IScope> logger;
        private readonly IScopeRegistry scopeRegistry;

        public MutableScopeInterceptor(ILogger<IScope> logger)
        {
            this.logger = logger;
        }
       
        public MutableScopeInterceptor(IScopeRegistry scopeRegistry)
        {
            this.scopeRegistry = scopeRegistry;
        }

        public IEnumerable<Type> GetAdditionalInterfaces(Type tScope)
        {
            yield return typeof(IInternalMutableScope);
        }

        private static readonly AsyncLocal<DependencyRecorder> DependencyRecorder = new();
        private static readonly AsyncLocal<DependencyTracker> DependencyTracker = new();
        private readonly ConcurrentDictionary<object, Dictionary<MethodInfo, object>> recomputes = new();
        private readonly ConcurrentDictionary<(IInternalMutableScope, MethodInfo), ConditionalWeakTable<IInternalMutableScope, HashSet<MethodInfo>>> invalidations = new();

        private readonly ConcurrentDictionary<(IInternalMutableScope, MethodInfo), ConditionalWeakTable<IInternalMutableScope, HashSet<MethodInfo>>> dependencies = new();
        //private event PropertyChangedEventHandler OnBatchPropertyChanged;

        private class PropertyChangedHandler
        {
            internal event ScopePropertyChangedEventHandler PropertyChanged;
            internal event ScopeInvalidatedHandler ScopeInvalidated;
            internal void FirePropertyChanged(object scope, ScopePropertyChangedEvent e) => PropertyChanged?.Invoke(scope, e);
            internal void FireScopeInvalidated(object sender, IInternalMutableScope changedScope) => ScopeInvalidated?.Invoke(sender, changedScope);
        }

        private readonly Dictionary<object, PropertyChangedHandler> propertyChangedHandlers = new();

        public override void Intercept(IInvocation invocation)
        {
            if (invocation.Method.IsPropertyGetter())
                GetValue(invocation);
            else if (invocation.Method.IsPropertySetter())
                SetValue(invocation);
            else if (invocation.Method.DeclaringType == typeof(IInternalMutableScope))
            {
                switch (invocation.Method.Name)
                {

                    case nameof(IInternalMutableScope.Invalidate):
                        Invalidate(invocation);
                        return;
                    case nameof(IInternalMutableScope.GetDependencies):
                        invocation.ReturnValue = GetDependencies(invocation);
                        return;
                    case nameof(IInternalMutableScope.GetScopeRegistry):
                        invocation.ReturnValue = scopeRegistry;
                        return;
                    case nameof(IInternalMutableScope.GetScopeById):
                        invocation.ReturnValue = GetScopeById(invocation);
                        return;
                    case nameof(IInternalMutableScope.Refresh):
                        Refresh(invocation.Proxy);
                        return;
                    case nameof(IInternalMutableScope.EvaluateWithDependenciesAsync):
                        invocation.ReturnValue = EvaluateWithDependenciesAsync(invocation);
                        return;
                    case "add_ScopeInvalidated":
                        propertyChangedHandlers.GetOrAdd(invocation.Proxy, _ => new()).ScopeInvalidated += (ScopeInvalidatedHandler)invocation.Arguments.Single();
                        return;
                    case "remove_ScopeInvalidated":
                        propertyChangedHandlers.GetOrAdd(invocation.Proxy, _ => new()).ScopeInvalidated -= (ScopeInvalidatedHandler)invocation.Arguments.Single();
                        return;
                    default:
                        throw new NotSupportedException();

                }
            }
            else if (invocation.Method.DeclaringType == typeof(IMutableScope))
            {
                switch (invocation.Method.Name)
                {
                    case "add_ScopePropertyChanged":
                        propertyChangedHandlers.GetOrAdd(invocation.Proxy, _ => new()).PropertyChanged += (ScopePropertyChangedEventHandler)invocation.Arguments.Single();
                        return;
                    case "remove_ScopePropertyChanged":
                        propertyChangedHandlers.GetOrAdd(invocation.Proxy, _ => new()).PropertyChanged -= (ScopePropertyChangedEventHandler)invocation.Arguments.Single();
                        return;
                    default:
                        throw new NotSupportedException();
                }
            }
        }


        private Task EvaluateWithDependenciesAsync(IInvocation invocation)
        {
            var expression = (Func<Task<object>>)invocation.Arguments.First();
            
            return EvaluateWithDependenciesAsync(expression);
        }


        // ReSharper disable once UnusedMethodReturnValue.Local
        private async Task<EvaluateWithDependenciesResult> EvaluateWithDependenciesAsync(Func<Task<object>> expression)
        {
            using var recorder = GetDependencyRecorder();
            try
            {
                var ret = await expression();
                return new(ret, recorder.Dependencies, ExpressionChangedStatus.Success);
            }
            catch (Exception e)
            {
                return new (e, recorder.Dependencies, ExpressionChangedStatus.Error);
            }
        }

        private DependencyRecorder GetDependencyRecorder()
        {
            var recorder = DependencyRecorder.Value;
            if (recorder != null)
                return recorder;
            recorder = new DependencyRecorder(_ => DependencyRecorder.Value = null);
            DependencyRecorder.Value = recorder;
            return recorder;
        }

        private IEnumerable<(IInternalMutableScope Scope, MethodInfo Method)> GetDependencies(IInvocation invocation)
        {
            var method = (MethodInfo)invocation.Arguments.First();
            var scope = (IInternalMutableScope)invocation.Proxy;
            return GetDependencies(scope, method);
        }

        private object GetScopeById(IInvocation invocation)
        {
            var id = (Guid)invocation.Arguments.First();
            return scopeRegistry.GetScope(id);
        }

        private IEnumerable<(IInternalMutableScope Scope, MethodInfo Method)> GetDependencies(IInternalMutableScope scope, MethodInfo method)
        {
            if (!dependencies.TryGetValue((scope, method), out var deps))
                return Enumerable.Empty<(IInternalMutableScope, MethodInfo)>();

            return deps.SelectMany(d => (d.Value.Select(x => (d.Key, x))));
        }

        //private IEnumerable<(IInternalMutableScope Scope, MethodInfo Method)> ConcatenateWithSubDependencies((IInternalMutableScope Scope, MethodInfo Method) tuple)
        //{
        //    return tuple.RepeatOnce().Concat(tuple.Scope.GetDependencies(tuple.Method));
        //}

        // ReSharper disable once InconsistentNaming
        private static readonly AspectPredicate[] predicates = { x => x.IsPropertyAccessor(), x => x.DeclaringType == typeof(IInternalMutableScope), x => x.DeclaringType == typeof(IMutableScope) };
        public override IEnumerable<AspectPredicate> Predicates => predicates;

        private void Invalidate(IInvocation invocation)
        {
            var methods = (IReadOnlyCollection<MethodInfo>)invocation.Arguments[0];
            var notifications = (ICollection<(object, IInternalMutableScope)>)invocation.Arguments[1];
            Invalidate(invocation, methods, notifications);
        }

        private void Invalidate(IInvocation invocation, IReadOnlyCollection<MethodInfo> methods, ICollection<(object, IInternalMutableScope)> notifications, bool recompute = true)
        {
            var scope = (IInternalMutableScope)invocation.Proxy;
            notifications.Add((this, scope));
            foreach (var method in methods)
            {
                var key = (scope, method);
                if (Cache.TryRemove(key, out var cached))
                {
                    if (recompute)
                    {
                        var recomputesForScope = recomputes.GetOrAdd(scope, _ => new Dictionary<MethodInfo, object>());
                        recomputesForScope[method] = cached;
                    }
                }

                if (invalidations.TryRemove(key, out var deps))
                {
                    foreach (var dep in deps)
                        dep.Key.Invalidate(dep.Value, notifications);
                }
            }

        }

        private void Refresh(object scope)
        {
            if (!recomputes.TryRemove(scope, out var recomputesForScope))
                return;
            foreach (var recompute in recomputesForScope)
            {
                var value = recompute.Key.GetReflector().Invoke(scope);
                if (recompute.Value == null && value == null)
                    continue;
                if (recompute.Value != null && recompute.Value.Equals(value))
                    continue;
                FirePropertyChanged(scope, recompute.Key.Name.Substring(4), value);
            }
        }

        private void SetValue(IInvocation invocation)
        {
            // ReSharper disable once PossibleNullReferenceException
            var method = invocation.Method.DeclaringType.GetMethod(string.Concat("g", invocation.Method.Name.TrimStart('s')));
            if (method == null)
                throw new ArgumentException($"Setter without getter: {invocation.Method.Name}");
            var proxy = invocation.Proxy;

            var key = (proxy, method);
            var value = invocation.Arguments.First();

            // first step: If we already have this value cached and if it is the same, just return
            if (IsSameAsCached(key, value))
                return;
            var propertyName = method.Name.Substring(4);
            Validate(proxy, method, propertyName, value);

            var notifications = new List<(object, IInternalMutableScope)>();
            object newValue = value;

            if (method.IsAbstract)
            {
                Invalidate(invocation, new []{ method }, notifications, false);
                Cache[key] = value;
                FirePropertyChanged(proxy, propertyName, newValue);
            }
            else
            {
                var mostSpecificOverride = proxy.GetMostSpecificOverride(invocation.Method);
                DefaultImplementationOfInterfacesExtensions.DynamicInvokeNonVirtually(mostSpecificOverride, proxy, invocation.Arguments);
                //Invalidate(invocation, new[] { method }, notifications, false);
            }

            foreach (var notification in notifications.Distinct()) // Note: order is important. Distinct does not declare such behaviour but it always was implemented like this.
            {
                var interceptor = (MutableScopeInterceptor)notification.Item1;
                var scope = notification.Item2;
                interceptor.FireScopeInvalidated(scope, scope);
            }
        }


        private void FirePropertyChanged(object scope, string propertyName, object value)
        {
            if (propertyChangedHandlers.TryGetValue(scope, out var pch))
                pch.FirePropertyChanged(scope, new ScopePropertyChangedEvent(scope,scopeRegistry.GetGuid(scope), propertyName, value, ScopeChangedStatus.Committed));
        }
        private void FireScopeInvalidated(object sender, IInternalMutableScope invalidated)
        {
            if (propertyChangedHandlers.TryGetValue(sender, out var pch))
                pch.FireScopeInvalidated(sender, invalidated);
        }

        private readonly Dictionary<MethodInfo, ICollection<ValidationAttribute>> attributes = new();

        private ICollection<ValidationAttribute> GetAttributes(MethodInfo method, string propertyName)
        {
            // ReSharper disable once PossibleNullReferenceException
            return attributes.GetOrAdd(method, m => m.DeclaringType.GetProperty(propertyName)?.GetCustomAttributes<ValidationAttribute>().ToArray());
        }

        private void Validate(object scope, MethodInfo method, string propertyName, object value)
        {
            var myAttributes = GetAttributes(method, propertyName);
            if (myAttributes.Count == 0)
                return;

            var validationContext = new ValidationContext(scope)
            {
                MemberName = method.Name.Substring(4)
            };
            List<ValidationResult> validationsResults = new();
            foreach (var attribute in myAttributes)
            {
                var validationResult = attribute.GetValidationResult(value, validationContext);
                if (validationResult != null)
                    validationsResults.Add(validationResult);
            }

            if (validationsResults.Count > 0)
                throw new ValidationException(string.Join("\n", validationsResults.Select(vr => vr.ErrorMessage)));
        }

        private bool IsSameAsCached((object Proxy, MethodInfo method) key, object value)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                if (cached != null && cached.Equals(value))
                    return true;
            }

            return cached == null && value == null;
        }

        protected override void GetValue(IInvocation invocation)
        {
            var topLevel = false;
            if (DependencyTracker.Value == null)
            {
                topLevel = true;
                DependencyTracker.Value = new();
            }

            try
            {
                DependencyTracker.Value.AddGetter((IInternalMutableScope)invocation.Proxy, invocation.Method);
                base.GetValue(invocation);
                AdministerDependencies(invocation);
            }
            finally
            {
                DependencyTracker.Value.Pop();
                if (topLevel)
                    DependencyTracker.Value = null;
            }
        }

        private void AdministerDependencies(IInvocation invocation)
        {
            var key = ((IInternalMutableScope)invocation.Proxy, invocation.Method);
            AdministerDependencies(key, invalidations, DependencyTracker.Value!.GetInvalidations());
            AdministerDependencies(key, dependencies, DependencyTracker.Value.GetDependencies());
            var recorder = DependencyRecorder.Value;
            if (recorder != null)
                AddDependenciesToRecorder(invocation, recorder);
        }

        private void AddDependenciesToRecorder(IInvocation invocation, DependencyRecorder recorder)
        {
            recorder.AddDependency(((IInternalMutableScope)invocation.Proxy, invocation.Method));
        }

        private void AdministerDependencies<TKey>(TKey key, ConcurrentDictionary<TKey, ConditionalWeakTable<IInternalMutableScope, HashSet<MethodInfo>>> collection, IEnumerable<(IInternalMutableScope Scope, MethodInfo method)> enumerable)
        {
            var deps = collection.GetOrAdd(key, _ => new());
            var existingInvalidations = deps.Any();
            var hasValues = false;

            foreach (var group in enumerable.GroupBy(x => x.Scope))
            {
                if (!deps.TryGetValue(group.Key, out var hs))
                    deps.Add(group.Key, hs = new());

                // ReSharper disable once PossibleNullReferenceException
                lock (hs)
                {
                    hs.UnionWith(group.Select(g => g.method));
                }
                hasValues = true;
            }

            if (!existingInvalidations && hasValues)
                collection[key] = deps;
        }

    }
}