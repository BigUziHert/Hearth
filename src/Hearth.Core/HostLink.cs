namespace Hearth.Core;

/// <summary>A shareable host identity. Owner credentials are deliberately separate.</summary>
public sealed record HostLink(string HostUrl, string? CertificatePin = null, string? HostId = null)
{
    public override string ToString() => $"hearth://host?host={Uri.EscapeDataString(HostUrl)}" + IdentityQuery(CertificatePin, HostId);

    public static HostLink Parse(string value)
    {
        if (value is null) throw new ArgumentException("Paste the complete host connection link from Hearth.");
        if (value.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return new(HearthApi.NormalizeHost(value));
        var query = ParseQuery(value, "host", ["host", "pin", "id"]);
        var address = Address(query);
        return new(address, Pin(query, address), OptionalIdentity(query));
    }

    internal static string IdentityQuery(string? pin, string? id) =>
        (pin is null ? "" : "&pin=" + HostCertificate.NormalizePin(pin)) +
        (id is null ? "" : "&id=" + Uri.EscapeDataString(ValidateValue(id)));

    internal static Dictionary<string, string> ParseQuery(string value, string kind, string[] allowed)
    {
        var description = kind == "join" ? "invite" : "host connection link";
        var message = $"This {description} is incomplete or invalid. Ask the owner to copy a fresh {description} from Hearth.";
        if (value is null || value.Length > 8192 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "hearth" || uri.Host != kind || uri.UserInfo.Length != 0 || uri.Port != -1
            || (uri.AbsolutePath.Length != 0 && uri.AbsolutePath != "/") || uri.Fragment.Length != 0)
            throw new ArgumentException(message);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        // Uri normalizes malformed percent escapes, so validate the original query before decoding.
        var original = uri.OriginalString;
        var question = original.IndexOf('?');
        var rawQuery = question < 0 ? "" : original[(question + 1)..];
        foreach (var part in rawQuery.Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !allowed.Contains(pair[0], StringComparer.Ordinal)) throw new ArgumentException(message);
            for (var i = 0; i < pair[1].Length; i++)
                if (pair[1][i] == '%' && (i + 2 >= pair[1].Length || !Uri.IsHexDigit(pair[1][i + 1]) || !Uri.IsHexDigit(pair[1][i + 2])))
                    throw new ArgumentException(message);
            var decoded = Uri.UnescapeDataString(pair[1]);
            if (string.IsNullOrWhiteSpace(decoded) || decoded.Any(char.IsControl) || !query.TryAdd(pair[0], decoded)) throw new ArgumentException(message);
        }
        return query;
    }

    internal static string Required(Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) ? ValidateValue(value) : throw new ArgumentException("This invite or host connection link is incomplete. Copy a fresh link from Hearth.");

    private static string ValidateValue(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)
            ? throw new ArgumentException("Invalid invite or host connection link value.") : value;

    internal static string Address(Dictionary<string, string> query) => HearthApi.NormalizeHost(Required(query, "host"));
    internal static string? Pin(Dictionary<string, string> query, string address)
    {
        if (!query.TryGetValue("pin", out var value)) return null;
        if (!address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A certificate identity requires an HTTPS host address.");
        return HostCertificate.NormalizePin(value);
    }
    internal static string? OptionalIdentity(Dictionary<string, string> query) => query.TryGetValue("id", out var id) ? ValidateValue(id) : null;
}
