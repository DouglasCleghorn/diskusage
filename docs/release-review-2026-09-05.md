# Release readiness review — 2026-09-05

Original verdict: the CLI export filters are implemented and verified, but the complete NuGet/Windows/WinGet release was not ready to sign off. The release scope was subsequently narrowed to NuGet/.NET tool `2.0.0-rc.1`; see [the candidate checklist](release-candidate-checklist.md) for the updated review. No packages were published by this review.

## Findings

1. **Incomplete CLI inventories can look successful (P2).** `CliApplication.CreateProgress` disables progress entirely when stderr is redirected. `FileSystemScanner` counts skipped filesystem entries through progress, but `ExportResult` contains only the output path, exported file count, and byte count. Export/upload final summaries omit skipped entries and return success. Consequently, an automated inventory of a partly inaccessible tree can silently be incomplete; top-N then represents only accessible matches. Before release, retain the skipped count independently of interactive progress, include it in the final stderr summary, define an incomplete-scan exit-status policy, and test redirected output with inaccessible entries. This is a pre-existing behavior, not introduced by filtering.

2. **WinGet distribution is not prepared.** The repository contains no WinGet manifests, installer project, or release workflow. Executable and NuGet icons do not create a WinGet package. A WinGet release still needs its chosen distributable, hosted versioned artifacts, hashes, manifests, and installation/upgrade validation. This does not prevent a separate NuGet-only release after its other gates pass.

## Verification completed

- Release solution build: passed, zero warnings and errors.
- Automated tests: 68 passed, including 47 new filter cases.
- NuGet dependency audit, including transitive packages: no known vulnerabilities reported by the configured sources at review time. This is not a security certification.
- Packed `diskusage.2.0.0.nupkg` and installed it into an isolated repository-local tool directory. The installed CLI assembly hash matches the build output.
- Executed the installed tool with extension, size, and top-N filters; decoded CSV, gzip TSV, and Parquet stdout output and checked their selected rows and ordering.
- Invalid size input exits unsuccessfully without writing stdout bytes.
- Tests cover sequential and parallel scanning, nested traversal, extensionless files, comparison boundaries, byte-unit parsing, invalid arguments, deterministic size ties, randomized top-N versus a full-sort reference, empty outputs, compressed formats, cancellation preserving an existing destination, and exclusion of the destination/temporary file from a scan.
- `git diff --check`: no whitespace errors.

## Remaining release gates / limitations

- Exercise upload against a real S3-compatible endpoint, including credentials, cancellation, failure cleanup, and filtered row contents. Only local preparation/argument paths were tested here; no live service credentials were used.
- Run a fresh WPF smoke test: drive display, navigation, scrolling during scanning, completion/save state, export, and cancellation. The full WPF project compiles; this review did not exercise the GUI interactively.
- Run the CLI suite and packaged-tool smoke tests on Linux/macOS if those platforms are advertised. The verification above was on Windows with .NET SDK 10.0.400.
- Confirm that NuGet version `2.0.0` is available before publishing. The public package index was unreachable from this environment, so availability was not established.
- Confirm Parquet's accepted `--compression-level 0` semantics: validation accepts it and the writer keeps the Zstandard codec while selecting `NoCompression`. Add a readback test and document or reject that case rather than implying that numeric zero necessarily disables Parquet compression.
- Early filtering avoids materializing rejected file records and pushing them through the export pipeline. It does not eliminate directory enumeration. Top-N must inspect all accessible candidates and uses O(N) retained records; no representative-drive performance benchmark was run for this change.

## Filter interface

```powershell
diskusage export C:\data --extensions .log,.txt --size ">=100MiB" --top 100 --format parquet --output largest.parquet
```

The same filters work with `upload`. Extension alternatives combine with OR; size conditions and different filter types combine with AND. `--min-size` and `--max-size` are inclusive. Top-N output is largest first, then ordinal full path for ties. Without top-N, records retain streaming enumeration order. See the README for units, compression, and piping examples.
