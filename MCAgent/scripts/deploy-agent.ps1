[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-x64",
    [switch]$SkipRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDirectory = if ([string]::IsNullOrWhiteSpace($PSScriptRoot))
{
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
else
{
    $PSScriptRoot
}

if ([string]::IsNullOrWhiteSpace($ConfigPath))
{
    $ConfigPath = Join-Path $scriptDirectory "deploy-targets.json"
}

function Assert-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Get-OptionalPropertyValue {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property)
    {
        return $null
    }

    return $property.Value
}

function Get-LinuxQuoted {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + $Value.Replace("'", "'""'""'") + "'"
}

function Assert-SafeLinuxPath {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $normalized = $Value.TrimEnd('/')
    if (-not $Value.StartsWith('/') -or [string]::IsNullOrWhiteSpace($normalized) -or
        $Value -match '[\r\n\\]' -or $Value.Contains('//') -or
        @($Value -split '/' | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -gt 0)
    {
        throw "$Label must be an absolute Linux directory below / without . or .. segments."
    }
    return $normalized
}

function Test-PathWithin {
    param([string]$Path, [string]$Parent)
    return $Path -eq $Parent -or $Path.StartsWith($Parent + '/', [StringComparison]::Ordinal)
}

function Get-RsyncExcludes {
    param($Target)

    $paths = @(
        '/updates/', '/state/', '/private/',
        '/appsettings.*.json', '**/appsettings.*.json',
        '/agent-command-state*.json', '**/agent-command-state*.json',
        '/agent-command-state*.json.tmp', '**/agent-command-state*.json.tmp'
    )
    $additional = Get-OptionalPropertyValue -Object $Target -PropertyName 'preservePaths'
    foreach ($path in @($additional))
    {
        if ($null -eq $path) { continue }
        $value = [string]$path
        if ([string]::IsNullOrWhiteSpace($value) -or $value.StartsWith('/') -or
            $value -match '[\r\n*?]' -or
            @($value -split '/' | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -gt 0)
        {
            throw "Invalid preservePaths entry '$value'; use a relative path without wildcards or traversal."
        }
        $paths += '/' + $value.TrimEnd('/')
        $paths += '/' + $value.TrimEnd('/') + '.tmp'
    }
    return $paths
}

function Assert-LocalWorkspacePath {
    param([string]$Path, [string]$WorkspaceRoot)

    $resolved = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\', '/')
    if (-not $resolved.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to delete a path outside the repository: $resolved"
    }
    return $resolved
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Command failed: $Tool $($Arguments -join ' ')"
    }
}

function Invoke-Ssh {
    param(
        [Parameter(Mandatory = $true)]$Target,
        [Parameter(Mandatory = $true)][string]$Command
    )

    $sshArgs = @("-tt")
    $sshKeyPath = Get-OptionalPropertyValue -Object $Target -PropertyName "sshKeyPath"
    if ($null -ne $sshKeyPath -and -not [string]::IsNullOrWhiteSpace([string]$sshKeyPath))
    {
        $sshArgs += "-i"
        $sshArgs += [string]$sshKeyPath
    }

    $sshPort = Get-OptionalPropertyValue -Object $Target -PropertyName "sshPort"
    if ($null -ne $sshPort)
    {
        $sshArgs += "-p"
        $sshArgs += [string]$sshPort
    }

    $sshArgs += "$($Target.sshUser)@$($Target.host)"
    $sshArgs += $Command

    Invoke-External -Tool "ssh" -Arguments $sshArgs
}

function Invoke-ScpDirectory {
    param(
        [Parameter(Mandatory = $true)]$Target,
        [Parameter(Mandatory = $true)][string]$LocalDirectory,
        [Parameter(Mandatory = $true)][string]$RemoteDirectory
    )

    $scpArgs = @()
    $sshKeyPath = Get-OptionalPropertyValue -Object $Target -PropertyName "sshKeyPath"
    if ($null -ne $sshKeyPath -and -not [string]::IsNullOrWhiteSpace([string]$sshKeyPath))
    {
        $scpArgs += "-i"
        $scpArgs += [string]$sshKeyPath
    }

    $sshPort = Get-OptionalPropertyValue -Object $Target -PropertyName "sshPort"
    if ($null -ne $sshPort)
    {
        $scpArgs += "-P"
        $scpArgs += [string]$sshPort
    }

    $scpArgs += "-r"
    $scpArgs += $LocalDirectory
    $scpArgs += "$($Target.sshUser)@$($Target.host):$RemoteDirectory"

    Invoke-External -Tool "scp" -Arguments $scpArgs
}

Assert-Command "dotnet"
Assert-Command "ssh"
Assert-Command "scp"

if (-not (Test-Path -LiteralPath $ConfigPath))
{
    $examplePath = Join-Path $scriptDirectory "deploy-targets.example.jsonc"
    throw "Config file not found: $ConfigPath`nCopy '$examplePath' to 'deploy-targets.json' and edit it."
}

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if ($null -eq $config.targets -or $config.targets.Count -eq 0)
{
    throw "Config must contain at least one target in 'targets'."
}

$repoRoot = (Resolve-Path (Join-Path $scriptDirectory "..\..")).Path
$projectPath = Join-Path $repoRoot "MCAgent\MCAgent.csproj"
$outRoot = Join-Path $repoRoot "out"
$publishOutputPath = Join-Path $outRoot "mc-agent-publish"
$deployDirectoryName = "mc-agent-deploy"
$deployDirectoryPath = Join-Path $outRoot $deployDirectoryName
$applyScriptSourcePath = Join-Path $scriptDirectory "apply-update-linux.sh"
$publishOutputPath = Assert-LocalWorkspacePath -Path $publishOutputPath -WorkspaceRoot $repoRoot
$deployDirectoryPath = Assert-LocalWorkspacePath -Path $deployDirectoryPath -WorkspaceRoot $repoRoot

if (-not (Test-Path -LiteralPath $projectPath))
{
    throw "Project not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $applyScriptSourcePath))
{
    throw "Required script not found: $applyScriptSourcePath"
}

if (Test-Path -LiteralPath $publishOutputPath)
{
    Remove-Item -LiteralPath $publishOutputPath -Recurse -Force
}

if (Test-Path -LiteralPath $deployDirectoryPath)
{
    Remove-Item -LiteralPath $deployDirectoryPath -Recurse -Force
}

New-Item -ItemType Directory -Path $publishOutputPath -Force | Out-Null
New-Item -ItemType Directory -Path $deployDirectoryPath -Force | Out-Null

Write-Host "Publishing MCAgent ($Configuration, $Runtime)..."
Invoke-External -Tool "dotnet" -Arguments @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "false",
    "-o", $publishOutputPath
)

$runtimeConfigPath = Join-Path $publishOutputPath 'MCAgent.runtimeconfig.json'
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App')
{
    throw "Expected a framework-dependent .NET agent publish at $runtimeConfigPath."
}
$requiredRuntime = [Version]::Parse([string]$runtimeConfig.runtimeOptions.framework.version)
$requiredRuntimePattern = "'^Microsoft[.]NETCore[.]App $($requiredRuntime.Major)[.]$($requiredRuntime.Minor)[.][0-9]+ '"

Copy-Item -Path (Join-Path $publishOutputPath "*") -Destination $deployDirectoryPath -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $deployDirectoryPath "scripts"))
{
    Remove-Item -LiteralPath (Join-Path $deployDirectoryPath "scripts") -Recurse -Force
}

New-Item -ItemType Directory -Path (Join-Path $deployDirectoryPath "scripts") -Force | Out-Null
Copy-Item -LiteralPath $applyScriptSourcePath -Destination (Join-Path $deployDirectoryPath "scripts\apply-update-linux.sh") -Force

foreach ($target in $config.targets)
{
    $name = if ([string]::IsNullOrWhiteSpace([string]$target.name)) { [string]$target.host } else { [string]$target.name }
    $targetHost = [string]$target.host
    $sshUser = [string]$target.sshUser
    $uploadBasePath = [string]$target.uploadBasePath
    $remotePath = [string]$target.remotePath
    $serviceUser = if ([string]::IsNullOrWhiteSpace([string]$target.serviceUser)) { "amp" } else { [string]$target.serviceUser }
    $serviceName = if ([string]::IsNullOrWhiteSpace([string]$target.serviceName)) { "mc-agent" } else { [string]$target.serviceName }

    if ([string]::IsNullOrWhiteSpace($targetHost) -or
        [string]::IsNullOrWhiteSpace($sshUser) -or
        [string]::IsNullOrWhiteSpace($uploadBasePath) -or
        [string]::IsNullOrWhiteSpace($remotePath))
    {
        throw "Target '$name' is missing required properties (host, sshUser, uploadBasePath, remotePath)."
    }

    $uploadBasePath = Assert-SafeLinuxPath -Value $uploadBasePath -Label "uploadBasePath for '$name'"
    $remotePath = Assert-SafeLinuxPath -Value $remotePath -Label "remotePath for '$name'"
    $uploadPath = $uploadBasePath + '/' + $deployDirectoryName
    if ((Test-PathWithin -Path $uploadPath -Parent $remotePath) -or
        (Test-PathWithin -Path $remotePath -Parent $uploadPath))
    {
        throw "Target '$name' upload path and remote application path must not overlap."
    }
    $rsyncExcludes = @(Get-RsyncExcludes -Target $target)
    $rsyncFilterArguments = ($rsyncExcludes | ForEach-Object { '--exclude=' + (Get-LinuxQuoted $_) }) -join ' '
    $remotePathGuard = @(
        "remote_real=`$(realpath -m -- $(Get-LinuxQuoted $remotePath))",
        "upload_real=`$(realpath -m -- $(Get-LinuxQuoted $uploadPath))",
        "test `"`$remote_real`" != /",
        "test `"`$upload_real`" != /",
        "test `"`$remote_real`" != `"`$upload_real`"",
        "case `"`$upload_real/`" in `"`$remote_real/`"*) exit 1 ;; esac",
        "case `"`$remote_real/`" in `"`$upload_real/`"*) exit 1 ;; esac"
    ) -join '; '

    $target.sshUser = $sshUser
    $target.host = $targetHost

    Write-Host "Deploying to $name ($sshUser@$targetHost)..."

    try
    {
        Invoke-Ssh -Target $target -Command "set -euo pipefail; dotnet --list-runtimes | grep -Eq $requiredRuntimePattern"
    }
    catch
    {
        throw "Target '$name' needs the .NET $($requiredRuntime.Major).$($requiredRuntime.Minor) Runtime before deployment. $($_.Exception.Message)"
    }

    $prepareCommand = @(
        "set -euo pipefail",
        $remotePathGuard,
        "mkdir -p $(Get-LinuxQuoted $uploadBasePath)",
        "rm -rf $(Get-LinuxQuoted $uploadPath)"
    ) -join "; "
    Invoke-Ssh -Target $target -Command $prepareCommand

    Invoke-ScpDirectory -Target $target -LocalDirectory $deployDirectoryPath -RemoteDirectory ($uploadBasePath.TrimEnd('/') + "/")

    $remoteCommands = @(
        "set -euo pipefail",
        $remotePathGuard,
        "sudo mkdir -p $(Get-LinuxQuoted ($remotePath.TrimEnd('/') + "/scripts"))",
        "sudo rsync -a --delete $rsyncFilterArguments $(Get-LinuxQuoted ($uploadPath.TrimEnd('/') + '/')) $(Get-LinuxQuoted ($remotePath.TrimEnd('/') + '/'))",
        "sudo chown -R $(Get-LinuxQuoted ($serviceUser + ':' + $serviceUser)) $(Get-LinuxQuoted $remotePath)",
        "sudo chmod +x $(Get-LinuxQuoted ($remotePath.TrimEnd('/') + '/scripts/apply-update-linux.sh'))"
    )

    if (-not $SkipRestart)
    {
        $remoteCommands += "sudo systemctl restart $(Get-LinuxQuoted $serviceName)"
        $remoteCommands += "sudo systemctl --no-pager --full status $(Get-LinuxQuoted $serviceName)"
    }

    $remoteCommands += "rm -rf $(Get-LinuxQuoted $uploadPath)"

    Invoke-Ssh -Target $target -Command ($remoteCommands -join "; ")
}

Write-Host "Deployment completed successfully."
