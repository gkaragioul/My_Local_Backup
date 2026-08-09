# MyLocalBackup 1.0 Professional Reliability Design

Date: 2026-08-10  
Status: Approved for implementation  
Branch: `feature/v1-professional`

## Objective

MyLocalBackup 1.0 will be a Windows file-backup product that creates immutable,
cryptographically verified recovery points. It must never report a successful
backup when a configured source, directory, file, manifest, or required metadata
was not captured. It is a file-backup product, not a bootable OS imaging tool.

Version 1.0 starts a new repository format. Existing 0.9.x history and snapshots
will not be imported or migrated.

## Product Guarantees

A recovery point may be shown as **Verified** only when:

1. Every configured source was found and captured from a consistent VSS view.
2. Every supported entry was enumerated exactly once under its immutable source ID.
3. Every stored content object was committed atomically and its SHA-256 was verified.
4. The destination manifest was committed and reopened successfully.
5. A verification pass proved that every manifest reference resolves to the expected object.

Missing sources, access failures, unsupported filesystem entries, VSS failure,
catalog failure, destination mismatch, or verification failure must produce a
failed recovery point and a persistent actionable alert. A partial backup is never
promoted to Verified.

No software can guarantee success when hardware disappears, media is full or
failing, or Windows cannot read a source. The professional guarantee is therefore
**no silent omission and no false success**, with safe retries and exact diagnostics.

## Repository Layout

Each destination contains a self-describing repository:

```text
MyLocalBackup/
  repository.json
  catalog.db
  objects/sha256/ab/<64-character-hash>
  snapshots/<snapshot-id>/manifest.db
  snapshots/<snapshot-id>/summary.json
  staging/<job-id>/
  quarantine/<job-id>/
  logs/
```

- `repository.json` stores format version, repository UUID, creation time, and the
  bound destination volume identity. It never relies on a drive letter alone.
- Content objects are immutable and addressed by SHA-256. Size and modification
  time may identify hashing candidates but can never prove content equality.
- `manifest.db` is the authoritative, self-contained snapshot inventory. It holds
  source IDs, normalized relative paths, entry types, content hashes, sizes,
  timestamps, attributes, link targets, stream records, and security metadata.
- `catalog.db` is a disposable index for the UI and scheduler. It can be rebuilt by
  scanning committed snapshot manifests after OS loss or database corruption.
- Work is written below `staging`; a snapshot becomes visible only through an
  atomic final-directory rename after all validation succeeds.

## Backup Pipeline

1. Acquire a repository-wide single-flight lock.
2. Resolve and validate configured sources and destination using canonical paths.
   Reject source/destination overlap and duplicate/colliding sources.
3. Verify the destination volume UUID/serial against the configured repository.
4. Estimate required space, apply retention safely, and reserve working headroom.
5. Create one coordinated VSS snapshot set for supported local Windows volumes.
   The default is strict: if a required source cannot be captured consistently,
   abort rather than silently falling back to a live copy.
6. Enumerate entries through stable source IDs. Preserve symbolic links and
   junctions as links without following them. Detect loops and path escapes.
7. Stream each new or changed file into a staging object while calculating SHA-256.
   Flush it, reopen it, verify size/hash, then atomically move it into the object store.
8. Record empty directories, timestamps, attributes, ACL/security metadata,
   alternate data streams, and supported reparse metadata in the manifest.
9. Commit and reopen the manifest, then verify every manifest-to-object reference.
10. Evaluate anomaly rules against the last Verified snapshot. A suspicious mass
    deletion or rewrite is committed only to quarantine and cannot trigger pruning
    of known-good history without explicit user approval.
11. Atomically promote the staged snapshot, update the rebuildable catalog, release
    VSS resources, and publish persistent success/failure health state.

Cancellation, process termination, reboot, and power loss leave only disposable
staging data. Startup recovery removes abandoned staging only after validating that
it is contained within the current repository.

## Windows Service and Security Boundary

A small Windows service performs scheduled and privileged operations while the WPF
application remains the user-facing controller.

- The service uses the minimum Windows privileges needed for VSS and backup semantics.
- UI/service IPC uses a local named pipe with an explicit ACL and authenticated user SID.
- All paths received over IPC are canonicalized and validated against stored jobs.
- Repository deletion and retention operations require canonical containment checks;
  no database value is trusted as a deletion path.
- Only one backup, restore, verify, retention, or garbage-collection mutation may run
  against a repository at a time.
- Scheduled jobs run when the UI is closed, support missed-run catch-up, and may wake
  the computer when configured.

## Restore and Verification

The restore browser reads the destination manifest, not the local catalog. Restore
copies data to a temporary sibling, verifies SHA-256, applies metadata, and then
atomically replaces or publishes the destination entry. It supports individual
files, directory trees, empty directories, and preserved links.

Restore never treats `File.Copy` completion as proof. The UI reports separate counts
for restored, verified, skipped-by-user, and failed entries. Any failed required
entry makes the restore incomplete.

Verification modes:

- **Manifest check:** schema, uniqueness, referential integrity, and object existence.
- **Sample check:** deterministic rotating content-hash sampling.
- **Full check:** rehash every referenced object.

The catalog-rebuild command discovers valid manifests, verifies their integrity and
repository IDs, and reconstructs history without needing `%LOCALAPPDATA%`.

## Retention and Full-Disk Behavior

Retention operates on complete snapshots, oldest first, according to user policy.
It always preserves at least one Verified recovery point and never counts failed or
quarantined runs as protected good history.

Deletion is transactional:

1. Mark the selected manifest pending deletion in the catalog.
2. Recompute live object references from remaining committed manifests.
3. Remove the manifest using a containment-validated path.
4. Garbage-collect only objects that no committed manifest references.
5. Verify repository consistency and finalize the catalog transaction.

Retention runs before a backup when space is low and after promotion for policy
maintenance. If safe pruning cannot provide enough space, the job fails clearly
without deleting the last Verified recovery point. Fixed magic thresholds are
replaced with estimated incoming data plus configurable absolute and percentage
headroom.

## Failure Handling and Observability

- All retryable I/O and database operations use bounded retries with exponential
  backoff, cancellation, and a final durable error record.
- Reads have real cancellation/time limits; a blocking read cannot be declared timed
  out only after it eventually returns.
- Error storage is uncapped. The UI paginates failures rather than truncating them.
- Each error includes source ID, path, operation, Windows/native error, retry count,
  and whether it invalidated the recovery point.
- Consecutive failures, overdue jobs, repository corruption, destination mismatch,
  and quarantined anomalies remain visible until acknowledged or repaired.
- Logs are structured, bounded by retention, and redact secrets while preserving paths
  needed for local diagnosis.

## Scheduling

Schedules are owned by the service, not by the tray process. They support interval
and daily-time modes, missed-run catch-up, optional wake timers, and a configurable
AC/battery policy. A machine that was off at the scheduled time runs the overdue job
once after startup rather than postponing it for another day.

## Compatibility and Supported Scope

Version 1.0 targets Windows 11 and .NET 10 LTS. Supported sources are local Windows
filesystems that can provide the required consistency and metadata semantics.
Network/removable sources must be explicitly identified and are either captured under
a documented strict non-VSS policy or rejected; they are never silently downgraded.

The backup model preserves files, directories, empty directories, timestamps,
attributes, supported ACL/security metadata, alternate streams, symbolic links, and
junctions. Unsupported entry types fail the recovery point with a precise reason.
The application does not claim to produce a bootable Windows/system image.

## Database and Manifest Invariants

- Snapshot entry key: `(snapshot_id, source_id, normalized_relative_path)`.
- Source IDs are immutable GUIDs; display names cannot cause path collisions.
- Paths are Windows-normalized, reject traversal, and preserve original casing.
- Each content object records algorithm, hash, logical size, and stored size.
- Catalog writes and manifest promotion are transactional and crash recoverable.
- The catalog receives routine checkpoint/maintenance and can always be discarded
  and rebuilt from the destination.

## Testing Strategy

Implementation is test-first. The solution gains unit, integration, and fault-
injection test projects covering:

- same size/mtime with different bytes;
- missing and inaccessible sources/directories/files;
- duplicate source leaf names;
- destination-inside-source and path traversal;
- links, junctions, loops, broken targets, and link targets outside a source;
- files changing, growing, locking, or disappearing during backup;
- database/object/manifest write failure and bounded retry exhaustion;
- cancellation, crash, reboot-style abandoned staging, and catalog rebuild;
- retention reference safety, last-good preservation, low/full disk, and corruption;
- drive-letter reassignment and destination volume mismatch;
- ransomware-style deletion/rewrite anomalies;
- verified restores of bytes, empty directories, metadata, streams, and links;
- scheduler catch-up, single-flight execution, and persistent failure health.

Windows integration tests exercise VSS and service IPC on disposable test volumes.
The release gate requires all tests, Release build, dependency vulnerability audit,
installer build, clean-machine install/uninstall smoke test instructions, manifest
rebuild test, and an end-to-end backup/full-verify/restore comparison.

## Release Engineering

- Upgrade projects from .NET 9 to .NET 10 LTS.
- Remove the vulnerable SQLite native dependency by upgrading to a patched supported
  package set and fail CI on known high/critical vulnerabilities.
- Add Windows GitHub Actions for restore, build, tests, packaging, vulnerability
  checks, and artifact hashes/SBOM.
- Keep signing hooks for Authenticode; unsigned local builds must be labeled clearly.
- Produce version `1.0.0` only through the repository's `release-build.ps1` workflow.

Per repository safety instructions, automation may build and verify the installer but
must not execute MSI/EXE installation. The user performs the final installer launch.

## Delivery Slices

1. Test infrastructure, repository primitives, hashing, manifests, and atomic commit.
2. Strict backup enumeration, VSS/service boundary, metadata, and diagnostics.
3. Restore, verification, catalog rebuild, retention, and anomaly protection.
4. Scheduler/UI integration, .NET 10 migration, CI/security, and release packaging.

Each slice must keep the branch buildable and must not be reported complete without
fresh verification evidence.
