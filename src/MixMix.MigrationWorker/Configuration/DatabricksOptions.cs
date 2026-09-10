namespace MixMix.MigrationWorker.Configuration;

public enum DatabricksAuthMode
{
    PersonalAccessToken,
    ServicePrincipal
}

public sealed class DatabricksOptions
{
    public const string SectionName = "Databricks";

    /// <summary>Workspace URL, for example https://adb-1234567890123456.7.azuredatabricks.net.</summary>
    public string WorkspaceUrl { get; set; } = string.Empty;

    public DatabricksAuthMode AuthMode { get; set; } = DatabricksAuthMode.PersonalAccessToken;

    /// <summary>Required when <see cref="AuthMode"/> is <see cref="DatabricksAuthMode.PersonalAccessToken"/>.</summary>
    public string? PersonalAccessToken { get; set; }

    /// <summary>Required when <see cref="AuthMode"/> is <see cref="DatabricksAuthMode.ServicePrincipal"/>.</summary>
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceUrl))
        {
            throw new MigrationConfigurationException($"{SectionName}:{nameof(WorkspaceUrl)} must be set.");
        }

        // Checked before parsing, because a template such as https://<workspace>.azuredatabricks.net is not a
        // valid URI at all and would otherwise be reported as a malformed URL rather than an unfilled blank.
        if (IsPlaceholderUrl(WorkspaceUrl))
        {
            throw new MigrationConfigurationException(
                $"{SectionName}:{nameof(WorkspaceUrl)} is still a placeholder ('{WorkspaceUrl}'). "
                + "Use your own workspace URL: open the Azure Databricks workspace and copy the host from the "
                + "browser address bar, or read it from the Azure portal overview blade. It looks like "
                + "https://adb-0000000000000000.0.azuredatabricks.net.");
        }

        if (!Uri.TryCreate(WorkspaceUrl, UriKind.Absolute, out _))
        {
            throw new MigrationConfigurationException(
                $"{SectionName}:{nameof(WorkspaceUrl)} must be an absolute URL, but was '{WorkspaceUrl}'.");
        }

        switch (AuthMode)
        {
            case DatabricksAuthMode.PersonalAccessToken when string.IsNullOrWhiteSpace(PersonalAccessToken):
                throw new MigrationConfigurationException(
                    $"{SectionName}:{nameof(PersonalAccessToken)} must be set when AuthMode is PersonalAccessToken.");

            case DatabricksAuthMode.PersonalAccessToken when !LooksLikeDatabricksToken(PersonalAccessToken!):
                throw new MigrationConfigurationException(
                    $"{SectionName}:{nameof(PersonalAccessToken)} does not look like a Databricks credential "
                    + $"(it is {PersonalAccessToken!.Trim().Length} characters and starts with neither 'dapi' nor "
                    + "'ey'). A personal access token starts with 'dapi'; generate one in the workspace under "
                    + "Settings > Developer > Access tokens. Note that an ADLS storage account key is not "
                    + "interchangeable with a Databricks token: the Files API rejects it as an unsupported "
                    + "credential type with a bare 401.");

            case DatabricksAuthMode.ServicePrincipal
                when string.IsNullOrWhiteSpace(TenantId)
                     || string.IsNullOrWhiteSpace(ClientId)
                     || string.IsNullOrWhiteSpace(ClientSecret):
                throw new MigrationConfigurationException(
                    $"{SectionName}:{nameof(TenantId)}, {nameof(ClientId)} and {nameof(ClientSecret)} must all be set "
                    + "when AuthMode is ServicePrincipal.");
        }
    }

    /// <summary>
    /// Catches the un-substituted templates people paste in, including the 123456789012345 workspace id used
    /// throughout the Databricks documentation. Left alone, these only surface as a DNS failure deep in an
    /// HTTP call, which reads like a network outage rather than a missing setting.
    /// </summary>
    /// <summary>
    /// Databricks bearer credentials are either a personal access token ('dapi' + hex) or an Entra ID / OAuth
    /// access token, which is a JWT and so starts with 'ey'. Anything else is refused by the Files API as an
    /// unsupported credential type, which looks identical to sending no Authorization header at all.
    /// </summary>
    private static bool LooksLikeDatabricksToken(string token)
    {
        var trimmed = token.Trim();

        return trimmed.StartsWith("dapi", StringComparison.Ordinal)
               || trimmed.StartsWith("ey", StringComparison.Ordinal);
    }

    private static bool IsPlaceholderUrl(string workspaceUrl) =>
        workspaceUrl.Contains("__", StringComparison.Ordinal)
        || workspaceUrl.Contains('<')
        || workspaceUrl.Contains('>')
        || workspaceUrl.Contains("your-workspace", StringComparison.OrdinalIgnoreCase)
        || workspaceUrl.Contains("yourworkspace", StringComparison.OrdinalIgnoreCase)
        || workspaceUrl.Contains("adb-123456789012345.", StringComparison.OrdinalIgnoreCase);
}
