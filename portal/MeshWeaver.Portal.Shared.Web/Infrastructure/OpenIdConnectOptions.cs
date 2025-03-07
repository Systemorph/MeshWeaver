using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MeshWeaver.Portal.Shared.Web.Infrastructure;

// Don't set values after validating/parsing options.
public sealed class OpenIdConnectOptions
{
    private string[] nameClaimTypes;
    private string[] usernameClaimTypes;

    public string NameClaimType { get; set; } = "name";
    public string UsernameClaimType { get; set; } = "preferred_username";

    /// <summary>
    /// Gets the optional name of a claim that users authenticated via OpenID Connect are required to have.
    /// If specified, users without this claim will be rejected. If <see cref="RequiredClaimValue"/>
    /// is also specified, then the value of this claim must also match <see cref="RequiredClaimValue"/>.
    /// </summary>
    public string RequiredClaimType { get; set; } = "";

    /// <summary>
    /// Gets the optional value of the <see cref="RequiredClaimType"/> claim for users authenticated via
    /// OpenID Connect. If specified, users not having this value for the corresponding claim type are
    /// rejected.
    /// </summary>
    public string RequiredClaimValue { get; set; } = "";

    public string[] GetNameClaimTypes()
    {
        Debug.Assert(nameClaimTypes is not null, "Should have been parsed during validation.");
        return nameClaimTypes;
    }

    public string[] GetUsernameClaimTypes()
    {
        Debug.Assert(usernameClaimTypes is not null, "Should have been parsed during validation.");
        return usernameClaimTypes;
    }

    internal bool TryParseOptions([NotNullWhen(false)] out IEnumerable<string> errorMessages)
    {
        List<string> messages = [];

        if (string.IsNullOrWhiteSpace(NameClaimType))
        {
            messages.Add(
                "OpenID Connect claim type for name not configured. Specify a Dashboard:Frontend:OpenIdConnect:NameClaimType value.");
        }
        else
        {
            nameClaimTypes =
                NameClaimType.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (string.IsNullOrWhiteSpace(UsernameClaimType))
        {
            messages ??= [];
            messages.Add(
                "OpenID Connect claim type for username not configured. Specify a Dashboard:Frontend:OpenIdConnect:UsernameClaimType value.");
        }
        else
        {
            usernameClaimTypes = UsernameClaimType.Split(",",
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        errorMessages = messages;

        return messages is null;
    }
}
