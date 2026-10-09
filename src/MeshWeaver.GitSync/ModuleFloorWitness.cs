using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.GitSync;

/// <summary>
/// A package's floor WITNESS as its tree states it — <c>&lt;Package&gt;/mesh-floor.lock</c>, written by
/// the node repository's <c>stamp-floors</c> job (MeshWeaver.Plugins <c>scripts/mesh-floors.py</c>,
/// schema <c>mw-mesh-floor/1</c>) once a green main run compiled and tested the package's sources on
/// a platform set.
/// </summary>
/// <param name="ContentHash">The content hash of the sources the stamp verified.</param>
/// <param name="VerifiedOn">The platform set they were verified on (<c>3.0.0-ci.&lt;n&gt;</c>).</param>
public sealed record FloorWitness(string? ContentHash, string? VerifiedOn);

/// <summary>
/// 🚨 <b>Whether a package's declared floor is a FACT about the sources an import would write.</b>
/// A floor (<c>content.minMeshVersion</c>) is stamped by a later commit than the one that brought the
/// sources in: the stamp runs after main's green run, so between a merge and its stamp every package
/// whose sources changed still declares the floor of its PREVIOUS sources. Measured 2026-10-09 on
/// memex.systemorph.com: MeshWeaver.Plugins@73e5065d carried the approvals-inbox row selection
/// (Plugins#3214) while <c>Hosting</c> still declared the 08:08Z floor <c>3.0.0-ci.10305</c>, so
/// GitSync synced it onto an instance running <c>3.0.0-ci.10310</c> at 12:07Z — whose image did not
/// carry the renderer — and nothing in the approvals inbox could be selected. The stamp of 11:57Z
/// raised that same content to <c>3.0.0-ci.10317</c>.
///
/// <para>The witness says which sources its floor vouches for: its <c>contentHash</c>. This class
/// recomputes that hash for the INCOMING tree exactly as <c>mesh-floors.py content_hash</c> does — the
/// package's files (raw-byte sha256, every file but <c>manifest.lock</c>, <c>.DS_Store</c> and the
/// top-level witness), the out-of-folder entries <c>manifest.lock</c> records (a mixed package's
/// <c>src/</c> project and the siblings riding its bundle, which a Space never carries), and
/// <c>index.json</c> hashed WITHOUT its <c>minMeshVersion</c> value (Python <c>json.dumps(…,
/// sort_keys=True, ensure_ascii=False)</c> of the node) — folded by <c>gen-manifests.py
/// module_version</c>. Equal ⇒ the declared floor is verified for these sources; different ⇒ the
/// sources moved after the last stamp and the floor is NOT a fact. Pinned against 76 real packages
/// in <c>ModuleFloorWitnessTest</c>.</para>
///
/// <para>Total and tolerant: anything it cannot read (no witness, no lock, no package folder in the
/// lock, unparsable JSON) answers <c>null</c> — "not judged" — and the import behaves exactly as it did
/// before this rule. Pure.</para>
/// </summary>
public static class ModuleFloorWitness
{
    /// <summary>The witness file name (<c>mesh-floors.py WITNESS</c>).</summary>
    public const string FileName = "mesh-floor.lock";

    /// <summary>File names never hashed, at any depth (<c>gen-manifests.py EXCLUDE_FILES</c>).</summary>
    private static readonly IReadOnlySet<string> ExcludedNames =
        new HashSet<string>(StringComparer.Ordinal) { ModuleSyncDecision.ManifestFileName, ".DS_Store" };

    /// <summary>Parses a witness, or null when the text is not one.</summary>
    /// <param name="json">The <c>mesh-floor.lock</c> text.</param>
    public static FloorWitness? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return null;
            var hash = StringOf(r, "contentHash");
            var verifiedOn = StringOf(r, "verifiedOn");
            return hash is null && verifiedOn is null ? null : new FloorWitness(hash, verifiedOn);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The package content hash of an incoming module root, as <c>mesh-floors.py content_hash</c>
    /// computes it, or null when it cannot be computed.
    /// </summary>
    /// <param name="filesUnderRoot">The module root's files: path RELATIVE to the module root, and the
    /// raw bytes.</param>
    /// <param name="manifestJson">The module root's <c>manifest.lock</c> text — it names the package
    /// folder and records the out-of-folder entries.</param>
    public static string? ContentHash(
        IEnumerable<(string Path, byte[] Bytes)> filesUnderRoot, string manifestJson)
    {
        ArgumentNullException.ThrowIfNull(filesUnderRoot);
        if (ManifestFiles(manifestJson) is not { } recorded || PackageFolder(recorded) is not { } folder)
            return null;

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        byte[]? index = null;
        foreach (var (path, bytes) in filesUnderRoot)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (ExcludedNames.Contains(name) || string.Equals(path, FileName, StringComparison.Ordinal))
                continue;
            if (string.Equals(path, "index.json", StringComparison.Ordinal))
                index = bytes;
            files[$"{folder}/{path}"] = Sha256Hex(bytes);
        }
        // The out-of-folder entries — a mixed package's src/ project and the siblings riding its
        // bundle — are only ever in the lock: a Space carries the package folder alone.
        foreach (var (path, hash) in recorded)
            if (!path.StartsWith(folder + "/", StringComparison.Ordinal))
                files[path] = hash;

        if (index is not null)
        {
            if (NormalizedIndexHash(index) is not { } normalized)
                return null;
            files[$"{folder}/index.json"] = normalized;
        }
        return ModuleVersion(files);
    }

    /// <summary><c>gen-manifests.py module_version</c>: sha256 over the ordinal-sorted
    /// <c>path\nhash\n</c> pairs, truncated to 16 hex.</summary>
    internal static string ModuleVersion(IEnumerable<KeyValuePair<string, string>> files)
    {
        var text = new StringBuilder();
        foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            text.Append(path).Append('\n').Append(hash).Append('\n');
        return Sha256Hex(Encoding.UTF8.GetBytes(text.ToString()))[..16];
    }

    /// <summary><c>index.json</c> hashed without the floor's own value: the node re-serialised as
    /// Python's <c>json.dumps({**node, "content": content_without_minMeshVersion}, sort_keys=True,
    /// ensure_ascii=False)</c>.</summary>
    internal static string? NormalizedIndexHash(byte[] indexBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(indexBytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var node = new SortedDictionary<string, string>(StringComparer.Ordinal);
            JsonElement? content = null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "content")
                    content = property.Value;
                else
                    node[property.Name] = PythonJson.Dumps(property.Value);
            }
            var contentEntries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (content is { ValueKind: JsonValueKind.Object } c)
                foreach (var property in c.EnumerateObject())
                    if (property.Name != "minMeshVersion")
                        contentEntries[property.Name] = PythonJson.Dumps(property.Value);
            node["content"] = PythonJson.Object(contentEntries);
            return Sha256Hex(Encoding.UTF8.GetBytes(PythonJson.Object(node)));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? ManifestFiles(string manifestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Object)
                return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in files.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } hash)
                    map[entry.Name] = hash;
            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The package's top-level folder: the lock records its root node as
    /// <c>&lt;Folder&gt;/index.json</c>. Null when no such entry exists.</summary>
    private static string? PackageFolder(IReadOnlyDictionary<string, string> recorded)
    {
        string? folder = null;
        foreach (var path in recorded.Keys)
        {
            var slash = path.IndexOf('/');
            if (slash <= 0 || !string.Equals(path[(slash + 1)..], "index.json", StringComparison.Ordinal))
                continue;
            if (folder is not null && !string.Equals(folder, path[..slash], StringComparison.Ordinal))
                return null; // two candidate folders — not a package lock this rule can read
            folder = path[..slash];
        }
        return folder;
    }

    private static string? StringOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The subset of Python's <c>json.dumps(value, sort_keys=True, ensure_ascii=False)</c> a package
    /// node uses — so a hash Python wrote can be recomputed here byte for byte. Separators
    /// <c>", "</c> and <c>": "</c>; keys in code-point order; strings escaped only for <c>"</c>,
    /// <c>\</c> and control characters; integers verbatim; floats as Python's shortest
    /// <c>repr</c>; duplicate keys last-wins, as <c>json.loads</c> reads them.
    /// </summary>
    internal static class PythonJson
    {
        /// <summary>Serialises one value.</summary>
        public static string Dumps(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => Object(ObjectEntries(value)),
            JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Dumps)) + "]",
            JsonValueKind.String => String(value.GetString()!),
            JsonValueKind.Number => Number(value.GetRawText()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => throw new FormatException($"unexpected JSON value kind {value.ValueKind}"),
        };

        /// <summary>An object from already-serialised members, in code-point order.</summary>
        public static string Object(IEnumerable<KeyValuePair<string, string>> members)
            => "{" + string.Join(", ", members
                   .OrderBy(m => m.Key, StringComparer.Ordinal)
                   .Select(m => String(m.Key) + ": " + m.Value)) + "}";

        private static SortedDictionary<string, string> ObjectEntries(JsonElement value)
        {
            var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                entries[property.Name] = Dumps(property.Value);
            return entries;
        }

        private static string String(string text)
        {
            var builder = new StringBuilder(text.Length + 2).Append('"');
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (ch < 0x20)
                            builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(ch);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        /// <summary>An integer verbatim (Python reads it as an int); anything with a fraction or an
        /// exponent as Python's <c>float.__repr__</c>.</summary>
        private static string Number(string raw)
        {
            if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
                return BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture);
            var number = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            return FloatRepr(number);
        }

        /// <summary>Python's <c>repr(float)</c>: the shortest round-trip digits, positional when the
        /// decimal exponent is in [-4, 16), scientific (<c>1e+16</c>, <c>1.5e-05</c>) otherwise.</summary>
        internal static string FloatRepr(double number)
        {
            if (double.IsNaN(number) || double.IsInfinity(number))
                throw new FormatException("JSON does not carry NaN or Infinity");
            if (number == 0)
                return double.IsNegative(number) ? "-0.0" : "0.0";
            // "E16" is not shortest; "R" is shortest but switches notation on .NET's own rule. Take
            // R's DIGITS and re-place the decimal point by Python's rule.
            var r = number.ToString("R", CultureInfo.InvariantCulture);
            var negative = r.StartsWith('-');
            if (negative)
                r = r[1..];
            var exponent = 0;
            var e = r.IndexOfAny(['E', 'e']);
            if (e >= 0)
            {
                exponent = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                r = r[..e];
            }
            var dot = r.IndexOf('.');
            var integerPart = dot < 0 ? r : r[..dot];
            var fraction = dot < 0 ? "" : r[(dot + 1)..];
            var digits = (integerPart + fraction).TrimStart('0');
            var leadingZeros = (integerPart + fraction).Length - digits.Length;
            // decimal exponent of the first significant digit
            var decimalExponent = integerPart.Length - 1 - leadingZeros + exponent;
            digits = digits.TrimEnd('0');
            if (digits.Length == 0)
                digits = "0";

            string body;
            if (decimalExponent < -4 || decimalExponent >= 16)
            {
                var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
                body = mantissa + "e" + (decimalExponent < 0 ? "-" : "+")
                       + Math.Abs(decimalExponent).ToString("00", CultureInfo.InvariantCulture);
            }
            else if (decimalExponent < 0)
                body = "0." + new string('0', -decimalExponent - 1) + digits;
            else if (digits.Length <= decimalExponent + 1)
                body = digits + new string('0', decimalExponent + 1 - digits.Length) + ".0";
            else
                body = digits[..(decimalExponent + 1)] + "." + digits[(decimalExponent + 1)..];
            return negative ? "-" + body : body;
        }
    }
}
