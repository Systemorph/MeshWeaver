using Microsoft.AspNetCore.Routing;

namespace MeshWeaver.Hosting.AspNetCore;

/// <summary>
/// The endpoint-contribution hook of the module lane (design #1655): a MODULE assembly carries one
/// attribute deriving from this, and the host applies its <see cref="EndpointConfigurations"/> at
/// endpoint-mapping time (<c>app.MapMeshModuleEndpoints()</c>). Delisting the module from
/// <c>Modules:Assemblies</c> removes its routes wholesale — a 404 instead of a compiled
/// optional-service 503.
///
/// <para>This is deliberately a SEPARATE attribute from <c>MeshNodeProviderAttribute</c>:
/// endpoint contributions are HOST-level (they need <see cref="IEndpointRouteBuilder"/>, an
/// ASP.NET surface the mesh contract must not reference), and they are applied at a different
/// time — endpoint mapping, after the auth middleware — than the mesh build. A module that
/// contributes both mesh registrations and endpoints carries both attributes.</para>
///
/// <para><b>Security model</b>: a module is TRUSTED CODE the deployment chose to list — unlike
/// <c>UiContribution</c> mesh DATA, whose closed vocabulary exists because data must never widen
/// anything. Two guardrails still apply, in the HOST: every contribution maps inside a group that
/// defaults to <c>RequireAuthorization()</c> — a route is only anonymous where the module
/// explicitly says <c>AllowAnonymous()</c> — and route collisions fail the app LOUDLY at startup
/// (never last-write-wins; a silent skip is the #683 trapdoor class).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public abstract class MeshEndpointProviderAttribute : Attribute
{
    /// <summary>
    /// The module's endpoint registrations. Each action receives a route-group builder that
    /// already carries the authenticated-by-default policy; register routes exactly as in a host
    /// (<c>MapGet</c>/<c>MapPost</c>/<c>MapGrpcService</c>/…), opting out per route via
    /// <c>AllowAnonymous()</c> where a route is genuinely public (webhook inboxes, link
    /// previews).
    /// </summary>
    public abstract IEnumerable<Action<IEndpointRouteBuilder>> EndpointConfigurations { get; }
}
