using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Memex.Portal.Shared;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Issue #2218 — every request to <c>/auth/ea/connect</c> answered HTTP 500 in memex prod:
/// <i>"Unable to resolve service for type 'MeshWeaver.Mesh.IEaGraphAuth' while attempting to
/// activate 'EaConsentController'"</i>, thrown by MVC's activator before a line of action code ran.
///
/// <para><b>Root cause: a routed controller with a feature-flagged dependency.</b>
/// <c>AddControllers().AddApplicationPart(...)</c> discovers controllers by TYPE — it has no idea
/// what any of them needs — so the consent endpoints are routed on EVERY deployment.
/// <c>IEaGraphAuth</c>, meanwhile, was registered only inside <c>if (emailOptions.Enabled)</c>.
/// Gating it did not disable the endpoint; it left the endpoint routed and unconstructible. And
/// <c>Email:Enabled</c> was the wrong condition anyway: it gates the SYSTEM mailbox's
/// app-permission sender, while the EA's delegated flow needs <c>Authentication:Microsoft</c>
/// credentials — which <c>EaGraphAuth.IsConfigured</c> already reports honestly, letting the
/// controller answer a 400 that says so instead of a 500 that reads as a bug.</para>
///
/// <para><b>Why every existing test passed.</b> <see cref="EaConnectRouteTest"/> asks the routing
/// table whether the path is served (it is) and deliberately never instantiates a controller;
/// <see cref="EaConnectReconsentTest"/> and <see cref="EaConnectOpenRedirectTest"/> construct the
/// controller with a hand-written fake. Nothing put the portal's REAL registrations and the
/// portal's REAL controllers in the same room — the only place this class of bug is visible.</para>
/// </summary>
public class EaConsentControllerActivationTest
{
    /// <summary>
    /// The portal's own service registrations at a given <c>Email:Enabled</c> setting. Nothing is
    /// started and no host is built — this is the composition, exactly as <c>Program.cs</c> runs it.
    /// </summary>
    /// <param name="emailEnabled">The <c>Email:Enabled</c> value to compose with.</param>
    /// <returns>The configured service collection.</returns>
    private static IServiceCollection PortalServices(bool emailEnabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Enabled"] = emailEnabled ? "true" : "false",
        });

        builder.ConfigureMemexServices();
        return builder.Services;
    }

    /// <summary>
    /// The regression itself: the EA consent seam is registered on a portal with mail OFF — which
    /// is the DEFAULT, not an exotic configuration.
    /// </summary>
    [Fact]
    public void EaGraphAuth_IsRegistered_EvenWithMailDisabled()
    {
        PortalServices(emailEnabled: false).Should()
            .Contain(d => d.ServiceType == typeof(IEaGraphAuth),
                "EaConsentController is routed on every deployment, so its seam must resolve on "
                + "every deployment. Whether the EA is USABLE is IEaGraphAuth.IsConfigured's answer "
                + "(Authentication:Microsoft credentials) — a 400 the controller already returns, "
                + "not a 500 from MVC's activator (issue #2218).");
    }

    /// <summary>
    /// The general form, and the one that generalises past today's instance: <b>no controller
    /// dependency may appear or disappear with a deployment flag.</b>
    ///
    /// <para>Stated as a DIFFERENCE between two compositions rather than as "everything must be
    /// registered", because the second question cannot be answered from this collection alone: the
    /// portal's controllers also take services the MESH contributes (<c>IMeshService</c>,
    /// <c>IMessageHub</c>, <c>AccessService</c>), which live in the mesh's provider — the one
    /// <c>MeshHostApplicationBuilder</c> makes the ASP.NET root provider at runtime — and are
    /// legitimately absent here. Comparing two compositions cancels those out exactly, and leaves
    /// the property that actually broke: a dependency present under one flag value and missing
    /// under another is a routed endpoint that 500s on half the deployments.</para>
    ///
    /// <para>🚨 <c>Email:Enabled</c> is the flag compared because it is the one that caused #2218.
    /// It is an EXPLICIT list of one: a new deploy-time toggle that gates a service belongs here
    /// too, and adding it is a line of config, not a redesign.</para>
    /// </summary>
    [Fact]
    public void NoControllerDependency_DependsOnADeploymentFlag()
    {
        var withMail = Registered(PortalServices(emailEnabled: true));
        var withoutMail = Registered(PortalServices(emailEnabled: false));

        var dependencies = Controllers().SelectMany(Dependencies).Distinct().ToArray();
        dependencies.Should().NotBeEmpty(
            "otherwise this test would pass by finding nothing to check — the same vacuity that "
            + "let the original bug ship");

        var flagged = dependencies
            .Where(d => withMail.Contains(d) != withoutMail.Contains(d))
            .Select(d => d.Name)
            .ToArray();

        flagged.Should().BeEmpty(
            "a controller is routed by TYPE, with no idea which flags are set, so its dependencies "
            + "must be registered unconditionally. Flag-dependent: " + string.Join(", ", flagged));
    }

    /// <summary>Non-vacuity: the controller at the centre of #2218 is actually in the sweep.</summary>
    [Fact]
    public void TheSweep_CoversTheConsentController()
    {
        Controllers().Should().Contain(typeof(EaConsentController));
        Dependencies(typeof(EaConsentController)).Should().Contain(typeof(IEaGraphAuth));
    }

    /// <summary>Every controller the portal's application part contributes.</summary>
    /// <returns>The controller types.</returns>
    private static Type[] Controllers() =>
        [.. typeof(EaConsentController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && typeof(ControllerBase).IsAssignableFrom(t))];

    /// <summary>The constructor parameter types MVC has to resolve for a controller.</summary>
    /// <param name="controller">The controller type.</param>
    /// <returns>The dependency types.</returns>
    private static IEnumerable<Type> Dependencies(Type controller) =>
        controller.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .Take(1)
            .SelectMany(c => c.GetParameters())
            // Values MVC binds itself rather than resolving from the container.
            .Where(p => !p.IsOptional
                        && p.GetCustomAttribute<FromServicesAttribute>() is null
                        && !p.ParameterType.IsPrimitive
                        && p.ParameterType != typeof(string))
            .Select(p => p.ParameterType);

    /// <summary>Folds a composition's registrations into a resolvability question.</summary>
    /// <param name="services">The composed services.</param>
    /// <returns>The set to ask about a dependency.</returns>
    private static ResolvableSet Registered(IServiceCollection services) =>
        new(services.Select(d => d.ServiceType).ToHashSet());

    /// <summary>The service types a composition can supply, closed generics folded in.</summary>
    /// <param name="serviceTypes">Every registered service type.</param>
    private sealed class ResolvableSet(HashSet<Type> serviceTypes)
    {
        /// <summary>
        /// Whether the container could supply <paramref name="dependency"/>. Closed generics count
        /// when their open form is registered — <c>ILogger&lt;T&gt;</c> is the everyday case.
        /// </summary>
        /// <param name="dependency">The dependency type.</param>
        /// <returns>Whether it resolves.</returns>
        public bool Contains(Type dependency) =>
            serviceTypes.Contains(dependency)
            || (dependency.IsGenericType && serviceTypes.Contains(dependency.GetGenericTypeDefinition()));
    }
}
