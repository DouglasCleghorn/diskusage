[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][bool]$IsPrerelease,
    [string]$ProjectPath = "$PSScriptRoot/../src/DiskUsage.Cli/DiskUsage.Cli.csproj"
)

$ErrorActionPreference = 'Stop'
# No build metadata or leading-zero numeric components in publication tags.
$number = '(0|[1-9][0-9]*)'
$identifier = '(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
if ($Tag -cnotmatch "^v$number\.$number\.$number(?:-$identifier(?:\.$identifier)*)?$") {
    throw 'Use a canonical version tag such as v2.0.0-rc.1 or v2.0.0.'
}
[xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
$versions = @($project.Project.PropertyGroup.Version | Where-Object { $_ })
if ($versions.Count -ne 1 -or $versions[0] -cne $Tag.Substring(1)) {
    throw 'The release tag must exactly match the CLI project Version, prefixed by v.'
}
if ($IsPrerelease -ne $Tag.Contains('-')) {
    throw 'The GitHub prerelease checkbox must match the version prerelease suffix.'
}
Write-Host "Validated release $Tag."
