namespace MCModpackAutoUpdater.Services;

public sealed class DuplicateSyncCommandException()
    : InvalidOperationException("This modpack already has a pending or running sync command.");
