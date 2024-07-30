using System.Reflection;
using Microsoft.JSInterop;

namespace OpenSmc.Blazor
{
    public static class JsRuntimeExtensions
    {
        public static ValueTask<IJSObjectReference> Import(this IJSRuntime jsRuntime, Assembly assembly, string script) =>
            jsRuntime.InvokeAsync<IJSObjectReference>("import", $"./_content/{assembly.GetName().Name}/{script}");

        public static ValueTask<IJSObjectReference> Import(this IJSRuntime jsRuntime, string script) =>
            jsRuntime.Import(Assembly.GetCallingAssembly(), script);
    }
}
