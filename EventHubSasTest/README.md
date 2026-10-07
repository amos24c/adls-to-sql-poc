# EventHubSasTest

A .NET 10 console app that proves whether a BizTalk-style SAS key can publish to an Azure Event Hub
from a given machine. It mirrors the configuration fields of the BizTalk Event Hubs send adapter, so
the values can be copied straight across from the send port, and it reports the network, credential,
and authorization layers separately instead of collapsing everything into one opaque failure.

## Configure

Put the real key in `appsettings.Local.json` (already covered by the repository `.gitignore`), not in
`appsettings.json`:

```json
{
  "EventHub": {
    "NamespaceUri": "sb://contoso.servicebus.windows.net/",
    "EventHubName": "orders",
    "SasKeyName": "BizTalkSendPolicy",
    "SasKey": "the-base64-key-from-the-portal"
  }
}
```

These four values map onto the BizTalk adapter fields:

| BizTalk send adapter field | Setting         |
| -------------------------- | --------------- |
| Event Hub Namespace URI    | `NamespaceUri`  |
| Event Hub Name             | `EventHubName`  |
| SAS Key Name               | `SasKeyName`    |
| SAS Key                    | `SasKey`        |

If the connector was configured with a full connection string instead, set `ConnectionString` and
leave the four fields blank. The `Endpoint`, `SharedAccessKeyName`, `SharedAccessKey`, `EntityPath`,
and `SharedAccessSignature` segments are all understood, and anything it supplies overrides the
discrete fields.

Settings can also come from user secrets, environment variables such as `EventHub__SasKey`, or
command line switches, in that order of increasing precedence.

### Other settings

| Setting              | Default | Purpose                                                                        |
| -------------------- | ------- | ------------------------------------------------------------------------------ |
| `TransportType`      | `Auto`  | `AmqpTcp` (5671), `AmqpWebSockets` (443), or `Auto` to try TCP then fall back.  |
| `Proxy`              | empty   | Outbound proxy for WebSockets and HTTPS, e.g. `http://proxy.corp.local:8080`.   |
| `PartitionKey`       | empty   | Sends to a specific partition, matching the adapter's partition key setting.    |
| `SasTokenTtlMinutes` | `20`    | Lifetime of the generated token.                                                |
| `TimeoutSeconds`     | `30`    | Per-attempt timeout for TCP, HTTPS, and AMQP.                                   |
| `TestHttps`          | `true`  | Runs the REST send.                                                             |
| `TestAmqp`           | `true`  | Runs the AMQP send, which is what BizTalk actually uses.                        |
| `SendTestMessage`    | `true`  | Set false to avoid writing events into a production hub.                        |
| `MessagePayload`     | empty   | Overrides the generated JSON probe with a literal payload.                      |
| `ShowToken`          | `false` | Prints the full SAS token so it can be replayed with curl or Postman.           |

## Run

```powershell
dotnet run --project EventHubSasTest
```

Everything can be overridden from the command line, which is handy for testing a second key without
editing files:

```powershell
dotnet run --project EventHubSasTest -- --hub orders --key-name SendOnly --key "..." --transport AmqpTcp
```

Available switches: `--namespace`, `--hub`, `--key-name`, `--key`, `--connection-string`,
`--transport`, `--partition-key`, `--proxy`, `--timeout`, `--send`, `--show-token`.

The process exits `0` when every check passes, `1` on any failure, and `2` if cancelled, so it can be
dropped into a scripted check.

## What it checks

1. **Configuration** — required fields, and whether the key is even shaped like a base64 value.
   Catches truncated copy/paste before anything hits the network.
2. **Network** — DNS resolution, then raw TCP to 5671 and 443. This separates a firewall problem from
   a credential problem, which is the distinction that usually matters when BizTalk fails.
3. **SAS token** — generates the token using the same HMAC-SHA256 construction the adapter and the
   Azure SDK use, and prints it with the signature redacted.
4. **HTTPS send** — posts an event through the Event Hubs REST API using that token. This is the
   cleanest test of the key itself, because it involves no SDK machinery.
5. **AMQP send** — publishes through `Azure.Messaging.EventHubs` over AMQP, the protocol the BizTalk
   adapter uses. Reads hub metadata first, which a send-only policy is not permitted to do, so an
   `INFO` line there is expected rather than a problem.

Failures carry a short interpretation. A 401 points at the key, key name, or clock skew; a 403 points
at a missing Send claim or an IP firewall; a 404 points at the entity name; a timeout on 5671 points
at the outbound firewall rules on the host.

Clock skew is measured against the `Date` header Azure returns, because SAS tokens are time-signed
and a drifted BizTalk server produces 401s that look exactly like a bad key.

## Notes

- Run it **on the BizTalk server**, under the BizTalk host instance account where possible. Passing
  from a developer workstation proves the key is good but says nothing about the firewall path from
  the server that actually sends.
- A default run writes **two** real events to the hub: one from the HTTPS check and one from the AMQP
  check. Both are tagged with `source: "EventHubSasTest"` and the correlation id printed at the top of
  the run, and the payload carries a `transport` field of `https` or `amqp` to tell them apart. Set
  `TestHttps` to false to write only the AMQP event, which is the one that matches what BizTalk does.
  Use `--send false` to write nothing, at the cost of not fully verifying authorization.
