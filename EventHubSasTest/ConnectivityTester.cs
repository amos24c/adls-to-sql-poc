using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace EventHubSasTest;

public sealed class ConnectivityTester(EventHubSettings settings, Report report)
{
    private const int AmqpPort = 5671;
    private const int HttpsPort = 443;

    private readonly string _correlationId = Guid.NewGuid().ToString("N");

    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        DescribeConfiguration();
        var resolved = await CheckNetworkAsync(cancellationToken);

        var token = IssueToken();

        if (!resolved)
        {
            report.Record("HTTPS send", CheckResult.Skip, "the namespace host name did not resolve");
            report.Record("AMQP send", CheckResult.Skip, "the namespace host name did not resolve");
            report.Summary();
            return false;
        }

        if (settings.TestHttps)
        {
            await CheckHttpsAsync(token, cancellationToken);
        }
        else
        {
            report.Record("HTTPS send", CheckResult.Skip, "disabled via TestHttps");
        }

        if (settings.TestAmqp)
        {
            await CheckAmqpAsync(cancellationToken);
        }
        else
        {
            report.Record("AMQP send", CheckResult.Skip, "disabled via TestAmqp");
        }

        report.Summary();
        return !report.HasFailures;
    }

    private void DescribeConfiguration()
    {
        report.Section("Configuration");

        report.Record("Namespace", CheckResult.Info, settings.FullyQualifiedNamespace);
        report.Record("Event hub", CheckResult.Info, settings.EventHubName);
        report.Record("SAS policy", CheckResult.Info, string.IsNullOrEmpty(settings.SharedAccessSignature)
            ? settings.SasKeyName
            : "(pre-issued SAS token)");

        if (string.IsNullOrEmpty(settings.SharedAccessSignature))
        {
            report.Record("SAS key", CheckResult.Info, Mask(settings.SasKey));
        }

        report.Record("Transport", CheckResult.Info, settings.TransportIsAuto
            ? "Auto (AMQP over TCP 5671, falling back to WebSockets 443)"
            : settings.ResolveTransports()[0].ToString());

        if (!string.IsNullOrWhiteSpace(settings.PartitionKey))
        {
            report.Record("Partition key", CheckResult.Info, settings.PartitionKey);
        }

        if (!string.IsNullOrWhiteSpace(settings.Proxy))
        {
            report.Record("Proxy", CheckResult.Info, settings.Proxy);
        }

        report.Record("Correlation id", CheckResult.Info, _correlationId);

        if (!settings.SendTestMessage)
        {
            report.Record("Send test message", CheckResult.Warn,
                "disabled — authentication cannot be fully verified without writing an event");
        }
    }

    private async Task<bool> CheckNetworkAsync(CancellationToken cancellationToken)
    {
        report.Section("Network");

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(settings.FullyQualifiedNamespace, cancellationToken);
            report.Record("DNS resolution", CheckResult.Pass, string.Join(", ", addresses.Select(a => a.ToString())));
        }
        catch (SocketException ex)
        {
            report.Record("DNS resolution", CheckResult.Fail, ex.Message);
            report.Hint("The namespace host name does not resolve. Check NamespaceUri for typos, and confirm " +
                        "whether this network uses a Private Endpoint with a private DNS zone.");
            return false;
        }

        var amqpOpen = await CheckPortAsync(AmqpPort, cancellationToken);
        var httpsOpen = await CheckPortAsync(HttpsPort, cancellationToken);

        if (!amqpOpen && httpsOpen)
        {
            report.Hint("Port 5671 is blocked but 443 is open. The BizTalk host server needs 5671 outbound for the " +
                        "standard adapter configuration, or the adapter must be switched to WebSockets.");
        }
        else if (!amqpOpen && !httpsOpen)
        {
            report.Hint("Neither port is reachable. Check the outbound firewall rules and any proxy requirement " +
                        "on this machine, then repeat the test from the BizTalk server itself.");
        }

        return true;
    }

    private async Task<bool> CheckPortAsync(int port, CancellationToken cancellationToken)
    {
        var name = $"TCP {settings.FullyQualifiedNamespace}:{port}";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout);

            await client.ConnectAsync(settings.FullyQualifiedNamespace, port, timeout.Token);
            report.Record(name, CheckResult.Pass, $"connected in {stopwatch.ElapsedMilliseconds} ms");
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            report.Record(name, CheckResult.Warn, $"timed out after {settings.Timeout.TotalSeconds:0} s");
            return false;
        }
        catch (Exception ex)
        {
            report.Record(name, CheckResult.Warn, ex.Message);
            return false;
        }
    }

    private SasToken IssueToken()
    {
        report.Section("SAS token");

        var token = string.IsNullOrEmpty(settings.SharedAccessSignature)
            ? SasTokenGenerator.Create(settings.HttpsResource, settings.SasKeyName, settings.SasKey, settings.SasTokenTtl)
            : SasTokenGenerator.Parse(settings.SharedAccessSignature, settings.HttpsResource);

        report.Record("Token generated", CheckResult.Pass, $"expires {token.ExpiresOn:u}");
        report.Detail(settings.ShowToken ? token.Value : token.Redacted);

        if (settings.ShowToken)
        {
            report.Hint("The full token above is a live credential until it expires. Do not paste it into a ticket.");
        }

        if (token.IsExpired)
        {
            report.Record("Token validity", CheckResult.Fail, "the supplied SAS token has already expired");
        }

        return token;
    }

    private async Task CheckHttpsAsync(SasToken token, CancellationToken cancellationToken)
    {
        report.Section("HTTPS (REST) send");

        using var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(settings.Proxy))
        {
            handler.Proxy = new WebProxy(settings.Proxy);
            handler.UseProxy = true;
        }

        using var http = new HttpClient(handler) { Timeout = settings.Timeout };

        if (!settings.SendTestMessage)
        {
            report.Record("HTTPS send", CheckResult.Skip, "SendTestMessage is false");
            return;
        }

        var url = $"{settings.HttpsResource.AbsoluteUri}/messages?timeout=60&api-version=2014-01";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", token.Value);
        request.Content = new StringContent(BuildPayload("https"), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        if (!string.IsNullOrWhiteSpace(settings.PartitionKey))
        {
            var brokerProperties = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["PartitionKey"] = settings.PartitionKey,
            });
            request.Headers.TryAddWithoutValidation("BrokerProperties", brokerProperties);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            ReportClockSkew(response.Headers.Date);

            if (response.IsSuccessStatusCode)
            {
                report.Record("HTTPS send", CheckResult.Pass,
                    $"{(int)response.StatusCode} {response.StatusCode} in {stopwatch.ElapsedMilliseconds} ms");
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            report.Record("HTTPS send", CheckResult.Fail, $"{(int)response.StatusCode} {response.ReasonPhrase}");

            if (body.Length > 0)
            {
                report.Detail(Truncate(body, 400));
            }

            foreach (var hint in InterpretHttpStatus(response.StatusCode))
            {
                report.Hint(hint);
            }
        }
        catch (Exception ex)
        {
            report.Record("HTTPS send", CheckResult.Fail, Describe(ex));
        }
    }

    private async Task CheckAmqpAsync(CancellationToken cancellationToken)
    {
        report.Section("AMQP send (the protocol the BizTalk adapter uses)");

        var transports = settings.ResolveTransports();

        for (var i = 0; i < transports.Count; i++)
        {
            var transport = transports[i];
            var isLastAttempt = i == transports.Count - 1;

            if (await TrySendOverAmqpAsync(transport, isLastAttempt, cancellationToken))
            {
                return;
            }

            if (!isLastAttempt)
            {
                report.Hint($"Retrying over {transports[i + 1]}.");
            }
        }
    }

    private async Task<bool> TrySendOverAmqpAsync(
        EventHubsTransportType transport,
        bool isLastAttempt,
        CancellationToken cancellationToken)
    {
        var label = $"AMQP send ({transport})";
        var options = new EventHubProducerClientOptions
        {
            ConnectionOptions = new EventHubConnectionOptions
            {
                TransportType = transport,
                Proxy = string.IsNullOrWhiteSpace(settings.Proxy) ? null : new WebProxy(settings.Proxy),
            },
            RetryOptions = new EventHubsRetryOptions
            {
                MaximumRetries = 1,
                TryTimeout = settings.Timeout,
            },
        };

        await using var producer = CreateProducer(options);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await DescribeHubAsync(producer, cancellationToken);

            if (!settings.SendTestMessage)
            {
                report.Record(label, CheckResult.Skip, "SendTestMessage is false");
                return true;
            }

            var batchOptions = new CreateBatchOptions();
            if (!string.IsNullOrWhiteSpace(settings.PartitionKey))
            {
                batchOptions.PartitionKey = settings.PartitionKey;
            }

            using var batch = await producer.CreateBatchAsync(batchOptions, cancellationToken);

            var eventData = new EventData(Encoding.UTF8.GetBytes(BuildPayload("amqp")));
            eventData.Properties["Source"] = "EventHubSasTest";
            eventData.Properties["CorrelationId"] = _correlationId;
            eventData.ContentType = "application/json;charset=utf-8";

            if (!batch.TryAdd(eventData))
            {
                report.Record(label, CheckResult.Fail, "the test event did not fit in an empty batch");
                return false;
            }

            await producer.SendAsync(batch, cancellationToken);
            report.Record(label, CheckResult.Pass, $"1 event accepted in {stopwatch.ElapsedMilliseconds} ms");
            return true;
        }
        catch (Exception ex)
        {
            report.Record(label, isLastAttempt ? CheckResult.Fail : CheckResult.Warn, Describe(ex));

            foreach (var hint in InterpretAmqpException(ex, transport))
            {
                report.Hint(hint);
            }

            return false;
        }
    }

    private EventHubProducerClient CreateProducer(EventHubProducerClientOptions options)
    {
        if (!string.IsNullOrEmpty(settings.SharedAccessSignature))
        {
            return new EventHubProducerClient(
                settings.FullyQualifiedNamespace,
                settings.EventHubName,
                new AzureSasCredential(settings.SharedAccessSignature),
                options);
        }

        var connectionString =
            $"Endpoint=sb://{settings.FullyQualifiedNamespace}/;" +
            $"SharedAccessKeyName={settings.SasKeyName};" +
            $"SharedAccessKey={settings.SasKey}";

        return new EventHubProducerClient(connectionString, settings.EventHubName, options);
    }

    /// <summary>
    /// Reads hub metadata. A send-only policy is not allowed to do this, so an authorization
    /// failure here says nothing about whether the key can send.
    /// </summary>
    private async Task DescribeHubAsync(EventHubProducerClient producer, CancellationToken cancellationToken)
    {
        try
        {
            var properties = await producer.GetEventHubPropertiesAsync(cancellationToken);
            report.Record("Hub metadata", CheckResult.Pass,
                $"{properties.PartitionIds.Length} partition(s), created {properties.CreatedOn:u}");
        }
        catch (UnauthorizedAccessException)
        {
            report.Record("Hub metadata", CheckResult.Info,
                "not readable — expected when the policy grants Send only");
        }
        catch (Exception ex)
        {
            report.Record("Hub metadata", CheckResult.Info, $"not readable ({Describe(ex)})");
        }
    }

    private string BuildPayload(string transport)
    {
        if (!string.IsNullOrWhiteSpace(settings.MessagePayload))
        {
            return settings.MessagePayload;
        }

        return JsonSerializer.Serialize(new
        {
            source = "EventHubSasTest",
            correlationId = _correlationId,
            transport,
            sentAtUtc = DateTimeOffset.UtcNow,
            machine = Environment.MachineName,
            user = Environment.UserName,
            eventHub = settings.EventHubName,
        }, new JsonSerializerOptions { WriteIndented = false });
    }

    private void ReportClockSkew(DateTimeOffset? serverTime)
    {
        if (serverTime is null)
        {
            return;
        }

        var skew = DateTimeOffset.UtcNow - serverTime.Value;

        if (Math.Abs(skew.TotalMinutes) >= 5)
        {
            report.Record("Clock skew", CheckResult.Warn, $"{skew.TotalMinutes:0.0} minutes ahead of Azure");
            report.Hint("SAS tokens are time-signed. A clock this far out will cause 401 Unauthorized even with " +
                        "a correct key. Sync the server against a reliable time source.");
        }
        else
        {
            report.Record("Clock skew", CheckResult.Pass, $"{skew.TotalSeconds:0.0} s from Azure");
        }
    }

    private static IEnumerable<string> InterpretHttpStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized =>
        [
            "401 means the signature was rejected. The usual causes are a mistyped or truncated SAS key, " +
            "a SasKeyName that does not match the policy the key belongs to, a key that was regenerated in " +
            "Azure, or a server clock that is out of sync.",
        ],
        HttpStatusCode.Forbidden =>
        [
            "403 means the credentials were understood but not allowed. Confirm the shared access policy " +
            "grants Send, and check whether the namespace has an IP firewall or Private Endpoint that " +
            "excludes this machine.",
        ],
        HttpStatusCode.NotFound =>
        [
            "404 means the namespace authenticated but the entity path was not found. Check EventHubName " +
            "for typos, and confirm the policy is scoped to this hub rather than a different one.",
        ],
        HttpStatusCode.BadRequest =>
        [
            "400 usually points at a malformed request rather than the key. Check the partition key and payload.",
        ],
        _ => [],
    };

    private static IEnumerable<string> InterpretAmqpException(Exception exception, EventHubsTransportType transport)
    {
        if (exception is UnauthorizedAccessException)
        {
            yield return "The key or policy name was rejected. Compare SasKeyName and SasKey against the shared " +
                         "access policy in the Azure portal, and confirm the key has not been regenerated.";
            yield break;
        }

        if (exception is EventHubsException eventHubsException)
        {
            switch (eventHubsException.Reason)
            {
                case EventHubsException.FailureReason.ResourceNotFound:
                    yield return "The event hub was not found in this namespace. Check EventHubName.";
                    break;
                case EventHubsException.FailureReason.ServiceCommunicationProblem:
                    yield return transport == EventHubsTransportType.AmqpTcp
                        ? "The AMQP connection on port 5671 could not be established. This is normally an outbound " +
                          "firewall or proxy rule rather than a credential problem."
                        : "The WebSocket connection on port 443 could not be established. If this network requires " +
                          "a proxy, set the Proxy value in appsettings.json.";
                    break;
                case EventHubsException.FailureReason.QuotaExceeded:
                    yield return "The namespace throttled the request. Retry, or check the throughput units on the namespace.";
                    break;
                case EventHubsException.FailureReason.MessageSizeExceeded:
                    yield return "The payload is larger than the namespace allows.";
                    break;
            }

            yield break;
        }

        if (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            yield return $"The connection attempt did not complete. Confirm outbound access to " +
                         $"{(transport == EventHubsTransportType.AmqpTcp ? "port 5671" : "port 443")} from this machine.";
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        EventHubsException eventHubsException => $"{eventHubsException.Reason}: {eventHubsException.Message}",
        AggregateException aggregate => Describe(aggregate.Flatten().InnerExceptions[0]),
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    private static string Mask(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 6
            ? $"(set, {trimmed.Length} chars)"
            : $"{trimmed[..4]}…{trimmed[^2..]} ({trimmed.Length} chars)";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : $"{value[..max]}…";
}
