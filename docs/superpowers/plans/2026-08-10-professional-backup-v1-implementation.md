# MyLocalBackup 1.0 Implementation Plan

> **Execution note:** Follow test-driven development for every behavior: add one
> focused failing test, observe the expected failure, implement the minimum correct
> behavior, rerun the focused test, then run the full suite before committing.

**Goal:** Replace the mutable hardlink snapshot engine with an immutable,
hash-verified, self-describing backup repository and ship a reliable Windows service,
restore path, retention system, and v1.0 installer.

**Architecture:** The Core library owns repository, manifest, backup, verification,
restore, retention, and scheduling contracts. A Windows worker service performs VSS
and scheduled operations. The WPF UI communicates with it through an ACL-restricted
named pipe and reads repository health through Core abstractions. Destination
manifests are authoritative; the local catalog is rebuildable.

**Technology:** C#/.NET 10 LTS, WPF, Windows Service, Microsoft.Data.Sqlite, xUnit,
SHA-256, VSS/Windows native APIs, WiX, GitHub Actions.

---

## Task 1: Establish test and platform foundations

**Files:**
- Create: `Directory.Build.props`
- Create: `MyLocalBackup.Core.Tests/MyLocalBackup.Core.Tests.csproj`
- Create: `MyLocalBackup.Core.Tests/TestFileSystem.cs`
- Modify: `MyLocalBackup.sln`
- Modify: `MyLocalBackup.Core/MyLocalBackup.Core.csproj`
- Modify: `MyLocalBackup.UI/MyLocalBackup.UI.csproj`

**Steps:**
1. Add an xUnit test project with FluentAssertions and temporary-directory helpers.
2. Add a deliberately trivial solution smoke test and observe the test project being absent/failing first.
3. Move common nullable, deterministic, warning, and version properties to `Directory.Build.props`.
4. Target .NET 10, update Microsoft.Data.Sqlite to a patched supported version, and set product version `1.0.0`.
5. Run `dotnet test MyLocalBackup.sln -c Release` and the vulnerable-package audit.
6. Commit: `test: establish v1 reliability test foundation`.

## Task 2: Define repository domain invariants

**Files:**
- Create: `MyLocalBackup.Core/Models/RepositoryModels.cs`
- Create: `MyLocalBackup.Core/Models/SnapshotModels.cs`
- Create: `MyLocalBackup.Core/Storage/PathRules.cs`
- Create: `MyLocalBackup.Core.Tests/Storage/PathRulesTests.cs`
- Modify: `MyLocalBackup.Core/Models/BackupConfig.cs`
- Modify: `MyLocalBackup.Core/Models/RestorePoint.cs`

**Steps:**
1. Write failing tests for path traversal, canonical overlap, duplicate leaf-name sources, case handling, and stable source IDs.
2. Introduce repository/snapshot/source IDs and statuses: Staging, Verified, Failed, Quarantined, Deleting.
3. Implement canonical containment and source/destination overlap rejection.
4. Replace bare source strings with immutable source records while preserving config deserialization defaults only.
5. Run focused and full tests; commit `feat: define v1 repository invariants`.

## Task 3: Implement atomic content-addressed object storage

**Files:**
- Create: `MyLocalBackup.Core/Storage/IObjectStore.cs`
- Create: `MyLocalBackup.Core/Storage/ContentAddressedObjectStore.cs`
- Create: `MyLocalBackup.Core/Storage/AtomicFileCommit.cs`
- Create: `MyLocalBackup.Core.Tests/Storage/ContentAddressedObjectStoreTests.cs`
- Delete: `MyLocalBackup.Core/Storage/HardLinkManager.cs`

**Steps:**
1. Write failing tests proving that equal size/mtime with different bytes produce different objects.
2. Test duplicate bytes deduplicate, interrupted writes never appear committed, and hash collision/corruption is rejected.
3. Stream to repository-local staging while hashing SHA-256, flush, reopen, verify, and atomically move.
4. Make committed objects immutable and never overwrite an existing hash path without verification.
5. Add cancellation and bounded retry fault injection.
6. Run tests; commit `feat: add immutable verified object store`.

## Task 4: Add self-contained snapshot manifests

**Files:**
- Create: `MyLocalBackup.Core/Storage/ManifestDatabase.cs`
- Create: `MyLocalBackup.Core/Storage/ManifestSchema.cs`
- Create: `MyLocalBackup.Core/Storage/RepositoryManager.cs`
- Create: `MyLocalBackup.Core.Tests/Storage/ManifestDatabaseTests.cs`
- Create: `MyLocalBackup.Core.Tests/Storage/RepositoryManagerTests.cs`

**Steps:**
1. Write failing schema tests for unique `(snapshot, source, relative_path)` entries and referential integrity.
2. Test atomic promotion, abandoned staging recovery, repository identity, and destination-volume mismatch.
3. Implement `repository.json`, per-snapshot SQLite manifests, summary JSON, and repository locking.
4. Reopen and integrity-check a manifest before promotion.
5. Run tests; commit `feat: add self-contained atomic snapshot manifests`.

## Task 5: Build strict filesystem enumeration

**Files:**
- Create: `MyLocalBackup.Core/FileSystem/IBackupSourceReader.cs`
- Create: `MyLocalBackup.Core/FileSystem/WindowsBackupSourceReader.cs`
- Create: `MyLocalBackup.Core/FileSystem/FileSystemEntry.cs`
- Create: `MyLocalBackup.Core.Tests/FileSystem/WindowsBackupSourceReaderTests.cs`

**Steps:**
1. Write failing tests for empty folders, inaccessible/missing entries, duplicate names, and disappearing files.
2. Write tests for symlinks, junctions, broken targets, outside targets, and loops; assert links are recorded and not followed.
3. Enumerate with stable source IDs and emit a fatal error for every unhandled required entry.
4. Capture timestamps, attributes, reparse/link data, supported ACLs, and alternate stream descriptors.
5. Run tests; commit `feat: enumerate Windows sources without silent omissions`.

## Task 6: Add VSS snapshot provider and service privilege boundary

**Files:**
- Create: `MyLocalBackup.Core/Consistency/ISnapshotProvider.cs`
- Create: `MyLocalBackup.Core/Consistency/VssSnapshotProvider.cs`
- Create: `MyLocalBackup.Core/Consistency/StrictSnapshotProvider.cs`
- Create: `MyLocalBackup.Core.Tests/Consistency/StrictSnapshotProviderTests.cs`
- Create: `MyLocalBackup.Service/MyLocalBackup.Service.csproj`
- Create: `MyLocalBackup.Service/Program.cs`
- Create: `MyLocalBackup.Service/BackupWorker.cs`
- Modify: `MyLocalBackup.sln`

**Steps:**
1. Test strict abort behavior when any configured source lacks a consistent snapshot.
2. Implement a coordinated VSS set with deterministic cleanup and cancellation.
3. Add the Windows worker service and minimum required privilege initialization.
4. Add administrator-gated local VSS smoke tests that skip explicitly in non-privileged CI.
5. Run tests/build; commit `feat: capture sources through strict VSS snapshots`.

## Task 7: Replace BackupJob with the verified pipeline

**Files:**
- Rewrite: `MyLocalBackup.Core/Engine/BackupJob.cs`
- Create: `MyLocalBackup.Core/Engine/BackupPipeline.cs`
- Create: `MyLocalBackup.Core/Engine/BackupResult.cs`
- Create: `MyLocalBackup.Core.Tests/Engine/BackupPipelineTests.cs`

**Steps:**
1. Write failing end-to-end tests for a complete verified snapshot and each current false-success case.
2. Test source changes, growth, locked reads, cancellation, DB/object failures, retry exhaustion, and no partial promotion.
3. Compose validation, VSS, enumeration, object store, manifest, verification, and promotion.
4. Ensure all failure lists are uncapped and structured.
5. Run full tests; commit `feat: replace backup job with strict verified pipeline`.

## Task 8: Implement verification and catalog rebuilding

**Files:**
- Create: `MyLocalBackup.Core/Engine/RepositoryVerifier.cs`
- Create: `MyLocalBackup.Core/Engine/CatalogRebuilder.cs`
- Rewrite: `MyLocalBackup.Core/Data/DatabaseManager.cs`
- Create: `MyLocalBackup.Core.Tests/Engine/RepositoryVerifierTests.cs`
- Create: `MyLocalBackup.Core.Tests/Engine/CatalogRebuilderTests.cs`

**Steps:**
1. Write failing tests for missing, corrupt, orphaned, and wrong-size objects.
2. Implement manifest, deterministic sample, and full verification modes.
3. Make the local catalog disposable and rebuild it only from committed valid manifests.
4. Add real bounded SQLite retries; final commit failure must fail the operation.
5. Run tests; commit `feat: verify repositories and rebuild local history`.

## Task 9: Implement verified restore

**Files:**
- Rewrite: `MyLocalBackup.Core/Engine/RestorationManager.cs`
- Create: `MyLocalBackup.Core/Engine/RestoreResult.cs`
- Create: `MyLocalBackup.Core.Tests/Engine/RestorationManagerTests.cs`

**Steps:**
1. Write failing restore tests for bytes, nested and empty directories, metadata, streams, links, collision policy, and corrupt objects.
2. Restore through a temporary sibling, verify hash, apply metadata, and atomically publish.
3. Return explicit restored/verified/skipped/failed counts; never report success after a failed required entry.
4. Run tests; commit `feat: restore and verify every recovered entry`.

## Task 10: Implement safe retention and garbage collection

**Files:**
- Rewrite: `MyLocalBackup.Core/Engine/RetentionManager.cs`
- Create: `MyLocalBackup.Core/Storage/ObjectGarbageCollector.cs`
- Create: `MyLocalBackup.Core/Engine/CapacityPlanner.cs`
- Create: `MyLocalBackup.Core.Tests/Engine/RetentionManagerTests.cs`
- Create: `MyLocalBackup.Core.Tests/Storage/ObjectGarbageCollectorTests.cs`

**Steps:**
1. Write failing tests for oldest-first policy, pinned/last-good preservation, failed/quarantined snapshots, and containment attacks.
2. Test shared-object reference safety, interruption, corruption, and full/low disk preflight.
3. Implement transactional manifest deletion followed by mark-and-sweep GC.
4. Replace fixed thresholds with estimated input plus absolute/percentage reserve.
5. Run tests; commit `feat: add reference-safe retention and capacity planning`.

## Task 11: Add mass-change anomaly protection

**Files:**
- Create: `MyLocalBackup.Core/Engine/ChangeAnomalyDetector.cs`
- Create: `MyLocalBackup.Core.Tests/Engine/ChangeAnomalyDetectorTests.cs`
- Modify: `MyLocalBackup.Core/Engine/BackupPipeline.cs`

**Steps:**
1. Write failing tests for ordinary churn, mass deletion, mass rewrite, empty source, and first backup.
2. Implement configurable thresholds with conservative defaults and evidence in the result.
3. Route suspicious completed jobs to quarantine and prevent them from triggering known-good pruning.
4. Run tests; commit `feat: quarantine destructive source-change anomalies`.

## Task 12: Implement service scheduling and secure IPC

**Files:**
- Create: `MyLocalBackup.Core/ServiceProtocol/*`
- Create: `MyLocalBackup.Service/Ipc/NamedPipeServer.cs`
- Create: `MyLocalBackup.UI/Services/BackupServiceClient.cs`
- Rewrite: `MyLocalBackup.Core/Engine/BackupScheduler.cs`
- Create: `MyLocalBackup.Core.Tests/Engine/BackupSchedulerTests.cs`
- Create: `MyLocalBackup.Core.Tests/ServiceProtocol/ProtocolValidationTests.cs`

**Steps:**
1. Write failing tests for missed daily runs, interval runs, reboot catch-up, overlap prevention, and clock changes.
2. Write security tests for unauthorized SID, unknown job IDs, traversal, and malformed/oversized messages.
3. Move schedule ownership to the service and add optional wake/AC policies.
4. Implement authenticated ACL-restricted named-pipe messages with versioned contracts.
5. Run tests; commit `feat: run reliable schedules through secured service IPC`.

## Task 13: Integrate v1 state into WPF

**Files:**
- Modify: `MyLocalBackup.UI/Views/DashboardView.xaml*`
- Modify: `MyLocalBackup.UI/Views/HistoryView.xaml*`
- Modify: `MyLocalBackup.UI/Views/SettingsView.xaml*`
- Modify: `MyLocalBackup.UI/RestoreWindow.xaml*`
- Modify: `MyLocalBackup.UI/App.xaml.cs`
- Create: `MyLocalBackup.UI/ViewModels/*`

**Steps:**
1. Add view-model tests for Verified/Failed/Quarantined/Overdue health and paginated uncapped errors.
2. Replace direct engine calls with the service client.
3. Add repository initialization, destination identity, verification, catalog rebuild, anomaly approval, and strict-source settings.
4. Make failures persistent until acknowledged; remove transient-only failure UX.
5. Run tests/build and perform a manual non-destructive UI smoke check; commit `feat: expose verified repository health in the UI`.

## Task 14: Harden CI, dependencies, and packaging

**Files:**
- Create: `.github/workflows/windows-ci.yml`
- Create: `.github/dependabot.yml`
- Create: `global.json`
- Modify: `release-build.ps1`
- Modify: `Staging/MyLocalBackup_Setup/Package.wxs`
- Modify: `Staging/MyLocalBackup_Setup/Bundle.wxs`

**Steps:**
1. Add restore/build/test/vulnerability/SBOM/package jobs on Windows.
2. Package and configure the Windows service with correct upgrade/removal behavior.
3. Add signing hooks without embedding secrets and label unsigned development artifacts.
4. Make release fail on warnings, failed tests, vulnerable high/critical packages, or version mismatch.
5. Run CI-equivalent local commands; commit `build: harden v1 CI and installer pipeline`.

## Task 15: End-to-end reliability qualification

**Files:**
- Create: `MyLocalBackup.IntegrationTests/*`
- Create: `docs/v1-recovery-validation.md`
- Modify: `README.md`
- Modify: `CHANGELOG.md`

**Steps:**
1. Add a disposable repository test: backup, full verify, delete originals, restore, and byte/metadata compare.
2. Add simulated crash, low-disk, destination swap, catalog loss/rebuild, and retention tests.
3. Run the entire suite repeatedly and a release build from a clean checkout.
4. Run `release-build.ps1` and verify installer/artifact hashes without launching MSI/EXE.
5. Document honest guarantees, unsupported cases, recovery procedure, and service behavior.
6. Commit `release: qualify MyLocalBackup 1.0.0`.

## Final Verification Gate

Run fresh and retain the output:

```powershell
dotnet restore MyLocalBackup.sln
dotnet build MyLocalBackup.sln -c Release --no-restore
dotnet test MyLocalBackup.sln -c Release --no-build
dotnet list MyLocalBackup.sln package --vulnerable --include-transitive
pwsh -File .\release-build.ps1
git status --short
```

Then inspect the generated installer metadata, service entries, SBOM, and SHA-256
artifacts. Do not execute the installer automatically; hand the verified path to the
user for the final manual installation click required by repository policy.
