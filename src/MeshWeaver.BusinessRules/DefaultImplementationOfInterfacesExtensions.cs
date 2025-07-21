using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Text;

namespace MeshWeaver.BusinessRules;

/// <summary>
/// The idea of this is taken from the Moq project. It has added support for default implementations of interfaces. See
/// https://github.com/moq/moq4/pull/1130.
/// The implementations were not publicly available, hence the copy / paste.
/// </summary>
internal static class DefaultImplementationOfInterfacesExtensions
{

    private static readonly ConcurrentDictionary<MethodInfo, Func<object, object[], object>> NonVirtualInvocationThunks = new();

    /// <summary>
    ///   Performs a non-virtual (non-polymorphic) call to the given <paramref name="method"/>
    ///   using the specified object <paramref name="instance"/> and <paramref name="arguments"/>.
    /// </summary>
    internal static object DynamicInvokeNonVirtually(MethodInfo method, object instance, object[] arguments)
    {
        // There are a couple of probable alternatives to the following implementation that
        // unfortunately don't work in practice:
        //
        //  * We could try `method.Invoke(instance, InvokeMethod | DeclaredOnly, arguments)`,
        //    unfortunately that doesn't work. `DeclaredOnly` does not have the desired effect.
        //
        //  * We could get a function pointer via `method.MethodHandle.GetFunctionPointer()`,
        //    then construct a delegate for it (see ECMA-335 §II.14.4). This does not work
        //    because the delegate signature would have to have a matching parameter list,
        //    not just an untyped `object[]`. It also doesn't work because we don't always have
        //    a suitable delegate type ready (e.g. when a method has by-ref parameters).
        //
        // So we end up having to create a dynamic method that transforms the `object[]`array
        // to a properly typed argument list and then invokes the method using the IL `call`
        // instruction.

        var thunk = NonVirtualInvocationThunks.GetOrAdd(method, static method =>
        {
            var originalParameterTypes = method.GetParameterTypes();
            var n = originalParameterTypes.Length;

            var dynamicMethod = new DynamicMethod(string.Empty, returnType: typeof(object), parameterTypes: new[] { typeof(object), typeof(object[]) });
            dynamicMethod.InitLocals = true;
            var il = dynamicMethod.GetILGenerator();

            var arguments = new LocalBuilder[n];
            var returnValue = il.DeclareLocal(typeof(object));

            // Erase by-ref-ness of parameter types to get at the actual type of value.
            // We need this because we are handed `invocation.Arguments` as an `object[]` array.
            var parameterTypes = originalParameterTypes.ToArray();
            for (var i = 0; i < n; ++i)
            {
                if (parameterTypes[i].IsByRef)
                {
                    parameterTypes[i] = parameterTypes[i].GetElementType() ?? typeof(object);
                }
            }

            // Transfer `invocation.Arguments` into appropriately typed local variables.
            // This involves unboxing value-typed arguments, and possibly down-casting others from `object`.
            // The `unbox.any` instruction will do the right thing in both cases.
            for (var i = 0; i < n; ++i)
            {
                arguments[i] = il.DeclareLocal(parameterTypes[i]);

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Unbox_Any, parameterTypes[i]);
                il.Emit(OpCodes.Stloc, arguments[i]);
            }

            // Now we're going to call the actual default implementation.

            // We do this inside a `try` block because we need to write back possibly modified
            // arguments to `invocation.Arguments` even if the called method throws.
            var returnLabel = il.DefineLabel();
            il.BeginExceptionBlock();

            // Perform the actual call.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, method.DeclaringType!);
            for (var i = 0; i < n; ++i)
            {
                il.Emit(originalParameterTypes[i].IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc, arguments[i]);
            }

            il.Emit(OpCodes.Call, method);

            // Put the return value in a local variable for later retrieval.
            if (method.ReturnType != typeof(void))
            {
                il.Emit(OpCodes.Box, method.ReturnType);
                il.Emit(OpCodes.Castclass, typeof(object));
                il.Emit(OpCodes.Stloc, returnValue);
            }

            il.Emit(OpCodes.Leave, returnLabel);

            il.BeginFinallyBlock();

            // Write back possibly modified arguments to `invocation.Arguments`.
            for (var i = 0; i < n; ++i)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloc, arguments[i]);
                il.Emit(OpCodes.Box, arguments[i].LocalType);
                il.Emit(OpCodes.Stelem_Ref);
            }

            il.Emit(OpCodes.Endfinally);

            il.EndExceptionBlock();
            il.MarkLabel(returnLabel);

            il.Emit(OpCodes.Ldloc, returnValue);
            il.Emit(OpCodes.Ret);

            return (Func<object, object[], object>)dynamicMethod.CreateDelegate(typeof(Func<object, object[], object>));
        });

        return thunk.Invoke(instance, arguments);
    }


    private readonly static ConcurrentDictionary<(MethodInfo, Type), MethodInfo> MostSpecificOverrides = new();

    /// <summary>
    ///   Attempts to find the most specific override for the given method <paramref name="declaration"/>
    ///   in the type chains (base class, interfaces) of the given <paramref name="proxyType"/>.
    /// </summary>
    internal static MethodInfo FindMostSpecificOverride(MethodInfo declaration, Type proxyType)
    {
        return MostSpecificOverrides.GetOrAdd((declaration, proxyType), static key =>
        {
            // This follows the rule specified in:
            // https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-8.0/default-interface-methods#the-most-specific-override-rule.

            var (declaration, proxyType) = key;

            var genericParameterCount = declaration.IsGenericMethod ? declaration.GetGenericArguments().Length : 0;
            var parameterTypes = declaration.GetParameterTypes().ToArray();
            var declaringType = declaration.DeclaringType;

            // If the base class has a method implementation, then by rule (2) it will be more specific
            // than any candidate method from an implemented interface:
            var baseClass = proxyType.BaseType;
            if (baseClass != null && declaringType!.IsAssignableFrom(baseClass))
            {
                var map = baseClass.GetInterfaceMap(declaringType);
                var index = Array.IndexOf(map.InterfaceMethods, declaration);
                return map.TargetMethods[index];
            }

            // Otherwise, we need to look for candidates in all directly or indirectly implemented interfaces:
            var implementedInterfaces = proxyType.GetInterfaces();
            var candidateMethods = new HashSet<MethodInfo>();
            foreach (var implementedInterface in implementedInterfaces.Where(i => declaringType!.IsAssignableFrom(i)))
            {
                // Search for an implicit override:
                var candidateMethod = implementedInterface.GetMethod(declaration.Name, genericParameterCount, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null);

                // Search for an explicit override:
                if (candidateMethod?.GetBaseDefinition() != declaration)
                {
                    // Unfortunately, we cannot use `.GetInterfaceMap` to find out whether an interface method
                    // overrides another base interface method, i.e. whether they share the same vtbl slot.
                    // It appears that the best thing we can do is to look for a non-public method having
                    // the right name and parameter types, and hope for the best:
                    var name = new StringBuilder();
                    var isGeneratedByScript = declaringType!.Module?.Name == "<Unknown>";
                    name.Append(isGeneratedByScript ? declaringType.Name : declaringType.FullName);
                    name.Replace('+', '.');
                    name.Append('.');
                    name.Append(declaration.Name);
                    candidateMethod = implementedInterface.GetMethod(name.ToString(), genericParameterCount, BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null);
                }

                if (candidateMethod == null) continue;

                // Now we have a candidate override. We need to check if it is less specific than any others
                // that we have already found earlier:
                if (candidateMethods.Any(cm => cm.DeclaringType != null && implementedInterface.IsAssignableFrom(cm.DeclaringType))) continue;

                // No, it is the most specific override so far. Add it to the list, but before doing so,
                // remove all less specific overrides from it:
                candidateMethods.ExceptWith(candidateMethods.Where(cm => cm.DeclaringType?.IsAssignableFrom(implementedInterface) == true).ToArray());
                candidateMethods.Add(candidateMethod);
            }

            var candidateCount = candidateMethods.Count();
            if (candidateCount > 1)
            {
                throw new AmbiguousImplementationException();
            }
            else if (candidateCount == 1)
            {
                return candidateMethods.First();
            }
            else
            {
                return declaration;
            }
        });
    }

    private static Type[] GetParameterTypes(this MemberInfo memberInfo)
    {
        var method = memberInfo as MethodBase;
        if (method != null)
            return method.GetParameters().Select(p => p.ParameterType).ToArray();
        else
            return Type.EmptyTypes;
    }

}
