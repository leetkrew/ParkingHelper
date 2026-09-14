# Milestone 10 — shared UI polish

## Phase status

- **10.1 Shared Design System:** refined blue palette, gradient hero styles, light/dark surface tokens, rounded cards, subtle shadows, shared heading/supporting/segment styles, and native tab colors.
- **10.2 Scan + Tickets:** Scan hero and white content card, softer plate selection outlines, Active/Archived segments, opaque rounded ticket rows, consistent swipe action colors. Existing layout policies, handlers and navigation are retained.
- **10.3 Ticket Preview + Full-Screen Ticket:** preview hero, consistent plate choices and actions, wrapping action labels with minimum rather than fixed height; presentation canvas and plate emphasis aligned. Barcode rendering and presentation layout logic are unchanged.
- **10.4 Settings + Manage Plate Numbers:** rounded settings groups, gradient headers, shared plate cards and colors; version moved below its label to avoid competing for horizontal space. Drive handlers and named controls retained.
- **10.5 Supporting Screens (partial):** initial setup, startup and archive export aligned; scanner/camera settings and Drive diagnostics inherit the refined shared cards. Full supporting-screen visual review remains pending.
- **10.6 Final Visual QA (partial):** automated checks and an iPhone initial-setup screenshot completed. Full device/interaction sign-off remains pending.

## Visual choices

Primary blue `#0866E6`, pale canvas `#F2F7FF`, navy ink `#132747`, gradient `#075CE0` to `#0875D6`. White hero text has at least 4.64:1 contrast against the gradient endpoints. Shared cards use 24-point corners, 0.5-point outlines and a subtle shadow. Swipe cards deliberately omit shadows so their existing clipped reveal geometry remains intact. Native bottom navigation, existing original icons, responsive breakpoints and accessible minimum hit areas remain in use.

No changes to persistence, sync, scanner state or ticket semantics. The follow-up below adjusts layout policy and native tab appearance only.

## Verification

- Regression suite: **PASS — 286 passed, 0 failed, 0 skipped** on final run.
- Android Debug: **PASS**, installed SDK `/Volumes/Red SSD/Android/SDK`.
- iOS Debug: **PASS**, `iossimulator-arm64` (not a signed device/archive build).
- Mac Catalyst Debug: **PASS**, `maccatalyst-arm64`.
- XAML parse and `git diff --check`: **PASS**.
- Existing responsive, navigation, swipe layout/action, plate, scanner, ticket and sync regression coverage passed. Swipe layout coverage includes 280-point rows (320-point viewport minus page padding).
- iPhone 16e / iOS 18.4: app installed and launched; initial setup visually inspected at 390-point width. Header, card, input, buttons and empty state fit without visible overlap. [Screenshot](qa/milestone-10-iphone-setup.png).
- **Not yet verified visually:** Scan, populated ticket lists, preview/presentation, Settings, iPad, Mac window resizing, large text, dark mode and 320-point screens. Native taps on segmented tabs/bottom navigation and live swipe gestures have not been exercised in this pass. Automated checks do not substitute for those checks.

Initial test runs exposed an assertion coupled to the old inline swipe surface color and a scheduling race in a 20 ms refresh-timeout test. The former now checks the shared opaque surface style; the latter observes the refreshing notification synchronously before verifying timeout and shared-sync preservation. No application logic was changed to accommodate tests.

Build warnings remain for existing AndroidX dependency constraints, an obsolete DisplayAlert call and Mac Catalyst keychain provisioning. Mac build success does not validate provisioned Keychain/Google Drive behavior.

## Commands

```sh
dotnet test ParkingHelper.Persistence.Tests/ParkingHelper.Persistence.Tests.csproj --no-restore -m:1
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-android --no-restore '-p:AndroidSdkDirectory=/Volumes/Red SSD/Android/SDK' -m:1
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64 --no-restore -m:1
dotnet build ParkingHelper.App/ParkingHelper.App.csproj -f net10.0-maccatalyst -p:RuntimeIdentifier=maccatalyst-arm64 --no-restore -m:1
```

## Files changed

- `ParkingHelper.App/Resources/Styles/Colors.xaml`
- `ParkingHelper.App/Resources/Styles/Styles.xaml`
- `ParkingHelper.App/Resources/Styles/PlateStyles.xaml`
- `ParkingHelper.App/Pages/ScanPage.xaml`
- `ParkingHelper.App/Pages/TicketsPage.xaml`
- `ParkingHelper.App/Pages/TicketPreviewPage.xaml`
- `ParkingHelper.App/Pages/FullScreenTicketPage.xaml`
- `ParkingHelper.App/Pages/SettingsPage.xaml`
- `ParkingHelper.App/Pages/ManagePlatesPage.xaml`
- `ParkingHelper.App/Pages/InitialSetupPage.xaml`
- `ParkingHelper.App/Pages/StartupPage.xaml`
- `ParkingHelper.App/Pages/ArchiveExportPage.xaml`
- `ParkingHelper.App/Views/PlateEditorView.xaml`
- `ParkingHelper.Persistence.Tests/SwipeLayoutTests.cs`
- `ParkingHelper.Persistence.Tests/TicketsLoadingTests.cs`
- `docs/milestone-10-ui-polish.md`
- `docs/qa/milestone-10-iphone-setup.png`


## Approved-mockup refinement (before advancing phases)

The previous inset hero and separate card treatment did not match the approved mockups. This follow-up refines the existing 10.1–10.4 work; it does not advance to another phase.

| Difference identified | Correction |
| --- | --- |
| Inset rounded hero, disconnected from content | Full-width page gradient and a white sheet overlapping it by 24 points; 28-point top corners |
| Nested Scan padding narrowed the camera | One 20-point content inset and a 24-point semibold heading |
| Fixed 340-point phone scanner pushed plates down | Width-based 0.8 aspect ratio: 280 high at 390 width, 224 at 320; bounded for short and wide windows |
| Closed scanner outline | Rounded corner guides; the same scanner-state stroke colors and tap overlay remain |
| Dispersed scanner feedback | Pale-blue rounded feedback panel with compact spacing |
| Separate Active/Archived buttons | Joined segmented surface, retaining existing click handlers and selected-state bindings |
| Plain native iPhone tab bar | Soft selected pill, no divider, explicit readable selected/unselected colors; native safe-area placement and reselection handling remain |

Applied the shared header/sheet relationship to Scan, Tickets, Ticket Preview, Full-Screen Ticket and Settings. Preview retains its native back action; full-screen presentation retains its tab-bar hiding and landscape grid. Existing plate wrapping, ticket collection virtualization, swipe clipping and tablet/desktop breakpoints remain.

### Follow-up verification

- Regression tests: **286 passed, 0 failed**; includes a new 320-point scanner-height assertion in the existing responsive test.
- Android: **PASS**. iOS (`iossimulator-arm64`): **PASS**. Mac Catalyst (`maccatalyst-arm64`): **PASS**. Existing warnings remain as documented above.
- Scan visually inspected on an isolated iPhone 16e / iOS 18.4 simulator with two sample plates. Header, white surface, camera, feedback, both selectors and selected-plate text fit above the native bottom bar without visible overlap.
- The simulator reports camera unavailable; live barcode capture was not exercised. Live swipes, native tab interaction, dark-mode appearance, all other updated screens and iPad/Mac visual QA remain pending. The screenshot is progress evidence, not final design approval.
- Follow-up screenshot: [Scan](qa/milestone-10-iphone-scan-refined.png).

Additional files changed in this refinement: `Layout/ResponsiveLayout.cs`, `Services/AppleRootShellRenderer.cs` under `ParkingHelper.App`, and `ParkingHelper.Persistence.Tests/ResponsiveLayoutTests.cs`, alongside the five pages and shared `Styles.xaml` listed above.
