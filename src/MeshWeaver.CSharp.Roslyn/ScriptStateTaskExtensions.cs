namespace MeshWeaver.CSharp.Roslyn
{
    internal static class ScriptStateTaskExtensions
    {
        internal static async Task<T> CastAsync<S, T>(this Task<S> task) where S : T
        {
            return await task.ConfigureAwait(true);
        }
    }
}