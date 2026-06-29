using System.Reflection;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor
{
    /// <summary>Extension methods for loading JavaScript modules from Razor class library (RCL) content packages.</summary>
    public static class JsRuntimeExtensions
    {
        /// <summary>Dynamically imports a JavaScript module from an RCL's <c>_content</c> package.</summary>
        /// <param name="jsRuntime">The JS runtime to use for the import call.</param>
        /// <param name="assembly">The RCL assembly whose <c>_content/{name}/{script}</c> path to import.</param>
        /// <param name="script">Relative path to the JS module within the assembly's content package.</param>
        /// <returns>A reference to the imported JS module.</returns>
        public static ValueTask<IJSObjectReference> Import(this IJSRuntime jsRuntime, Assembly assembly, string script) =>
            jsRuntime.InvokeAsync<IJSObjectReference>("import", $"./_content/{assembly.GetName().Name}/{script}");

        /// <summary>Imports a JavaScript module from the calling assembly's <c>_content</c> package.</summary>
        /// <param name="jsRuntime">The JS runtime to use for the import call.</param>
        /// <param name="script">Relative path to the JS module within the calling assembly's content package.</param>
        /// <returns>A reference to the imported JS module.</returns>
        public static ValueTask<IJSObjectReference> Import(this IJSRuntime jsRuntime, string script) =>
            jsRuntime.Import(Assembly.GetCallingAssembly(), script);
    }
}
