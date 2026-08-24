# diskusage

`diskusage` is a fast disk-space browser and file inventory exporter for .NET 10. It includes:

- A cross-platform `dotnet` tool with an ncdu-style terminal browser.
- Script-friendly disk summaries.
- Streaming TSV, Brotli-compressed CSV, and row-grouped Parquet exports.
- Direct uploads to Amazon S3, MinIO, and other S3-compatible endpoints.
- A dense, virtualized WPF desktop app for Windows.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build the repository.
- The command-line tool runs anywhere supported by .NET 10.
- The desktop app requires Windows.

## Command-line tool

Build and install it from this checkout:

```powershell
dotnet pack src/DiskUsage.Cli/DiskUsage.Cli.csproj --configuration Release
dotnet tool install --global --add-source artifacts/packages diskusage --version 2.0.0
```

Once a release is published to NuGet, install it with:

```powershell
dotnet tool install --global diskusage
```

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
