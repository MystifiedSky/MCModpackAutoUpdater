using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Models.Web;
using MCModpackAutoUpdater.Security;

namespace MCModpackAutoUpdater.Controllers;

[Authorize(Roles = UpdaterRoles.Admin)]
public sealed class AgentsController : Controller
{
    private readonly UpdaterIdentityDbContext _dbContext;

    public AgentsController(UpdaterIdentityDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("/agents")]
    public async Task<IActionResult> Index(string? token, CancellationToken cancellationToken)
    {
        return View(await BuildModelAsync(token, cancellationToken));
    }

    [HttpPost("/agents/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AgentNodeFormModel model, CancellationToken cancellationToken)
    {
        if (await _dbContext.UpdaterAgentNodes.AnyAsync(agent => agent.Name == model.Name.Trim(), cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Name), "An agent with this name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(null, cancellationToken));
        }

        var token = UpdaterAgentTokenUtility.GenerateToken();
        var utcNow = DateTime.UtcNow;
        _dbContext.UpdaterAgentNodes.Add(new UpdaterAgentNode
        {
            Name = model.Name.Trim(),
            Host = model.Host.Trim(),
            ApiBaseUrl = model.ApiBaseUrl.Trim(),
            Platform = model.Platform.Trim(),
            ExecutionMode = UpdaterAgentExecutionMode.Remote,
            Enabled = model.Enabled,
            AuthTokenHash = UpdaterAgentTokenUtility.HashToken(token),
            AuthTokenLastRotatedUtc = utcNow,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = $"Remote agent '{model.Name}' created. Copy the token before leaving this page.";
        return RedirectToAction(nameof(Index), new { token });
    }

    [HttpPost("/agents/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(AgentNodeFormModel model, CancellationToken cancellationToken)
    {
        var agent = await _dbContext.UpdaterAgentNodes.FirstOrDefaultAsync(item => item.Id == model.Id, cancellationToken);
        if (agent is null)
        {
            TempData["Message"] = "Agent was not found.";
            return RedirectToAction(nameof(Index));
        }

        if (await _dbContext.UpdaterAgentNodes.AnyAsync(item => item.Id != model.Id && item.Name == model.Name.Trim(), cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Name), "An agent with this name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(null, cancellationToken));
        }

        agent.Name = model.Name.Trim();
        agent.Host = model.Host.Trim();
        agent.Platform = model.Platform.Trim();
        agent.Enabled = model.Enabled;
        agent.UpdatedUtc = DateTime.UtcNow;
        if (agent.ExecutionMode == UpdaterAgentExecutionMode.Remote)
        {
            agent.ApiBaseUrl = model.ApiBaseUrl.Trim();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = $"Agent '{agent.Name}' updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/agents/rotate-token")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateToken(int id, CancellationToken cancellationToken)
    {
        var agent = await _dbContext.UpdaterAgentNodes.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (agent is null)
        {
            TempData["Message"] = "Agent was not found.";
            return RedirectToAction(nameof(Index));
        }

        if (agent.ExecutionMode != UpdaterAgentExecutionMode.Remote)
        {
            TempData["Message"] = "Local runner tokens are internal and cannot be rotated from the UI.";
            return RedirectToAction(nameof(Index));
        }

        var token = UpdaterAgentTokenUtility.GenerateToken();
        agent.AuthTokenHash = UpdaterAgentTokenUtility.HashToken(token);
        agent.AuthTokenLastRotatedUtc = DateTime.UtcNow;
        agent.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["Message"] = $"Token rotated for '{agent.Name}'. Copy the new token before leaving this page.";
        return RedirectToAction(nameof(Index), new { token });
    }

    private async Task<AgentsIndexViewModel> BuildModelAsync(string? token, CancellationToken cancellationToken)
    {
        var agents = await _dbContext.UpdaterAgentNodes
            .AsNoTracking()
            .Select(agent => new AgentNodeViewModel
            {
                Id = agent.Id,
                Name = agent.Name,
                Host = agent.Host,
                ApiBaseUrl = agent.ApiBaseUrl,
                Platform = agent.Platform,
                ExecutionMode = agent.ExecutionMode,
                Enabled = agent.Enabled,
                LastSeenUtc = agent.LastSeenUtc,
                LastReportedStatus = agent.LastReportedStatus,
                LastReportedVersion = agent.LastReportedVersion,
                ProfileCount = agent.ModpackProfiles.Count,
                PendingCommandCount = agent.Commands.Count(command =>
                    command.Status == UpdaterAgentCommandStatus.Pending ||
                    command.Status == UpdaterAgentCommandStatus.InProgress)
            })
            .OrderBy(agent => agent.Name)
            .ToListAsync(cancellationToken);

        return new AgentsIndexViewModel
        {
            Agents = agents,
            NewAgent = new AgentNodeFormModel(),
            GeneratedToken = token
        };
    }
}
