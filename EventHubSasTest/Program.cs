using EventHubSasTest;
using Microsoft.Extensions.Configuration;

var switchMappings = new Dictionary<string, string>
{
    ["--namespace"] = "EventHub:NamespaceUri",
    ["--hub"] = "EventHub:EventHubName",
    ["--key-name"] = "EventHub:SasKeyName",
    ["--key"] = "EventHub:SasKey",
    ["--connection-string"] = "EventHub:ConnectionString",
    ["--transport"] = "EventHub:TransportType",
    ["--partition-key"] = "EventHub:PartitionKey",
    ["--proxy"] = "EventHub:Proxy",
    ["--send"] = "EventHub:SendTestMessage",
    ["--timeout"] = "EventHub:TimeoutSeconds",
    ["--show-token"] = "EventHub:ShowToken",
};

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddUserSecrets<EventHubSettings>(optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args, switchMappings)
    .Build();

var settings = configuration.GetSection("EventHub").Get<EventHubSettings>() ?? new EventHubSettings();

Console.WriteLine("Azure Event Hubs SAS connectivity test");
Console.WriteLine($"Run at {DateTimeOffset.Now:u} from {Environment.MachineName} as {Environment.UserName}");

var report = new Report();
var configurationErrors = settings.Normalize();

if (configurationErrors.Count > 0)
{
    report.Section("Configuration");

    foreach (var error in configurationErrors)
    {
        report.Record("Configuration", CheckResult.Fail, error);
    }

    report.Hint("Set these under the \"EventHub\" section of appsettings.json, or override them with " +
                "appsettings.Local.json, user secrets, EventHub__SasKey style environment variables, " +
                "or command line switches such as --key.");
    report.Summary();
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var tester = new ConnectivityTester(settings, report);
    var succeeded = await tester.RunAsync(cancellation.Token);
    return succeeded ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("Cancelled.");
    return 2;
}
