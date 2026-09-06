namespace MeshWeaver.Payments;

/// <summary>
/// The metadata keys a MeshWeaver checkout stamps onto a provider's session and subscription, and
/// the keys its delivery reader looks for.
///
/// <para>🚨 <b>Producer and consumer must agree, and they are hours apart.</b> A subscription's
/// metadata is written once at submit and read back months later on a renewal; a typo on either
/// side is not a compile error and not a test failure — it is a renewal that arrives naming nobody,
/// and a subscriber who lapses with nothing anywhere to say why. One constant per key, so the two
/// sides cannot drift.</para>
///
/// <para>The keys are also ON THE WIRE at the provider: sessions created before a rename would hand
/// back the old key. Treat them as durable contract, exactly like configuration keys. They live in
/// the platform rather than in a payment module because the mesh-compiled commerce content that
/// STAMPS them must bind something present in every portal image, whether or not a payment module
/// is mounted.</para>
/// </summary>
public static class PaymentMetadata
{
    /// <summary>The mesh path of the order this purchase is for.</summary>
    public const string OrderPath = "orderPath";

    /// <summary>The mesh path of the package being bought (a one-off purchase).</summary>
    public const string PluginPath = "pluginPath";

    /// <summary>The viewer buying.</summary>
    public const string Buyer = "buyer";

    /// <summary>The coupon applied, when one was.</summary>
    public const string CouponCode = "couponCode";

    /// <summary>The plan tier being subscribed to (a recurring purchase).</summary>
    public const string PlanTier = "planTier";

    /// <summary>The billing cadence the plan was bought on.</summary>
    public const string Cadence = "cadence";
}

/// <summary>
/// Deployment settings the COMMERCE surfaces read directly, independent of which payment provider
/// (if any) is mounted.
/// </summary>
public static class PaymentSettings
{
    /// <summary>
    /// The configuration key of the portal's public base URL — what a checkout's success and cancel
    /// redirects are built from, and what a delivery endpoint has to be registered against.
    ///
    /// <para>Provider-neutral on purpose: a portal declares its own public URL once, and a
    /// deployment that swaps payment providers does not restate it. The key STRING is contract —
    /// it is read out of an environment variable, a Key Vault secret name and a helm values file,
    /// so renaming it is a silent deletion on every running deployment.</para>
    /// </summary>
    public const string BaseUrlConfig = "Commerce:BaseUrl";
}
