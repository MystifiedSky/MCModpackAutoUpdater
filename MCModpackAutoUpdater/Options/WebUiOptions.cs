namespace MCModpackAutoUpdater.Options;

public sealed class WebUiOptions
{
    public bool Enabled { get; set; } = true;

    public string BindUrl { get; set; } = "http://0.0.0.0:9090";

    public string DatabasePath { get; set; } = "mc-modpack-auto-updater.db";

    public int SessionMinutes { get; set; } = 480;
}
