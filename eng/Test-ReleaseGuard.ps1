$ErrorActionPreference = 'Stop'
[xml]$project = Get-Content "$PSScriptRoot/../src/DiskUsage.Cli/DiskUsage.Cli.csproj" -Raw
$version = [string]$project.Project.PropertyGroup.Version
& "$PSScriptRoot/Test-Release.ps1" -Tag "v$version" -IsPrerelease $version.Contains('-')

$cases = @(
    @{ Tag = $version; Preview = $version.Contains('-') },
    @{ Tag = "v$version"; Preview = !$version.Contains('-') },
    @{ Tag = 'v01.0.0'; Preview = $false },
    @{ Tag = 'v1.0.0-rc.01'; Preview = $true },
    @{ Tag = 'v1.0.0+metadata'; Preview = $false },
    @{ Tag = 'v9999.0.0'; Preview = $false },
    @{ Tag = 'v1.0.0; echo unsafe'; Preview = $false }
)
foreach ($case in $cases) {
    $rejected = $false
    try { & "$PSScriptRoot/Test-Release.ps1" -Tag $case.Tag -IsPrerelease $case.Preview }
    catch { $rejected = $true }
    if (!$rejected) { throw "Release guard incorrectly accepted '$($case.Tag)'." }
}
Write-Host 'Release guard: valid version accepted; seven invalid cases rejected.'
