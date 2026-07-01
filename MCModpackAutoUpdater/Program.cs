using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MCAgent.Commands;
using MCAgent.Options;
using MCAgent.Services;
using MCModpackAutoUpdater;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Options;
using MCModpackAutoUpdater.Security;
using MCModpackAutoUpdater.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "MC_UPDATER__");
builder.Configuration.AddEnvironmentVariables(prefix: "MC_AGENT__");

builder.Services
    .AddOptions<WebUiOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        var section = configuration.GetSection("WebUi");
        if (section.Exists())
        {
            section.Bind(options);
        }
    })
    .Validate(options => options.SessionMinutes is >= 5 and <= 10080, "WebUi:SessionMinutes must be between 5 and 10080.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DatabasePath), "WebUi:DatabasePath is required.")
    .ValidateOnStart();

builder.Services
    .AddOptions<StandaloneUpdaterOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        var section = configuration.GetSection("Updater");
        if (section.Exists())
        {
            section.Bind(options);
        }

        configuration.Bind(options);
    })
    .Validate(options => options.LoopDelaySeconds is >= 5 and <= 3600, "LoopDelaySeconds must be between 5 and 3600.")
    .Validate(options => CanResolveTimeZone(options.ScheduleTimeZone), "ScheduleTimeZone must be Local, UTC, or a time zone ID available on this host.")
    .Validate(
        options => !options.AmpController.Enabled ||
                   (!string.IsNullOrWhiteSpace(options.AmpController.ControllerApiUrl) &&
                    !string.IsNullOrWhiteSpace(options.AmpController.Username) &&
                    !string.IsNullOrWhiteSpace(options.AmpController.Password)),
        "Enabled AmpController requires ControllerApiUrl, Username, and Password.")
    .ValidateOnStart();

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
    .Validate(options => options.ModpackSync.MaxWarningMinutes is >= 0 and <= 1440, "Agent:ModpackSync:MaxWarningMinutes must be between 0 and 1440.")
    .ValidateOnStart();

var webUiOptions = new WebUiOptions();
builder.Configuration.GetSection("WebUi").Bind(webUiOptions);
builder.WebHost.UseUrls(webUiOptions.BindUrl);

builder.Services.AddDbContext<UpdaterIdentityDbContext>((serviceProvider, options) =>
{
    var configured = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebUiOptions>>().Value;
    var databasePath = ResolvePath(configured.DatabasePath);
    var directory = Path.GetDirectoryName(databasePath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    options.UseSqlite($"Data Source={databasePath}");
});

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<UpdaterIdentityDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/denied";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(webUiOptions.SessionMinutes);
});

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery();
builder.Services.AddSingleton<FirstRunSetupToken>();
builder.Services.AddHostedService<IdentityBootstrapService>();
builder.Services.AddHostedService<UpdaterDatabaseBootstrapService>();
builder.Services.AddScoped<UpdaterAgentAuthenticationService>();
builder.Services.AddScoped<UpdaterCommandService>();
builder.Services.AddHttpClient<IModpackVersionResolver, ModpackVersionResolver>();
builder.Services.AddSingleton<IAgentApiClient, StandaloneAgentApiClient>();
builder.Services.AddSingleton<IAgentCommandHandler, NoOpCommandHandler>();
builder.Services.AddSingleton<SyncModpackCommandHandler>();
builder.Services.AddSingleton<IAgentCommandHandler>(static serviceProvider => serviceProvider.GetRequiredService<SyncModpackCommandHandler>());
builder.Services.AddSingleton<IAgentCommandHandler, AmpConsoleCommandHandler>();
builder.Services.AddSingleton<IAgentCommandHandler, AmpConfigCommandHandler>();
builder.Services.AddSingleton<IAgentCommandHandler, SelfUpdateCommandHandler>();
builder.Services.AddHostedService<LocalAgentCommandWorker>();
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
builder.Services.AddHttpClient(DiscordAnnouncementWorker.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://discord.com/api/v10/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHostedService<DiscordAnnouncementWorker>();
builder.Services.AddHostedService<StandaloneUpdaterWorker>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

await app.RunAsync();

static bool CanResolveTimeZone(string? timeZoneId)
{
    try
    {
        StandaloneTimeZoneResolver.Resolve(timeZoneId);
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

static string ResolvePath(string path)
{
    return Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
}
