# Optional Google Drive synchronization

Synchronization is local-first and schema-versioned. `SyncEnvelope` currently uses
schema version 1; each record has a stable GUID and UTC update timestamp. Newer
timestamps win, deleted records are tombstones, and equal timestamps use a
canonical deterministic tie-break. Ticket entry time, barcode data, and plate
snapshots are carried through merges.

`GoogleDriveSynchronizationService` depends on authentication, transport, network,
retry abstractions. The platform-neutral `GoogleDriveRestTransport` uses a bearer
token provider and Drive v3 `version` values exposed as `VersionToken` (not HTTP
ETags). Listing, creation, metadata reads, and updates explicitly request
`id,name,version,modifiedTime`; listing also requests `headRevisionId`. Uploads
return the server version, using a follow-up metadata GET when needed. Missing
metadata produces a synchronization failure while preserving local data.

Metadata reads bracket content downloads to detect changes during the read.
Before PATCH, the transport reads metadata and compares its version with the
version observed before merging. A mismatch restarts download/merge/upload, with
the existing limit of three conflict attempts. This read-compare-update sequence
is **not atomic**: another writer can intervene between the check and PATCH.
The [Drive v3 update reference](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/update)
does not document an atomic version precondition for media uploads. No ETag is
fabricated and no unsupported conditional header is sent. Record-level merging
and bounded retries remain, but cannot guarantee prevention of every lost update
in that race window. The [File resource reference](https://developers.google.com/workspace/drive/api/reference/rest/v3/files)
defines the monotonically increasing `version` field.

 Duplicate matching files are
reported by the transport and a deterministic canonical-file reconciler is used.
Android uses Google Identity `AuthorizationClient`, identified by the Android
package name and signing certificate. iOS/iPadOS and Mac Catalyst use the
system-browser PKCE flow with the reversed client-ID callback scheme. Build-time OAuth configuration is read from
`ParkingHelper.App/GoogleAuth.local.json` when that project-local file exists,
then `GoogleAuth.local.json` in the repository root;
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

## Version handling verification (2026-09-13)

All 175 regression tests pass, including HTTP transport create/update responses,
metadata fallback, missing versions, stale versions, changes during download,
and integration with the existing three-attempt conflict retry loop. Android
build succeeds with existing NU1608 dependency warnings. OAuth and the record
merge engine were not changed. Live authentication was reported working by the
user; live creation/update/version verification remains pending an unlocked
connected device. Milestone 8 is not yet signed off.

## Mac Catalyst secure token storage

`MissingEntitlement` from `SecureStorage.SetAsync` means the Apple Keychain
rejected token persistence after the Google token exchange. It is separate from
Google client registration and Drive synchronization. The Mac Catalyst
entitlements now declare `$(AppIdentifierPrefix)$(CFBundleIdentifier)` as the
Keychain access group, and the project explicitly consumes that entitlements file
and requires provisioning. The prefix must come from a valid signing profile;
ad-hoc signing with no team identifier cannot provide this setup.

Install a Mac Catalyst development provisioning profile for
`com.rjregalado.parkinghelper`, associated with the development certificate and
this Mac. Select that profile and certificate in the IDE signing configuration
(or pass `CodesignProvision` and `CodesignKey` to MSBuild). The installed profile
at verification time targets a different iOS application, so the normal Mac
Catalyst build correctly stops with “Could not find any available provisioning
profiles”. After provisioning, rebuild and confirm the signed bundle contains
the expanded Keychain access group before retrying Connect Google Drive.

Token state is committed in memory only after secure persistence succeeds.
Persistence errors now show a secure-storage error instead of escaping the
Connect button's async event handler. No plaintext fallback is used. The Google
OAuth endpoints and redirects remain unchanged; the account-display update below
adds only the OpenID identity scopes. The Drive merge logic is unchanged.

References: [MAUI Mac Catalyst entitlements](https://learn.microsoft.com/en-us/dotnet/maui/mac-catalyst/entitlements?view=net-maui-10.0)
and [Apple signing build properties](https://learn.microsoft.com/en-us/dotnet/ios/building-apps/build-properties).


## Google Drive Settings and logout

Settings shows only **Connect to Google Drive** while disconnected. Account name
and email, **Sync Now**, **Disconnect**, and the last successful sync time are
shown only for an authenticated connection. Missing names fall back to email;
missing identity information leaves synchronization usable. Identity consists
only of name/email. Apple stores it alongside the existing secure token record;
Android keeps it with the current in-memory authorization session. No tokens are
written to Preferences, logs, SQLite, or ordinary files.

Connect requests exactly `openid profile email` and
`https://www.googleapis.com/auth/drive.appdata`. Android uses the supported
`AuthorizationRequest.Prompt.SELECT_ACCOUNT`; Apple uses
`prompt=select_account consent`, a fresh checked OAuth state, PKCE, and an
ephemeral browser session. Cancellation leaves the disconnected controls visible.
The connection is not published until token acquisition (and Apple secure
persistence) succeeds. Initial synchronization still uses the existing engine.

`GoogleDriveConnection` serializes automatic and Settings-triggered syncs and
notifies the page of busy/account/status changes. The latest successful sync time
is retained for the current app session even after a later sync failure and is
cleared on disconnect. A 401 triggers one token recovery attempt; unrecoverable
authorization clears the account and requires reconnecting. Transient sync or
refresh failures retain the connection. No merge, schema, or bounded conflict
retry logic was changed.

Disconnect cancels active sync, waits for it to finish, clears Parking Helper's
local session, and attempts revocation. Android uses GIS `revokeAccess` for the
selected account/scopes and clears the cached access token; Apple revokes the
refresh token (or access token if no refresh token is available). Revocation
failure is reported as a local disconnect with instructions to remove access in
Google Account settings. It is not falsely reported as confirmed remote
revocation. No global Google sign-out or Drive/local data deletion is performed.
The next Connect starts account selection again.

Automated verification: 197 regression tests pass, including real browser-request
construction with fake browser/secure-storage adapters, cancellation, identity
failure, secure-save failure, refresh/revocation, connection transitions,
concurrent sync/logout, preservation of local/cloud records, and HTTP token
recovery. Android builds and Mac Catalyst compilation (signing disabled for the
compile check) are verified separately. Live chooser/consent/reconnect verification
is pending an unlocked Android device; Mac live verification still requires a
matching provisioning profile. Automated tests do not claim live Google success.

References: [Google Android authorization](https://developer.android.com/identity/authorization),
[account-selection prompt](https://developers.google.com/android/reference/com/google/android/gms/auth/api/identity/AuthorizationRequest.Builder),
and [Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect).
