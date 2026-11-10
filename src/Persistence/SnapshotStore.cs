using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WarframeCompanion.Core;

namespace WarframeCompanion.Persistence;

public sealed record CommitResult(long Id, bool Promoted, string Reason);
public sealed record SnapshotHistory(long Id, string AccountId, string ReadAt, double? SourceTimestamp, bool Complete, bool Current);

public sealed class SnapshotStore
{
    private readonly string connectionString;

    public SnapshotStore(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true, Pooling = false, DefaultTimeout = 10
        }.ToString();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var version = db.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "PRAGMA user_version";
        var schema = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schema > 1) throw new InventoryException("Database was created by a newer engine; refusing to modify it.");
        if (schema == 0)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    account TEXT NOT NULL,
                    read_at TEXT NOT NULL,
                    source_time REAL,
                    complete INTEGER NOT NULL CHECK(complete IN (0,1)),
                    payload TEXT NOT NULL,
                    UNIQUE(account,id)
                );
                CREATE TABLE current_snapshots (
                    account TEXT PRIMARY KEY,
                    snapshot_id INTEGER NOT NULL,
                    FOREIGN KEY(account,snapshot_id) REFERENCES snapshots(account,id)
                );
                CREATE INDEX snapshots_account_id ON snapshots(account,id DESC);
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=FULL";
        cmd.ExecuteNonQuery();
        return db;
    }

    public CommitResult Commit(InventorySnapshot snapshot, CancellationToken cancellation = default)
    {
        InventoryParser.ValidateAccount(snapshot.AccountId);
        if (snapshot.SchemaVersion != 1) throw new InventoryException("Unsupported snapshot schema.");
        if (snapshot.SourceTimestamp is double time && (!double.IsFinite(time) || time <= 0))
            throw new InventoryException("Invalid source timestamp.");
        // Serialize before acquiring the write lock. Never store the original raw JSON.
        var payload = JsonSerializer.Serialize(snapshot, SnapshotJson.Options);
        cancellation.ThrowIfCancellationRequested();
        using var db = Open();
        using var tx = db.BeginTransaction(); // serializes writers across processes as well
        var previous = Current(db, tx, snapshot.AccountId);
        var promote = snapshot.Complete;
        var reason = promote ? "Confirmed snapshot committed." : "Partial observation retained; confirmed inventory unchanged.";
        if (promote && previous != null)
        {
            if (snapshot.SourceTimestamp == null || previous.SourceTimestamp == null)
            {
                promote = false;
                reason = "Observation retained; cannot establish source ordering without both sync timestamps.";
            }
            else if (snapshot.SourceTimestamp <= previous.SourceTimestamp)
            {
                promote = false;
                reason = "Observation retained; source is not newer than the confirmed snapshot.";
            }
        }
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO snapshots(account,read_at,source_time,complete,payload) VALUES($account,$read,$time,$complete,$payload);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$account", snapshot.AccountId);
        insert.Parameters.AddWithValue("$read", snapshot.ReadAt.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$time", (object?)snapshot.SourceTimestamp ?? DBNull.Value);
        insert.Parameters.AddWithValue("$complete", snapshot.Complete ? 1 : 0);
        insert.Parameters.AddWithValue("$payload", payload);
        var id = (long)insert.ExecuteScalar()!;
        if (promote)
        {
            using var update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                INSERT INTO current_snapshots(account,snapshot_id) VALUES($account,$id)
                ON CONFLICT(account) DO UPDATE SET snapshot_id=excluded.snapshot_id;
                """;
            update.Parameters.AddWithValue("$account", snapshot.AccountId);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        cancellation.ThrowIfCancellationRequested();
        tx.Commit();
        return new(id, promote, reason);
    }

    public InventorySnapshot? Current(string account)
    {
        InventoryParser.ValidateAccount(account);
        using var db = Open();
        return Current(db, null, account);
    }

    private static InventorySnapshot? Current(SqliteConnection db, SqliteTransaction? tx, string account)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT s.payload FROM snapshots s JOIN current_snapshots c ON c.account=s.account AND c.snapshot_id=s.id WHERE c.account=$account";
        cmd.Parameters.AddWithValue("$account", account);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<InventorySnapshot>(json, SnapshotJson.Options) : null;
    }

    public IReadOnlyList<SnapshotHistory> History(string account, int limit = 100)
    {
        InventoryParser.ValidateAccount(account);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT s.id,s.account,s.read_at,s.source_time,s.complete,c.snapshot_id IS NOT NULL
            FROM snapshots s LEFT JOIN current_snapshots c ON c.account=s.account AND c.snapshot_id=s.id
            WHERE s.account=$account ORDER BY s.id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$account", account);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<SnapshotHistory>();
        while (reader.Read()) list.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        return list;
    }

    public void ClearCurrent(string account)
    {
        InventoryParser.ValidateAccount(account);
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM current_snapshots WHERE account=$account";
        cmd.Parameters.AddWithValue("$account", account); cmd.ExecuteNonQuery();
    }

    public string IntegrityCheck()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        return (string)cmd.ExecuteScalar()!;
    }

    public void Backup(string destination)
    {
        var target = Path.GetFullPath(destination);
        // CreateNew makes accidental overwrite (including the source) impossible.
        using (new FileStream(target, FileMode.CreateNew, FileAccess.Write)) { }
        using var db = Open();
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        backup.Open();
        db.BackupDatabase(backup);
    }
}
