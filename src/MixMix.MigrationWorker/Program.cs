using Microsoft.Extensions.Options;
using MixMix.MigrationWorker;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Sql;
using MixMix.MigrationWorker.Sync;

var builder = Host.CreateApplicationBuilder(args);

var databricksSection = builder.Configuration.GetSection(DatabricksOptions.SectionName);

builder.Services.Configure<MigrationOptions>(builder.Configuration.GetSection(MigrationOptions.SectionName));
builder.Services.Configure<DatabricksOptions>(databricksSection);

var requestTimeout = databricksSection.Get<DatabricksOptions>()?.RequestTimeout
                     ?? new DatabricksOptions().RequestTimeout;

builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<MigrationRunStore>();
builder.Services.AddTransient<DatabricksAuthHandler>();

builder.Services
    .AddHttpClient<IDatabricksFilesClient, DatabricksFilesClient>((serviceProvider, client) =>
    {
        var databricks = serviceProvider.GetRequiredService<IOptions<DatabricksOptions>>().Value;
        databricks.Validate();

        client.BaseAddress = new Uri($"{databricks.WorkspaceUrl.TrimEnd('/')}/");
        client.Timeout = databricks.RequestTimeout;
    })
    .AddHttpMessageHandler<DatabricksAuthHandler>()
    .AddStandardResilienceHandler(resilience =>
    {
        // The defaults assume short API calls, but staged Parquet files can take minutes to stream down.
        resilience.AttemptTimeout.Timeout = requestTimeout;
        resilience.TotalRequestTimeout.Timeout = requestTimeout * 3;
        resilience.CircuitBreaker.SamplingDuration = requestTimeout * 2;
    });

// Registration order is the load order: each table is loaded after the parents its foreign keys point at.
builder.Services.AddScoped<ITableMigration, ItemCategoryMigration>();
builder.Services.AddScoped<ITableMigration, BlobMigration>();
builder.Services.AddScoped<ITableMigration, ItemMigration>();
builder.Services.AddScoped<ITableMigration, ItemBlobMigration>();

builder.Services.AddScoped<MigrationRunner>();
builder.Services.AddHostedService<MigrationSyncWorker>();

var host = builder.Build();
host.Run();
