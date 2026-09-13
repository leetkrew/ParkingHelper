# Ticket workflow changes

The scan page scans automatically and has no Rescan button. Retry Camera appears only after a camera error or unavailable/denied camera. Saved-ticket navigation retries and plate-loading retries have separate actions. Cancel pauses scanning; returning to the Scan page starts a new session.

Preview actions are Full Screen / Open Wallet / Details followed by Edit / Archive (Restore when archived) / Delete. Details is read-only and shows plate, barcode format/value, actual scan time, entry time, archive time when present, status and permanent ticket ID. Full screen uses Close and offers Move to Archive only for Active tickets, through the same view-model/service archive path as preview.

Edit loads existing plates through IPlateService in SortOrder and provides local-device date/time pickers. There is no free-text plate input. A single repository transaction validates and updates the selected plate and entry time. Active entry cannot exceed current UTC; archived entry cannot exceed ArchivedUtc. No-op saves preserve UpdatedUtc. Editing preserves identity, scan timestamp, barcode/raw data, status and archive timestamp; plate snapshots change only when plate ID changes. Nonexistent local times during daylight-saving transitions are rejected; unchanged local times retain the original UTC instant.

## Schema migration

Schema version 4 adds ParkingTickets.EntryUtc as non-null INTEGER UTC ticks and backfills every existing row from CreatedUtc, including archived/deleted records. Existing versions 1 and 2 still pass through the existing migrations, then version 4. All upgrades run in the existing initialization transaction; opening an upgraded database does not repeat the backfill. IDs, payloads, states and timestamps are retained.

CreatedUtc remains the stored actual scan timestamp and is exposed by the model as ScannedUtc. New scans receive the capture instant from ScanSession's UTC clock; EntryUtc defaults to that same instant. There is no second scan-time column or destructive timestamp rename. All duration calculations now use EntryUtc, ending at current UTC for Active tickets and ArchivedUtc for Archived tickets. User-facing dates are converted to device-local time.

## Files changed for this request

- ParkingHelper.Core/Models/ParkingTicket.cs
- ParkingHelper.Core/Models/TicketDuration.cs
- ParkingHelper.Core/Services/IParkingRepository.cs
- ParkingHelper.Core/Services/ITicketService.cs
- ParkingHelper.Core/Services/TicketService.cs
- ParkingHelper.Persistence/Schema.cs
- ParkingHelper.Persistence/SqliteParkingRepository.cs
- ParkingHelper.App/Services/IBarcodeScannerService.cs
- ParkingHelper.App/Services/BarcodeScannerService.cs
- ParkingHelper.App/ViewModels/ScanViewModel.cs
- ParkingHelper.App/ViewModels/TicketPreviewViewModel.cs
- ParkingHelper.App/Pages/ScanPage.xaml
- ParkingHelper.App/Pages/ScanPage.xaml.cs
- ParkingHelper.App/Pages/TicketPreviewPage.xaml
- ParkingHelper.App/Pages/TicketPreviewPage.xaml.cs
- ParkingHelper.App/Pages/FullScreenTicketPage.xaml
- ParkingHelper.App/Pages/FullScreenTicketPage.xaml.cs
- ParkingHelper.Persistence.Tests/PlateManagementTests.cs
- ParkingHelper.Persistence.Tests/TicketCreationTests.cs
- ParkingHelper.Persistence.Tests/TicketPlateEditingTests.cs
- ParkingHelper.Persistence.Tests/ScanSaveFlowTests.cs
- ParkingHelper.Persistence.Tests/TicketPreviewTests.cs
- docs/ticket-workflow-changes.md

Existing archive-export workspace changes are preserved.

## Validation

The regression suite passes all 134 tests, including entry-time boundary validation, atomic rejection, unchanged saves, duration, local editor conversion, immutable scan/payload/state preservation and migration from legacy schemas. The Mac Catalyst app build passes with zero warnings/errors, and the iOS simulator target also compiles. The combined all-target build is blocked on Android with XA5300 because the Android SDK is not installed in the default/local SDK locations. Physical camera and native date/time-picker interactions require device verification.
