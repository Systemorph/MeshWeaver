using System.Linq;
using FluentAssertions;
using MeshWeaver.Documentation.BusinessRules;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Tests for the reinsurance cession example from the Business Rules documentation.
/// Verifies the Excess-of-Loss cession engine with sample data.
/// </summary>
public class CessionExampleTest
{
    [Fact]
    public void CedeIntoLayer_ClaimBelowAttachment_FullyRetained()
    {
        var layer = CessionSampleData.Layer; // 500k xs 200k
        var claim = new Cashflow("C001", "Motor", 150_000);

        var result = CessionEngine.CedeIntoLayer([claim], layer);

        result.Should().HaveCount(1);
        result[0].CededAmount.Should().Be(0, "claim is below attachment point");
        result[0].RetainedAmount.Should().Be(150_000);
    }

    [Fact]
    public void CedeIntoLayer_ClaimWithinLayer_PartiallyCeded()
    {
        var layer = CessionSampleData.Layer; // 500k xs 200k
        var claim = new Cashflow("C002", "Motor", 350_000);

        var result = CessionEngine.CedeIntoLayer([claim], layer);

        result[0].CededAmount.Should().Be(150_000, "350k - 200k attachment = 150k ceded");
        result[0].RetainedAmount.Should().Be(200_000);
    }

    [Fact]
    public void CedeIntoLayer_ClaimAboveLimit_CappedAtLimit()
    {
        var layer = CessionSampleData.Layer; // 500k xs 200k
        var claim = new Cashflow("C003", "Motor", 800_000);

        var result = CessionEngine.CedeIntoLayer([claim], layer);

        result[0].CededAmount.Should().Be(500_000, "capped at layer limit");
        result[0].RetainedAmount.Should().Be(300_000);
    }

    [Fact]
    public void CedeIntoLayer_LargeClaim_CappedAtLimit()
    {
        var layer = CessionSampleData.Layer; // 500k xs 200k
        var claim = new Cashflow("C005", "Motor", 1_200_000);

        var result = CessionEngine.CedeIntoLayer([claim], layer);

        result[0].CededAmount.Should().Be(500_000, "1.2M claim: capped at 500k limit");
        result[0].RetainedAmount.Should().Be(700_000);
    }

    [Fact]
    public void CedeIntoLayer_AllSampleClaims_CorrectTotals()
    {
        var layer = CessionSampleData.Layer;
        var results = CessionEngine.CedeIntoLayer(CessionSampleData.Claims, layer);

        results.Should().HaveCount(10);

        // Claims below attachment (C001=150k, C004=50k, C008=180k): 0 ceded
        results.First(r => r.ClaimId == "C001").CededAmount.Should().Be(0);
        results.First(r => r.ClaimId == "C004").CededAmount.Should().Be(0);
        results.First(r => r.ClaimId == "C008").CededAmount.Should().Be(0);

        // Partially ceded: C006=250k → 50k, C010=300k → 100k, C002=350k → 150k, C007=400k → 200k
        results.First(r => r.ClaimId == "C006").CededAmount.Should().Be(50_000);
        results.First(r => r.ClaimId == "C010").CededAmount.Should().Be(100_000);
        results.First(r => r.ClaimId == "C002").CededAmount.Should().Be(150_000);
        results.First(r => r.ClaimId == "C007").CededAmount.Should().Be(200_000);

        // Capped at limit: C003=800k, C005=1.2M, C009=700k → all 500k
        results.First(r => r.ClaimId == "C003").CededAmount.Should().Be(500_000);
        results.First(r => r.ClaimId == "C005").CededAmount.Should().Be(500_000);
        results.First(r => r.ClaimId == "C009").CededAmount.Should().Be(500_000);
    }

    [Fact]
    public void Summarize_SampleClaims_CorrectStatistics()
    {
        var layer = CessionSampleData.Layer;
        var results = CessionEngine.CedeIntoLayer(CessionSampleData.Claims, layer);
        var summary = CessionEngine.Summarize(results);

        summary.ClaimCount.Should().Be(10);
        summary.TotalGross.Should().Be(4_380_000);
        summary.TotalCeded.Should().Be(2_000_000);
        summary.TotalRetained.Should().Be(2_380_000);
        summary.CessionRatio.Should().BeApproximately(0.4566, 0.001);
    }
}
