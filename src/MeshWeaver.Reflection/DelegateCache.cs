using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Utils;

namespace MeshWeaver.Reflection
{
    public static class DelegateCache
    {
        #region GetActions

        public static Action GetAction(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, Type.EmptyTypes);
            var @delegate = InnerCache.GetInstance(token);
            return (Action)@delegate;
        }

        public static Action<T1> GetAction<T1>(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, new[] { typeof(T1) });
            var @delegate = InnerCache.GetInstance(token);
            return (Action<T1>)@delegate;
        }

        public static Action<T1, T2> GetAction<T1, T2>(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, new[] { typeof(T1), typeof(T2) });
            var @delegate = InnerCache.GetInstance(token);
            return (Action<T1, T2>)@delegate;
        }

        public static Action<T1, T2, T3> GetAction<T1, T2, T3>(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, new[] { typeof(T1), typeof(T2), typeof(T3) });
            var @delegate = InnerCache.GetInstance(token);
            return (Action<T1, T2, T3>)@delegate;
        }

        public static Action<T1, T2, T3, T4> GetAction<T1, T2, T3, T4>(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) });
            var @delegate = InnerCache.GetInstance(token);
            return (Action<T1, T2, T3, T4>)@delegate;
        }

        public static Action<T1, T2, T3, T4, T5> GetAction<T1, T2, T3, T4, T5>(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5) });
            var @delegate = InnerCache.GetInstance(token);
            return (Action<T1, T2, T3, T4, T5>)@delegate;
        }

        public static Action<T1, T2, T3, T4, T5, T6> GetAction<T1, T2, T3, T4, T5, T6>(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6) });
            var @delegate = InnerCache.GetInstance(token);
            return (Action<T1, T2, T3, T4, T5, T6>)@delegate;
        }

        public static Action<T1, T2, T3, T4, T5, T6, T7> GetAction<T1, T2, T3, T4, T5, T6, T7>(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7) });
            var @delegate = InnerCache.GetInstance(token);
            return (Action<T1, T2, T3, T4, T5, T6, T7>)@delegate;
        }

        public static Action<T1, T2, T3, T4, T5, T6, T7, T8> GetAction<T1, T2, T3, T4, T5, T6, T7, T8>(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var token = Token.Action(method, args, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8) });
            var @delegate = InnerCache.GetInstance(token);
            return (Action<T1, T2, T3, T4, T5, T6, T7, T8>)@delegate;
        }

        #endregion

        #region InvokeAsActions

        public static void InvokeAsAction(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var action = GetAction(method, args);
            action();
        }

        public static void InvokeAsAction<T1>(this MethodInfo method, T1 arg1, DelegateCacheArgs? args = null)
        {
            var action = GetAction<T1>(method, args);
            action(arg1);
        }

        public static void InvokeAsAction<T1, T2>(this MethodInfo method, T1 arg1, T2 arg2, DelegateCacheArgs? args = null)
        {
            var action = GetAction<T1, T2>(method, args);
            action(arg1, arg2);
        }

        public static void InvokeAsAction<T1, T2, T3>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, DelegateCacheArgs? args = null)
        {
            var action = GetAction<T1, T2, T3>(method, args);
            action(arg1, arg2, arg3);
        }

        public static void InvokeAsAction<T1, T2, T3, T4>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, DelegateCacheArgs? args = null)
        {
            var action = GetAction<T1, T2, T3, T4>(method, args);
            action(arg1, arg2, arg3, arg4);
        }

        public static void InvokeAsAction<T1, T2, T3, T4, T5>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, DelegateCacheArgs? args = null)
        {
            var action = GetAction<T1, T2, T3, T4, T5>(method, args);
            action(arg1, arg2, arg3, arg4, arg5);
        }

        public static void InvokeAsAction<T1, T2, T3, T4, T5, T6>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, DelegateCacheArgs? args = null)
        {
            var action = GetAction<T1, T2, T3, T4, T5, T6>(method, args);
            action(arg1, arg2, arg3, arg4, arg5, arg6);
        }

        public static void InvokeAsAction<T1, T2, T3, T4, T5, T6, T7>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, DelegateCacheArgs? args = null)
        {
            var action = GetAction<T1, T2, T3, T4, T5, T6, T7>(method, args);
            action(arg1, arg2, arg3, arg4, arg5, arg6, arg7);
        }

        public static void InvokeAsAction<T1, T2, T3, T4, T5, T6, T7, T8>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, DelegateCacheArgs? args = null)
        {
            var action = GetAction<T1, T2, T3, T4, T5, T6, T7, T8>(method, args);
            action(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
        }

        #endregion

        #region InvokeAsActions

        public static async Task InvokeAsActionAsync(this MethodInfo method, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<Task>(method, args);
            await action();
        }

        public static async Task InvokeAsActionAsync<T1>(this MethodInfo method, T1 arg1, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<T1,Task>(method, args);
            await action(arg1);
        }

        public static async Task InvokeAsActionAsync<T1, T2>(this MethodInfo method, T1 arg1, T2 arg2, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<T1, T2,Task>(method, args);
            await action(arg1, arg2);
        }

        public static async Task InvokeAsActionAsync<T1, T2, T3>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<T1, T2, T3,Task>(method, args);
            await action(arg1, arg2, arg3);
        }

        public static async Task InvokeAsActionAsync<T1, T2, T3, T4>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<T1, T2, T3, T4,Task>(method, args);
            await action(arg1, arg2, arg3, arg4);
        }

        public static async Task InvokeAsActionAsync<T1, T2, T3, T4, T5>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<T1, T2, T3, T4, T5,Task>(method, args);
            await action(arg1, arg2, arg3, arg4, arg5);
        }

        public static async Task InvokeAsActionAsync<T1, T2, T3, T4, T5, T6>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<T1, T2, T3, T4, T5, T6,Task>(method, args);
            await action(arg1, arg2, arg3, arg4, arg5, arg6);
        }

        public static async Task InvokeAsActionAsync<T1, T2, T3, T4, T5, T6, T7>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<T1, T2, T3, T4, T5, T6, T7,Task>(method, args);
            await action(arg1, arg2, arg3, arg4, arg5, arg6, arg7);
        }

        public static async Task InvokeAsActionAsync<T1, T2, T3, T4, T5, T6, T7, T8>(this MethodInfo method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, DelegateCacheArgs? args = null)
        {
            var action = GetFunc<T1, T2, T3, T4, T5, T6, T7, T8,Task>(method, args);
            await action(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
        }

        #endregion

        #region GetFuncs

        public static Func<TResult> GetFunc<TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), Type.EmptyTypes);
            var @delegate = InnerCache.GetInstance(token);
            return (Func<TResult>)@delegate;
        }

        public static Func<T1, TResult> GetFunc<T1, TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), new[] { typeof(T1) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, TResult>)@delegate;
        }

        public static Func<T1, T2, TResult> GetFunc<T1, T2, TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), new[] { typeof(T1), typeof(T2) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, TResult>)@delegate;
        }

        public static Func<T1, T2, T3, TResult> GetFunc<T1, T2, T3, TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), new[] { typeof(T1), typeof(T2), typeof(T3) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, TResult>)@delegate;
        }

        public static Func<T1, T2, T3, T4, TResult> GetFunc<T1, T2, T3, T4, TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, TResult>)@delegate;
        }

        public static Func<T1, T2, T3, T4, T5, TResult> GetFunc<T1, T2, T3, T4, T5, TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, T5, TResult>)@delegate;
        }

        public static Func<T1, T2, T3, T4, T5, T6, TResult> GetFunc<T1, T2, T3, T4, T5, T6, TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, T5, T6, TResult>)@delegate;
        }

        public static Func<T1, T2, T3, T4, T5, T6, T7, TResult> GetFunc<T1, T2, T3, T4, T5, T6, T7, TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, T5, T6, T7, TResult>)@delegate;
        }

        public static Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult> GetFunc<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(TResult), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult>)@delegate;
        }

        #endregion
        #region GetFuncsAsync

        public static Func<Task<object>> GetFuncAsync(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), Type.EmptyTypes);
            var @delegate = InnerCache.GetInstance(token);
            return (Func<Task<object>>)@delegate;
        }

        public static Func<T1, Task<object>> GetFuncAsync<T1>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), new[] { typeof(T1) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, Task<object>>)@delegate;
        }

        public static Func<T1, T2, Task<object>> GetFuncAsync<T1, T2>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), new[] { typeof(T1), typeof(T2) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, Task<object>>)@delegate;
        }

        public static Func<T1, T2, T3, Task<object>> GetFuncAsync<T1, T2, T3>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), new[] { typeof(T1), typeof(T2), typeof(T3) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, Task<object>>)@delegate;
        }

        public static Func<T1, T2, T3, T4, Task<object>> GetFuncAsync<T1, T2, T3, T4>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, Task<object>>)@delegate;
        }

        public static Func<T1, T2, T3, T4, T5, Task<object>> GetFuncAsync<T1, T2, T3, T4, T5>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, T5, Task<object>>)@delegate;
        }

        public static Func<T1, T2, T3, T4, T5, T6, Task<object>> GetFuncAsync<T1, T2, T3, T4, T5, T6>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, T5, T6, Task<object>>)@delegate;
        }

        public static Func<T1, T2, T3, T4, T5, T6, T7, Task<object>> GetFuncAsync<T1, T2, T3, T4, T5, T6, T7>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, T5, T6, T7, Task<object>>)@delegate;
        }

        public static Func<T1, T2, T3, T4, T5, T6, T7, T8, Task<object>> GetFuncAsync<T1, T2, T3, T4, T5, T6, T7, T8>(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var token = Token.Func(method, args, typeof(Task<object>), new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6), typeof(T7), typeof(T8) });
            var @delegate = InnerCache.GetInstance(token);
            return (Func<T1, T2, T3, T4, T5, T6, T7, T8, Task<object>>)@delegate;
        }

        #endregion

        #region InvokeAsFuncs

        public static object InvokeAsFunction(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<object>(method, args);
            return func();
        }

        public static object InvokeAsFunction<T1>(this MethodBase method, T1 arg1, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<T1, object>(method, args);
            return func(arg1);
        }

        public static object InvokeAsFunction<T1, T2>(this MethodBase method, T1 arg1, T2 arg2, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<T1, T2, object>(method, args);
            return func(arg1, arg2);
        }

        public static object InvokeAsFunction<T1, T2, T3>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<T1, T2, T3, object>(method, args);
            return func(arg1, arg2, arg3);
        }

        public static object InvokeAsFunction<T1, T2, T3, T4>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<T1, T2, T3, T4, object>(method, args);
            return func(arg1, arg2, arg3, arg4);
        }

        public static object InvokeAsFunction<T1, T2, T3, T4, T5>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<T1, T2, T3, T4, T5, object>(method, args);
            return func(arg1, arg2, arg3, arg4, arg5);
        }

        public static object InvokeAsFunction<T1, T2, T3, T4, T5, T6>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<T1, T2, T3, T4, T5, T6, object>(method, args);
            return func(arg1, arg2, arg3, arg4, arg5, arg6);
        }

        public static object InvokeAsFunction<T1, T2, T3, T4, T5, T6, T7>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<T1, T2, T3, T4, T5, T6, T7, object>(method, args);
            return func(arg1, arg2, arg3, arg4, arg5, arg6, arg7);
        }

        public static object InvokeAsFunction<T1, T2, T3, T4, T5, T6, T7, T8>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, DelegateCacheArgs? args = null)
        {
            var func = GetFunc<T1, T2, T3, T4, T5, T6, T7, T8, object>(method, args);
            return func(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
        }

        #endregion
        #region InvokeAsFuncsAsync

        public static Task<object> InvokeAsFunctionAsync(this MethodBase method, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync(method, args);
            return func();
        }

        public static Task<object> InvokeAsFunctionAsync<T1>(this MethodBase method, T1 arg1, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync<T1>(method, args);
            return func(arg1);
        }

        public static Task<object> InvokeAsFunctionAsync<T1, T2>(this MethodBase method, T1 arg1, T2 arg2, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync<T1, T2>(method, args);
            return func(arg1, arg2);
        }

        public static Task<object> InvokeAsFunctionAsync<T1, T2, T3>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync<T1, T2, T3>(method, args);
            return func(arg1, arg2, arg3);
        }

        public static Task<object> InvokeAsFunctionAsync<T1, T2, T3, T4>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync<T1, T2, T3, T4>(method, args);
            return func(arg1, arg2, arg3, arg4);
        }

        public static Task<object> InvokeAsFunctionAsync<T1, T2, T3, T4, T5>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync<T1, T2, T3, T4, T5>(method, args);
            return func(arg1, arg2, arg3, arg4, arg5);
        }

        public static Task<object> InvokeAsFunctionAsync<T1, T2, T3, T4, T5, T6>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync<T1, T2, T3, T4, T5, T6>(method, args);
            return func(arg1, arg2, arg3, arg4, arg5, arg6);
        }

        public static Task<object> InvokeAsFunctionAsync<T1, T2, T3, T4, T5, T6, T7>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync<T1, T2, T3, T4, T5, T6, T7>(method, args);
            return func(arg1, arg2, arg3, arg4, arg5, arg6, arg7);
        }

        public static Task<object> InvokeAsFunctionAsync<T1, T2, T3, T4, T5, T6, T7, T8>(this MethodBase method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, DelegateCacheArgs? args = null)
        {
            var func = GetFuncAsync<T1, T2, T3, T4, T5, T6, T7, T8>(method, args);
            return func(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
        }

        #endregion

        #region InnerCache

        private static readonly CreatableObjectStore<Token, Delegate> InnerCache = new CreatableObjectStore<Token, Delegate>(CreateDelegate);
#pragma warning disable 4014
        private static readonly MethodInfo AsTaskMethod = ReflectionHelper.GetStaticMethodGeneric(() => TaskCast<MethodInfo, MethodBase>(default)); // <MethodInfo, MethodBase> are used as example of types of one hierarchy
#pragma warning restore 4014

        private static Delegate CreateDelegate(Token token)
        {
            ValidateToken(token);

            var method = token.Method;
            var allParameters = token.Types.Select((x, i) => Expression.Parameter(x, "arg" + i)).ToArray();

            var body = GetBodyExpression(token, allParameters, method);

            var delegateType = GetDelegateType(token);
            var lambda = Expression.Lambda(delegateType, body, allParameters);

            var compiled = lambda.Compile();
            return compiled;
        }

        private static Expression GetBodyExpression(Token token, ParameterExpression[] allParameters, MethodBase methodBase)
        {
            var ctor = methodBase as ConstructorInfo;
            var isCtor = ctor != null;

            var parameterInfos = methodBase.GetParameters();
            var methodCallParameters = allParameters.Skip(methodBase.IsStatic || isCtor ? 0 : 1)
                                                    .Zip(parameterInfos,
                                                         (pe, pi) => ConvertIfNeeded(pe, pi.ParameterType, pi.Name, token.Args))
                                                    .ToList();

            var constantExpressions = parameterInfos.Skip(methodCallParameters.Count)
                                                    .Select(p => Expression.Constant(p.DefaultValue, p.ParameterType));
            methodCallParameters.AddRange(constantExpressions);

            if (isCtor)
                return Expression.New(ctor, methodCallParameters);

            var methodInfo = methodBase as MethodInfo;
            if (methodInfo != null)
            {
                Expression body = methodBase.IsStatic
                                          ? Expression.Call(methodInfo, methodCallParameters)
                                          : Expression.Call(ConvertIfNeeded(allParameters[0], methodBase.DeclaringType, "this", token.Args),
                                                            methodInfo, methodCallParameters);

                if (methodInfo.ReturnType != typeof(void) && methodInfo.ReturnType != token.ReturnType)
                {
                    if (token.ReturnType == typeof(Task<object>))
                    {
                        var asTask = AsTaskMethod.MakeGenericMethod(methodInfo.ReturnType.GetGenericArguments()[0], typeof(object));
                        body = Expression.Call(asTask, body);
                    }
                    else
                    {
                        body = Expression.Convert(body, token.ReturnType);
                    }
                }

                return body;
            }

            throw new NotSupportedException(string.Format("{0} is not supported. Only {1} and {2} are supported.", methodBase.GetType(), typeof(MethodInfo), typeof(ConstructorInfo)));
        }

        private static void ValidateToken(Token token)
        {
            var methodBase = token.Method;
            var parameters = methodBase.GetParameters();

            var ctor = methodBase as ConstructorInfo;
            var isCtor = ctor != null;
            if (isCtor && token.ReturnType == typeof(void))
            {
                // should be impossible due to extension signatures, but just in case
                throw new ArgumentException("Invalid call. Cannot call constructor as action");
            }

            var expectedTypesCount = parameters.Length;
            var isInstanceMethod = !methodBase.IsStatic && !isCtor;
            if (isInstanceMethod)
                expectedTypesCount++;

            var actualTypesLength = token.Types.Length;
            if (expectedTypesCount < actualTypesLength)
                throw new ArgumentException(string.Format("Too many type parameters specified. Expected:{0} Actual:{1}", expectedTypesCount, token.Types.Length));
            
            if (expectedTypesCount > actualTypesLength)
            {
                var notCoveredParams = parameters.Skip(isInstanceMethod ? actualTypesLength - 1 : actualTypesLength).ToArray();
                if (notCoveredParams.Length == 0 || notCoveredParams.Any(p => !p.IsOptional))
                    throw new ArgumentException(string.Format("Too few type parameters specified. For non-static methods first parameter must be 'this'. Expected:{0} Actual:{1}", expectedTypesCount, token.Types.Length));
            }

            var method = methodBase as MethodInfo;
            if (method != null && (token.ReturnType != typeof (void) && method.ReturnType == typeof (void)))
                throw new ArgumentException(string.Format("Invalid return type. Expected:{0} Actual:{1}", typeof (void).Name, token.ReturnType));
        }

        private static Expression ConvertIfNeeded(ParameterExpression pe, Type parameterType, string parameterName, DelegateCacheArgs args)
        {
            if (pe.Type == parameterType || parameterType.IsAssignableFrom(pe.Type))
                return pe;

            if (!args.OmitCastCheck && !pe.Type.IsAssignableFrom(parameterType))
                throw new ArgumentException(string.Format("Type mismatch for parameter '{0}' : cannot convert instance of type '{1}' to '{2}'", parameterName, pe.Type, parameterType));

            return Expression.Convert(pe, parameterType);
        }

        private static Type GetDelegateType(Token token)
        {
            if (token.ReturnType == typeof(void))
                return Expression.GetActionType(token.Types);

            var typeArgs = token.Types.Concat(new[] { token.ReturnType }).ToArray();
            return Expression.GetFuncType(typeArgs);
        }

        private class Token : IEquatable<Token>
        {
            public static Token Func(MethodBase method, DelegateCacheArgs args, Type returnType, Type[] types)
            {
                return new Token(method, types, returnType, args);
            }

            public static Token Action(MethodBase method, DelegateCacheArgs args, Type[] types)
            {
                return new Token(method, types, typeof(void), args);
            }

            private readonly int hashCode;

            public readonly MethodBase Method;
            public readonly Type[] Types;
            public readonly Type ReturnType;
            public readonly DelegateCacheArgs Args;
            private readonly TypeArrayKey typesKey;

            private Token(MethodBase method, Type[] types, Type returnType, DelegateCacheArgs args)
            {
                if (method == null)
                    throw new ArgumentNullException(nameof(method));

                Args = args ?? new DelegateCacheArgs();
                Method = method;
                Types = types;
                typesKey = Types.GetKey();
                ReturnType = returnType;

                hashCode = GetHashCodeInner();
            }

            private int GetHashCodeInner()
            {
                unchecked
                {
                    var hash = (Method != null ? Method.GetHashCode() : 0);
                    hash = (hash * 397) ^ typesKey.GetHashCode();
                    hash = (hash * 397) ^ ReturnType.GetHashCode();
                    hash = (hash * 397) ^ Args.GetHashCode();
                    return hash;
                }
            }

            #region Equality

            public bool Equals(Token other)
            {
                if (ReferenceEquals(null, other))
                    return false;
                if (ReferenceEquals(this, other))
                    return true;

                return Method == other.Method
                       && typesKey.Equals(other.typesKey)
                       && ReturnType == other.ReturnType
                       && Args.Equals(other.Args);
            }

            public sealed override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                    return false;
                if (ReferenceEquals(this, obj))
                    return true;
                if (obj.GetType() != this.GetType())
                    return false;
                return Equals((Token)obj);
            }

            public sealed override int GetHashCode()
            {
                return hashCode;
            }

            #endregion

            public override string ToString()
            {
                var signature = Method.GetSignature();
                return $"Method: {Method.DeclaringType.GetSpeakingName()} {ReturnType.GetSpeakingName()} {signature}. Args: {Args}";
            }
        }

        #endregion

        /// <summary>
        /// Casts the result type of the input task as if it were covariant
        /// </summary>
        /// <typeparam name="T">The original result type of the task</typeparam>
        /// <typeparam name="TResult">The covariant type to return</typeparam>
        /// <param name="task">The target task to cast</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<TResult> TaskCast<T, TResult>(this Task<T> task)
            where T : TResult
            where TResult : class
        {
            return await task;
        }

    }

    public sealed class DelegateCacheArgs : IEquatable<DelegateCacheArgs>
    {
        public readonly bool OmitCastCheck;

        public DelegateCacheArgs(bool omitCastCheck = false)
        {
            OmitCastCheck = omitCastCheck;
        }

        #region Equality members
        public bool Equals(DelegateCacheArgs? other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return OmitCastCheck.Equals(other.OmitCastCheck);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
                return false;
            if (ReferenceEquals(this, obj))
                return true;
            return obj is DelegateCacheArgs && Equals((DelegateCacheArgs)obj);
        }

        public override int GetHashCode()
        {
            return OmitCastCheck.GetHashCode();
        } 
        #endregion

        public override string ToString()
        {
            return string.Format("OmitCastCheck: {0}", OmitCastCheck);
        }
    }
}
