using System;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Round-trips <see cref="QrCodeControl"/>, <see cref="ShareLinkControl"/> and
/// <see cref="InstallAppControl"/> through the hub's polymorphic JSON pipeline (MeshWeaver.Plugins#2062):
/// each carries its SHORT <c>$type</c> (the reflective <c>AddLayoutTypes</c> sweep registers it) and
/// comes back typed, literal values and data-binding pointers alike — a view binds whichever it gets.
/// </summary>
public class QrShareInstallControlSerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    private T RoundTrip<T>(UiControl control) where T : UiControl
    {
        var options = GetClient().JsonSerializerOptions;
        var serialized = JsonSerializer.Serialize(control, options);
        using (var doc = JsonDocument.Parse(serialized))
            doc.RootElement.GetProperty("$type").GetString().Should().Be(typeof(T).Name);
        return JsonSerializer.Deserialize<UiControl>(serialized, options).Should().BeOfType<T>().Subject;
    }

    [Fact]
    public void QrCode_RoundTrips_TheServerEncodedMatrix()
    {
        var qr = RoundTrip<QrCodeControl>(Controls.QrCodeOf("https://portal.example.com/a/0123").WithLabel("Invitation"));
        var rows = JsonSerializer.Deserialize<string[]>(JsonSerializer.Serialize(qr.Modules));
        rows.Should().Equal(QrCodes.Encode("https://portal.example.com/a/0123"));
        qr.Label!.ToString().Should().Be("Invitation");
        qr.Size.ToString().Should().Be(QrCodeControl.DefaultSize.ToString());
    }

    [Fact]
    public void QrCode_RoundTrips_ABoundMatrix()
    {
        var qr = RoundTrip<QrCodeControl>(Controls.QrCode(new JsonPointerReference("qrModules")).WithSize(320));
        qr.Modules.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("qrModules");
        qr.Size.ToString().Should().Be("320");
    }

    /// <summary>
    /// The matrix is a square of the code's own size — version 1 is 21 modules, every version adds 4 —
    /// of '1' and '0' only, with the three finder patterns in their corners: a 7×7 dark ring at the
    /// top-left, top-right and bottom-left, which is what a camera locks on to.
    /// </summary>
    [Theory]
    [InlineData(QrErrorCorrection.Low)]
    [InlineData(QrErrorCorrection.Medium)]
    [InlineData(QrErrorCorrection.Quartile)]
    [InlineData(QrErrorCorrection.High)]
    public void Encode_IsASquareWithTheThreeFinderPatterns(string level)
    {
        var rows = QrCodes.Encode("https://portal.example.com/a/0123456789abcdef0123456789abcdef", level);
        var n = rows.Count;
        n.Should().BeInRange(21, 177);
        ((n - 21) % 4).Should().Be(0);
        rows.Should().OnlyContain(r => r.Length == n && r.All(c => c == '0' || c == '1'));
        foreach (var (top, left) in new[] { (0, 0), (0, n - 7), (n - 7, 0) })
        {
            for (var i = 0; i < 7; i++)
            {
                rows[top][left + i].Should().Be('1');
                rows[top + 6][left + i].Should().Be('1');
                rows[top + i][left].Should().Be('1');
                rows[top + i][left + 6].Should().Be('1');
            }
            rows[top + 1][left + 1].Should().Be('0');
            rows[top + 3][left + 3].Should().Be('1');
        }
        // A stronger level never yields a SMALLER code for the same text.
        QrCodes.Encode("x", QrErrorCorrection.High).Count.Should().BeGreaterThanOrEqualTo(QrCodes.Encode("x", QrErrorCorrection.Low).Count);
    }

    [Fact]
    public void Encode_EmptyText_IsTheEmptyMatrix() =>
        QrCodes.Encode("").Should().BeEmpty();

    [Fact]
    public void Encode_RefusesAnUnknownLevel_ByName()
    {
        Action act = () => QrCodes.Encode("x", "Ultra");
        act.Should().Throw<ArgumentException>().WithMessage("*'Ultra'*");
    }

    [Fact]
    public void ShareLink_RoundTrips()
    {
        var share = RoundTrip<ShareLinkControl>(Controls.ShareLink(new JsonPointerReference("url"))
            .WithTitle("Welcome")
            .WithText("Start here."));
        share.Url.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("url");
        share.Title!.ToString().Should().Be("Welcome");
        share.Text!.ToString().Should().Be("Start here.");
    }

    [Fact]
    public void InstallApp_RoundTrips()
    {
        var install = RoundTrip<InstallAppControl>(Controls.InstallApp("/a/0123/manifest.webmanifest")
            .WithAppName("Course card")
            .WithPageUrl(new JsonPointerReference("pageUrl")));
        install.ManifestHref.ToString().Should().Be("/a/0123/manifest.webmanifest");
        install.AppName!.ToString().Should().Be("Course card");
        install.PageUrl.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("pageUrl");
    }
}
