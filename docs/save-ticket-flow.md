# Save Ticket Flow (milestone 4)

Scan automatically saves an Active ticket to the selected vehicle. There is no Save button and no post-scan plate tap. After a new ticket is committed, the camera is released immediately, the bundled success beep and strong haptic are triggered where supported, and navigation opens Ticket Preview using only the permanent ticket ID. No Active/Archived management UI, archive operations, barcode display, sync, export, ads, or Wallet features are introduced. No NuGet versions change.

## Plate selection

`ScanViewModel` loads through `IPlateService`, orders by saved SortOrder (then ID), and resolves `ISelectedPlatePreference.SelectedPlateId`. `SelectedPlatePreference` stores the permanent GUID in MAUI Preferences under `scanner.lastSelectedPlateId`, never the number text. Every tap immediately changes the selected row and writes its ID. The selected button is solid blue, bold, and marked with a check; its accessibility description also says selected. Buttons remain enabled before scanning, during saving, and during confirmation.

If there is no valid saved preference, or the ID has been deleted, the first saved plate is selected and the replacement preference is persisted. Editing a number preserves selection by ID. Returning from plate management or resuming reloads the list and resolves selection again. No plates returns the page to initial setup; a plate-read failure keeps detection disabled and offers retry. Preference-write failure is reported without losing the current in-memory choice.

## Accepted scan and automatic save

`BarcodeScannerService.Captured` fires once, on the UI thread, after the existing ScanSession accepts a decoded result and ZXing detection is paused. Generic status notifications do not trigger saves. `ScanViewModel.SaveScanAsync` snapshots `SelectedPlate` before its first await and calls `ITicketService.CreateActiveTicketAsync`. Subsequent taps change the next scan's selection without changing the accepted ticket.

`TicketService` validates the scan, selected ID and capture timestamp, builds a permanent GUID ticket, and delegates to the existing `IParkingRepository`. The selected ID is checked against the database inside the insertion transaction. Values and formats are not trimmed, re-encoded or converted. Null raw bytes remain null; available scanner bytes are persisted as a BLOB. The plate snapshot is the displayed selected number at acceptance, so later edits do not rewrite it.

The ticket fields are:

- Id: new permanent GUID.
- VehiclePlateId: selected plate ID at acceptance.
- PlateNumberSnapshot: copied selected number.
- BarcodeValue and BarcodeFormat: original scanner value and exact symbology identifier.
- RawBarcodeData: optional scanner bytes.
- State: Active.
- CreatedUtc and UpdatedUtc: both `ScanResult.DetectedUtc.UtcDateTime`.
- ArchivedUtc and DeletedUtc: null.

The Scan page displays no barcode format or raw payload. A committed success immediately opens Ticket Preview; it does not wait for an inline confirmation cooldown. Duplicate feedback is `Ticket already saved` with the **existing ticket's** plate snapshot/start time; it does not use the newly selected plate. A failed transaction displays `Ticket not saved`, never a success check/haptic.

## Duplicate identity and concurrency

A duplicate is an Active ticket with the same exact **BarcodeFormat + BarcodeValue**, comparison using SQLite BINARY collation. This is independent of plate, raw-byte availability, timestamps and ID. Case, whitespace and symbology differences are preserved; distinct formats with the same text remain distinct barcodes. Archived/deleted foundation records do not block a new Active scan.

`SqliteParkingRepository.CreateActiveTicketAsync` uses an immediate transaction to check for the existing Active ticket, validate plate existence, and insert only if absent. This serializes separate repository/connection instances as well as the app's singleton. It returns `TicketCreationResult(ticket, Created)`; a duplicate returns the existing record unchanged. Historical duplicates, if any, resolve deterministically to the earliest CreatedUtc then ID.

There are several cooperating protections:

1. Existing `ScanSession` lock/generation gate accepts only one frame and rejects stale frames across resets/lifecycle changes.
2. The scanner pauses detection before publishing `Captured`.
3. `ScanViewModel` rejects an in-flight second callback and repeat handling of the same `ScanResult` instance. The page keeps one save/feedback flow at a time.
4. The SQLite transaction performs the cross-plate Active duplicate check, with database triggers guarding other insert paths too.

After duplicate feedback, detection resumes automatically after 1.8 seconds; recoverable errors show for 3 seconds before scanning resumes. Rescan/Cancel controls are briefly disabled while the save/feedback flow runs, but plate selection remains available. A ticket left in view can cause later duplicate feedback after the pause, but cannot create extra records. Duplicate feedback does not haptically repeat success. Android declares the VIBRATE permission required by [MAUI haptic feedback](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/device/haptic-feedback?view=net-maui-10.0).

Navigation/background stops the camera and cancels only the pending feedback delay. A successfully committed ticket keeps a pending preview ID if the app backgrounds before it can open the preview; returning to Scan opens that preview before starting another camera session. An already accepted transaction finishes for its original plate. A new camera session waits for that save task to finish, reloads selection, clears consumed capture state, and then enables detection. This avoids replaying an old result against a changed selection. The initial camera session is also gated on plate loading.

## Timestamps

`ScanSession` uses the injected TimeProvider when a decoded result is successfully accepted, rather than the first undecoded video frame. That timestamp supplies both CreatedUtc and UpdatedUtc. SQLite persists UTC .NET ticks and restores DateTimeKind.Utc. Database queue/commit latency does not move the parking start time forward. The confirmation alone converts to local time. A failed attempt creates no record; a subsequent accepted scan has its own capture time. Duplicate reads do not change any timestamps.

## Schema version 3

The existing transactional migration chain remains v1 → v2 → v3. Version 3 adds:

- `ParkingTickets.PlateNumberSnapshot TEXT NULL`.
- `ParkingTickets.RawBarcodeData BLOB NULL`.
- Partial lookup index `IX_ParkingTickets_ActiveBarcode(BarcodeFormat, BarcodeValue) WHERE State = 0`.
- Insert/update triggers rejecting a new conflicting Active barcode. The update guard protects existing foundation write paths; no new archive/restore UI or operations were introduced.

Legacy snapshot fields are backfilled from the linked current plate number, since previous historical spelling cannot be recovered. Legacy raw bytes remain null. IDs, associations, barcode data, state and timestamps remain unchanged. Existing duplicate Active rows are retained rather than deleted or merged. The index is intentionally non-unique so such databases can migrate; triggers prevent future conflicting writes. Newer unsupported database versions continue to be rejected without erasing data.

## Verification

Android Debug and iOS simulator arm64 builds pass with zero warnings/errors. The 67-test suite includes all previous regressions and new cases for exact data/UTC persistence, QR/PDF417/Aztec/Data Matrix preservation, snapshot stability after editing, duplicate scans on another plate, simultaneous inserts through 16 repository instances, save rollback/recovery, direct-insert constraints, v1/v2 migration, historical duplicates, last-selected ID restoration, fallback after deletion, selection changes while a save is pending, repeated callbacks, and UI error/duplicate feedback.

Live camera/haptic and background-navigation timing checks on physical devices remain pending. Mac Catalyst could not be built locally because its workload is not installed; the scanner foundation's documented camera-library limitations remain unchanged.

```sh
dotnet test ParkingHelper.Persistence.Tests/ParkingHelper.Persistence.Tests.csproj --no-restore -m:1
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-android --no-restore '-p:AndroidSdkDirectory=/Volumes/Red SSD/Android/SDK' -m:1
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64 --no-restore -m:1
```

## Files added

- `ParkingHelper.Core/Models/TicketCreationResult.cs`
- `ParkingHelper.Core/Services/ITicketService.cs`
- `ParkingHelper.Core/Services/TicketService.cs`
- `ParkingHelper.Core/Services/TicketOperationException.cs`
- `ParkingHelper.Core/Services/ISelectedPlatePreference.cs`
- `ParkingHelper.App/Services/SelectedPlatePreference.cs`
- `ParkingHelper.Persistence.Tests/TicketCreationTests.cs`
- `ParkingHelper.Persistence.Tests/ScanSaveFlowTests.cs`
- `docs/save-ticket-flow.md`

## Files changed

- `ParkingHelper.Core/Models/ParkingTicket.cs`: snapshot/raw fields.
- `ParkingHelper.Core/Services/IParkingRepository.cs`: atomic creation contract.
- `ParkingHelper.Persistence/Schema.cs`, `SqliteParkingRepository.cs`: v3 migration, duplicate protection, snapshot/raw round trips.
- `ParkingHelper.App/ViewModels/ScanViewModel.cs`: plate selection, auto-save state, callback guard and feedback.
- `ParkingHelper.App/Pages/ScanPage.xaml`, `ScanPage.xaml.cs`: selectable plate buttons, format-free save feedback, lifecycle/feedback orchestration and haptic.
- `ParkingHelper.App/Services/IBarcodeScannerService.cs`, `BarcodeScannerService.cs`: once-per-capture event and user-facing status without symbology.
- `ParkingHelper.App/MauiProgram.cs`: DI registrations.
- `ParkingHelper.App/Platforms/Android/AndroidManifest.xml`: haptic permission.
- `ParkingHelper.Persistence.Tests/ParkingHelper.Persistence.Tests.csproj`: link the MAUI-independent preference adapter for tests.
- `ParkingHelper.Persistence.Tests/PlateManagementTests.cs`: complete the historical ticket schema fixture and assert v3 migration.
- `README.md`, `docs/architecture.md`, `docs/scanner.md`: current milestone links and scope.


## Ticket Preview follow-up

`AppNavigation.ShowTicketPreviewAsync(INavigation, Guid ticketId)` resolves the preview through DI and passes only the ID, using the app's existing `Navigation.PushAsync` pattern. `TicketPreviewPage`/`TicketPreviewViewModel` reload the persisted record via `ITicketService.GetTicketAsync`, which delegates to the repository. No full database object is carried between pages.

The preview shows the plate snapshot prominently, saved local date/time, live duration, a barcode-display placeholder, and Done/default Back navigation. Duration is recomputed from the saved UTC CreatedUtc and TimeProvider every second, including days, and clamped to zero if the device clock moves backwards. The timer stops when the page disappears or the window backgrounds, and catches up on resume. Loading, missing records, and read failures have explicit retryable states; a failed load does not show a stale ticket. No barcode rendering, Archive/Delete, Wallet or other ticket actions are added.

The successful-save branch calls scanner.Stop before navigating and never calls Rescan. A navigation failure preserves the saved ID and offers Open saved ticket rather than attempting another insert. Duplicate/validation/storage failures do not enter the preview branch. On Back/Done, Scan waits for the previous save flow, restores the selected plate, clears the consumed result with Cancel/Rescan, and starts a fresh camera session.

Added: `Pages/TicketPreviewPage.xaml(.cs)`, `ViewModels/TicketPreviewViewModel.cs`, and `ParkingHelper.Persistence.Tests/TicketPreviewTests.cs`. Changed: Scan page/ViewModel, AppNavigation, DI, ITicketService/TicketService, the test-project source links, and its service test double. No package or schema changes for this follow-up.

Preview tests cover ID-based reads from committed storage, snapshot preservation, live multi-day duration, backward-clock handling, no database reads on timer updates, read failures/retry, missing IDs, and stale read suppression. Android and iOS simulator builds pass. Device-level navigation/camera/haptic testing and Mac Catalyst validation remain pending.


## Milestone 4 completion: success feedback

The existing save and preview implementation was retained. The final native feedback work fixes the Apple audio-session overload, uses supported Android vibration APIs by OS version, and uses the view-attached iOS heavy-impact generator on iOS 17.5+. No new NuGet packages or schema changes were needed for feedback.

- `Resources/Raw/scan_success.wav` is an original bundled 0.565-second two-tone cue (mono, 16-bit PCM, 44.1 kHz), verified in both Android APK and iOS app outputs. Native player gain is 1.0; system volume, mute behavior and routing are not overridden.
- `ScanFeedbackSettings.ScanSoundEnabled` uses `scanner.soundEnabled`, defaults true, and can support a future settings toggle. Disabling sound does not disable haptics.
- Android uses MediaPlayer with AssistanceSonification audio attributes and a 200 ms strong vibration, subject to system haptic settings. Apple uses AVAudioPlayer with Ambient audio-session behavior and a heavy impact on supported iOS hardware. Mac Catalyst has no equivalent UIKit haptic implementation.
- `ScanPage` awaits `SaveScanAsync`. Only a newly committed result enters the branch that stops/releases the scanner, triggers feedback, and opens the preview by permanent ID. It does not await the end of the sound. Preview retries do not replay feedback or insert again.
- Silent preload does not play anything. Duplicate results, invalid scans and database failures produce no success sound or haptic. Sound and haptic exceptions are contained independently and cannot turn a committed ticket into a failed save or prevent normal preview navigation.
- Audio error callbacks are detached before release/disposal. Camera frame/detection events are detached before teardown; native disconnection is attempted even if resetting a torch/control throws. Existing generation and in-flight save guards reject queued old callbacks. Returning from preview clears the previous scan and retains the selected plate.

Validation: the complete solution builds for the installed Android and iOS simulator workloads with zero warnings/errors. All 67 tests pass. Feedback integration tests use real SQLite saves and verify that a separate connection sees the committed ticket when playback begins; duplicate, validation-failed and forced database-failed saves remain silent. Existing tests cover duplicate races, queued scan generations, selection snapshots, preview loading and duration. The success-only stop → feedback → preview ordering and callback unsubscription were also checked in the page/adapter source.

Build command:

```sh
dotnet build ParkingHelper.slnx '-p:ParkingHelperBuildTargets="net10.0-android;net10.0-ios"' '-p:AndroidSdkDirectory=/Volumes/Red SSD/Android/SDK' --no-restore -m:1
```

Implementation and available automated checks are complete. Physical-device audibility, haptic strength, and native camera/navigation smoke tests remain pending; no simulator/build claim substitutes for these. The Mac Catalyst workload is absent, so that target was not built. Platform mute/volume behavior can intentionally make the beep inaudible.
