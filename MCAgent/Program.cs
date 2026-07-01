using MCAgent;
using MCAgent.Commands;
using MCAgent.Options;
using MCAgent.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "MC_AGENT__");

builder.Services
    .AddOptions<AgentOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        var section = configuration.GetSection("Agent");
        if (section.Exists())
        {
            section.Bind(options);
        }

        configuration.Bind(options);
    })
    .PostConfigure(options =>
    {
        if (string.IsNullOrWhiteSpace(options.AgentVersion))
        {
            options.AgentVersion = AgentVersionProvider.GetVersion();
        }
    })
    .Validate(options => !string.IsNullOrWhiteSpace(options.ApiBaseUrl), "ApiBaseUrl is required.")
    .Validate(options => Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var parsedUri) &&
                         (parsedUri.Scheme == Uri.UriSchemeHttp || parsedUri.Scheme == Uri.UriSchemeHttps),
        "ApiBaseUrl must be an absolute HTTP/HTTPS URL.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.AuthToken), "AuthToken is required.")
    .Validate(options => options.PollIntervalSeconds is >= 5 and <= 300, "PollIntervalSeconds must be between 5 and 300.")
    .Validate(options => options.CommandBatchSize is >= 1 and <= 100, "CommandBatchSize must be between 1 and 100.")
    .Validate(options => options.HttpTimeoutSeconds is >= 5 and <= 300, "HttpTimeoutSeconds must be between 5 and 300.")
    .Validate(options => options.ErrorBackoffSeconds is >= 5 and <= 300, "ErrorBackoffSeconds must be between 5 and 300.")
    .ValidateOnStart();

builder.Services.AddSingleton<AgentRuntimeState>();
builder.Services.AddSingleton<IAgentApiClient, AgentApiClient>();
builder.Services.AddSingleton<IAgentCommandHandler, NoOpCommandHandler>();
builder.Services.AddSingleton<IAgentCommandHandler, SyncModpackCommandHandler>();
builder.Services.AddSingleton<IAgentCommandHandler, AmpConsoleCommandHandler>();
builder.Services.AddSingleton<IAgentCommandHandler, AmpConfigCommandHandler>();
builder.Services.AddSingleton<IAgentCommandHandler, SelfUpdateCommandHandler>();
builder.Services.AddHttpClient(SyncModpackCommandHandler.DownloadHttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddHttpClient(SyncModpackCommandHandler.AmpApiHttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient(SelfUpdateCommandHandler.UpdateHttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
