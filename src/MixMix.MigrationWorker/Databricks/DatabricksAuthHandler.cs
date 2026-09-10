using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;

namespace MixMix.MigrationWorker.Databricks;

public sealed class DatabricksAuthHandler : DelegatingHandler
{
    /// <summary>Well-known Entra ID application id for the Azure Databricks service.</summary>
    private const string DatabricksScope = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";

    private readonly DatabricksOptions _options;
    private readonly TokenCredential? _credential;

    public DatabricksAuthHandler(IOptions<DatabricksOptions> options)
    {
        _options = options.Value;
        _options.Validate();

        if (_options.AuthMode == DatabricksAuthMode.ServicePrincipal)
        {
            _credential = new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = _options.AuthMode switch
        {
            // Trimmed because a token pasted into config often arrives with a trailing newline, which would
            // otherwise be rejected as an invalid header value.
            DatabricksAuthMode.PersonalAccessToken =>
                new AuthenticationHeaderValue("Bearer", _options.PersonalAccessToken!.Trim()),
            DatabricksAuthMode.ServicePrincipal =>
                new AuthenticationHeaderValue("Bearer", await GetEntraTokenAsync(cancellationToken).ConfigureAwait(false)),
            _ => throw new MigrationConfigurationException($"Unsupported Databricks auth mode '{_options.AuthMode}'.")
        };

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetEntraTokenAsync(CancellationToken cancellationToken)
    {
        // ClientSecretCredential caches tokens internally, so this is cheap on the hot path.
        var token = await _credential!
            .GetTokenAsync(new TokenRequestContext([DatabricksScope]), cancellationToken)
            .ConfigureAwait(false);

        return token.Token;
    }
}
