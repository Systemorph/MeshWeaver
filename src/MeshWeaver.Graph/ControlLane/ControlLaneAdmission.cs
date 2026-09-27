using System.Text.RegularExpressions;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>
/// What the target answers a delivery with — each maps to ONE HTTP status the endpoint returns, so
/// a sender can fail on the verdict rather than on a 2xx that meant "bytes arrived" (#3312).
/// Open string constants.
/// </summary>
public static class ControlLaneVerdict
{
    /// <summary>Verified, recorded, and started → 202.</summary>
    public const string Accepted = "accepted";

    /// <summary>The lane is not armed on this instance → 503 (a misconfiguration HERE, not the caller's).</summary>
    public const string NotArmed = "not-armed";

    /// <summary>The signature is absent or does not verify → 401. Says nothing more.</summary>
    public const string SignatureInvalid = "signature-invalid";

    /// <summary>Not a lane request, or a version this target does not speak → 400.</summary>
    public const string Malformed = "malformed";

    /// <summary>The request names another deployment → 403.</summary>
    public const string WrongDeployment = "wrong-deployment";

    /// <summary>Outside its validity window → 410.</summary>
    public const string Expired = "expired";

    /// <summary>This request id was already used → 409. Single use.</summary>
    public const string Replayed = "replayed";

    /// <summary>Well-formed and authentic, but refused (unknown operation, missing approval, …) → 422.</summary>
    public const string Refused = "refused";

    /// <summary>The HTTP status a verdict is answered with.</summary>
    public static int StatusCodeOf(string verdict) => verdict switch
    {
        Accepted => 202,
        NotArmed => 503,
        SignatureInvalid => 401,
        Malformed => 400,
        WrongDeployment => 403,
        Expired => 410,
        Replayed => 409,
        _ => 422,
    };
}

/// <summary>
/// The pure admission rules a target applies to a VERIFIED request, in order, before anything is
/// recorded or read (Doc/Architecture/ControlLane → "What the target checks").
/// </summary>
public static class ControlLaneAdmission
{
    /// <summary>The longest a request may be valid for, from issue to expiry.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How far in the future an issue instant may lie (clock skew between two pods).</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>The envelope version this target speaks.</summary>
    public const int Version = 1;

    private static readonly Regex RequestId = new("^[A-Za-z0-9-]{16,64}$", RegexOptions.Compiled);
    private static readonly Regex Digest = new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// The verdict and the reason for a verified request, or <c>(Accepted, null)</c>. Pure.
    /// <paramref name="knownOperations"/> is what the registered <see cref="IControlLaneOperation"/>s
    /// claim — an unknown value is refused by name, never defaulted.
    /// </summary>
    public static (string Verdict, string? Why) Admit(
        ControlLaneRequest? request, string self, DateTimeOffset now, IReadOnlyCollection<string> knownOperations)
    {
        if (request is null)
            return (ControlLaneVerdict.Malformed, "the body is not a control-lane request");
        if (request.Version != Version)
            return (ControlLaneVerdict.Malformed, $"envelope version {request.Version} is not spoken here (this target speaks {Version})");
        if (!RequestId.IsMatch(request.RequestId ?? ""))
            return (ControlLaneVerdict.Malformed, "the request id is not 16–64 letters, digits or dashes");
        if (!string.Equals(request.Deployment, self, StringComparison.Ordinal))
            return (ControlLaneVerdict.WrongDeployment,
                $"the request names deployment '{request.Deployment}', and this instance is '{self}'");
        if (request.ExpiresAt <= request.IssuedAt || request.ExpiresAt - request.IssuedAt > MaxLifetime)
            return (ControlLaneVerdict.Expired,
                $"the validity window {request.IssuedAt:O} → {request.ExpiresAt:O} is empty or longer than {MaxLifetime.TotalMinutes:0} minutes");
        if (request.IssuedAt > now + ClockSkew)
            return (ControlLaneVerdict.Expired, $"the request was issued in the future ({request.IssuedAt:O}; now {now:O})");
        if (now >= request.ExpiresAt)
            return (ControlLaneVerdict.Expired, $"the request expired at {request.ExpiresAt:O} (now {now:O})");
        if (string.IsNullOrWhiteSpace(request.Operation) || !knownOperations.Contains(request.Operation))
            return (ControlLaneVerdict.Refused,
                $"operation '{request.Operation}' is not one this target executes (it executes: {string.Join(", ", knownOperations.OrderBy(o => o, StringComparer.Ordinal))})");
        if (string.IsNullOrWhiteSpace(request.Target) || !IsPlainPath(request.Target))
            return (ControlLaneVerdict.Refused, $"'{request.Target}' is not a plain mesh path");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return (ControlLaneVerdict.Refused, "the request carries no reason — every lane operation states why");
        if (string.IsNullOrWhiteSpace(request.Action) || !IsPlainPath(request.Action))
            return (ControlLaneVerdict.Refused, "the request names no plain control-side action path to report to");
        if (!request.DryRun)
        {
            if (!Digest.IsMatch(request.PlanDigest ?? ""))
                return (ControlLaneVerdict.Refused,
                    "a real run carries no approved plan digest — the target executes only the plan an approval bound");
            if (string.IsNullOrWhiteSpace(request.ApprovedBy))
                return (ControlLaneVerdict.Refused, "a real run carries no approver");
        }
        return (ControlLaneVerdict.Accepted, null);
    }

    /// <summary>A mesh path with no empty segment, no <c>..</c>, and no leading or trailing slash. Pure.</summary>
    public static bool IsPlainPath(string path) =>
        path.Length > 0 && path[0] != '/' && path[^1] != '/'
        && !path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '?' or '#' or '\\' or '@')
        && path.Split('/').All(segment => segment.Length > 0 && segment != ".." && segment != ".");

    /// <summary>The ledger path a request id is claimed at on the target.</summary>
    public static string LedgerPath(string requestId) => $"{LedgerNamespace}/{requestId}";

    /// <summary>Where the target's ledger lives — the Admin partition, which every instance stores
    /// and no space deletion may touch.</summary>
    public const string LedgerNamespace = "Admin/ControlLane";
}
