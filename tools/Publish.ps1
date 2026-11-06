[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputPath,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'artifacts/publish/SnowPakTool.Desktop' }
$publishArguments = @('publish', (Join-Path $repoRoot 'src/SnowPakTool.Desktop/SnowPakTool.Desktop.csproj'),
    '--configuration', $Configuration, '--output', $OutputPath, '--self-contained', 'false',
    '-p:UseSharedCompilation=false')
if ($NoBuild) { $publishArguments += @('--no-build', '--no-restore') }
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed ($LASTEXITCODE)." }
