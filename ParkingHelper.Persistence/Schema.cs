namespace ParkingHelper.Persistence;

internal static class Schema
{
    public const int Version = 3;

    public const string Create = """
        CREATE TABLE VehiclePlates (
            Id TEXT NOT NULL PRIMARY KEY,
            PlateNumber TEXT NOT NULL CHECK(length(trim(PlateNumber)) > 0),
            CreatedUtc INTEGER NOT NULL,
            UpdatedUtc INTEGER NOT NULL CHECK(UpdatedUtc >= CreatedUtc)
        );
        CREATE UNIQUE INDEX IX_VehiclePlates_PlateNumber ON VehiclePlates(PlateNumber);
        CREATE TABLE ParkingTickets (
            Id TEXT NOT NULL PRIMARY KEY,
            VehiclePlateId TEXT NOT NULL REFERENCES VehiclePlates(Id) ON DELETE RESTRICT,
            BarcodeFormat TEXT NOT NULL CHECK(length(trim(BarcodeFormat)) > 0),
            BarcodeValue TEXT NOT NULL CHECK(length(BarcodeValue) > 0),
            State INTEGER NOT NULL CHECK(State IN (0, 1, 2)),
            CreatedUtc INTEGER NOT NULL,
            UpdatedUtc INTEGER NOT NULL CHECK(UpdatedUtc >= CreatedUtc),
            ArchivedUtc INTEGER NULL CHECK(ArchivedUtc IS NULL OR ArchivedUtc BETWEEN CreatedUtc AND UpdatedUtc),
            DeletedUtc INTEGER NULL CHECK(DeletedUtc IS NULL OR DeletedUtc BETWEEN CreatedUtc AND UpdatedUtc),
            CHECK((State = 0 AND ArchivedUtc IS NULL AND DeletedUtc IS NULL)
               OR (State = 1 AND ArchivedUtc IS NOT NULL AND DeletedUtc IS NULL)
               OR (State = 2 AND DeletedUtc IS NOT NULL))
        );
        CREATE INDEX IX_ParkingTickets_State_CreatedUtc ON ParkingTickets(State, CreatedUtc DESC);
        CREATE INDEX IX_ParkingTickets_VehiclePlateId ON ParkingTickets(VehiclePlateId);
        PRAGMA user_version = 1;
        """;

    // Preserve the original schema so existing databases upgrade through the same path.
    // Existing rows retain the alphabetical order displayed by milestone 1.
    public const string UpgradeToVersion2 = """
        ALTER TABLE VehiclePlates ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0 CHECK(SortOrder >= 0);
        WITH Ordered AS (
            SELECT Id, ROW_NUMBER() OVER (ORDER BY PlateNumber, Id) - 1 AS Position FROM VehiclePlates
        )
        UPDATE VehiclePlates SET SortOrder = (SELECT Position FROM Ordered WHERE Ordered.Id = VehiclePlates.Id);
        CREATE INDEX IX_VehiclePlates_SortOrder ON VehiclePlates(SortOrder, Id);
        CREATE TRIGGER TR_VehiclePlates_Length_Insert BEFORE INSERT ON VehiclePlates
        WHEN length(NEW.PlateNumber) > 64 BEGIN SELECT RAISE(ABORT, 'Plate number is too long'); END;
        CREATE TRIGGER TR_VehiclePlates_Length_Update BEFORE UPDATE OF PlateNumber ON VehiclePlates
        WHEN length(NEW.PlateNumber) > 64 BEGIN SELECT RAISE(ABORT, 'Plate number is too long'); END;
        PRAGMA user_version = 2;
        """;
    // Keep any pre-milestone duplicates untouched. Triggers enforce uniqueness for future writes
    // without making migration destructive or failing on historical duplicate Active rows.
    public const string UpgradeToVersion3 = """
        ALTER TABLE ParkingTickets ADD COLUMN PlateNumberSnapshot TEXT NULL;
        ALTER TABLE ParkingTickets ADD COLUMN RawBarcodeData BLOB NULL;
        UPDATE ParkingTickets SET PlateNumberSnapshot =
            (SELECT PlateNumber FROM VehiclePlates WHERE Id = ParkingTickets.VehiclePlateId);
        CREATE INDEX IX_ParkingTickets_ActiveBarcode ON ParkingTickets(BarcodeFormat, BarcodeValue)
            WHERE State = 0;
        CREATE TRIGGER TR_ParkingTickets_ActiveDuplicate_Insert BEFORE INSERT ON ParkingTickets
        WHEN NEW.State = 0 AND EXISTS (
            SELECT 1 FROM ParkingTickets WHERE State = 0
            AND BarcodeFormat = NEW.BarcodeFormat COLLATE BINARY AND BarcodeValue = NEW.BarcodeValue COLLATE BINARY
        ) BEGIN SELECT RAISE(ABORT, 'Active ticket already exists'); END;
        CREATE TRIGGER TR_ParkingTickets_ActiveDuplicate_Update BEFORE UPDATE OF State, BarcodeFormat, BarcodeValue ON ParkingTickets
        WHEN NEW.State = 0 AND EXISTS (
            SELECT 1 FROM ParkingTickets WHERE State = 0 AND Id != NEW.Id
            AND BarcodeFormat = NEW.BarcodeFormat COLLATE BINARY AND BarcodeValue = NEW.BarcodeValue COLLATE BINARY
        ) BEGIN SELECT RAISE(ABORT, 'Active ticket already exists'); END;
        PRAGMA user_version = 3;
        """;
}
