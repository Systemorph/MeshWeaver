using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Payments;

/// <summary>
/// How a caller reaches the payment provider — the ONE resolve, so no surface names an
/// implementation and every one of them answers the no-provider case the same way.
/// </summary>
public static class HubPaymentExtensions
{
    /// <summary>
    /// The payment provider this mesh has, or <c>null</c> when it has none — which is an ordinary,
    /// supported configuration meaning "this portal does not sell", never an error.
    ///
    /// <para>🚨 The null is the point. It is what makes "no payments module mounted" a state a
    /// caller must answer in code, instead of a missing type a caller cannot compile against.
    /// Nothing anywhere probes for an assembly, reflects for a provider or catches a
    /// <c>TypeLoadException</c>: the provider is either registered in this mesh's services or it is
    /// not.</para>
    /// </summary>
    public static IPaymentProvider? PaymentProvider(this IMessageHub hub) =>
        hub.ServiceProvider.GetService<IPaymentProvider>();

    /// <summary>
    /// Whether this portal takes money at all: a provider is registered AND holds the credential it
    /// would need. False both when no module is mounted and when one is mounted unconfigured — two
    /// routes to the same, correct behaviour of a portal that does not sell.
    /// </summary>
    public static bool Sells(this IMessageHub hub) => hub.PaymentProvider() is { Sells: true };

    /// <summary>
    /// This portal's public base URL, trimmed of its trailing slash; empty when it declares none.
    /// Read from <see cref="PaymentSettings.BaseUrlConfig"/>, independent of which provider (if
    /// any) is mounted — a portal states its own URL once.
    /// </summary>
    public static string CommerceBaseUrl(this IMessageHub hub) =>
        (hub.ServiceProvider.GetService<IConfiguration>()?[PaymentSettings.BaseUrlConfig] ?? string.Empty)
        .Trim()
        .TrimEnd('/');
}
