# diskusage automation reference

Use `diskusage` to find large files, summarize disk usage, and export file metadata to CSV, TSV, or Parquet, locally or to S3-compatible storage. It is a .NET command-line tool; you do not need to reference its assemblies or clone its source to use it.

## Install and discover commands

Requires the .NET 10 SDK for `dotnet tool install` and the .NET 10 runtime to run. For version 2.0.0, once published:

```sh
dotnet tool install --global diskusage --version 2.0.0
diskusage --help
diskusage export --help
diskusage help upload
```

Command-specific help is available without scanning or connecting to a service. This reference describes the current source version; use the installed tool's help for its supported options.

Parsing and option help use System.CommandLine. `-h` is an alias for `--help`; `--version` prints the tool version. Commands and long option names remain case-insensitive, while path/value casing is preserved. Options accept `--name value` or `--name=value`. Only `--extensions` and `--size` may be repeated. Unrelated options and extra paths are rejected rather than ignored. Use `--` before a path beginning with a dash, for example `diskusage export --format csv --stdout -- -folder`. `@`-prefixed paths are literal; response-file expansion is disabled.

## Choose the command

| Need | Command | Output |
| --- | --- | --- |
| Largest files or a complete file listing | `export` | CSV/TSV/Parquet records |
| Directory sizes | `scan` | Human-readable tree and counts; not JSON |
| Store an inventory in S3/MinIO | `upload` | Export staged locally, then uploaded |
| Explore manually | `browse` | Interactive terminal browser |

Paths default to the current directory. Prefer an explicit path in automation. With no command, `diskusage [path]` selects `browse`; redirected input/output makes it print a plain summary instead. All scans are recursive. `scan --depth` limits the displayed report, not traversal.

## Recipes

The following local recipes run in CI against a small synthetic directory. Relative output paths place generated files outside the scanned tree.

Top two matching files as CSV, largest first:

<!-- smoke:filtered-csv -->
```sh
diskusage export . --extensions .log,.txt --size ">=1KiB" --top 2 --format csv --stdout
```

Gzip-compressed TSV with an explicit compression level:

<!-- smoke:compressed-tsv -->
```sh
diskusage export . --extensions .log,.txt --size ">=1KiB" --top 2 --format tsv.gz --compression-level 6 --output ../inventory.tsv.gz
```

Upload to MinIO using credentials supplied through the AWS credential chain. Substitute your directory, endpoint, bucket, and key. This is a live-service example, not executed by CI:

```sh
diskusage upload /data --extensions .log,.txt --min-size 1KiB --format csv.gz --endpoint http://localhost:9000 --bucket inventories --key workstation/files.csv.gz
```

`--endpoint` is optional for Amazon S3. `--bucket` is required; `--region` defaults to `us-east-1`. Addressing is path-style unless `--virtual-hosted-style` is supplied. If `--key` is omitted, the tool generates `diskusage/<machine>-yyyyMMdd-HHmmss.<format>` using UTC. An existing object at the selected key can be replaced. Upload prepares a temporary local export and uses one PUT, not multipart upload; live S3/MinIO integration has not yet been validated.

The AWS SDK credential chain supports environment variables (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, optional `AWS_SESSION_TOKEN`), shared profiles, and workload credentials. Alternatively supply both `--access-key` and `--secret-key`, with optional `--session-token`.

## Filters and ordering

- `--extensions` accepts comma-separated final extensions; `txt`, `.txt`, and `*.txt` are equivalent and case-insensitive. Repeat for OR. Quote `"<none>"` to match extensionless files. `.tar.gz` has final extension `.gz`.
- `--size` accepts `<`, `<=`, `=`, `==`, `!=`, `>=`, or `>`; a bare size means equality. Quote comparisons in a shell. Repeated conditions combine as AND.
- `--min-size` and `--max-size` are inclusive. Different filter types combine as AND.
- Sizes use bytes by default, decimal `KB/MB/GB/TB`, or binary `KiB/MiB/GiB/TiB`. Fractional units are allowed when they resolve to whole bytes.
- `export/upload --top N` selects the globally largest N matching files; N must be positive. Ties sort by ordinal full path. Without it, all matches stream in unspecified enumeration order.
- Extension/size filters apply during enumeration, but directories are still traversed. Top-N examines all candidates while retaining only N records.
- `scan --top N` instead limits displayed directories and, separately, files **per directory** (default 50). `scan --depth N` defaults to 1. Both accept 0 to hide children. Add `--include-files` to display files. Export filters are not available for `scan` or `browse`.

## Scan defaults

Hidden entries are excluded unless `--include-hidden` is supplied. Linked directories are not traversed unless `--follow-links` is supplied; repeated targets are detected. `--threads N` must be positive, defaults to up to four workers depending on CPU count, and is capped at 32. Use 1 for sequential enumeration.

Inaccessible entries are skipped. `scan` reports skipped counts in its final summary. Export/upload final summaries currently omit skipped counts, so exit 0 does not imply that every filesystem entry was readable. Progress shows skipped counts only when stderr is attached to a terminal. Files may change during enumeration.

## Output schema and formats

All export formats contain file records, not directory aggregates, with these columns in order:

| Column | Value |
| --- | --- |
| `full_path` | Absolute path string |
| `size_bytes` | 64-bit integer byte count |
| `created_utc` | UTC creation timestamp |
| `modified_utc` | UTC last-write timestamp |

CSV/TSV use UTF-8 without a BOM, a header row, invariant integer formatting, and ISO 8601 round-trip timestamps. Quoting escapes delimiters, quotes, and newlines; use a delimited-data parser rather than splitting lines. No matches produces a valid empty export. Only metadata is exported, not file contents.

`--format` defaults to **Parquet**, independently of the output filename. Always specify it for CSV/TSV. Supported formats are `parquet`, `csv`, `tsv`, `csv.br`, `tsv.br`, `csv.gz`, `tsv.gz`, `csv.zip`, and `tsv.zip`. Alternatively use `--format csv|tsv --compression none|br|gz|zip`; conflicting selections are rejected. ZIP contains one CSV/TSV entry.

`--compression-level`: Brotli accepts 0–11; gzip/ZIP accept 0–9. Omit for the codec default. Plain CSV/TSV does not accept a compression level. Parquet uses Zstandard with the `Optimal` default (Zstd 3), 250,000-row groups, and byte-stream-split encoding for file sizes. Parquet numeric presets map 1–3 to Zstd 1, 4–7 to Zstd 3, and 8–9 to Zstd 19; they are not arbitrary Zstd levels. Avoid Parquet level 0, which does not reliably mean uncompressed.

## Streams, destinations, and exit codes

| Invocation | stdout | stderr |
| --- | --- | --- |
| `export --stdout` or `export --output -` | Export bytes only | Progress, completion summary, errors |
| `export --output FILE` | Completion summary | Progress, errors |
| `scan` | Size tree and final counts | Progress, errors |
| `upload` | Completion location | Progress, upload status, errors |
| `help` / `--help` | Help text | Errors for invalid requests |

When export output is omitted, redirected stdout receives the export; otherwise the tool creates `diskusage-yyyyMMdd-HHmmss.<format>` in the working directory. Prefer explicit `--stdout` or `--output FILE`. `--stdout` cannot be combined with a file destination. Never combine stderr into stdout when consuming export bytes.

CSV/TSV can stream directly to a pipe, including to an external compressor. Parquet stdout is first completed in a temporary file because writing requires seeking. `--output FILE` uses a temporary file and atomic replacement; the destination and temporary file are excluded from the scan. Shell-created destinations are not excluded or atomically managed: place them outside the scanned tree and expect partial output if interrupted.

Exit codes: **0** completed (including help and zero matches), **1** invalid arguments or operation failure, **130** Ctrl+C cancellation. Errors are human-readable on stderr. There is no JSON result/error mode currently. Check the process exit code, not a completion-message substring.
