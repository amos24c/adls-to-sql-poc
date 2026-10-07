using Azure.Messaging.EventHubs;

namespace EventHubSasTest;

/// <summary>
/// Mirrors the fields of the BizTalk Event Hubs send adapter so values can be copied
/// straight across from the send port configuration.
/// </summary>
public sealed class EventHubSettings
{
    /// <summary>Namespace URI as entered in BizTalk, e.g. sb://contoso.servicebus.windows.net/</summary>
    public string NamespaceUri { get; set; } = "";

    public string EventHubName { get; set; } = "";

    /// <summary>Shared access policy name, e.g. RootManageSharedAccessKey.</summary>
    public string SasKeyName { get; set; } = "";

    public string SasKey { get; set; } = "";

    /// <summary>Optional full connection string; when set it overrides the fields above.</summary>
    public string? ConnectionString { get; set; }

    public string? PartitionKey { get; set; }

    /// <summary>AmqpTcp (5671), AmqpWebSockets (443), or Auto to try TCP then fall back.</summary>
    public string TransportType { get; set; } = "Auto";

    /// <summary>Optional outbound proxy, e.g. http://proxy.corp.local:8080. Only applies to AmqpWebSockets.</summary>
    public string? Proxy { get; set; }

    public int SasTokenTtlMinutes { get; set; } = 20;

    public int TimeoutSeconds { get; set; } = 30;

    public bool TestHttps { get; set; } = true;

    public bool TestAmqp { get; set; } = true;

    /// <summary>When false, authentication is exercised but no event is written to the hub.</summary>
    public bool SendTestMessage { get; set; } = true;

    public string? MessagePayload { get; set; }

    /// <summary>Prints the full SAS token, including the signature, so it can be replayed with curl or Postman.</summary>
    public bool ShowToken { get; set; }

    /// <summary>A pre-issued SAS token, populated only when the connection string carries one.</summary>
    public string? SharedAccessSignature { get; private set; }

    public string FullyQualifiedNamespace { get; private set; } = "";

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 5, 300));

    public TimeSpan SasTokenTtl => TimeSpan.FromMinutes(Math.Clamp(SasTokenTtlMinutes, 1, 1440));

    /// <summary>
    /// The resource the SAS token is signed against. Lowercased so that the signed audience and the
    /// requested URL match byte for byte; Event Hubs treats entity names as case insensitive.
    /// </summary>
    public Uri HttpsResource => new($"https://{FullyQualifiedNamespace}/{EventHubName.Trim().ToLowerInvariant()}");

    /// <summary>
    /// Folds the connection string (if supplied) into the discrete fields, normalises the
    /// namespace into a bare host name, and reports anything still missing.
    /// </summary>
    public IReadOnlyList<string> Normalize()
    {
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            ApplyConnectionString(ConnectionString, errors);
        }

        FullyQualifiedNamespace = NormalizeNamespace(NamespaceUri);

        if (string.IsNullOrWhiteSpace(FullyQualifiedNamespace))
        {
            errors.Add("NamespaceUri is required, e.g. sb://contoso.servicebus.windows.net/");
        }
        else if (!FullyQualifiedNamespace.Contains('.'))
        {
            errors.Add($"NamespaceUri '{FullyQualifiedNamespace}' is not a fully qualified host name. " +
                       "Include the full suffix such as .servicebus.windows.net.");
        }

        if (string.IsNullOrWhiteSpace(EventHubName))
        {
            errors.Add("EventHubName is required. In BizTalk this is the 'Event Hub Name' field.");
        }

        if (string.IsNullOrWhiteSpace(SharedAccessSignature))
        {
            if (string.IsNullOrWhiteSpace(SasKeyName))
            {
                errors.Add("SasKeyName is required. This is the shared access policy name, not the key itself.");
            }

            if (string.IsNullOrWhiteSpace(SasKey) || SasKey.StartsWith("PASTE-", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("SasKey is required. Put the real key in appsettings.Local.json or user secrets.");
            }
            else if (!IsLikelyBase64(SasKey))
            {
                errors.Add("SasKey does not look like a base64 key. Check for a truncated or partially copied value.");
            }
        }

        if (!TestHttps && !TestAmqp)
        {
            errors.Add("TestHttps and TestAmqp are both false, so there is nothing to test.");
        }

        return errors;
    }

    /// <summary>Transports to attempt, in order.</summary>
    public IReadOnlyList<EventHubsTransportType> ResolveTransports() => TransportType.Trim().ToLowerInvariant() switch
    {
        "amqptcp" or "tcp" or "amqp" => [EventHubsTransportType.AmqpTcp],
        "amqpwebsockets" or "websockets" or "ws" => [EventHubsTransportType.AmqpWebSockets],
        _ => [EventHubsTransportType.AmqpTcp, EventHubsTransportType.AmqpWebSockets],
    };

    public bool TransportIsAuto => ResolveTransports().Count > 1;

    private void ApplyConnectionString(string connectionString, List<string> errors)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();

            switch (name.ToLowerInvariant())
            {
                case "endpoint":
                    NamespaceUri = value;
                    break;
                case "sharedaccesskeyname":
                    SasKeyName = value;
                    break;
                case "sharedaccesskey":
                    SasKey = value;
                    break;
                case "sharedaccesssignature":
                    SharedAccessSignature = value;
                    break;
                case "entitypath":
                    EventHubName = value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(NamespaceUri))
        {
            errors.Add("ConnectionString is missing the Endpoint=sb://... segment.");
        }
    }

    private static readonly string[] UriSchemes = ["sb://", "amqps://", "https://", "http://"];

    private static string NormalizeNamespace(string value)
    {
        var host = value.Trim();
        if (host.Length == 0)
        {
            return "";
        }

        foreach (var scheme in UriSchemes)
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                host = host[scheme.Length..];
                break;
            }
        }

        // Drop any trailing path (a pasted endpoint sometimes carries the entity name) and the port.
        var slash = host.IndexOf('/');
        if (slash >= 0)
        {
            host = host[..slash];
        }

        var colon = host.IndexOf(':');
        if (colon >= 0)
        {
            host = host[..colon];
        }

        return host.ToLowerInvariant();
    }

    private static bool IsLikelyBase64(string value)
    {
        Span<byte> buffer = stackalloc byte[256];
        return Convert.TryFromBase64String(value.Trim(), buffer, out _);
    }
}
