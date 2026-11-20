// The declared physical site of a host record (Plugins Hosting/MacStudioFleet §2c): where a machine of kind
// mac-host is meant to stand. Zero MeshWeaver dependencies, like every type in this assembly.
using System;
using System.ComponentModel;

namespace MeshWeaver.Deployment;

/// <summary>
/// Where a host is DECLARED to stand — the premises a machine of kind <c>mac-host</c> was handed to
/// (an office, a client's premises, a rack). Intent, recorded in git like the rest of the record; where
/// the machine's heartbeat actually comes from is OBSERVED by the control instance and stored on the
/// host's status, never here (Plugins Hosting/MacStudioFleet §2c). The two are compared on the fleet
/// board, and a disagreement is shown as a warning.
///
/// <para>Every field is optional: a site may be declared before its address is complete. An address is
/// identifying data about premises, not a secret, but it is still only what the board needs — no
/// contact person, no access code, no phone number.</para>
/// </summary>
public sealed record HostSite
{
    /// <summary>The name of the site — the organisation or building the host stands in (e.g. <c>Fabrikam AG, head office</c>).</summary>
    [Description("Site name — the organisation or building the host stands in")]
    public string? Name { get; init; }

    /// <summary>Street and number.</summary>
    [Description("Street and number")]
    public string? Street { get; init; }

    /// <summary>Postal code, as the country writes it.</summary>
    [Description("Postal code")]
    public string? PostalCode { get; init; }

    /// <summary>City or municipality, as the address writes it.</summary>
    [Description("City or municipality")]
    public string? City { get; init; }

    /// <summary>
    /// The country as an ISO 3166-1 alpha-2 code (<c>CH</c>, <c>DE</c>, …) — the form IP geolocation
    /// reports, so the two compare without a name table.
    /// </summary>
    [Description("Country, ISO 3166-1 alpha-2 (e.g. CH)")]
    public string? Country { get; init; }

    /// <summary>Latitude of the site in decimal degrees (WGS 84). Approximate is enough: the board compares cities, not metres.</summary>
    [Description("Latitude, decimal degrees (WGS 84)")]
    public double? Latitude { get; init; }

    /// <summary>Longitude of the site in decimal degrees (WGS 84).</summary>
    [Description("Longitude, decimal degrees (WGS 84)")]
    public double? Longitude { get; init; }

    /// <summary>The day the host has stood at this site — a calendar fact, never converted to a time zone.</summary>
    [Description("At this site since (date)")]
    public DateOnly? Since { get; init; }

    /// <summary>Free text for the operator (who placed it, which room). Nothing secret: the record syncs to git.</summary>
    [Description("Notes on the site — nothing secret")]
    public string? Notes { get; init; }
}
