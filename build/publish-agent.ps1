<#
.SYNOPSIS
    Publishes the agent as a single self-contained-free executable and builds the MSI.
.DESCRIPTION
    The agent is published with PublishSingleFile so the installer only has to carry one file,
    which keeps the WiX authoring free of harvested component groups.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$agentProject = Join-Path $repoRoot 'src/HaActiveUser.Agent/HaActiveUser.Agent.csproj'
$sessionAgentProject = Join-Path $repoRoot 'src/HaActiveUser.SessionAgent/HaActiveUser.SessionAgent.csproj'
$installerProject = Join-Path $repoRoot 'installer/HaActiveUser.Installer.wixproj'

Write-Host 'Publishing agent...' -ForegroundColor Cyan
dotnet publish $agentProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$publishDir = Join-Path $repoRoot "src/HaActiveUser.Agent/bin/$Configuration/net8.0-windows/win-x64/publish/"
Write-Host "Published to $publishDir" -ForegroundColor Green

Write-Host 'Publishing session tray agent...' -ForegroundColor Cyan
dotnet publish $sessionAgentProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Session agent publish failed with exit code $LASTEXITCODE." }

$sessionAgentPublishDir = Join-Path $repoRoot "src/HaActiveUser.SessionAgent/bin/$Configuration/net8.0-windows/win-x64/publish/"
Write-Host "Published to $sessionAgentPublishDir" -ForegroundColor Green

if ($SkipInstaller) { return }

Write-Host 'Building installer...' -ForegroundColor Cyan
dotnet build $installerProject `
    -c $Configuration `
    -p:AgentPublishDir=$publishDir `
    -p:SessionAgentPublishDir=$sessionAgentPublishDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Installer build failed with exit code $LASTEXITCODE." }

Get-ChildItem -Path (Join-Path $repoRoot 'installer/bin') -Filter '*.msi' -Recurse |
    ForEach-Object { Write-Host "MSI: $($_.FullName)" -ForegroundColor Green }
