# Milestone 5 — Tickets

## Behavior and storage

Tickets defaults to Active Tickets, with a separate Archived segment. Active records sort by `CreatedUtc DESC, Id`; archived records sort by `ArchivedUtc DESC, Id`. Returning from preview reloads the current list. Rows show the saved plate snapshot, local saved time, duration, and archived time where applicable. Barcode rendering is reserved for preview/presentation rather than repeating expensive previews in every row.

No schema migration or NuGet change is needed. Schema version **3** already has `State`, `ArchivedUtc`, `DeletedUtc`, `UpdatedUtc`, the plate snapshot, and raw barcode bytes.

`ITicketService` exposes active/archived queries, lookup by permanent ID, archive, restore, and delete. Mutations use the repository's transaction and update timestamps in UTC. A backwards device clock cannot regress `UpdatedUtc` (the existing monotonic timestamp rule applies). Repeating the current state is an idempotent no-op.

- Active → Archived: keep ID, CreatedUtc, snapshot, value, format, and bytes; set ArchivedUtc and UpdatedUtc.
- Archived → Active: keep the original ID/start; clear ArchivedUtc and advance UpdatedUtc. The transaction rejects restoration when another Active record has the same exact format/value. Both records remain unchanged on conflict.
- Active/Archived → Deleted: ask “Delete this ticket?” / “This ticket will be removed from Parking Helper.”, with Cancel/Delete. Persist a tombstone with DeletedUtc/UpdatedUtc; retain the row and barcode data. Normal lists and service preview lookup exclude Deleted. No restore-from-deleted or purge exists.

`TicketDuration` computes `CurrentUtc - CreatedUtc` for Active and `ArchivedUtc - CreatedUtc` for Archived, clamps negative results to zero, and includes days for long stays. Restoring resumes from the original CreatedUtc, including time spent archived. Duration is never written to SQLite. Pages tick once per second while displayed; timers and window callbacks stop/detach on disappearance and suspend on backgrounding.

## Preview and full screen

Both successful scanner saves and ticket-list selection call `AppNavigation.ShowTicketPreviewAsync` with only the permanent ID. The preview reloads through ITicketService, so edits to a plate do not rewrite previously saved displays. It shows plate, barcode, local saved time, duration, state, full-screen action, archive/restore, and a secondary destructive delete action. Storage failures preserve the displayed record and remain retryable.

Full screen is a separate ID-loaded page on the same navigation stack, with large plate/barcode/duration and Done. Back/Done pops to the actual caller. It has no ticket mutations. `IScreenAwakeService` scopes MAUI's keep-screen-on capability to a disposable lease: restore the previous setting on disappearance/background, reacquire on resume. Unsupported screen-awake behavior is tolerated.

The scanner's existing successful-commit → stop camera → beep/haptic → preview flow remains intact. No ticket is created when navigating from Tickets or Full Screen. Returning from preview to Scan clears the previous capture and retains plate selection. Duplicate/invalid/failed saves still have no success feedback or preview navigation.

## Edit ticket plate number

Ticket Preview offers Edit plate number for Active and Archived tickets. The editor loads the current saved plates through `IPlateService` in saved SortOrder, preselects by permanent PlateId (even when the plate text was edited), and displays large selectable buttons with a blue/checkmarked selection. There is no text input or plate-creation action. Selecting a button only changes editor state; Cancel discards it and Save explicitly confirms it. Saving the existing selection simply closes the editor.

The reusable `ITicketService.ChangeTicketPlateAsync(ticketId, plateId)` uses the existing repository transaction. It verifies that both the non-deleted ticket and selected saved plate exist, reads the latest saved plate text, and updates only VehiclePlateId, PlateNumberSnapshot, and UpdatedUtc. ID, CreatedUtc, barcode value/format/raw data, state, and ArchivedUtc remain intact. Same-plate service calls perform no UPDATE and preserve the historical snapshot and UpdatedUtc. Failed writes retain the original display and keep the editor retryable. No schema or package changes are needed.

`TicketPlateEditingTests` covers Active/Archived preservation, saved order, ID-based preselection, explicit confirmation/cancellation, no-op selection, unavailable plates, deleted tickets, and failed database updates.

## Barcode rendering

`IBarcodeRenderingService` isolates ZXing from the display controls. It resolves the exact stored ZXing.Net.MAUI enum name, checks the installed `MultiFormatWriter.SupportedWriters`, and encodes the saved value with that format. It never falls back to QR or mutates persisted data. `TicketBarcodeView` draws the resulting black modules on white with quiet space, pixel-aligned modules, and no antialiasing; 1D bars receive presentation height, while 2D modules retain aspect ratio.

Installed versions remain ZXing.Net.Maui.Controls / ZXing.Net.Maui **0.10.4**, ZXing.Net **0.16.11**. The latter exposes these 15 writers: EAN-8, UPC-E, EAN-13, UPC-A, QR Code, Code 39, Code 93, Code 128, ITF, PDF417, Codabar, MSI, Plessey, Data Matrix, Aztec.

The six remaining scanner enum formats have no writer: MaxiCode, RSS-14, RSS Expanded, UPC/EAN extension, Intelligent Mail, Pharmacode. These and invalid/unencodable content show a readable fallback alongside plate/metadata. Original symbology/value/raw data remain stored. Raw scan bytes may represent codewords rather than text payload, so rendering uses the saved decoded value; this is not a promise to recreate the original mask, error-correction level, ECI/GS1 metadata, or byte-for-byte symbol. Complex payloads and very dense symbols require validation with the parking operator's reader.

## Wallet platforms

`IWalletLauncherService` is a convenience launcher only. No payment or pass is created, and no payment data is received.

- **Android:** check the installed Google Wallet package `com.google.android.apps.walletnfcrel` using the public PackageManager launch intent. A narrow manifest `<queries>` declaration makes this package visible. Hide when absent/unlaunchable; catch removal/launch failures and show feedback. Respect the platform's normal app launch behavior.
- **iPhone/iPad:** hide the generic shortcut. Apple's documented `PKPass.passURL` opens a specific pass; Parking Helper has no such pass, and no private/undocumented generic Wallet scheme is used.
- **Mac Catalyst:** hide Wallet. The shared preview/full-screen remains usable; no desktop-specific layout work was added.

Official references: [Android PackageManager](https://developer.android.com/reference/android/content/pm/PackageManager), [package visibility](https://developer.android.com/training/package-visibility/declaring), [Google Wallet package](https://play.google.com/store/apps/details?id=com.google.android.apps.walletnfcrel), [Apple passURL](https://developer.apple.com/documentation/passkit/pkpass/passurl), [Apple URL schemes](https://developer.apple.com/library/archive/featuredarticles/iPhoneURLScheme_Reference/Introduction/Introduction.html).

## Files

Added:

- Core: `Models/TicketDuration.cs`.
- App services: `BarcodeRenderingService.cs`, `WalletLauncherService.cs`, `ScreenAwakeService.cs` (interfaces and implementations).
- App UI: `ViewModels/TicketsViewModel.cs`, `Views/TicketBarcodeView.cs`, `Pages/FullScreenTicketPage.xaml` and `.xaml.cs`.
- Tests: `TicketManagementTests.cs`, `BarcodeRenderingTests.cs`.
- Documentation: `docs/tickets.md`.

Changed:

- Core: `Services/ITicketService.cs`, `Services/TicketService.cs`.
- Persistence: `SqliteParkingRepository.cs` (archived ordering and atomic restore-conflict validation).
- App: `AppShell.xaml`/`.xaml.cs`, `MauiProgram.cs`, `Services/AppNavigation.cs`, `Platforms/Android/AndroidManifest.xml`.
- App UI: Tickets and TicketPreview XAML/code-behind, `ViewModels/TicketPreviewViewModel.cs`.
- Tests: project links plus `ScanSaveFlowTests.cs`/`TicketPreviewTests.cs` service doubles.
- Documentation: `README.md`, `docs/architecture.md`.

The existing uncommitted Milestone 4 audio/callback cleanup changes were retained.

## Verification

The solution builds for the installed Android and iOS targets with no warnings/errors. **84 automated tests passed**, covering active/archived ordering, duration ticking/freeze/restore, snapshot and payload preservation, tombstone exclusion, restore conflicts, failed writes, original-format barcode round trips, decoder-only fallback, and the prior scanner/save/feedback regressions.

An isolated iPhone 17 Pro / iOS 26.5 XCTest smoke test passed Tickets → Preview → Full Screen → Done, archive, restore, cancellation/confirmation of deletion, disappearance from both lists, and return to Scan. Screenshots were visually inspected, and ZXing decoded the final full-screen screenshot back to the fixture’s original QR value/symbology. SQLite inspection confirmed the same ID remained as a Deleted tombstone. The fixture was confined to a newly created simulator, which was removed after verification. Direct post-scan entry is covered by the existing committed-save/ID-loaded-preview tests and unchanged navigation path; a physical camera scan is not simulated.

Build command for the available workloads:

```sh
dotnet build ParkingHelper.slnx '-p:ParkingHelperBuildTargets="net10.0-android;net10.0-ios"' '-p:AndroidSdkDirectory=/Volumes/Red SSD/Android/SDK' --no-restore -m:1
dotnet test ParkingHelper.Persistence.Tests/ParkingHelper.Persistence.Tests.csproj --no-restore -m:1
```

Mac Catalyst cannot be built here because its workload is absent. Native Wallet launch, physical reader acceptance, screen-awake behavior, and camera/audio/haptics still need physical-device checks. No Google Drive, ads, or desktop/tablet layout refinements were added.

## Archived export

Only the Archived Tickets section exposes Export Archives. `IArchiveExportService` reads current Archived records through `ITicketService`, then applies Selected, inclusive local-date-range, or All filtering. Active records and deleted tombstones cannot enter an export. PlateNumber uses `PlateNumberSnapshot`; Duration is computed as `ArchivedUtc - CreatedUtc` and is never persisted.

CSV is UTF-8 with a header, ISO 8601 UTC timestamps, invariant formatting, and quoting for commas, quotes, and line breaks. JSON is UTF-8 with `exportedUtc`, `ticketCount`, and a `tickets` array containing stable string barcode formats. The app writes bytes to its cache with the intended filename and invokes the native MAUI share sheet, removing the temporary file afterward. Cancelling the native sheet is a no-op.
