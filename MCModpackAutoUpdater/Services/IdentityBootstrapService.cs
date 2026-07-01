using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Security;

namespace MCModpackAutoUpdater.Services;

public sealed class IdentityBootstrapService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IdentityBootstrapService> _logger;

    public IdentityBootstrapService(
        IServiceProvider serviceProvider,
        ILogger<IdentityBootstrapService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in UpdaterRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to create role '{role}': {string.Join("; ", result.Errors.Select(static error => error.Description))}");
                }
            }
        }

        var hasUsers = await dbContext.Users.AsNoTracking().AnyAsync(cancellationToken);
        if (!hasUsers)
        {
            _logger.LogWarning(
                "MCModpackAutoUpdater has no users. Open /setup to print the first-run setup token.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
