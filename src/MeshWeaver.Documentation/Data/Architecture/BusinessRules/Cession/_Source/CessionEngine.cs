// <meshweaver>
// Id: CessionEngine
// DisplayName: Cession Calculation Engine
// </meshweaver>

/// <summary>
/// Pure business logic for Excess-of-Loss cession.
/// No framework dependencies — just math.
/// </summary>
public static class CessionEngine
{
    /// <summary>
    /// Applies an XL layer to claims.
    /// Per claim: Ceded = min(Limit, max(0, Gross - AttachmentPoint))
    /// </summary>
    public static CededCashflow[] CedeIntoLayer(
        IEnumerable<Cashflow> cashflows,
        ExcessOfLossLayer layer)
    {
        return cashflows.Select(cf =>
        {
            var ceded = Math.Min(layer.Limit,
                                 Math.Max(0, cf.GrossAmount - layer.AttachmentPoint));
            return new CededCashflow
            {
                ClaimId = cf.ClaimId,
                LayerId = layer.Id,
                GrossAmount = cf.GrossAmount,
                CededAmount = ceded,
                RetainedAmount = cf.GrossAmount - ceded
            };
        }).ToArray();
    }
}
