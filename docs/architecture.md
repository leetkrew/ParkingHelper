# Parking Helper architecture

**Scan. Save. Show.**

Milestone 5 adds [ticket management and presentation](tickets.md) to [automatic ticket saving](save-ticket-flow.md). Both scanner success and ticket-list selection push an ID-only Ticket Preview; full-screen presentation reloads that ID too. ITicketService owns archive/restore/soft-delete and queries. Schema version 3 already contains all required lifecycle fields and remains unchanged. Google Drive, export, ads, and desktop/tablet layout refinements remain deferred. [Scanner platform limitations](scanner.md) still apply.

## Project boundaries

App → Core and Persistence; Persistence → Core; Core → .NET only. The composition root is `ParkingHelper.App/MauiProgram.cs`. No new architectural framework was introduced. ZXing.Net.Maui.Controls supplies the scanner UI behind the app scanner abstraction.

### Core

- `Models/VehiclePlate.cs`: immutable plate record with permanent GUID `Id`, normalized `PlateNumber`, integer `SortOrder`, `CreatedUtc`, and `UpdatedUtc`. Editing never changes its identity.
- `Services/IPlateService.cs` and `PlateService.cs`: UI-facing operations for querying, setup detection, adding, editing, deleting, and moving plates. Uses the injected `TimeProvider` for UTC time.
- `Services/PlateNumberRules.cs`: trims outer whitespace, uppercases invariantly, rejects blank values, and limits normalized input to 64 UTF-16 code units. No country-format validation; internal spaces and punctuation are preserved. Overlong values are rejected rather than truncated.
- `Services/PlateOperationException.cs`: validation/conflict messages suitable for display without exposing SQL errors.
- `Services/IParkingRepository.cs`: local persistence contract, extended with update/delete/move plate operations. All UI plate writes go through `IPlateService`.
- `ParkingService.AddPlateAsync` delegates to the new plate service so the existing foundation entry point follows the same rules.
- `ITicketService`/`TicketService` validate accepted scans and delegate atomic duplicate checking/insertion to the repository. `ParkingTicket` retains the original format/value, raw data, capture timestamps, and plate snapshot. The same service now exposes active/archived queries and transactional archive/restore/soft-delete. The older `ParkingService` lifecycle APIs remain foundation code. Local operations do not depend on network or login.

### Persistence

`SqliteParkingRepository` owns parameterized SQL, SQLite error translation for plate conflicts, connection lifetime, and transactions. `Microsoft.Data.Sqlite` 10.0.12 supplies SQLite/native runtime dependencies. The app supplies the path `FileSystem.Current.AppDataDirectory/parking-helper.db3`; no platform API appears in Persistence.

Each operation uses a short-lived connection with foreign keys enabled. The singleton repository serializes local operations; synchronous disk work runs off the UI thread. Initialization is lazy, awaited, retryable after failure, and never resets a newer database.

### UI and navigation

- `ViewModels/PlateEditorViewModel.cs`: observable editor state, ordered rows, inline validation/errors, busy state, loading/retry, edit cancellation, and Continue eligibility. It depends on `IPlateService` and logging, with no SQL or MAUI types.
- `Views/PlateEditorView.xaml(.cs)`: reusable input/list editor. Code-behind handles focus, button events, and the native delete confirmation; persistence stays in the service/view model. The prompt is `Delete ABC123?` with `Cancel` and `Delete` actions.
- `Pages/StartupPage.xaml(.cs)`: starts with a loading view, checks saved plates, and offers retry on storage failure.
- `Pages/InitialSetupPage.xaml(.cs)`: “Add Your Plate Numbers”, explanation, editor/list, and Continue. Continue requires a successfully loaded list with at least one persisted plate; it rechecks storage before opening the shell. Hardware back cannot bypass setup.
- `Pages/ManagePlatesPage.xaml(.cs)`: the same editor in Settings, including edit, confirmed deletion, and explicit Up/Down controls. Deleting the last plate immediately replaces the shell with setup.
- `Pages/SettingsPage.xaml(.cs)`: opens Manage Plate Numbers on the current Shell navigation stack.
- `Services/AppNavigation.cs`: replaces the root of the originating `Window` with setup or a fresh shell. Setup sits outside the shell/back stack, so tabs cannot bypass it. Page resolution is confined to UI composition/navigation; pages and view models are transient, local service/repository/clock are singletons.
- `App.xaml.cs`, `AppShell.xaml(.cs)`, and `MauiProgram.cs`: wire startup and page dependencies using DI. Primary destinations remain Scan / Tickets / Settings. Tickets opens Active/Archived lists; Scan remains the primary camera screen with saved plate buttons.
- `Resources/Styles/PlateStyles.xaml`, `Colors.xaml`, `Styles.xaml`: blue accents, light/dark colors, rounded plate cards, prominent wrapping plate text, and 48–52-point button targets. No third-party branding or proprietary artwork.

## Setup detection

There is no separate preferences flag that could drift out of sync with SQLite. Zero saved plates means setup is required. One or more saved plates means normal shell startup. A plate is persisted when Add succeeds, so closing the app after adding a plate but before tapping Continue opens the normal shell on next launch. Within setup, typing alone never enables Continue.

Deleting all plates returns to setup immediately and on subsequent launches. A failed database read displays retry instead of treating the database as empty. An ordinary edit or a reorder does not reset setup.

## Plate migration (SQLite schema version 2)

`PRAGMA user_version` advances from 1 to 2 in a transaction. Fresh databases create the original version 1 schema and run the same upgrade, keeping one migration path.

Changes to `VehiclePlates`:

- Add `SortOrder INTEGER NOT NULL DEFAULT 0 CHECK(SortOrder >= 0)`.
- Backfill dense zero-based positions in the old UI's alphabetical plate order, with ID as a deterministic tie-breaker. Preserve every existing ID and timestamp.
- Add `IX_VehiclePlates_SortOrder` on `(SortOrder, Id)`.
- Add insert/update-of-PlateNumber length triggers for a 64-character storage cap. Service validation is slightly stricter for supplementary Unicode characters because .NET counts UTF-16 code units. Existing overlong legacy values are not silently truncated; editing them requires a valid value.
- Retain the unique normalized plate-number index. Concurrent duplicate writes are rejected by SQLite and translated into a friendly service error.

The Save Ticket Flow subsequently upgrades ticket storage to schema version 3; see [migration details](save-ticket-flow.md). No seed/example records are added. Migration and regression tests verify preservation of existing plate/ticket associations. Future schema versions must add ordered, transactional migrations; databases newer than this app are rejected without erasing records.

## Ordering and timestamps

Reads always use `ORDER BY SortOrder, Id`. Adds append at `MAX(SortOrder) + 1` in the insertion statement. Move Up/Down swaps the requested plate with its current neighbor inside a transaction, then persists dense positions. First/last boundary moves are no-ops and their UI buttons are disabled. Deletes compact remaining positions in the same transaction. A later scanner can simply consume `IPlateService.GetPlatesAsync()` in its returned order.

Actual edits and changed positions advance `UpdatedUtc`; `CreatedUtc` never changes. No-op edits/moves preserve timestamps. Updates use the later of the current UTC clock or the prior update plus one tick, so a backwards device clock does not regress timestamps. Storage uses integer .NET ticks and restores `DateTimeKind.Utc` explicitly.

## Deletion and future synchronization

The UI calls `IPlateService.DeleteAsync(id)` after confirmation. The current repository physically deletes the local row and compacts ordering. A future implementation can replace that operation with tombstones without changing the UI contract. Do not expose SQLite delete calls to pages.

The ticket tables are populated by the scanner auto-save flow. Its foreign key still protects plates linked to existing/test tickets. Such a delete is rejected with an explanatory message; neither tickets nor associations are destroyed.

Synchronization is not implemented. Stable IDs and timestamps are foundations, not a sync protocol. Ticket deletion already retains tombstones. Future work must define plate tombstones, conflict handling, order merging, checkpoints, clock skew, and retention. Local operations must remain independent of authentication and network state.

## Platforms and verification

The same UI and explicit Up/Down controls are used on iPhone, iPad, Android phones/tablets, and Mac Catalyst. Native lifecycle code/manifests remain under `Platforms/`; the scanner milestone adds camera permissions and a small native torch-capability probe; camera and decode operations use ZXing. Up/Down is the deliberate fallback instead of relying on cross-platform drag reordering. macOS supports mouse/keyboard activation of the same controls.

Run automated SQLite and editor-state tests without a MAUI workload:

```sh
dotnet test ParkingHelper.Persistence.Tests/ParkingHelper.Persistence.Tests.csproj
```

`PersistenceTests.cs` retains milestone 1 regression tests. `PlateManagementTests.cs` tests normalization, limits, setup detection, edit identity, duplicates, ordering, deletion, migration, concurrent appends, and UTC monotonicity. `PlateEditorTests.cs` tests Continue gating, duplicate edit recovery/cancellation, move availability, and storage-error retries. The MAUI-independent view model source is linked into the test project to exercise it without a device.

On this machine the Mac Catalyst workload is missing. Preserve both installed targets during restore with the optional app-scoped `ParkingHelperBuildTargets` property:

```sh
dotnet restore ParkingHelper.App/ParkingHelper.App.csproj '-p:ParkingHelperBuildTargets="net10.0-android;net10.0-ios"'
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-android --no-restore '-p:AndroidSdkDirectory=/Volumes/Red SSD/Android/SDK'
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64 --no-restore
```

Keep both outer shell single quotes and inner double quotes in the target-list argument. A single-target restore replaces the shared assets file; it must not be the last restore before returning to Rider. Do not override standard `TargetFrameworks` globally, which also affects library projects. With every platform workload installed, use a normal solution restore/build.
