[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$buildArguments = @('build', (Join-Path $repoRoot 'SnowRunnerTools.sln'),
    '--configuration', $Configuration, '-m:1', '-p:UseSharedCompilation=false')
if ($NoRestore) { $buildArguments += '--no-restore' }
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw "Solution build failed ($LASTEXITCODE)." }
