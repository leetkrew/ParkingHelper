# Scanner foundation (milestone 3)

This document records the scanner-only milestone. The [Save Ticket Flow](save-ticket-flow.md) supersedes its interaction: plates are selectable before scanning, tickets auto-save, the format is hidden from the Scan page, and processing resumes after brief feedback. The package, format catalog, and platform findings below still apply.

Scan is the primary screen. It shows device-local date/time, a live preview with a visual targeting guide, detection status and exact format, Rescan/Cancel, camera selection, torch when available, and saved plates in SortOrder. The guide is visual, not a crop restriction. Plates remain disabled because ticket saving is intentionally deferred. No ticket writes or barcode generation occur.

## Packages

- App: **ZXing.Net.Maui.Controls 0.10.4** (new direct reference).
- Transitive: **ZXing.Net.Maui 0.10.4**, **ZXing.Net 0.16.11**, and AndroidX CameraX dependencies. Existing direct package versions are unchanged.
- Tests: ZXing.Net.Maui 0.10.4, to verify the installed enum rather than a duplicate definition.

Verified against package repository commit `8add6b40723f68f0226c1377ab9c5450d726f977`, recorded in the installed NuGet manifest. [Upstream source](https://github.com/Redth/ZXing.Net.Maui/tree/8add6b40723f68f0226c1377ab9c5450d726f977).

## Formats and Auto

The installed `BarcodeFormat` enum has 21 values:

| Display format | Preserved identifier |
| --- | --- |
| Aztec | `Aztec` |
| Codabar | `Codabar` |
| Code 39 | `Code39` |
| Code 93 | `Code93` |
| Code 128 | `Code128` |
| Data Matrix | `DataMatrix` |
| EAN-8 | `Ean8` |
| EAN-13 | `Ean13` |
| ITF | `Itf` |
| MaxiCode | `MaxiCode` |
| PDF417 | `Pdf417` |
| QR Code | `QrCode` |
| RSS 14 | `Rss14` |
| RSS Expanded | `RssExpanded` |
| UPC-A | `UpcA` |
| UPC-E | `UpcE` |
| UPC/EAN extension | `UpcEanExtension` |
| MSI | `Msi` |
| Plessey | `Plessey` |
| Intelligent Mail | `Imb` |
| Pharmacode | `PharmaCode` |

`ScannerFormatCatalog` enumerates `Enum.GetValues<BarcodeFormat>()`. Auto bitwise-ORs every value and supplies that mask to `BarcodeReaderOptions.Formats`. It deliberately does **not** use `BarcodeFormats.All`: that library constant excludes Pharmacode. Specific settings resolve to the exact enum value. The result boundary also rejects a different symbology, so an underlying reader fallback cannot bypass the restriction. Unknown stored settings fall back to Auto. The picker uses the same catalog, with presentation-only labels.

UPC/EAN extension is not a standalone barcode. Inclusion in the enum does not guarantee that every format is decodable by the bundled underlying reader on every image. Specialist formats require hardware/sample validation. Upstream excludes Pharmacode from its aggregate because of false positives; this application includes it to meet the all-formats requirement. Restricting the format can improve accuracy.

## Result and concurrency

`Core/Models/ScanResult` carries the unmodified decoded `Value`, exact enum name `Format`, optional copied `RawBytes`, and `DetectedUtc` as UTC `DateTimeOffset`. Raw bytes come from `BarcodeResult.Raw` (upstream maps it from ZXing `RawBytes`); null stays null, and bytes are never reconstructed from text. Byte arrays are copied on input and output. Format is library-independent text such as `Pdf417`; it is never derived from the selected setting or replaced with QR Code.

The singleton `ScanSession` holds one result in memory. A lock plus a generation gate accepts only one concurrent result and rejects queued frames after cancellation, rescan, camera changes, or page lifecycle transitions. Detection is paused on success and the torch turns off. The result survives tab/settings navigation and background/resume in the current process. Cancel clears and pauses; Rescan clears and starts a new generation. Process termination discards it. Nothing calls a ticket repository.

## Services, settings, and UI

The existing DI and observable-view-model/code-behind pattern is retained. `IBarcodeScannerService` is the MAUI boundary, exposing an ordinary MAUI `View` and application models. `BarcodeScannerService` owns ZXing controls, events, selection, permissions, and lifecycle. Pages and Core do not reference ZXing. A separate transient scanner instance previews sources in Camera Settings with detection disabled. All instances share persistent settings and the in-memory session. `ScanViewModel` obtains plates only through `IPlateService`.

MAUI `IPreferences` stores `scanner.format` (empty = Auto), `scanner.camera` (empty = Automatic), and the camera permission request marker. Camera identifiers are opaque ZXing identifiers. Missing preferences are reset to Automatic, keeping the picker usable. They are not automatically restored if a device later reappears.

Permission is checked/requested before the control is created or cameras enumerated. The app waits for a loaded view/handler and retries initial enumeration while CameraX initializes. Camera denial/restriction shows a system-settings action and retry. Preview resources are disconnected when pages disappear or windows stop, then recreated on resume. A library support check prevents creating a preview when no camera is reported.

`GetAvailableCameras()` supplies every camera row, without front/rear filtering or device-name assumptions. `SelectedCamera` receives the actual enumerated object; Automatic clears it and defaults to rear, with a front-only fallback. The mobile Switch control cycles enumerated devices. Mac Catalyst labels the always-visible source selector **Video Source**.

While active, a two-second watchdog refreshes enumeration. A missing selected device falls back to Automatic. No devices, enumeration/selection failures, and a ten-second frame stall show a retryable error and release the preview. Fifteen seconds without a decoded result shows positioning/light/format guidance while scanning continues. ZXing has no public camera-error event in this version, so unavailable/in-use sources are diagnosed by frame stalls rather than an exact native error code.

`CameraCapabilities` is a small platform adapter because ZXing exposes `IsTorchOn` but no public torch-capability property. Android queries CameraX camera info; Apple inspects the actual AVCapture preview input. Unknown/unavailable capability hides the button. Torch control itself exclusively uses ZXing `IsTorchOn`, never a flashlight API.

## Platform findings

| Target | Enumeration and selection | Limitations |
| --- | --- | --- |
| Android phones/tablets | ZXing uses CameraX; IDs are facing-plus-index (`rear-0`, `front-0`, etc.). Automatic requests the rear/main camera. | IDs are positional and may change when camera topology changes. Upstream maps every non-front device to Rear, and selection searches back-facing cameras; external cameras may be listed but fail selection. The app reports unavailability rather than promising external support. |
| iOS/iPadOS | ZXing uses AVFoundation discovery, device `UniqueID`, and localized camera names. Automatic prefers rear multi-camera/wide-angle devices. | Hardware is required for real camera/decode tests. Discovery can include depth camera types unsuitable for barcode scanning. iPad multitasking availability is platform/device dependent. |
| Mac Catalyst | Same Apple adapter and opaque IDs; explicit Video Source selector; camera usage description and sandbox camera entitlement. | **0.10.4 discovery only requests built-in camera types. It does not request AVFoundation external/Continuity types**, contrary to the broader README wording. Every returned device is shown, but USB webcams, capture cards and Continuity sources may not be returned. No device-specific workarounds are added. Full external-source support needs an upstream library fix/version. Mac Catalyst workload is absent here, so this target could not be compiled or exercised locally. |

The source-of-truth implementation is [Apple discovery](https://github.com/Redth/ZXing.Net.Maui/blob/8add6b40723f68f0226c1377ab9c5450d726f977/ZXing.Net.MAUI/Apple/CameraManager.ios.maccatalyst.cs) and [Android CameraX adapter](https://github.com/Redth/ZXing.Net.Maui/blob/8add6b40723f68f0226c1377ab9c5450d726f977/ZXing.Net.MAUI/Platforms/Android/CameraManager.android.cs), rather than README claims. Native driver errors inside the library are not exposed as public error events; real disconnect/in-use stress testing remains necessary.

## Verification

- Android Debug build: succeeded with zero warnings/errors.
- iOS simulator arm64 Debug build: succeeded with zero warnings/errors.
- Automated tests: 30 passing (scanner and existing plate/persistence regressions).
- Scanner tests cover every installed enum value, exact format restrictions, settings persistence and invalid-format fallback, QR/PDF417/Aztec/Data Matrix preservation, UTC/raw-byte ownership, concurrent frame suppression, rescan, and lifecycle generation invalidation.
- Physical barcode, permission-dialog, torch, unplug/reconnect, and camera-in-use testing remains pending. No hardware support claim is inferred from compilation.

Commands (both installed app targets must remain in the shared restore assets):

```sh
dotnet restore ParkingHelper.App/ParkingHelper.App.csproj '-p:ParkingHelperBuildTargets="net10.0-android;net10.0-ios"'
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-android --no-restore '-p:AndroidSdkDirectory=/Volumes/Red SSD/Android/SDK' -m:1
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64 --no-restore -m:1
dotnet test ParkingHelper.Persistence.Tests/ParkingHelper.Persistence.Tests.csproj -m:1
```

On-device acceptance: grant/deny/revoke permission; scan QR/PDF417/Aztec/Data Matrix and available specialist samples; verify exact format/value, one result, Rescan/Cancel, no new ticket rows; restrict formats and relaunch; reorder plates then return to Scan; switch actual cameras; test torch presence; background/resume; unplug a selected source and reconnect; hold the camera in use in another app; verify persisted preferences and Automatic fallback. Repeat on Android, iPhone/iPad, and Mac Catalyst with available video sources.

## Files

Created:

- `ParkingHelper.Core/Models/ScanResult.cs`
- `ParkingHelper.Core/Services/ScanSession.cs`
- `ParkingHelper.App/Services/IBarcodeScannerService.cs`
- `ParkingHelper.App/Services/BarcodeScannerService.cs`
- `ParkingHelper.App/Services/ScannerSettingsService.cs`
- `ParkingHelper.App/Services/CameraCapabilities.cs`
- `ParkingHelper.App/ViewModels/ScanViewModel.cs`
- `ParkingHelper.App/Pages/ScannerSettingsPage.cs`
- `ParkingHelper.App/Pages/CameraSettingsPage.cs`
- `ParkingHelper.Persistence.Tests/ScannerTests.cs`
- `docs/scanner.md`

Changed:

- `ParkingHelper.App/ParkingHelper.App.csproj`, `MauiProgram.cs`
- `ParkingHelper.App/AppShell.xaml`, `AppShell.xaml.cs`
- `ParkingHelper.App/Pages/ScanPage.xaml`, `ScanPage.xaml.cs`
- `ParkingHelper.App/Pages/SettingsPage.xaml`, `SettingsPage.xaml.cs`
- Android `AndroidManifest.xml`, iOS `Info.plist`, Mac Catalyst `Info.plist` and `Entitlements.plist`
- `ParkingHelper.Persistence.Tests/ParkingHelper.Persistence.Tests.csproj`
- `README.md`, `docs/architecture.md`

Rider may also generate AndroidX library-index files under `.idea` during restore; these are not application implementation files.
