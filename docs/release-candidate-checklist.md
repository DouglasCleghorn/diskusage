# NuGet / .NET tool release candidate checklist

Review date: 2026-09-05. Candidate: `diskusage` **2.0.0-rc.1**. Windows installers and WinGet are out of scope; WPF remains in the source repository. This document records checks, not a guarantee that no undiscovered issue exists.

## Privacy and repository exposure

- [x] Enumerated all tracked and pending source files, historical filenames, all five commits, commit messages, and author/committer metadata. The remote advertises only `master`, matching local history; no tags or additional advertised refs were found.
- [x] Ran Gitleaks 8.30.1 (official download, verified SHA-256) over all reachable Git history and a copy of the tracked/pending tree. No credential leaks were reported. Supplementary checks covered email addresses, local user paths, private hosts/addresses, credentials/configuration, generated inventories, and deleted historical files.
- [x] With the owner's approval, replaced the historical personal author/committer email with the existing GitHub no-reply address using git-filter-repo. Verified every historical file tree, author/committer name, timestamp, and commit message was unchanged. Rewriting changes commit IDs and removes old cryptographic signatures. The rewritten branch contains no MSN author/committer address; publication uses an explicit force-with-lease, never an unconditional force push. Local recovery objects/app snapshots and previously cloned or cached copies are not erased by this operation.
- [x] Checked GitHub visibility: the repository is **already public**. No visibility setting was changed. No releases, Actions runs/artifacts, issues/PRs, forks, or Pages site were found; discussions are disabled. The wiki Git endpoint did not expose a repository.
- [x] Checked PNG assets for metadata: only image/palette data chunks, no EXIF or text metadata. Tracked binary assets are icons, not drive screenshots or inventories.
- [x] Identified local Windows user paths in an earlier locally built package. Enabled normalized deterministic build paths and checked the rebuilt DLLs/PDBs and all archive entries: no local username, personal email domain, or Windows user-directory path detected. An upstream Parquet DLL contains its public CI runner path, not this developer's path.
- [x] Inspected the already-public NuGet `1.0.0` archive: no credential leaks reported, but its application DLL contains a personal build-path reference. The new RC fixes future build-path exposure; it cannot remove copies of the old published package. No unlisting/deletion was attempted. If removal is required, consult NuGet support; unlisting alone does not erase downloaded or directly addressable copies.
- [x] Keep build outputs, scan inventories, benchmark artifacts, credentials, and local environment files ignored. No generated inventory or audit log is staged for publication. Public upstream attribution/contact details remain in third-party license notices.
- [ ] Enable GitHub secret scanning and push protection; both were disabled when inspected. Dependabot security updates were enabled. Settings were inspected, not changed.

GitHub warns that making a repository public also exposes Actions history/logs. Rewriting Git history cannot retract existing clones or every cached view. If any real credential is discovered, revoke/rotate it first; do not rely on deleting its file. [GitHub visibility guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/managing-repository-settings/setting-repository-visibility), [sensitive-data removal guidance](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository).

## Package identity and contents

- [x] Mark the version as prerelease: `2.0.0-rc.1`. NuGet currently lists stable `1.0.0` under the owner's account and links to this repository. The candidate version was not listed at review time; availability is not a reservation.
- [x] Verify `PackAsTool`, command name `diskusage`, target .NET 10, and bundled Core/runtime dependencies. The WPF executable, tests, benchmarks, local config, and real inventories are not shipped in the tool package.
- [x] Include description, author, project/repository URLs, tags, copyright, release notes, embedded PNG icon, README, MIT license, and third-party license/attribution notices for bundled dependencies.
- [x] Document exact-version install/update commands so users receive the candidate rather than stable 1.0.0.
- [x] Preserve source-link metadata while normalizing local source paths. Build the upload artifact from the final clean commit after any history rewrite so its repository commit metadata is current.
- [ ] Preview the final README/package on NuGet before submitting. Confirm the icon and links render correctly and the account has ownership/publish rights. Do not assume the metadata `Authors` field grants ownership.

NuGet recommends an explicit prerelease version for previews and metadata covering README, license, icon, repository, and release notes. A .NET tool is distributed as a NuGet package; it does not need a separate tool registry. [NuGet authoring guidance](https://learn.microsoft.com/en-us/nuget/create-packages/package-authoring-best-practices), [.NET tool creation](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create).

## Functional and security review

- [x] Full Release build: zero warnings/errors. Automated suite: 68 passing tests, including filtering, top-N, compression formats, cancellation, and atomic destination preservation.
- [x] Audit direct and transitive NuGet dependencies: no known vulnerabilities reported by the configured advisory sources at review time. This does not rule out undisclosed vulnerabilities.
- [x] Verify a repository-local installation of the packed tool, filtered/compressed stdout exports, readable Parquet, invalid-argument exit behavior, and absence of diagnostics in piped data.
- [x] Review network behavior: scanning/exporting is local; upload uses the caller-selected S3 endpoint and AWS credential chain or explicit credentials. No application telemetry or hardcoded live credentials found.
- [x] Document the inventory privacy model: absolute paths/timestamps may be sensitive; compression is not encryption; prefer HTTPS and credential providers. Temporary export files also contain inventory information and use OS directory permissions.
- [x] Document RC limitations: skipped entries are not retained in final export/upload summaries, changing files prevent snapshot consistency, Parquet level 0 is ambiguous, stdout redirection is not atomic, terminal names are rendered literally, and uploads use a single PUT rather than multipart.
- [ ] Before stable release: report partial inventories explicitly with a tested exit-status policy, harden terminal rendering for hostile filenames, resolve level-0 semantics, and validate large-object upload limits. RC warnings are not substitutes for those fixes.
- [ ] Before claiming verified cross-platform or live S3 support: run Linux/macOS packaged-tool tests and real S3/MinIO upload/error/cancellation tests. This review's execution environment was Windows; it did not use service credentials or upload user data.

## Publish and post-publish procedure

1. Resolve the historical email decision and review the exact staged diff. Commit with the no-reply identity. Push only the intended branch; if rewriting is approved, verify the old remote SHA and use an explicit lease, not an unconditional force push. Do not publish backup refs containing the old history.
2. From the final clean commit, run the Release build/tests, dependency audit, and `dotnet pack src/DiskUsage.Cli/DiskUsage.Cli.csproj -c Release`. Reinspect the exact `.nupkg`, its repository SHA, source paths, notices, and SHA-256; repeat an isolated `dotnet tool install` and smoke test.
3. Publish only that candidate package after explicit publication authorization, using a narrowly scoped, short-lived API key or trusted publishing. Never commit a token or put one in example commands, logs, screenshots, or the repository. Use an environment/secret store and confirm the NuGet account first.
4. Install `diskusage --version 2.0.0-rc.1` from nuget.org in a clean tool directory, check exported output, and confirm the public package page and README. Mark any GitHub release as a prerelease; create a tag/release only when requested.
5. Keep release artifacts immutable, monitor feedback/advisories, and publish a new RC version for fixes. Do not silently replace a package or claim stable validation from the RC tests.

NuGet publishing is a separate external action from pushing source commits. Its guidance covers ownership, package preview, scoped API keys, and secret handling. [.NET/NuGet publishing guidance](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package).
