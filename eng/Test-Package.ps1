[CmdletBinding()]
param([string]$PackageDirectory = "$PSScriptRoot/../artifacts/ci-package")

$ErrorActionPreference = 'Stop'
[xml]$project = Get-Content "$PSScriptRoot/../src/DiskUsage.Cli/DiskUsage.Cli.csproj" -Raw
$version = [string]$project.Project.PropertyGroup.Version
$commit = git -C "$PSScriptRoot/.." rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine package source commit.' }
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter *.nupkg)
if ($packages.Count -ne 1 -or $packages[0].Name -cne "diskusage.$version.nupkg") {
    throw 'Expected exactly one diskusage package matching the project version.'
}
$package = $packages[0]
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    foreach ($required in 'diskusage.nuspec', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'diskusage.png',
        'tools/net10.0/any/DotnetToolSettings.xml', 'tools/net10.0/any/DiskUsage.Core.dll', 'tools/net10.0/any/diskusage.cli.dll') {
        if (!$archive.GetEntry($required)) { throw "Missing package entry: $required" }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('diskusage.nuspec').Open())
    try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($spec.package.metadata.id -cne 'diskusage' -or $spec.package.metadata.version -cne $version -or
        $spec.package.metadata.repository.commit -cne $commit) { throw 'Package metadata does not match the source.' }
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -match '(^|/)(\.local|\.git|docs|tests|benchmarks)/|\.env$|(^|/)diskusage\.exe$') {
            throw "Unexpected private/development/desktop package entry: $($entry.FullName)"
        }
        # Inspect our own build outputs; third-party binaries have their own provenance.
        if ($entry.Name -match '^(DiskUsage\.Core|diskusage\.cli)\.(dll|pdb)$') {
            $stream = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try { $stream.CopyTo($memory); $bytes = $memory.ToArray() }
            finally { $stream.Dispose(); $memory.Dispose() }
            foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
                if ($encoding.GetString($bytes) -match '(?i)[A-Z]:[\\/]Users[\\/]|/Users/|/home/') {
                    throw "Unnormalized private build path in $($entry.FullName)."
                }
            }
        }
    }
} finally { $archive.Dispose() }

# All generated files live under ignored artifacts, never under the scanned fixture.
$work = Join-Path "$PSScriptRoot/../artifacts" ('tool-smoke-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $work 'source'
$toolDirectory = Join-Path $work 'tool'
New-Item -ItemType Directory -Path $source -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $source 'a.txt'), [byte[]]::new(10))
[IO.File]::WriteAllBytes((Join-Path $source 'b.txt'), [byte[]]::new(20))
[IO.File]::WriteAllBytes((Join-Path $source 'excluded.bin'), [byte[]]::new(30))
dotnet tool install diskusage --version $version --tool-path $toolDirectory --source $package.DirectoryName --no-http-cache
if ($LASTEXITCODE -ne 0) { throw 'Packaged tool installation failed.' }
$tool = Join-Path $toolDirectory $(if ($IsWindows) { 'diskusage.exe' } else { 'diskusage' })
$installedAssembly = @(Get-ChildItem $toolDirectory -Recurse -Force -Filter diskusage.cli.dll)
if ($installedAssembly.Count -ne 1) { throw 'Installed tool assembly is ambiguous or missing.' }
$builtAssembly = "$PSScriptRoot/../src/DiskUsage.Cli/bin/Release/net10.0/diskusage.cli.dll"
if ((Get-FileHash $installedAssembly[0].FullName).Hash -ne (Get-FileHash $builtAssembly).Hash) {
    throw 'The installed tool is not the freshly built package.'
}
foreach ($format in 'csv', 'tsv', 'csv.br', 'tsv.br', 'csv.gz', 'tsv.gz', 'csv.zip', 'tsv.zip', 'parquet') {
    $output = Join-Path $work "inventory.$format"
    & $tool export $source --extensions .txt --size '>=10' --top 1 --format $format --output $output
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $output) -or (Get-Item $output).Length -eq 0) { throw "Export failed: $format" }
}
$rows = @(Import-Csv (Join-Path $work 'inventory.csv'))
if ($rows.Count -ne 1 -or $rows[0].size_bytes -ne '20' -or [IO.Path]::GetFileName($rows[0].full_path) -cne 'b.txt') {
    throw 'Installed-tool filtering/top-N output is incorrect.'
}
$stdout = Join-Path $work 'stdout.csv'
& $tool export $source --extensions .txt --top 1 --format csv --stdout > $stdout
if ($LASTEXITCODE -ne 0 -or @(Import-Csv $stdout).Count -ne 1) { throw 'CSV stdout export failed.' }
$invalid = Join-Path $work 'invalid.csv'
& $tool export $source --size invalid --format csv --stdout > $invalid
if ($LASTEXITCODE -ne 1 -or (Get-Item $invalid).Length -ne 0) { throw 'Invalid input exit/stdout contract failed.' }
$hash = (Get-FileHash $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $package.DirectoryName 'SHA256SUMS'), "$hash  $($package.Name)`n", [Text.UTF8Encoding]::new($false))
Write-Host "Validated package $($package.Name), SHA-256 $hash"
# The preceding invalid-input test intentionally exits 1; reset it for workflow runners.
$global:LASTEXITCODE = 0
