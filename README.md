# Parking Helper

**Scan. Save. Show.**

An offline-first .NET 10 MAUI application for associating parking-ticket barcodes with vehicle plates, targeting iPhone, iPad, Android phones/tablets, and Mac Catalyst.

Milestone 5 adds Active/Archived ticket lists, live/frozen duration, archive/restore and confirmed soft-delete, original-symbology barcode rendering, and full-screen presentation. Saved plate snapshots and ticket IDs are retained. Android offers Open Wallet when Google Wallet is launchable; Apple platforms hide the unsupported generic shortcut. Scanner auto-save still opens Ticket Preview only after a committed save. See [Tickets](docs/tickets.md), [Save Ticket Flow](docs/save-ticket-flow.md), and [scanner platform limitations](docs/scanner.md). Synchronization, export, advertising, and desktop/tablet layout refinements remain deferred.

See [the architecture guide](docs/architecture.md) for the file map, schema, lifecycle rules, extension points, and build instructions.

Run the persistence tests without MAUI workloads:

```sh
dotnet test ParkingHelper.Persistence.Tests/ParkingHelper.Persistence.Tests.csproj
```
