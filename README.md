# diskusage

<p align="center">
  <img src="https://raw.githubusercontent.com/DouglasCleghorn/diskusage/master/src/diskusage.png" alt="diskusage icon" width="160" height="160">
</p>

`diskusage` is a cross-platform .NET 10 command-line tool to **find large files, analyze disk usage, and export filtered file inventories** to CSV, TSV, or Parquet. Upload inventories directly to Amazon S3, MinIO, or other S3-compatible endpoints.

- A cross-platform `dotnet` tool with an ncdu-style terminal browser.
- Script-friendly disk summaries.
- Streaming CSV/TSV with optional Brotli, gzip, or ZIP compression, and row-grouped Parquet exports.
- Direct uploads to Amazon S3, MinIO, and other S3-compatible endpoints.
- A dense, virtualized WPF desktop app for Windows.

## Quick start

Version **2.0.0** is packaged as a NuGet/.NET tool. WPF remains available from source; Windows installers and WinGet distribution are outside this release.

Install from NuGet once 2.0.0 is published (requires .NET 10):

```powershell
dotnet tool install --global diskusage --version 2.0.0
```

To update an existing installation, use `dotnet tool update --global diskusage --version 2.0.0`.

Find the 20 largest files under the current directory, with CSV on stdout:

<!-- smoke:largest -->
```sh
diskusage export . --top 20 --format csv --stdout
```

Example CSV output (paths and timestamps vary):

```csv
full_path,size_bytes,created_utc,modified_utc
/data/app.log,4096,2026-01-01T00:00:00.0000000Z,2026-01-02T00:00:00.0000000Z
/data/notes.txt,2048,2026-01-01T00:00:00.0000000Z,2026-01-02T00:00:00.0000000Z
```

Export matching log/text files of at least 1 KiB to Parquet:

<!-- smoke:filtered -->
```sh
diskusage export . --extensions .log,.txt --size ">=1KiB" --format parquet --output ../inventory.parquet
```

Print a disk-usage summary:

<!-- smoke:scan -->
```sh
diskusage scan . --depth 2 --top 10
```

For agents and scripts, start with the [automation reference](https://github.com/DouglasCleghorn/diskusage/blob/master/AUTOMATION.md): command selection, defaults, output schema, exit codes, and copyable recipes. Run `diskusage export --help` (or `-h`) or `diskusage help upload` for command-specific help, generated with System.CommandLine. `diskusage --version` prints the installed version. Use `browse` for interactive exploration, `scan` for readable summaries, and `export` for machine-readable records.

## Requirements and source installation

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build or install the tool using `dotnet tool`.
- The installed command-line tool requires the .NET 10 runtime on Windows, Linux, or macOS.
- The desktop app requires Windows.

Build and install from this checkout:

```powershell
dotnet pack src/DiskUsage.Cli/DiskUsage.Cli.csproj --configuration Release
dotnet tool install --global --add-source artifacts/packages diskusage --version 2.0.0
```

## Command-line reference

### Known limitations

- Inaccessible files are skipped. Interactive scan progress shows skipped counts, but export/upload summaries do not yet include them.
- Live S3/MinIO integration has not yet been validated. Uploads use a single S3 PUT; multipart upload is not supported.
- Parquet compression levels select library presets, not arbitrary Zstandard levels. Use the default or documented `1–9` presets; level `0` does not reliably produce uncompressed Parquet.
- Terminal views render control characters in filenames literally, which can disrupt the display on Unix-like systems.
- Interrupted stdout pipelines can leave partial destination files. Atomic file replacement applies only when the tool writes directly to a file.

### Browse interactively

```powershell
diskusage browse C:\
diskusage .
```

Use the arrow keys or `j`/`k` to move, Enter to open a directory, Backspace to go up, and `q` to quit. If input or output is redirected, `browse` produces a plain summary instead of an interactive screen.

### Print a summary

```powershell
diskusage scan C:\data --depth 2 --top 25
diskusage scan . --depth 1 --include-files
```

`--depth` controls directory recursion in the report and `--top` limits the entries shown per directory.

### Export a file inventory

```powershell
diskusage export C:\data --format parquet --compression-level 8 --output inventory.parquet
diskusage export C:\data --format csv --output inventory.csv
diskusage export C:\data --format tsv --output inventory.tsv
diskusage export C:\data --format csv.br --output inventory.csv.br
diskusage export C:\data --format tsv --compression gz --compression-level 6 --output inventory.tsv.gz
diskusage export C:\data --format csv.zip --compression-level 9 --output inventory.csv.zip
diskusage export C:\data --format csv --stdout | gzip > inventory.csv.gz
```

Every format includes these columns:

| Column | Description |
| --- | --- |
| `full_path` | Absolute file path |
| `size_bytes` | File size in bytes |
| `created_utc` | Creation timestamp in UTC |
| `modified_utc` | Last-write timestamp in UTC |

### Filter exported files

Filters work with `export` and `upload`, including compressed formats, Parquet, and stdout:

```powershell
diskusage export C:\data --extensions .log,.txt --size ">=100MiB" --top 100 --format parquet --output largest.parquet
diskusage export C:\data --extensions csv --extensions tsv --min-size 1MiB --max-size 1GiB --format csv --stdout
diskusage export C:\data --size ">0" --size "<10MB" --format tsv.gz --output small-files.tsv.gz
diskusage export C:\data --extensions "<none>" --format csv --output extensionless.csv
```

- `--extensions` accepts a comma-separated list; repeated options add alternatives (OR). Match the final extension, case-insensitively: `txt`, `.txt`, and `*.txt` are equivalent. Use `<none>` for extensionless files. `.tar.gz` has final extension `.gz`.
- `--size` accepts `<`, `<=`, `=`, `==`, `!=`, `>=`, or `>`; a bare size means equality. Quote comparisons to prevent shell redirection. Repeated comparisons and other filters combine as AND.
- `--min-size` and `--max-size` are inclusive bounds. Sizes default to bytes; `KB/MB/GB/TB` use powers of 1000 and `KiB/MiB/GiB/TiB` use powers of 1024. Fractional units such as `1.5MiB` are accepted when they resolve to whole bytes.
- `--top N` exports at most N matching files, largest first, with ordinal full-path ordering for equal sizes. N must be positive. Without it, records stream in enumeration order.

Extension and size filters run against directory entries before allocating full paths, timestamps, or file records. Directories are still traversed to find matching descendants. Top-N requires a complete scan but retains only N matching records in a bounded heap, then orders those records for export. Progress counts matching candidates before top-N selection; the final summary counts exported records. Exports with no matches remain valid empty inventories. When using `--output`, the destination and its temporary file are excluded from enumeration; for shell redirection, place the redirected file outside the scanned directory.

### Compression and piping

Parquet output is written in 250,000-row groups, uses byte-stream-split encoding for file sizes, and defaults to Parquet.Net's `Optimal` preset, which maps to Zstandard level 3. Parquet.Net exposes Zstandard through presets: `--compression-level 1–3` selects Zstd level 1, `4–7` selects level 3, and `8–9` selects level 19. CSV and TSV are streamed as plain text or with Brotli (`br`), gzip (`gz`), or ZIP compression. A compressed format can be selected with shorthand such as `--format csv.br` or independently with `--format csv --compression br`. `--compression-level` accepts `0–11` for Brotli and `0–9` for gzip/ZIP; omit it to use the codec default. ZIP output contains one `.csv` or `.tsv` entry.

Use `--stdout` or `--output -` to send an export to standard output. When stdout is redirected and `--output` is omitted, stdout is selected automatically. Progress and summaries remain on stderr, so the stream can be safely piped through external compressors. CSV and TSV stream directly; because a Parquet writer needs to seek while producing its footer, Parquet output is completed in a temporary file and then copied to stdout.

### Upload to S3 or MinIO

```powershell
diskusage upload C:\data `
  --format parquet `
  --endpoint http://localhost:9000 `
  --bucket inventories `
  --key workstation/data.parquet `
  --access-key minioadmin `
  --secret-key minioadmin
```

The endpoint is optional for Amazon S3. Path-style addressing is the default for S3-compatible services; pass `--virtual-hosted-style` when the service requires it. `--region` defaults to `us-east-1`.

Credentials can be passed as arguments, but command arguments may be visible in shell history and process listings. When credentials are omitted, the AWS SDK credential chain is used, including `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, optional `AWS_SESSION_TOKEN`, shared profiles, and instance/container credentials.

Common scan switches:

- `--include-hidden` includes hidden files and directories.
- `--follow-links` follows symbolic links and Windows reparse points while detecting repeated targets.
- `--threads N` controls bounded parallel directory enumeration. The default uses up to four workers; `--threads 1` can be preferable on rotational disks.
- `--help` prints the full command reference.

## Windows desktop app

Run the WPF app from source:

```powershell
dotnet run --project src/diskusage.csproj
```

Or publish it:

```powershell
dotnet publish src/diskusage.csproj --configuration Release --output artifacts/publish/wpf
artifacts\publish\wpf\diskusage.exe
```

The UI shows ready drives and their used/free capacity immediately, without crawling the filesystem. Opening a drive or folder displays it immediately while a cancellable background scan fills in its children, file counts, and sizes. Rows refresh throughout the scan, and the state badge clearly changes from `UPDATING` to `FINAL` (or `CANCELED`). Results use cached Windows shell icons and a recycling virtualized grid with filtering. Double-click or press Enter to drill into a folder; press Backspace or use the Up button to return to the drive overview. Save is enabled after a completed scan and serializes the in-memory snapshot without rescanning the filesystem; entries reported as skipped are therefore absent from the saved snapshot. It exports the current folder inventory as Parquet or as CSV/TSV with optional Brotli, gzip, or ZIP compression using the same writer as the CLI. File exports are completed in a same-directory temporary file and atomically moved into place, so interruption cannot replace an existing valid inventory with a partial one. The Level field controls compressed output quality.

## Build and test

```powershell
dotnet build src/diskusage.sln --configuration Release
dotnet test tests/DiskUsage.Core.Tests/DiskUsage.Core.Tests.csproj --configuration Release
```

## Scanner benchmarks

The BenchmarkDotNet project compares the current scanner with the previous `FileSystemInfo`-based implementation for both complete usage trees and streaming inventories:

```powershell
dotnet run --project benchmarks/DiskUsage.Benchmarks/DiskUsage.Benchmarks.csproj --configuration Release
```

By default it creates a repeatable 4,096-file fixture under the system temporary directory. To measure a real directory without modifying it, set `DISKUSAGE_BENCHMARK_PATH` before running. Benchmark results and allocation reports are written under `BenchmarkDotNet.Artifacts`.

The benchmark includes the previous scanner, the optimized single-worker scanner, and the default four-worker scanner. On the development NVMe system, the short-run tree benchmark improved from 4.81 ms to 1.51 ms (3.2×), and streaming inventory improved from 4.75 ms to 1.84 ms (2.6×). Managed allocations fell by 28% for tree scans and 31% for inventories. Storage hardware varies, so use a full benchmark against a representative directory before choosing a custom worker count.

## License

Licensed under the [MIT license](LICENSE).
