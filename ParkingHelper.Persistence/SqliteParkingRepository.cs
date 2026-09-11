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
        using var command = Command(connection, """
            INSERT INTO VehiclePlates (Id, PlateNumber, CreatedUtc, UpdatedUtc, SortOrder)
            SELECT $id, $number, $created, $updated, COALESCE(MAX(SortOrder), -1) + 1 FROM VehiclePlates;
            """, ("$id", plate.Id.ToString()), ("$number", number),
            ("$created", UtcTicks(plate.CreatedUtc)), ("$updated", UtcTicks(plate.UpdatedUtc)));
        try { return command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode == 2067)
        {
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
        var plate = ReadPlates(connection, transaction).FirstOrDefault(p => p.Id == id)
            ?? throw new PlateOperationException("This plate no longer exists. Refresh the list and try again.");
        if (plate.PlateNumber != number)
        {
            using var command = Command(connection, """
                UPDATE VehiclePlates SET PlateNumber = $number, UpdatedUtc = MAX(UpdatedUtc + 1, $now) WHERE Id = $id;
                """, ("$number", number), ("$now", UtcTicks(utcNow)), ("$id", id.ToString()));
            command.Transaction = transaction;
            try { command.ExecuteNonQuery(); }
            catch (SqliteException error) when (error.SqliteExtendedErrorCode == 2067)
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
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            // Retain the existing ticket foreign key even though this milestone has no ticket UI.
            throw new PlateOperationException("This plate is linked to a saved ticket and cannot be deleted.");
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
            "SELECT Id, PlateNumber, CreatedUtc, UpdatedUtc, SortOrder FROM VehiclePlates ORDER BY SortOrder, Id;");
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
        using (var plate = Command(connection, "SELECT 1 FROM VehiclePlates WHERE Id = $id;",
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

    public Task AddTicketAsync(ParkingTicket ticket) => ExecuteAsync(connection => InsertTicket(connection, ticket));

    private static int InsertTicket(SqliteConnection connection, ParkingTicket ticket, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        RequireId(ticket.Id);
        RequireId(ticket.VehiclePlateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.BarcodeFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.BarcodeValue);
        using var command = Command(connection, """
            INSERT INTO ParkingTickets
            (Id, VehiclePlateId, BarcodeFormat, BarcodeValue, State, CreatedUtc, UpdatedUtc, ArchivedUtc, DeletedUtc,
             PlateNumberSnapshot, RawBarcodeData)
            VALUES ($id, $plate, $format, $value, $state, $created, $updated, $archived, $deleted, $snapshot, $raw);
            """, ("$id", ticket.Id.ToString()), ("$plate", ticket.VehiclePlateId.ToString()),
            ("$format", ticket.BarcodeFormat), ("$value", ticket.BarcodeValue),
            ("$state", (int)ticket.State), ("$created", UtcTicks(ticket.CreatedUtc)),
            ("$updated", UtcTicks(ticket.UpdatedUtc)), ("$archived", NullableTicks(ticket.ArchivedUtc)),
            ("$deleted", NullableTicks(ticket.DeletedUtc)),
            ("$snapshot", (object?)ticket.PlateNumberSnapshot ?? DBNull.Value),
            ("$raw", (object?)ticket.RawBarcodeData ?? DBNull.Value));
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
                $"{TicketSelect} WHERE State = $state ORDER BY {(state == ParkingTicketState.Archived ? "ArchivedUtc" : "CreatedUtc")} DESC, Id;", ("$state", (int)state));
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
            if (changed != ticket)
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
        ExecuteAsync(connection =>
        {
            if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC timestamp is required.", nameof(utcNow));
            using var transaction = connection.BeginTransaction(deferred: false);
            var ticket = FindTicket(connection, ticketId, transaction)
                ?? throw new TicketOperationException("This ticket could not be found.");
            if (ticket.State == ParkingTicketState.Deleted)
                throw new TicketOperationException("This ticket has been deleted.");
            // Resolve the current plate text inside the write transaction, never trust a stale UI snapshot.
            using var plateCommand = Command(connection, "SELECT PlateNumber FROM VehiclePlates WHERE Id = $id;", ("$id", plateId.ToString()));
            plateCommand.Transaction = transaction;
            var number = plateCommand.ExecuteScalar() as string
                ?? throw new TicketOperationException("This plate is no longer available. Choose another saved plate.");
            if (ticket.VehiclePlateId == plateId)
            {
                transaction.Commit();
                return ticket;
            }
            var changed = ticket with
            {
                VehiclePlateId = plateId,
                PlateNumberSnapshot = number,
                UpdatedUtc = utcNow > ticket.UpdatedUtc ? utcNow : ticket.UpdatedUtc.AddTicks(1)
            };
            using var update = Command(connection, """
                UPDATE ParkingTickets SET VehiclePlateId = $plate, PlateNumberSnapshot = $number,
                    UpdatedUtc = $updated WHERE Id = $id;
                """, ("$plate", plateId.ToString()), ("$number", number),
                ("$updated", UtcTicks(changed.UpdatedUtc)), ("$id", ticketId.ToString()));
            update.Transaction = transaction;
            update.ExecuteNonQuery();
            transaction.Commit();
            return changed;
        });

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

    private static void Initialize(SqliteConnection connection)
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
        }
        transaction.Commit();
    }

    private const string TicketSelect = """
        SELECT Id, VehiclePlateId, BarcodeFormat, BarcodeValue, State,
               CreatedUtc, UpdatedUtc, ArchivedUtc, DeletedUtc, PlateNumberSnapshot, RawBarcodeData FROM ParkingTickets
        """;

    private static ParkingTicket? FindTicket(SqliteConnection connection, Guid id, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, $"{TicketSelect} WHERE Id = $id;", ("$id", id.ToString()));
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
        RawBarcodeData = reader.IsDBNull(10) ? null : (byte[])reader.GetValue(10)
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
