using System;
using System.IO;
using Memex.Portal.Shared.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #6640 — a module publish whose shelf landing hit a STORAGE fault (measured: <c>Access to the path
/// '/data/modules/activation.d/MeshWeaver.Markdown.Export.json' is denied.</c>) was answered 409, which
/// reads as a version clash. A storage fault is the replica failing to write: 500, saying so. A real
/// refusal of the upload stays 409 — the negative control that the split is not "everything is 500".
/// </summary>
public class PublishStorageFaultIsNotAConflictTest
{
    private const string Denied = "Access to the path '/data/modules/activation.d/MeshWeaver.Markdown.Export.json' is denied.";

    public static TheoryData<Exception> StorageFaults => new()
    {
        new UnauthorizedAccessException(Denied),
        new IOException("The process cannot access the file because it is being used by another process."),
        new InvalidOperationException("landing faulted", new UnauthorizedAccessException(Denied)),
        new AggregateException(new UnauthorizedAccessException(Denied)),
    };

    /// <summary>Every shape of a storage fault is a 500 that names the underlying error.</summary>
    [Theory]
    [MemberData(nameof(StorageFaults))]
    public void AStorageFault_IsA500_NamingTheError(Exception fault)
    {
        var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(PluginBundleEndpoints.LandingFailure(fault));
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        var error = (string)value.GetType().GetProperty("error")!.GetValue(value)!;
        Assert.Contains("infrastructure fault", error, StringComparison.Ordinal);
        Assert.Contains(fault is UnauthorizedAccessException or AggregateException or InvalidOperationException
            ? Denied : "being used by another process", error, StringComparison.Ordinal);
    }

    /// <summary>A refusal of the upload itself stays a 409 carrying its own reason.</summary>
    [Fact]
    public void ARefusalOfTheUpload_StaysA409()
    {
        var refusal = new InvalidOperationException("module name 'x y' is malformed");
        var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(PluginBundleEndpoints.LandingFailure(refusal));
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
    }
}
