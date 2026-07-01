namespace MCAgent.Models.AgentApi;

public sealed class AgentAmpRuntimeConfigResponse
{
    public string ControllerApiUrl { get; set; } = string.Empty;

    public string InstanceName { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = true;

    public bool DirectAmpApiEnabled { get; set; }

    public string DirectAmpApiUrl { get; set; } = string.Empty;

    public string DirectAmpApiUsername { get; set; } = string.Empty;

    public string DirectAmpApiPassword { get; set; } = string.Empty;

    public string DirectAmpApiToken { get; set; } = string.Empty;

    public bool DirectAmpApiRememberMe { get; set; } = true;

    public string DirectAmpApiWarningMessageTemplate { get; set; } = string.Empty;
}
