// <meshweaver>
// Id: CessionSampleData
// DisplayName: Sample Claims Data
// </meshweaver>

/// <summary>
/// Sample data for the cession example.
/// Motor XL layer 500k xs 200k with 10 claims spanning all scenarios.
/// </summary>
public static class CessionSampleData
{
    public static readonly ExcessOfLossLayer Layer = new()
    {
        Id = "XL1",
        Name = "Motor XL 500k xs 200k",
        AttachmentPoint = 200_000,
        Limit = 500_000
    };

    public static readonly Cashflow[] Claims =
    [
        new() { ClaimId = "C001", LineOfBusiness = "Motor", GrossAmount = 150_000 },
        new() { ClaimId = "C002", LineOfBusiness = "Motor", GrossAmount = 350_000 },
        new() { ClaimId = "C003", LineOfBusiness = "Motor", GrossAmount = 800_000 },
        new() { ClaimId = "C004", LineOfBusiness = "Motor", GrossAmount = 50_000 },
        new() { ClaimId = "C005", LineOfBusiness = "Motor", GrossAmount = 1_200_000 },
        new() { ClaimId = "C006", LineOfBusiness = "Motor", GrossAmount = 250_000 },
        new() { ClaimId = "C007", LineOfBusiness = "Motor", GrossAmount = 400_000 },
        new() { ClaimId = "C008", LineOfBusiness = "Motor", GrossAmount = 180_000 },
        new() { ClaimId = "C009", LineOfBusiness = "Motor", GrossAmount = 700_000 },
        new() { ClaimId = "C010", LineOfBusiness = "Motor", GrossAmount = 300_000 },
    ];
}
