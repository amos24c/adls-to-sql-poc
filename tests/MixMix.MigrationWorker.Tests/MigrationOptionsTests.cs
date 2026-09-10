using MixMix.MigrationWorker.Configuration;

namespace MixMix.MigrationWorker.Tests;

public class MigrationOptionsTests
{
    /// <summary>Options that differ from the shipped defaults only by having a real volume path filled in.</summary>
    private static MigrationOptions Configured() =>
        new() { StageRootPath = "/Volumes/demo_catalog/demo_schema/demo_volume" };

    [Fact]
    public void The_defaults_describe_the_staged_layout()
    {
        var options = Configured();

        options.Validate();

        Assert.Equal("run_", options.RunFolderPrefix);
        Assert.Equal("itemcategory", options.GetSourceFolder("ItemCategory"));
        Assert.Equal("blob", options.GetSourceFolder("Blob"));
        Assert.Equal("item", options.GetSourceFolder("Item"));
        Assert.Equal("itemblob", options.GetSourceFolder("ItemBlob"));
    }

    [Fact]
    public void The_shipped_stage_root_is_a_placeholder_and_has_to_be_filled_in()
    {
        // The repo ships a template path, so an unconfigured worker must say so rather than let the Files API
        // reject the path with a bare 'Invalid path'.
        var exception = Assert.Throws<MigrationConfigurationException>(new MigrationOptions().Validate);

        Assert.Contains("placeholder", exception.Message);
        Assert.Contains(nameof(MigrationOptions.StageRootPath), exception.Message);
    }

    [Fact]
    public void A_relative_stage_root_is_rejected_because_volume_paths_are_absolute()
    {
        var options = new MigrationOptions { StageRootPath = "migration_stage" };

        Assert.Throws<MigrationConfigurationException>(options.Validate);
    }

    [Fact]
    public void A_non_positive_batch_size_is_rejected()
    {
        var options = Configured();
        options.BatchSize = 0;

        Assert.Throws<MigrationConfigurationException>(options.Validate);
    }

    [Fact]
    public void An_interval_is_required_unless_the_worker_runs_a_single_pass()
    {
        var options = Configured();
        options.Interval = TimeSpan.Zero;

        Assert.Throws<MigrationConfigurationException>(options.Validate);

        options.RunOnce = true;
        options.Validate();
    }

    [Fact]
    public void An_unmapped_table_names_the_setting_that_needs_fixing()
    {
        var options = Configured();
        options.SourceFolders.Remove("Item");

        var exception = Assert.Throws<MigrationConfigurationException>(() => options.GetSourceFolder("Item"));

        Assert.Contains("SourceFolders", exception.Message);
    }
}

public class DatabricksOptionsTests
{
    // Starts with 'dapi' so it still exercises the format check, but is deliberately unlike a real token
    // (which is 'dapi' plus 32 hex characters): a realistic-looking value trips secret scanners on push.
    private const string ExampleToken = "dapi-example-not-a-real-token";

    [Fact]
    public void A_personal_access_token_is_required_in_token_mode()
    {
        var options = new DatabricksOptions { WorkspaceUrl = "https://adb-1.1.azuredatabricks.net" };

        Assert.Throws<MigrationConfigurationException>(options.Validate);

        options.PersonalAccessToken = ExampleToken;
        options.Validate();
    }

    [Fact]
    public void A_storage_account_key_pasted_into_the_token_field_is_rejected_up_front()
    {
        // 64 hex characters: the shape of an ADLS key, which the Files API rejects with an opaque 401.
        var options = new DatabricksOptions
        {
            WorkspaceUrl = "https://adb-1.1.azuredatabricks.net",
            PersonalAccessToken = new string('a', 64)
        };

        var exception = Assert.Throws<MigrationConfigurationException>(options.Validate);

        Assert.Contains("dapi", exception.Message);
    }

    [Fact]
    public void An_entra_id_access_token_is_accepted_in_token_mode()
    {
        // OAuth/Entra tokens are JWTs, and Databricks accepts them as bearer credentials too.
        new DatabricksOptions
        {
            WorkspaceUrl = "https://adb-1.1.azuredatabricks.net",
            PersonalAccessToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature"
        }.Validate();
    }

    [Fact]
    public void Surrounding_whitespace_on_a_pasted_token_does_not_cause_a_false_rejection()
    {
        new DatabricksOptions
        {
            WorkspaceUrl = "https://adb-1.1.azuredatabricks.net",
            PersonalAccessToken = $"  {ExampleToken}\n"
        }.Validate();
    }

    [Fact]
    public void All_three_service_principal_values_are_required()
    {
        var options = new DatabricksOptions
        {
            WorkspaceUrl = "https://adb-1.1.azuredatabricks.net",
            AuthMode = DatabricksAuthMode.ServicePrincipal,
            TenantId = "tenant",
            ClientId = "client"
        };

        Assert.Throws<MigrationConfigurationException>(options.Validate);

        options.ClientSecret = "secret";
        options.Validate();
    }

    [Theory]
    [InlineData("https://adb-123456789012345.7.azuredatabricks.net")]
    [InlineData("https://__YOUR_WORKSPACE__.azuredatabricks.net")]
    [InlineData("https://<workspace>.azuredatabricks.net")]
    [InlineData("https://your-workspace.azuredatabricks.net")]
    public void An_unsubstituted_placeholder_workspace_url_is_rejected(string workspaceUrl)
    {
        var options = new DatabricksOptions
        {
            WorkspaceUrl = workspaceUrl,
            PersonalAccessToken = "dapi-token"
        };

        var exception = Assert.Throws<MigrationConfigurationException>(options.Validate);

        Assert.Contains("placeholder", exception.Message);
    }

    [Fact]
    public void A_real_looking_workspace_url_is_accepted()
    {
        new DatabricksOptions
        {
            WorkspaceUrl = "https://adb-0000000000000000.1.azuredatabricks.net",
            PersonalAccessToken = "dapi-token"
        }.Validate();
    }

    [Fact]
    public void A_workspace_url_that_is_not_absolute_is_rejected()
    {
        var options = new DatabricksOptions
        {
            WorkspaceUrl = "adb-1.1.azuredatabricks.net",
            PersonalAccessToken = "dapi-token"
        };

        Assert.Throws<MigrationConfigurationException>(options.Validate);
    }
}
