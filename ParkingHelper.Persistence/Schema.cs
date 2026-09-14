namespace ParkingHelper.Persistence;

internal static class Schema
{
    public const int Version = 6;

    public const string UpgradeToVersion6 = """
        ALTER TABLE SyncTombstones ADD COLUMN DeletedUtc INTEGER NULL;
        ALTER TABLE SyncTombstones ADD COLUMN CanonicalPlateId TEXT NULL;
        INSERT INTO SyncTombstones (Id, RecordType, UpdatedUtc, DeletedUtc)
            SELECT Id, 1, UpdatedUtc, COALESCE(DeletedUtc, UpdatedUtc) FROM ParkingTickets WHERE State = 2
            ON CONFLICT(Id, RecordType) DO UPDATE SET UpdatedUtc = MAX(UpdatedUtc, excluded.UpdatedUtc),
                DeletedUtc = COALESCE(DeletedUtc, excluded.DeletedUtc);
        DELETE FROM ParkingTickets WHERE State = 2;
        PRAGMA user_version = 6;
        """;

    // Sync identity is the GUID, not a display value independently entered on devices.
    // Local duplicate-entry guards remain active outside the atomic sync-apply transaction.
    // The context row is inserted and removed under BEGIN IMMEDIATE, so it is never
    // committed or visible to another writer; failures and process loss roll it back.
    public const string UpgradeToVersion5 = """
        CREATE TABLE IF NOT EXISTS SyncApplyContext (Id INTEGER NOT NULL PRIMARY KEY CHECK(Id = 1));
        DROP INDEX IF EXISTS IX_VehiclePlates_PlateNumber;
        CREATE INDEX IX_VehiclePlates_PlateNumber ON VehiclePlates(PlateNumber);
        DROP TRIGGER IF EXISTS TR_VehiclePlates_Duplicate_Insert;
        DROP TRIGGER IF EXISTS TR_VehiclePlates_Duplicate_Update;
        CREATE TRIGGER TR_VehiclePlates_Duplicate_Insert BEFORE INSERT ON VehiclePlates
        WHEN NOT EXISTS (SELECT 1 FROM SyncApplyContext) AND EXISTS (
            SELECT 1 FROM VehiclePlates p WHERE p.Id != NEW.Id AND p.PlateNumber = NEW.PlateNumber
            AND NOT EXISTS (SELECT 1 FROM SyncTombstones t WHERE t.RecordType = 0 AND t.Id = p.Id)
        ) BEGIN SELECT RAISE(ABORT, 'parkinghelper_duplicate_plate'); END;
        CREATE TRIGGER TR_VehiclePlates_Duplicate_Update BEFORE UPDATE OF PlateNumber ON VehiclePlates
        WHEN NOT EXISTS (SELECT 1 FROM SyncApplyContext) AND NEW.PlateNumber != OLD.PlateNumber AND EXISTS (
            SELECT 1 FROM VehiclePlates p WHERE p.Id != NEW.Id AND p.PlateNumber = NEW.PlateNumber
            AND NOT EXISTS (SELECT 1 FROM SyncTombstones t WHERE t.RecordType = 0 AND t.Id = p.Id)
        ) BEGIN SELECT RAISE(ABORT, 'parkinghelper_duplicate_plate'); END;
        DROP TRIGGER IF EXISTS TR_ParkingTickets_ActiveDuplicate_Insert;
        DROP TRIGGER IF EXISTS TR_ParkingTickets_ActiveDuplicate_Update;
        CREATE TRIGGER TR_ParkingTickets_ActiveDuplicate_Insert BEFORE INSERT ON ParkingTickets
        WHEN NOT EXISTS (SELECT 1 FROM SyncApplyContext) AND NEW.State = 0 AND EXISTS (
            SELECT 1 FROM ParkingTickets WHERE State = 0 AND Id != NEW.Id
            AND BarcodeFormat = NEW.BarcodeFormat COLLATE BINARY AND BarcodeValue = NEW.BarcodeValue COLLATE BINARY
        ) BEGIN SELECT RAISE(ABORT, 'Active ticket already exists'); END;
        CREATE TRIGGER TR_ParkingTickets_ActiveDuplicate_Update BEFORE UPDATE OF State, BarcodeFormat, BarcodeValue ON ParkingTickets
        WHEN NOT EXISTS (SELECT 1 FROM SyncApplyContext) AND NEW.State = 0 AND EXISTS (
            SELECT 1 FROM ParkingTickets WHERE State = 0 AND Id != NEW.Id
            AND BarcodeFormat = NEW.BarcodeFormat COLLATE BINARY AND BarcodeValue = NEW.BarcodeValue COLLATE BINARY
        ) BEGIN SELECT RAISE(ABORT, 'Active ticket already exists'); END;
        PRAGMA user_version = 5;
        """;

    public const string UpgradeToVersion4 = """
        ALTER TABLE ParkingTickets ADD COLUMN EntryUtc INTEGER NOT NULL DEFAULT 0;
        UPDATE ParkingTickets SET EntryUtc = CreatedUtc;
        PRAGMA user_version = 4;
        """;

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
        CREATE TABLE SyncTombstones (
            Id TEXT NOT NULL,
            RecordType INTEGER NOT NULL CHECK(RecordType IN (0, 1)),
            UpdatedUtc INTEGER NOT NULL,
            PRIMARY KEY (Id, RecordType)
        );
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
