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

    $target.sshUser = $sshUser
    $target.host = $targetHost
    $uploadPath = ($uploadBasePath.TrimEnd('/') + "/" + $deployDirectoryName)

    Write-Host "Deploying to $name ($sshUser@$targetHost)..."

    $prepareCommand = @(
        "set -euo pipefail",
        "mkdir -p $(Get-LinuxQuoted $uploadBasePath)",
        "rm -rf $(Get-LinuxQuoted $uploadPath)"
    ) -join "; "
    Invoke-Ssh -Target $target -Command $prepareCommand

    Invoke-ScpDirectory -Target $target -LocalDirectory $deployDirectoryPath -RemoteDirectory ($uploadBasePath.TrimEnd('/') + "/")

    $remoteCommands = @(
        "set -euo pipefail",
        "sudo mkdir -p $(Get-LinuxQuoted ($remotePath.TrimEnd('/') + "/scripts"))",
        "sudo rsync -a --delete $(Get-LinuxQuoted ($uploadPath.TrimEnd('/') + '/')) $(Get-LinuxQuoted ($remotePath.TrimEnd('/') + '/'))",
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
