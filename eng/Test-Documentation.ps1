[CmdletBinding()]
param([Parameter(Mandatory)][string]$ToolPath)

$ErrorActionPreference = 'Stop'
$tool = (Resolve-Path -LiteralPath $ToolPath).Path
$work = Join-Path "$PSScriptRoot/../artifacts" ('documentation-smoke-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $work 'source'
New-Item -ItemType Directory -Path $source -Force | Out-Null
foreach ($fixture in @(@{ Name = 'notes.txt'; Size = 2048 }, @{ Name = 'app.log'; Size = 4096 }, @{ Name = 'excluded.bin'; Size = 8192 })) {
    $stream = [IO.File]::Create((Join-Path $source $fixture.Name))
    try { $stream.SetLength($fixture.Size) } finally { $stream.Dispose() }
}

# Execute only marked, single-line local recipes. No shell evaluation or live upload.
function Invoke-DocumentedCommand([string]$Document, [string]$Id) {
    $markdown = Get-Content -LiteralPath "$PSScriptRoot/../$Document" -Raw
    $pattern = '(?s)<!-- smoke:' + [regex]::Escape($Id) + ' -->\s*```sh\r?\n(?<command>[^\r\n]+)\r?\n```'
    $matches = [regex]::Matches($markdown, $pattern)
    if ($matches.Count -ne 1) { throw "Expected one documented recipe: $Document/$Id" }
    $line = $matches[0].Groups['command'].Value
    if ($line -notmatch '^diskusage (scan|export) ') { throw "Unexpected recipe command: $line" }
    # These recipes use whitespace-separated arguments and simple double-quoted values.
    $tokens = @([regex]::Matches($line, '"([^"\r\n]*)"|([^\s"]+)') | ForEach-Object {
        if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value }
    })
    $arguments = $tokens[1..($tokens.Count - 1)]
    $output = @(& $tool @arguments)
    if ($LASTEXITCODE -ne 0) { throw "Documented recipe failed: $Document/$Id" }
    return $output
}

function Assert-FilteredRows($Rows) {
    if ($Rows.Count -ne 2 -or [IO.Path]::GetFileName($Rows[0].full_path) -cne 'app.log' -or
        $Rows[0].size_bytes -ne '4096' -or [IO.Path]::GetFileName($Rows[1].full_path) -cne 'notes.txt' -or
        $Rows[1].size_bytes -ne '2048') { throw 'Documented filtering, ordering, or output schema changed.' }
}

Push-Location $source
try {
    $largest = @(Invoke-DocumentedCommand 'README.md' 'largest' | ConvertFrom-Csv)
    if ($largest.Count -ne 3 -or $largest[0].size_bytes -ne '8192' -or $largest[2].size_bytes -ne '2048') {
        throw 'README largest-files example failed.'
    }
    $schema = @($largest[0].PSObject.Properties.Name)
    if (($schema -join ',') -cne 'full_path,size_bytes,created_utc,modified_utc') { throw 'CSV schema differs from documentation.' }
    $scan = Invoke-DocumentedCommand 'README.md' 'scan'
    if (($scan -join "`n") -notmatch '3 files in .*0 skipped') { throw 'README scan example failed.' }
    Invoke-DocumentedCommand 'README.md' 'filtered' | Out-Null
    $parquet = [IO.File]::ReadAllBytes((Join-Path $work 'inventory.parquet'))
    if ($parquet.Length -lt 8 -or [Text.Encoding]::ASCII.GetString($parquet, 0, 4) -cne 'PAR1' -or
        [Text.Encoding]::ASCII.GetString($parquet, $parquet.Length - 4, 4) -cne 'PAR1') { throw 'README Parquet example failed.' }
    Assert-FilteredRows @(Invoke-DocumentedCommand 'AUTOMATION.md' 'filtered-csv' | ConvertFrom-Csv)
    Invoke-DocumentedCommand 'AUTOMATION.md' 'compressed-tsv' | Out-Null
    $inputStream = [IO.File]::OpenRead((Join-Path $work 'inventory.tsv.gz'))
    $gzip = [IO.Compression.GZipStream]::new($inputStream, [IO.Compression.CompressionMode]::Decompress)
    $reader = [IO.StreamReader]::new($gzip)
    try { Assert-FilteredRows @($reader.ReadToEnd() | ConvertFrom-Csv -Delimiter "`t") }
    finally { $reader.Dispose(); $gzip.Dispose(); $inputStream.Dispose() }

    # Read the native stdout byte stream directly: parser/help output must never pollute it.
    foreach ($format in 'csv.gz', 'parquet') {
        $start = [Diagnostics.ProcessStartInfo]::new($tool)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('export', $source, '--extensions', '.log,.txt', '--top', '2', '--format', $format, '--stdout')) {
            $start.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::Start($start)
        $destination = [IO.MemoryStream]::new()
        try {
            $errorTask = $process.StandardError.ReadToEndAsync()
            $process.StandardOutput.BaseStream.CopyTo($destination)
            $process.WaitForExit()
            $stderrText = $errorTask.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0 -or $stderrText -notmatch 'Wrote 2 files') { throw "Binary stdout export failed: $format" }
            $destination.Position = 0
            if ($format -eq 'csv.gz') {
                $compressed = [IO.Compression.GZipStream]::new($destination, [IO.Compression.CompressionMode]::Decompress, $true)
                $textReader = [IO.StreamReader]::new($compressed)
                try { Assert-FilteredRows @($textReader.ReadToEnd() | ConvertFrom-Csv) }
                finally { $textReader.Dispose(); $compressed.Dispose() }
            } else {
                $bytes = $destination.ToArray()
                if ($bytes.Length -lt 8 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -cne 'PAR1' -or
                    [Text.Encoding]::ASCII.GetString($bytes, $bytes.Length - 4, 4) -cne 'PAR1') { throw 'Parquet stdout is not intact.' }
            }
        } finally { $destination.Dispose(); $process.Dispose() }
    }

    $overview = @(& $tool --help)
    if ($LASTEXITCODE -ne 0 -or ($overview -join "`n") -notmatch 'AUTOMATION.md') { throw 'Global help failed.' }
    [xml]$project = Get-Content "$PSScriptRoot/../src/DiskUsage.Cli/DiskUsage.Cli.csproj" -Raw
    $version = @(& $tool --version)
    if ($LASTEXITCODE -ne 0 -or ($version -join '').Trim() -cne [string]$project.Project.PropertyGroup.Version) { throw 'Installed tool version is incorrect.' }
    foreach ($command in 'browse', 'scan', 'export', 'upload') {
        $stderr = Join-Path $work "help-$command.stderr"
        $help = @(& $tool $command --help 2> $stderr)
        if ($LASTEXITCODE -ne 0 -or ($help -join "`n") -notmatch "Usage:\s+diskusage $command" -or
            (Get-Item $stderr).Length -ne 0) { throw "Installed command help failed: $command" }
        $alias = @(& $tool help $command)
        if ($LASTEXITCODE -ne 0 -or ($alias -join "`n") -cne ($help -join "`n")) { throw "Help alias differs: $command" }
        $short = @(& $tool $command -h)
        if ($LASTEXITCODE -ne 0 -or ($short -join "`n") -cne ($help -join "`n")) { throw "Short help alias differs: $command" }
    }
    $invalid = @(& $tool help unknown 2> (Join-Path $work 'invalid-help.stderr'))
    if ($LASTEXITCODE -ne 1 -or $invalid.Count -ne 0) { throw 'Invalid help topic exit/stream contract failed.' }
} finally { Pop-Location }

Write-Host 'Validated five documented local recipes and installed command-specific help.'
$global:LASTEXITCODE = 0
