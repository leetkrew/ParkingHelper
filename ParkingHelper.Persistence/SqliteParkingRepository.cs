using System.Text.Json;
using Microsoft.Data.Sqlite;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.Persistence;

/// <summary>Register once per app. Disk I/O runs off the UI thread using short-lived connections.</summary>
public sealed class SqliteParkingRepository : IParkingRepository
{
    private readonly string databasePath;
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    public GoogleDriveSyncDiagnostics? Diagnostics { get; init; }

    public SqliteParkingRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public Task InitializeAsync() => ExecuteAsync(_ => true);

    public Task AddPlateAsync(VehiclePlate plate) => ExecuteAsync(connection =>
    {
        ArgumentNullException.ThrowIfNull(plate);
        RequireId(plate.Id);
        var number = PlateNumberRules.Normalize(plate.PlateNumber);
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var tombstone = Command(connection, "DELETE FROM SyncTombstones WHERE Id = $id AND RecordType = 0;", ("$id", plate.Id.ToString())))
        {
            tombstone.Transaction = transaction;
            tombstone.ExecuteNonQuery();
        }
        using var command = Command(connection, """
            INSERT INTO VehiclePlates (Id, PlateNumber, CreatedUtc, UpdatedUtc, SortOrder)
            SELECT $id, $number, $created, $updated, COALESCE(MAX(SortOrder), -1) + 1 FROM VehiclePlates;
            """, ("$id", plate.Id.ToString()), ("$number", number),
            ("$created", UtcTicks(plate.CreatedUtc)), ("$updated", UtcTicks(plate.UpdatedUtc)));
        command.Transaction = transaction;
        try { var result = command.ExecuteNonQuery(); transaction.Commit(); return result; }
        catch (SqliteException error) when (IsDuplicatePlate(error))
        {
            transaction.Rollback();
            throw new PlateOperationException("That plate number is already saved.");
        }
    });

    public Task<IReadOnlyList<VehiclePlate>> GetPlatesAsync() =>
        ExecuteAsync<IReadOnlyList<VehiclePlate>>(connection => ReadPlates(connection));

    public Task UpdatePlateAsync(Guid id, string plateNumber, DateTime utcNow) => ExecuteAsync(connection =>
    {
        RequireId(id);
        var number = PlateNumberRules.Normalize(plateNumber);
        using var transaction = connection.BeginTransaction();
        using (var tombstone = Command(connection, "DELETE FROM SyncTombstones WHERE Id = $id AND RecordType = 0;", ("$id", id.ToString())))
        {
            tombstone.Transaction = transaction;
            tombstone.ExecuteNonQuery();
        }
        var plate = ReadPlates(connection, transaction).FirstOrDefault(p => p.Id == id)
            ?? throw new PlateOperationException("This plate no longer exists. Refresh the list and try again.");
        if (plate.PlateNumber != number)
        {
            using var command = Command(connection, """
                UPDATE VehiclePlates SET PlateNumber = $number, UpdatedUtc = MAX(UpdatedUtc + 1, $now) WHERE Id = $id;
                """, ("$number", number), ("$now", UtcTicks(utcNow)), ("$id", id.ToString()));
            command.Transaction = transaction;
            try { command.ExecuteNonQuery(); }
            catch (SqliteException error) when (IsDuplicatePlate(error))
            {
                throw new PlateOperationException("That plate number is already saved.");
            }
        }
        transaction.Commit();
        return true;
    });

    public Task DeletePlateAsync(Guid id, DateTime utcNow) => ExecuteAsync(connection =>
    {
        RequireId(id);
        var now = UtcTicks(utcNow);
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection, "DELETE FROM VehiclePlates WHERE Id = $id;", ("$id", id.ToString()));
        command.Transaction = transaction;
        int deleted;
        try { deleted = command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            // Only live Active/Archived tickets retain plate foreign keys.
            throw new PlateOperationException("This plate is linked to a saved ticket and cannot be deleted.");
        }
        if (deleted > 0)
        {
            using var tombstone = Command(connection, """
                INSERT INTO SyncTombstones (Id, RecordType, UpdatedUtc) VALUES ($id, 0, $updated)
                ON CONFLICT(Id, RecordType) DO UPDATE SET UpdatedUtc = MAX(UpdatedUtc, excluded.UpdatedUtc);
                """, ("$id", id.ToString()), ("$updated", now));
            tombstone.Transaction = transaction;
            tombstone.ExecuteNonQuery();
        }
        PersistOrder(connection, transaction, ReadPlates(connection, transaction), now);
        transaction.Commit();
        return true;
    });

    public Task MovePlateAsync(Guid id, int direction, DateTime utcNow) => ExecuteAsync(connection =>
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction));
        var now = UtcTicks(utcNow);
        using var transaction = connection.BeginTransaction();
        var plates = ReadPlates(connection, transaction);
        var index = plates.FindIndex(p => p.Id == id);
        if (index < 0)
            throw new PlateOperationException("This plate no longer exists. Refresh the list and try again.");
        var destination = index + direction;
        if (destination >= 0 && destination < plates.Count)
        {
            (plates[index], plates[destination]) = (plates[destination], plates[index]);
            PersistOrder(connection, transaction, plates, now);
        }
        transaction.Commit();
        return true;
    });

    private static List<VehiclePlate> ReadPlates(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection,
            "SELECT Id, PlateNumber, CreatedUtc, UpdatedUtc, SortOrder FROM VehiclePlates p WHERE NOT EXISTS (SELECT 1 FROM SyncTombstones t WHERE t.RecordType = 0 AND t.Id = p.Id) ORDER BY SortOrder, Id;");
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var plates = new List<VehiclePlate>();
        while (reader.Read())
            plates.Add(new VehiclePlate(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                ReadUtc(reader, 2), ReadUtc(reader, 3)) { SortOrder = reader.GetInt32(4) });
        return plates;
    }

    private static void PersistOrder(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<VehiclePlate> plates, long utcTicks)
    {
        for (var index = 0; index < plates.Count; index++)
        {
            if (plates[index].SortOrder == index)
                continue;
            using var command = Command(connection, """
                UPDATE VehiclePlates SET SortOrder = $order, UpdatedUtc = MAX(UpdatedUtc + 1, $now) WHERE Id = $id;
                """, ("$order", index), ("$now", utcTicks), ("$id", plates[index].Id.ToString()));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
    }

    public Task<TicketCreationResult> CreateActiveTicketAsync(ParkingTicket ticket) => ExecuteAsync(connection =>
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket.State != ParkingTicketState.Active)
            throw new TicketOperationException("Only an Active ticket can be created from a scan.");
        RequireId(ticket.Id);
        RequireId(ticket.VehiclePlateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.BarcodeFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.BarcodeValue);
        // BEGIN IMMEDIATE serializes the read/insert across separate repository instances, too.
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var duplicate = Command(connection, $"""
            {TicketSelect} WHERE State = 0 AND BarcodeFormat = $format COLLATE BINARY
            AND BarcodeValue = $value COLLATE BINARY ORDER BY CreatedUtc, Id LIMIT 1;
            """, ("$format", ticket.BarcodeFormat), ("$value", ticket.BarcodeValue)))
        {
            duplicate.Transaction = transaction;
            using var reader = duplicate.ExecuteReader();
            if (reader.Read())
            {
                var existing = ReadTicket(reader);
                reader.Close();
                transaction.Commit();
                return new TicketCreationResult(existing, false);
            }
        }
        using (var plate = Command(connection, "SELECT 1 FROM VehiclePlates WHERE Id = $id AND Id NOT IN (SELECT Id FROM SyncTombstones WHERE RecordType = 0);",
                   ("$id", ticket.VehiclePlateId.ToString())))
        {
            plate.Transaction = transaction;
            if (plate.ExecuteScalar() == null)
                throw new TicketOperationException("This plate is no longer available. Refresh your plates and scan again.");
        }
        InsertTicket(connection, ticket, transaction);
        transaction.Commit();
        return new TicketCreationResult(ticket, true);
    });

    public Task AddTicketAsync(ParkingTicket ticket) => ExecuteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        if (ticket.State == ParkingTicketState.Deleted) WriteTicketTombstone(connection, transaction, SyncRecord.ForTicket(ticket));
        else InsertTicket(connection, ticket, transaction);
        transaction.Commit();
        return true;
    });

    private static int InsertTicket(SqliteConnection connection, ParkingTicket ticket, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        RequireId(ticket.Id);
        RequireId(ticket.VehiclePlateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.BarcodeFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.BarcodeValue);
        using (var tombstone = Command(connection, "SELECT 1 FROM SyncTombstones WHERE RecordType = 1 AND Id = $id;", ("$id", ticket.Id.ToString())))
        {
            tombstone.Transaction = transaction;
            if (tombstone.ExecuteScalar() is not null) throw new TicketOperationException("This ticket has been deleted.");
        }
        using var command = Command(connection, """
            INSERT INTO ParkingTickets
            (Id, VehiclePlateId, BarcodeFormat, BarcodeValue, State, CreatedUtc, UpdatedUtc, ArchivedUtc, DeletedUtc,
             PlateNumberSnapshot, RawBarcodeData, EntryUtc)
            VALUES ($id, $plate, $format, $value, $state, $created, $updated, $archived, $deleted, $snapshot, $raw, $entry);
            """, ("$id", ticket.Id.ToString()), ("$plate", ticket.VehiclePlateId.ToString()),
            ("$format", ticket.BarcodeFormat), ("$value", ticket.BarcodeValue),
            ("$state", (int)ticket.State), ("$created", UtcTicks(ticket.CreatedUtc)),
            ("$updated", UtcTicks(ticket.UpdatedUtc)), ("$archived", NullableTicks(ticket.ArchivedUtc)),
            ("$deleted", NullableTicks(ticket.DeletedUtc)),
            ("$snapshot", (object?)ticket.PlateNumberSnapshot?.Trim().ToUpperInvariant() ?? DBNull.Value),
            ("$raw", (object?)ticket.RawBarcodeData ?? DBNull.Value), ("$entry", UtcTicks(ticket.EntryUtc)));
        command.Transaction = transaction;
        return command.ExecuteNonQuery();
    }

    public Task<ParkingTicket?> GetTicketAsync(Guid id) => ExecuteAsync(connection => FindTicket(connection, id));

    public Task<IReadOnlyList<ParkingTicket>> GetTicketsAsync(ParkingTicketState state)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        return ExecuteAsync<IReadOnlyList<ParkingTicket>>(connection =>
        {
            using var command = Command(connection,
                $"{TicketSelect} WHERE State = $state AND State != 2 ORDER BY {(state == ParkingTicketState.Archived ? "ArchivedUtc" : "CreatedUtc")} DESC, Id;", ("$state", (int)state));
            using var reader = command.ExecuteReader();
            var tickets = new List<ParkingTicket>();
            while (reader.Read())
                tickets.Add(ReadTicket(reader));
            return tickets;
        });
    }

    public Task<ParkingTicket> ChangeTicketStateAsync(Guid id, ParkingTicketState state, DateTime utcNow) =>
        ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var ticket = FindTicket(connection, id, transaction)
                ?? throw new KeyNotFoundException($"Ticket {id} does not exist.");
            if (state == ParkingTicketState.Active && ticket.State == ParkingTicketState.Archived)
            {
                using var duplicate = Command(connection, """
                    SELECT COUNT(*) FROM ParkingTickets WHERE State = 0 AND Id <> $id
                    AND BarcodeFormat = $format COLLATE BINARY AND BarcodeValue = $value COLLATE BINARY;
                    """, ("$id", id.ToString()), ("$format", ticket.BarcodeFormat), ("$value", ticket.BarcodeValue));
                duplicate.Transaction = transaction;
                if (Convert.ToInt64(duplicate.ExecuteScalar()) > 0)
                    throw new TicketOperationException("An active ticket already uses this barcode. Archive it before restoring this ticket.");
            }
            var changed = ticket.ChangeState(state, utcNow);
            if (changed.State == ParkingTicketState.Deleted)
                WriteTicketTombstone(connection, transaction, SyncRecord.ForTicket(changed));
            else if (changed != ticket)
            {
                using var command = Command(connection, """
                    UPDATE ParkingTickets SET State = $state, UpdatedUtc = $updated,
                        ArchivedUtc = $archived, DeletedUtc = $deleted WHERE Id = $id;
                    """, ("$state", (int)changed.State), ("$updated", UtcTicks(changed.UpdatedUtc)),
                    ("$archived", NullableTicks(changed.ArchivedUtc)),
                    ("$deleted", NullableTicks(changed.DeletedUtc)), ("$id", id.ToString()));
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return changed;
        });

    public Task<ParkingTicket> ChangeTicketPlateAsync(Guid ticketId, Guid plateId, DateTime utcNow) =>
        EditTicketCoreAsync(ticketId, plateId, null, utcNow);

    public Task<ParkingTicket> EditTicketAsync(Guid ticketId, Guid plateId, DateTime entryUtc, DateTime utcNow) =>
        EditTicketCoreAsync(ticketId, plateId, entryUtc, utcNow);

    private Task<ParkingTicket> EditTicketCoreAsync(Guid ticketId, Guid plateId, DateTime? entryUtc, DateTime utcNow) =>
        ExecuteAsync(connection =>
        {
            if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC timestamp is required.", nameof(utcNow));
            using var transaction = connection.BeginTransaction(deferred: false);
            var ticket = FindTicket(connection, ticketId, transaction)
                ?? throw new TicketOperationException("This ticket could not be found.");
            if (ticket.State == ParkingTicketState.Deleted)
                throw new TicketOperationException("This ticket has been deleted.");
            // Resolve the current plate text inside the write transaction, never trust a stale UI snapshot.
            using var plateCommand = Command(connection, "SELECT PlateNumber FROM VehiclePlates WHERE Id = $id AND Id NOT IN (SELECT Id FROM SyncTombstones WHERE RecordType = 0);", ("$id", plateId.ToString()));
            plateCommand.Transaction = transaction;
            var number = plateCommand.ExecuteScalar() as string
                ?? throw new TicketOperationException("This plate is no longer available. Choose another saved plate.");
            var entry = entryUtc ?? ticket.EntryUtc;
            if (entry.Kind != DateTimeKind.Utc) throw new TicketOperationException("A UTC entry time is required.");
            if (entryUtc != null && ticket.State == ParkingTicketState.Active && entry > utcNow)
                throw new TicketOperationException("Entry time cannot be in the future.");
            if (entryUtc != null && ticket.State == ParkingTicketState.Archived && entry > ticket.ArchivedUtc)
                throw new TicketOperationException("Entry time cannot be later than the archive time.");
            if (ticket.VehiclePlateId == plateId && ticket.EntryUtc == entry)
            {
                transaction.Commit();
                return ticket;
            }
            var changed = ticket with
            {
                VehiclePlateId = plateId,
                PlateNumberSnapshot = ticket.VehiclePlateId == plateId ? ticket.PlateNumberSnapshot : number,
                EntryUtc = entry,
                UpdatedUtc = utcNow > ticket.UpdatedUtc ? utcNow : ticket.UpdatedUtc.AddTicks(1)
            };
            using var update = Command(connection, """
                UPDATE ParkingTickets SET VehiclePlateId = $plate, PlateNumberSnapshot = $number,
                    UpdatedUtc = $updated, EntryUtc = $entry WHERE Id = $id;
                """, ("$plate", plateId.ToString()), ("$number", (object?)changed.PlateNumberSnapshot ?? DBNull.Value), ("$entry", UtcTicks(entry)),
                ("$updated", UtcTicks(changed.UpdatedUtc)), ("$id", ticketId.ToString()));
            update.Transaction = transaction;
            update.ExecuteNonQuery();
            transaction.Commit();
            return changed;
        });

    public Task<IReadOnlyList<SyncRecord>> GetSyncRecordsAsync() =>
        ExecuteAsync<IReadOnlyList<SyncRecord>>(connection => ReadSyncRecords(connection));

    private static List<SyncRecord> ReadSyncRecords(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var records = ReadPlates(connection, transaction).Select(SyncRecord.ForPlate).ToList();
        using (var tombstones = Command(connection, "SELECT Id, RecordType, UpdatedUtc, DeletedUtc, CanonicalPlateId FROM SyncTombstones ORDER BY RecordType, Id;"))
        {
            tombstones.Transaction = transaction;
            using var reader = tombstones.ExecuteReader();
            while (reader.Read()) records.Add(reader.GetInt32(1) == 0
                    ? SyncRecord.ForPlateTombstone(Guid.Parse(reader.GetString(0)), ReadUtc(reader, 2)) with
                        { CanonicalPlateId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)) }
                    : SyncRecord.ForTicketTombstone(Guid.Parse(reader.GetString(0)), reader.IsDBNull(3) ? ReadUtc(reader, 2) : ReadUtc(reader, 3), ReadUtc(reader, 2)));
        }
        using (var tickets = Command(connection, $"{TicketSelect} ORDER BY Id;"))
        {
            tickets.Transaction = transaction;
            using var reader = tickets.ExecuteReader();
            while (reader.Read()) records.Add(SyncRecord.ForTicket(ReadTicket(reader)));
        }
        return records;
    }

    public Task ApplySyncRecordsAsync(IReadOnlyList<SyncRecord> records) => ExecuteAsync(connection => ApplySyncRecords(connection, records));

    private bool ApplySyncRecords(SqliteConnection connection, IReadOnlyList<SyncRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        using var transaction = connection.BeginTransaction(deferred: false);
        var results = new Dictionary<SyncRecordIdentity, SyncApplyRecordResult>();
        var expected = new Dictionary<SyncRecordIdentity, SyncRecord>();
        SyncRecord? applying = null;
        try
        {
            var before = ReadSyncRecords(connection, transaction).ToDictionary(Identity);
            // Reconcile again under the write lock to include mutations made during download.
            // This also remaps tickets to canonical plate GUIDs consistently with cloud merge.
            records = SyncMergeEngine.Merge(before.Values, records);
            expected = records.ToDictionary(Identity);
            using (var context = Command(connection, "INSERT INTO SyncApplyContext (Id) VALUES (1);"))
            {
                context.Transaction = transaction;
                context.ExecuteNonQuery();
            }
            foreach (var record in records.OrderBy(record => record.RecordType))
            {
                applying = record;
                var key = Identity(record);
                var winner = expected[key];
                if (before.TryGetValue(key, out var local) && SameRecord(local, winner))
                {
                    results[key] = Result(record, SyncApplyResult.Skipped,
                        SameRecord(local, record) ? "AlreadyPersisted" : "LocalConflictWinnerRetained");
                    continue;
                }
                var reason = "GuidUpsert";
                if (winner.RecordType == SyncRecordType.VehiclePlate)
                {
                    if (!winner.IsDeleted && HasOtherPlateWithNumber(connection, transaction, winner.Plate!))
                        reason = "CanonicalPlateUpsert";
                    if (winner.IsDeleted) reason = "PlateTombstonePreserved";
                    WriteSyncedPlate(connection, transaction, winner);
                }
                else WriteSyncedTicket(connection, transaction, winner);
                results[key] = Result(record, before.ContainsKey(key) ? SyncApplyResult.Updated : SyncApplyResult.Inserted, reason);
            }
            using (var cleanup = Command(connection, """
                DELETE FROM VehiclePlates WHERE Id IN (SELECT Id FROM SyncTombstones WHERE RecordType = 0)
                    AND NOT EXISTS (SELECT 1 FROM ParkingTickets WHERE VehiclePlateId = VehiclePlates.Id);
                """))
            {
                cleanup.Transaction = transaction;
                cleanup.ExecuteNonQuery();
            }
            applying = null;
            var actual = ReadSyncRecords(connection, transaction).ToDictionary(Identity);
            var report = PersistenceReport("Verifying", results.Values.ToArray(), expected, actual);
            if (report.MissingIds.Count != 0 || report.MismatchedIds.Count != 0 || report.UnexpectedIds.Count != 0)
            {
                foreach (var key in report.MissingIds.Concat(report.MismatchedIds))
                    results[key] = Result(expected[key], SyncApplyResult.Rejected, "ReadBackIdentityOrStateMismatch");
                throw new SyncPersistenceException("ReadBackIdentityOrStateMismatch");
            }
            using (var context = Command(connection, "DELETE FROM SyncApplyContext WHERE Id = 1;"))
            {
                context.Transaction = transaction;
                context.ExecuteNonQuery();
            }
            transaction.Commit();
            Diagnostics?.Update(d => d with { Persistence = report with { Transaction = "Committed" } });
            return true;
        }
        catch (Exception error)
        {
            var reason = error is SyncPersistenceException persistence ? persistence.Reason
                : error is SqliteException sqlite && sqlite.SqliteErrorCode == 19 ? "SQLiteConstraintRejected"
                : error is SqliteException ? "SQLiteWriteFailure" : "InvalidSynchronizedState";
            if (applying is not null)
                results[Identity(applying)] = Result(applying, SyncApplyResult.Rejected, reason,
                    error is SqliteException sql ? sql.SqliteExtendedErrorCode : null);
            foreach (var record in records)
                results.TryAdd(Identity(record), Result(record, SyncApplyResult.Skipped, "TransactionAbortedBeforeRecord"));
            // Disposing/rolling back the transaction restores all earlier writes and the
            // duplicate-guard context. No partially applied envelope is committed.
            transaction.Rollback();
            if (Diagnostics is not null)
            {
                try
                {
                    var actual = ReadSyncRecords(connection).ToDictionary(Identity);
                    var report = PersistenceReport("RolledBack", results.Values.ToArray(), expected, actual) with { FailureReason = reason };
                    Diagnostics.Update(d => d with { Persistence = report });
                }
                catch { /* A diagnostic read failure cannot turn a failed apply into success. */ }
            }
            // Do not carry SQLite messages/parameters (possibly private payloads) into logs.
            throw new SyncPersistenceException(reason);
        }
    }

    private static SyncRecordIdentity Identity(SyncRecord record) => new(record.RecordType, record.Id);
    private static bool SameRecord(SyncRecord first, SyncRecord second) =>
        JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    private static SyncApplyRecordResult Result(SyncRecord record, SyncApplyResult result, string reason, int? code = null) =>
        new(record.RecordType, record.Id, record.Ticket?.VehiclePlateId, result, reason, code);
    private static SyncPersistenceReport PersistenceReport(string state, IReadOnlyList<SyncApplyRecordResult> results,
        Dictionary<SyncRecordIdentity, SyncRecord> expected, Dictionary<SyncRecordIdentity, SyncRecord> actual) => new(
            state, results, expected.Keys.ToArray(), actual.Keys.ToArray(),
            expected.Keys.Except(actual.Keys).ToArray(),
            expected.Keys.Intersect(actual.Keys).Where(key => !SameRecord(expected[key], actual[key])).ToArray(),
            actual.Keys.Except(expected.Keys).ToArray());

    private static bool HasOtherPlateWithNumber(SqliteConnection connection, SqliteTransaction transaction, VehiclePlate plate)
    {
        using var command = Command(connection, "SELECT 1 FROM VehiclePlates WHERE PlateNumber = $number AND Id != $id LIMIT 1;",
            ("$number", plate.PlateNumber), ("$id", plate.Id.ToString()));
        command.Transaction = transaction;
        return command.ExecuteScalar() is not null;
    }

    private static void WriteSyncedPlate(SqliteConnection connection, SqliteTransaction transaction, SyncRecord incoming)
    {
        if (incoming.IsDeleted)
        {
            // Keep a hidden parent row when tickets still reference this GUID. The
            // synchronized state and plate list expose only its tombstone, never a live duplicate.
            using var delete = Command(connection, """
                DELETE FROM VehiclePlates WHERE Id = $id AND NOT EXISTS
                    (SELECT 1 FROM ParkingTickets WHERE VehiclePlateId = $id);
                """, ("$id", incoming.Id.ToString()));
            delete.Transaction = transaction;
            delete.ExecuteNonQuery();
            using var save = Command(connection, """
                INSERT INTO SyncTombstones (Id, RecordType, UpdatedUtc, CanonicalPlateId) VALUES ($id, 0, $updated, $canonical)
                ON CONFLICT(Id, RecordType) DO UPDATE SET UpdatedUtc = excluded.UpdatedUtc, CanonicalPlateId = excluded.CanonicalPlateId;
                """, ("$id", incoming.Id.ToString()), ("$updated", UtcTicks(incoming.UpdatedUtc)), ("$canonical", incoming.CanonicalPlateId?.ToString() ?? (object)DBNull.Value));
            save.Transaction = transaction;
            if (save.ExecuteNonQuery() != 1) throw new SyncPersistenceException("TombstoneWriteIgnored");
            return;
        }
        var plate = incoming.Plate ?? throw new SyncPersistenceException("MissingPlatePayload");
        using (var remove = Command(connection, "DELETE FROM SyncTombstones WHERE Id = $id AND RecordType = 0;", ("$id", plate.Id.ToString())))
        {
            remove.Transaction = transaction;
            remove.ExecuteNonQuery();
        }
        using var upsert = Command(connection, """
            INSERT INTO VehiclePlates (Id, PlateNumber, CreatedUtc, UpdatedUtc, SortOrder)
            VALUES ($id, $number, $created, $updated, $sort)
            ON CONFLICT(Id) DO UPDATE SET PlateNumber = excluded.PlateNumber,
                CreatedUtc = excluded.CreatedUtc, UpdatedUtc = excluded.UpdatedUtc, SortOrder = excluded.SortOrder;
            """, ("$id", plate.Id.ToString()), ("$number", plate.PlateNumber),
            ("$created", UtcTicks(plate.CreatedUtc)), ("$updated", UtcTicks(plate.UpdatedUtc)), ("$sort", plate.SortOrder));
        upsert.Transaction = transaction;
        if (upsert.ExecuteNonQuery() != 1) throw new SyncPersistenceException("PlateWriteIgnored");
    }

    private static void WriteSyncedTicket(SqliteConnection connection, SqliteTransaction transaction, SyncRecord incoming)
    {
        if (incoming.IsDeleted)
        {
            WriteTicketTombstone(connection, transaction, incoming);
            return;
        }
        var ticket = incoming.Ticket ?? throw new SyncPersistenceException("MissingTicketPayload");
        if (ReadPlateRecord(connection, transaction, ticket.VehiclePlateId) is null)
        {
            var tombstone = ReadTombstoneRecord(connection, transaction, ticket.VehiclePlateId, SyncRecordType.VehiclePlate)
                ?? throw new SyncPersistenceException("MissingReferencedPlateGuid");
            // A tombstone has no plate payload. Supply only the relational anchor using
            // that exact synchronized GUID; it stays hidden and is never exported as live.
            using var parent = Command(connection, """
                INSERT INTO VehiclePlates (Id, PlateNumber, CreatedUtc, UpdatedUtc, SortOrder)
                VALUES ($id, 'DELETED PLATE', $created, $updated, 0);
                """, ("$id", ticket.VehiclePlateId.ToString()),
                ("$created", Math.Min(UtcTicks(ticket.CreatedUtc), UtcTicks(tombstone.UpdatedUtc))),
                ("$updated", UtcTicks(tombstone.UpdatedUtc)));
            parent.Transaction = transaction;
            if (parent.ExecuteNonQuery() != 1) throw new SyncPersistenceException("TombstoneParentWriteIgnored");
        }
        using var upsert = Command(connection, """
            INSERT INTO ParkingTickets
            (Id, VehiclePlateId, BarcodeFormat, BarcodeValue, State, CreatedUtc, UpdatedUtc, ArchivedUtc, DeletedUtc,
             PlateNumberSnapshot, RawBarcodeData, EntryUtc)
            VALUES ($id, $plate, $format, $value, $state, $created, $updated, $archived, $deleted, $snapshot, $raw, $entry)
            ON CONFLICT(Id) DO UPDATE SET VehiclePlateId = excluded.VehiclePlateId,
                BarcodeFormat = excluded.BarcodeFormat, BarcodeValue = excluded.BarcodeValue,
                State = excluded.State, CreatedUtc = excluded.CreatedUtc, UpdatedUtc = excluded.UpdatedUtc,
                ArchivedUtc = excluded.ArchivedUtc, DeletedUtc = excluded.DeletedUtc,
                PlateNumberSnapshot = excluded.PlateNumberSnapshot, RawBarcodeData = excluded.RawBarcodeData,
                EntryUtc = excluded.EntryUtc;
            """, ("$id", ticket.Id.ToString()), ("$plate", ticket.VehiclePlateId.ToString()),
            ("$format", ticket.BarcodeFormat), ("$value", ticket.BarcodeValue), ("$state", (int)ticket.State),
            ("$created", UtcTicks(ticket.CreatedUtc)), ("$updated", UtcTicks(ticket.UpdatedUtc)),
            ("$archived", NullableTicks(ticket.ArchivedUtc)), ("$deleted", NullableTicks(ticket.DeletedUtc)),
            ("$snapshot", (object?)ticket.PlateNumberSnapshot?.Trim().ToUpperInvariant() ?? DBNull.Value), ("$raw", (object?)ticket.RawBarcodeData ?? DBNull.Value),
            ("$entry", UtcTicks(ticket.EntryUtc)));
        upsert.Transaction = transaction;
        if (upsert.ExecuteNonQuery() != 1) throw new SyncPersistenceException("TicketWriteIgnored");
    }

    private static void WriteTicketTombstone(SqliteConnection connection, SqliteTransaction transaction, SyncRecord incoming)
    {
        using var remove = Command(connection, "DELETE FROM ParkingTickets WHERE Id = $id;", ("$id", incoming.Id.ToString()));
        remove.Transaction = transaction;
        remove.ExecuteNonQuery();
        using var save = Command(connection, """
            INSERT INTO SyncTombstones (Id, RecordType, UpdatedUtc, DeletedUtc) VALUES ($id, 1, $updated, $deleted)
            ON CONFLICT(Id, RecordType) DO UPDATE SET UpdatedUtc = MAX(UpdatedUtc, excluded.UpdatedUtc),
                DeletedUtc = CASE WHEN excluded.UpdatedUtc >= UpdatedUtc THEN excluded.DeletedUtc ELSE DeletedUtc END;
            """, ("$id", incoming.Id.ToString()), ("$updated", UtcTicks(incoming.UpdatedUtc)),
            ("$deleted", UtcTicks(incoming.Ticket?.DeletedUtc ?? incoming.UpdatedUtc)));
        save.Transaction = transaction;
        if (save.ExecuteNonQuery() != 1) throw new SyncPersistenceException("TicketTombstoneWriteIgnored");
    }

    private static SyncRecord? ReadPlateRecord(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = Command(connection, "SELECT Id, PlateNumber, CreatedUtc, UpdatedUtc, SortOrder FROM VehiclePlates WHERE Id = $id;", ("$id", id.ToString()));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var plate = new VehiclePlate(Guid.Parse(reader.GetString(0)), reader.GetString(1), ReadUtc(reader, 2), ReadUtc(reader, 3)) { SortOrder = reader.GetInt32(4) };
        return SyncRecord.ForPlate(plate);
    }

    private static SyncRecord? ReadTicketRecord(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        var ticket = FindTicket(connection, id, transaction);
        return ticket is null ? null : SyncRecord.ForTicket(ticket);
    }

    private static SyncRecord? ReadTombstoneRecord(SqliteConnection connection, SqliteTransaction transaction, Guid id, SyncRecordType type)
    {
        using var command = Command(connection, "SELECT Id, UpdatedUtc FROM SyncTombstones WHERE Id = $id AND RecordType = $type;", ("$id", id.ToString()), ("$type", (int)type));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        return reader.Read() ? new SyncRecord(SyncSchema.CurrentVersion, type, id, ReadUtc(reader, 1), true, null, null) : null;
    }

    private async Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // SQLite's async provider APIs also perform synchronous disk I/O.
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                if (!initialized)
                {
                    Initialize(connection);
                    initialized = true;
                }
                return action(connection);
            }).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Initialize(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var versionCommand = Command(connection, "PRAGMA user_version;");
        versionCommand.Transaction = transaction;
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > Schema.Version)
            throw new InvalidOperationException($"Database version {version} is newer than this app supports.");
        if (version == 0)
        {
            using var create = Command(connection, Schema.Create);
            create.Transaction = transaction;
            create.ExecuteNonQuery();
            version = 1;
        }
        if (version == 1)
        {
            using var upgrade = Command(connection, Schema.UpgradeToVersion2);
            upgrade.Transaction = transaction;
            upgrade.ExecuteNonQuery();
            version = 2;
        }
        if (version == 2)
        {
            using var upgrade = Command(connection, Schema.UpgradeToVersion3);
            upgrade.Transaction = transaction;
            upgrade.ExecuteNonQuery();
            version = 3;
        }
        if (version == 3)
        {
            using var upgrade = Command(connection, Schema.UpgradeToVersion4);
            upgrade.Transaction = transaction;
            upgrade.ExecuteNonQuery();
            version = 4;
        }
        using (var syncTable = Command(connection, """
            CREATE TABLE IF NOT EXISTS SyncTombstones (
                Id TEXT NOT NULL,
                RecordType INTEGER NOT NULL CHECK(RecordType IN (0, 1)),
                UpdatedUtc INTEGER NOT NULL,
                PRIMARY KEY (Id, RecordType)
            );
            """))
        {
            syncTable.Transaction = transaction;
            syncTable.ExecuteNonQuery();
        }
        if (version == 4)
        {
            using var upgrade = Command(connection, Schema.UpgradeToVersion5);
            upgrade.Transaction = transaction;
            upgrade.ExecuteNonQuery();
            version = 5;
        }
        if (version == 5)
        {
            // Inspect columns so an existing schema is not altered twice.
            using var columns = Command(connection, "SELECT COUNT(*) FROM pragma_table_info('SyncTombstones') WHERE name = 'DeletedUtc';");
            columns.Transaction = transaction;
            if (Convert.ToInt64(columns.ExecuteScalar()) == 0)
            {
                using var upgrade = Command(connection, Schema.UpgradeToVersion6);
                upgrade.Transaction = transaction;
                upgrade.ExecuteNonQuery();
            }
            else
            {
                using var versionUpdate = Command(connection, "PRAGMA user_version = 6;");
                versionUpdate.Transaction = transaction;
                versionUpdate.ExecuteNonQuery();
            }
            RepairPlateNormalization(connection, transaction);
        }
        transaction.Commit();
    }

    private static void RepairPlateNormalization(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Repair display values and references without deserializing or rewriting ticket history.
        var plates = SyncMergeEngine.Merge(ReadPlates(connection, transaction).Select(SyncRecord.ForPlate), []);
        using var context = Command(connection, "INSERT INTO SyncApplyContext (Id) VALUES (1);");
        context.Transaction = transaction;
        context.ExecuteNonQuery();
        foreach (var plate in plates)
        {
            WriteSyncedPlate(connection, transaction, plate);
            if (plate.CanonicalPlateId is not { } canonical) continue;
            using var remap = Command(connection, "UPDATE ParkingTickets SET VehiclePlateId = $canonical WHERE VehiclePlateId = $duplicate;",
                ("$canonical", canonical.ToString()), ("$duplicate", plate.Id.ToString()));
            remap.Transaction = transaction;
            remap.ExecuteNonQuery();
        }
        // Historical snapshots keep their original value, normalized only for casing/spacing.
        // Read IDs as strings so this repair also preserves legacy non-sync rows verbatim.
        var snapshots = new List<(string Id, string Number)>();
        using (var read = Command(connection, "SELECT Id, PlateNumberSnapshot FROM ParkingTickets WHERE PlateNumberSnapshot IS NOT NULL;"))
        {
            read.Transaction = transaction;
            using var reader = read.ExecuteReader();
            while (reader.Read()) snapshots.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var (id, number) in snapshots)
        {
            var normalized = number.Trim().ToUpperInvariant();
            if (normalized == number) continue;
            using var update = Command(connection, "UPDATE ParkingTickets SET PlateNumberSnapshot = $number WHERE Id = $id;",
                ("$number", normalized), ("$id", id));
            update.Transaction = transaction;
            update.ExecuteNonQuery();
        }
        using var cleanup = Command(connection, """
            DELETE FROM VehiclePlates WHERE Id IN (SELECT Id FROM SyncTombstones WHERE RecordType = 0)
                AND NOT EXISTS (SELECT 1 FROM ParkingTickets WHERE VehiclePlateId = VehiclePlates.Id);
            DELETE FROM SyncApplyContext;
            """);
        cleanup.Transaction = transaction;
        cleanup.ExecuteNonQuery();
    }

    private static bool IsDuplicatePlate(SqliteException error) => error.SqliteExtendedErrorCode == 2067 ||
        error.SqliteExtendedErrorCode == 1811 && error.Message.Contains("parkinghelper_duplicate_plate", StringComparison.Ordinal);

    private const string TicketSelect = """
        SELECT Id, VehiclePlateId, BarcodeFormat, BarcodeValue, State,
               CreatedUtc, UpdatedUtc, ArchivedUtc, DeletedUtc, PlateNumberSnapshot, RawBarcodeData, EntryUtc FROM ParkingTickets
        """;

    private static ParkingTicket? FindTicket(SqliteConnection connection, Guid id, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, $"{TicketSelect} WHERE Id = $id AND State != 2;", ("$id", id.ToString()));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTicket(reader) : null;
    }

    private static ParkingTicket ReadTicket(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
        (ParkingTicketState)reader.GetInt32(4), ReadUtc(reader, 5), ReadUtc(reader, 6),
        reader.IsDBNull(7) ? null : ReadUtc(reader, 7), reader.IsDBNull(8) ? null : ReadUtc(reader, 8))
    {
        PlateNumberSnapshot = reader.IsDBNull(9) ? null : reader.GetString(9),
        RawBarcodeData = reader.IsDBNull(10) ? null : (byte[])reader.GetValue(10),
        EntryUtc = ReadUtc(reader, 11)
    };

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static DateTime ReadUtc(SqliteDataReader reader, int ordinal) => new(reader.GetInt64(ordinal), DateTimeKind.Utc);
    private static object NullableTicks(DateTime? value) => value.HasValue ? UtcTicks(value.Value) : DBNull.Value;

    private static long UtcTicks(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("All persisted timestamps must be UTC.");
        return value.Ticks;
    }

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("An identifier must not be empty.");
    }
}
