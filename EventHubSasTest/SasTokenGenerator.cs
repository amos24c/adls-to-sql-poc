using System.Security.Cryptography;
using System.Text;

namespace EventHubSasTest;

/// <summary>
/// Builds a Service Bus / Event Hubs SAS token exactly the way the BizTalk adapter and the
/// Azure SDK do: HMAC-SHA256 over "{url-encoded resource}\n{unix expiry}" using the policy key.
/// </summary>
public static class SasTokenGenerator
{
    public static SasToken Create(Uri resource, string keyName, string key, TimeSpan timeToLive)
    {
        var expiresOn = DateTimeOffset.UtcNow.Add(timeToLive);
        var expirySeconds = expiresOn.ToUnixTimeSeconds();

        var encodedResource = Uri.EscapeDataString(resource.AbsoluteUri);
        var stringToSign = $"{encodedResource}\n{expirySeconds}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        var value = $"SharedAccessSignature sr={encodedResource}" +
                    $"&sig={Uri.EscapeDataString(signature)}" +
                    $"&se={expirySeconds}" +
                    $"&skn={Uri.EscapeDataString(keyName)}";

        return new SasToken(value, resource, keyName, expiresOn);
    }

    /// <summary>Parses the expiry out of a token that was issued elsewhere.</summary>
    public static SasToken Parse(string token, Uri fallbackResource)
    {
        var resource = fallbackResource;
        var keyName = "(from token)";
        var expiresOn = DateTimeOffset.MinValue;

        var payload = token.StartsWith("SharedAccessSignature ", StringComparison.OrdinalIgnoreCase)
            ? token["SharedAccessSignature ".Length..]
            : token;

        foreach (var pair in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Trim());

            switch (name.ToLowerInvariant())
            {
                case "sr" when Uri.TryCreate(value, UriKind.Absolute, out var parsed):
                    resource = parsed;
                    break;
                case "skn":
                    keyName = value;
                    break;
                case "se" when long.TryParse(value, out var seconds):
                    expiresOn = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    break;
            }
        }

        return new SasToken(token, resource, keyName, expiresOn);
    }
}

public sealed record SasToken(string Value, Uri Resource, string KeyName, DateTimeOffset ExpiresOn)
{
    public bool IsExpired => ExpiresOn != DateTimeOffset.MinValue && ExpiresOn <= DateTimeOffset.UtcNow;

    /// <summary>The token with the signature redacted, safe to paste into a ticket or email.</summary>
    public string Redacted
    {
        get
        {
            var index = Value.IndexOf("&sig=", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return "SharedAccessSignature sr=...&sig=<redacted>";
            }

            var end = Value.IndexOf('&', index + 5);
            var tail = end < 0 ? "" : Value[end..];
            return $"{Value[..index]}&sig=<redacted>{tail}";
        }
    }
}
