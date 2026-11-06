[CmdletBinding()]
param(
    [string[]]$Check = @('SharedPreset', 'VariousRegression', 'UpgradePlannerRegression', 'WorkspaceLayoutMigration'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$regressionRoot = Join-Path $repoRoot 'tests/Regression'
$selectedProjects = foreach ($name in $Check) {
    if ($name -notmatch '^[A-Za-z0-9_-]+$') { throw "Invalid check name: $name" }
    $projects = @(Get-ChildItem -LiteralPath (Join-Path $regressionRoot $name) -Filter '*.csproj' -File)
    if ($projects.Count -ne 1) { throw "Expected one maintained project for check '$name'." }
    $projects[0].FullName
}
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration }
Push-Location $repoRoot
try {
    foreach ($project in $selectedProjects) {
        Write-Host "Running $([IO.Path]::GetFileNameWithoutExtension($project))"
        $checkArguments = @()
        if ([IO.Path]::GetFileNameWithoutExtension($project) -eq 'WorkspaceLayoutMigration') {
            $fixture = Join-Path $repoRoot ('artifacts/checks/migration-' + [Guid]::NewGuid().ToString('N'))
            $original = Join-Path $fixture 'original_files/[media]/classes/trucks'
            $updated = Join-Path $fixture 'updated/[media]/classes/trucks'
            [IO.Directory]::CreateDirectory($original) | Out-Null
            [IO.Directory]::CreateDirectory($updated) | Out-Null
            [IO.File]::WriteAllText((Join-Path $original 'synthetic.xml'), '<Truck><TruckData FuelCapacity="20" /></Truck>')
            [IO.File]::WriteAllText((Join-Path $updated 'synthetic.xml'), '<Truck><TruckData FuelCapacity="30" /></Truck>')
            $checkArguments = @('--apply', $fixture, (Join-Path $fixture 'migration-report.json'))
        }
        & dotnet run --project $project --configuration $Configuration --no-build -- @checkArguments
        if ($LASTEXITCODE -ne 0) { throw "Regression check failed: $project ($LASTEXITCODE)." }
    }
}
finally { Pop-Location }
