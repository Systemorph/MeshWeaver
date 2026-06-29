using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Services;

/// <summary>
/// Centralized streaming autocomplete service for Blazor components.
/// Returns <see cref="IObservable{T}"/> snapshots — Monaco's
/// <c>CompletionCallback</c> subscribes once and pushes each snapshot to the suggest
/// widget as items arrive. No <c>Task</c>, no <c>await</c>, no blocking.
/// </summary>
public class BlazorAutocompleteService(
    IMeshService meshQuery,
    PortalApplication portalApplication,
    ICircuitContextAccessor circuitContextAccessor)
{
    private const int CompletionLimit = 20;

    /// <summary>
    /// Streams completion snapshots for <paramref name="query"/>. Resolves <c>@</c>-prefixed
    /// references vs free-text searches and dispatches to the appropriate mesh autocomplete.
    /// </summary>
    public IObservable<IReadOnlyList<CompletionItem>> GetCompletions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Observable.Return<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());

        if (query.StartsWith("@"))
            return GetReferenceCompletions(query[1..]);

        return Stream("", query, addressCategory: "");
    }

    /// <summary>
    /// Streams completions for <c>@</c>-references (without the <c>@</c> prefix).
    /// </summary>
    public IObservable<IReadOnlyList<CompletionItem>> GetReferenceCompletions(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return Stream("", "", addressCategory: "Addresses");

        if (reference.EndsWith("/"))
            return Stream(reference.TrimEnd('/'), "", addressCategory: "");

        // Partial match like "@Sys" — split into basePath/namePrefix.
        var lastSlash = reference.LastIndexOf('/');
        var basePath = lastSlash >= 0 ? reference[..lastSlash] : "";
        var namePrefix = lastSlash >= 0 ? reference[(lastSlash + 1)..] : reference;
        return Stream(basePath, namePrefix, addressCategory: "Addresses");
    }

    private IObservable<IReadOnlyList<CompletionItem>> Stream(
        string basePath, string namePrefix, string addressCategory) =>
        // 🚨 Run the query UNDER THE DURABLE CIRCUIT USER. Autocomplete is subscribed from a Monaco
        // JS-interop callback, where the circuit's AsyncLocal AccessContext (Context / CircuitContext)
        // has been NULLED — so a bare meshQuery.Autocomplete(...) runs as Anonymous, the query's RLS
        // clause filters out every row the user can actually read, and the suggest widget shows nothing
        // (the "autocomplete doesn't forward access tokens" report). RunUnderCircuitUser re-establishes
        // the user (ICircuitContextAccessor.UserContext survives every hop) on the HUB AccessService —
        // the same instance the query's RLS reads — held for the whole subscription so the IIoPool /
        // query hops carry it. Same pattern BlazorView.RunUnderCircuitUser uses for picker/skill/chat
        // completions; this is the ONE place that fixes every component using this shared service.
        RunUnderCircuitUser(
            meshQuery.Autocomplete(basePath, namePrefix, AutocompleteMode.RelevanceFirst, CompletionLimit)
                .Select(snapshot => (IReadOnlyList<CompletionItem>)snapshot
                    .Select(s => new CompletionItem
                    {
                        Label = s.Path,
                        InsertText = string.IsNullOrEmpty(addressCategory) ? s.Path : $"@{s.Path}",
                        Description = s.NodeType ?? s.Name,
                        Detail = s.Name,
                        Category = addressCategory
                    })
                    .ToArray()));

    /// <summary>
    /// Subscribes <paramref name="source"/> with the durable circuit user re-established on the HUB
    /// <see cref="AccessService"/> (the same singleton the mesh query's RLS reads). The identity is
    /// switched at SUBSCRIBE and held for the whole subscription via <see cref="Observable.Using{TResult,TResource}(System.Func{TResource},System.Func{TResource,System.IObservable{TResult}})"/>, so
    /// every IIoPool / synced-query hop the query fans out across carries it. Mirrors
    /// <c>BlazorView.RunUnderCircuitUser</c> for the shared (non-component) autocomplete service.
    /// </summary>
    private IObservable<T> RunUnderCircuitUser<T>(IObservable<T> source)
    {
        var hubAccess = portalApplication.Hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
            () =>
            {
                var user = ResolveCircuitUser(hubAccess);
                return user is not null && hubAccess is not null
                    ? hubAccess.SwitchAccessContext(user)
                    : (IDisposable)Disposable.Empty;
            },
            _ => source);
    }

    /// <summary>
    /// The durable circuit user, usable from a deferred (post-JS-interop) subscription. Prefers
    /// <see cref="ICircuitContextAccessor.UserContext"/> (survives every hop), then the live
    /// <see cref="AccessService.CircuitContext"/> / <see cref="AccessService.Context"/> AsyncLocals.
    /// A leaked <c>system-security</c> / hub-shaped principal is rejected — never a real user.
    /// </summary>
    private AccessContext? ResolveCircuitUser(AccessService? hubAccess)
    {
        foreach (var candidate in new[]
                 { circuitContextAccessor.UserContext, hubAccess?.CircuitContext, hubAccess?.Context })
            if (candidate is not null
                && !string.IsNullOrEmpty(candidate.ObjectId)
                && candidate.ObjectId != MeshWeaver.Mesh.Security.WellKnownUsers.System
                && !AccessService.LooksLikeHubPrincipal(candidate.ObjectId))
                return candidate;
        return null;
    }
}
