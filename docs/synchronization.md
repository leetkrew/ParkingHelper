# Optional Google Drive synchronization

Synchronization is local-first and schema-versioned. `SyncEnvelope` currently uses
schema version 1; each record has a stable GUID and UTC update timestamp. Newer
timestamps win, deleted records are tombstones, and equal timestamps use a
canonical deterministic tie-break. Ticket entry time, barcode data, and plate
snapshots are carried through merges.

`GoogleDriveSynchronizationService` depends on authentication, transport, network,
retry abstractions. The platform-neutral `GoogleDriveRestTransport` uses a bearer
token provider, Drive file ETags, and `If-Match` optimistic writes; a bounded
download/merge/retry handles version conflicts. Duplicate matching files are
reported by the transport and a deterministic canonical-file reconciler is used.
Android uses Google Identity `AuthorizationClient`, identified by the Android
package name and signing certificate. iOS/iPadOS and Mac Catalyst use the
system-browser PKCE flow with the reversed client-ID callback scheme. Build-time OAuth configuration is read from
`ParkingHelper.App/GoogleAuth.local.json` when that project-local file exists;
otherwise the default is `~/.config/ParkingHelper/GoogleAuth.local.json`.
Override the path with
`-p:GoogleAuthConfigPath=/path/to/GoogleAuth.local.json`. The committed
`GoogleAuth.example.json` documents the required structure. The file must
contain only the Android, iOS, and Mac Catalyst OAuth client IDs plus the
reversed client IDs required by the Apple callback schemes. It is read by a
build-only generator into `obj`; it is not an app resource or package content.
Use owner-only permissions where supported (for example `chmod 600`).
Development and production builds should provide the file through local or
protected CI/build configuration; never add it to source control. No client
secret is required or stored. Local reads and writes do
not depend on Drive. Settings exposes Connect, Sync Now, status, and Disconnect;
offline sync is a no-op that preserves local changes.

Manual synchronization is always available. When a real authenticated adapter is
configured, writes are debounced and app resume requests a conservative automatic
sync; unconfigured/offline adapters are skipped.
