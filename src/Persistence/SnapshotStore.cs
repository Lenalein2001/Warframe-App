using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WarframeCompanion.Core;

namespace WarframeCompanion.Persistence;

public sealed record SnapshotMetadata(long? MasteryRank, double? SourceTimestamp);
public sealed record CommitResult(long Id, bool Promoted, string Reason, bool Recorded = true);
public sealed record WatchedExport(string PathHash, string ContentHash);
public sealed record SnapshotHistory(long Id, string AccountId, string ReadAt, double? SourceTimestamp, bool Complete, bool Current);

public sealed partial class SnapshotStore
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
        if (schema > 3) throw new InventoryException("Database was created by a newer engine; refusing to modify it.");
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
        if (schema < 2)
        {
            using var upgrade = db.CreateCommand();
            upgrade.Transaction = transaction;
            // Old history includes complete observations that were never promoted.
            // The existing current row becomes the baseline on the next promotion.
            upgrade.CommandText = """
                ALTER TABLE current_snapshots ADD COLUMN previous_id INTEGER REFERENCES snapshots(id);
                CREATE TABLE watched_exports (
                    path_hash TEXT PRIMARY KEY,
                    content_hash TEXT NOT NULL,
                    snapshot_id INTEGER NOT NULL REFERENCES snapshots(id)
                );
                PRAGMA user_version=2;
                """;
            upgrade.ExecuteNonQuery();
        }
        if (schema < 3)
        {
            using var upgrade = db.CreateCommand();
            upgrade.Transaction = transaction;
            upgrade.CommandText = """
                CREATE TABLE inventory_actions (
                    owner TEXT NOT NULL, inventory_account TEXT NOT NULL, token TEXT NOT NULL,
                    event_time REAL NOT NULL, uid TEXT, settled INTEGER NOT NULL,
                    effects TEXT NOT NULL, PRIMARY KEY(owner, token)
                );
                CREATE INDEX inventory_actions_pending ON inventory_actions(inventory_account, owner) WHERE settled=0;
                PRAGMA user_version=3;
                """;
            upgrade.ExecuteNonQuery();
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

    public CommitResult Commit(InventorySnapshot snapshot, CancellationToken cancellation = default, WatchedExport? export = null)
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
        if (export != null)
        {
            using var receipt = db.CreateCommand();
            receipt.Transaction = tx;
            receipt.CommandText = "SELECT snapshot_id FROM watched_exports WHERE path_hash=$path AND content_hash=$hash";
            receipt.Parameters.AddWithValue("$path", export.PathHash);
            receipt.Parameters.AddWithValue("$hash", export.ContentHash);
            if (receipt.ExecuteScalar() is long seen) return new(seen, false, "Inventory file already processed.", false);
        }
        using var lookup = db.CreateCommand();
        lookup.Transaction = tx;
        lookup.CommandText = "SELECT s.id,s.source_time FROM current_snapshots c JOIN snapshots s ON s.id=c.snapshot_id WHERE c.account=$account";
        lookup.Parameters.AddWithValue("$account", snapshot.AccountId);
        long? previousId = null;
        double? previousTime = null;
        using (var reader = lookup.ExecuteReader())
            if (reader.Read()) { previousId = reader.GetInt64(0); previousTime = reader.IsDBNull(1) ? null : reader.GetDouble(1); }
        var promote = snapshot.Complete;
        var reason = promote ? "Confirmed snapshot committed." : "Partial observation retained; confirmed inventory unchanged.";
        if (promote && previousId.HasValue)
        {
            if (export != null && previousTime.HasValue && snapshot.SourceTimestamp == previousTime)
            {
                SaveReceipt(previousId.Value);
                cancellation.ThrowIfCancellationRequested();
                tx.Commit();
                return new(previousId.Value, false, "Inventory sync already confirmed.", false);
            }
            if (snapshot.SourceTimestamp == null || previousTime == null)
            {
                promote = false;
                reason = "Observation retained; cannot establish source ordering without both sync timestamps.";
            }
            else if (snapshot.SourceTimestamp <= previousTime)
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
                ON CONFLICT(account) DO UPDATE SET previous_id=current_snapshots.snapshot_id, snapshot_id=excluded.snapshot_id;
                """;
            update.Parameters.AddWithValue("$account", snapshot.AccountId);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            SettleActions(db, tx, snapshot.AccountId, snapshot.SourceTimestamp);
        }
        SaveReceipt(id);
        cancellation.ThrowIfCancellationRequested();
        tx.Commit();
        return new(id, promote, reason);

        void SaveReceipt(long snapshotId)
        {
            if (export == null) return;
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO watched_exports(path_hash,content_hash,snapshot_id) VALUES($path,$hash,$id)
                ON CONFLICT(path_hash) DO UPDATE SET content_hash=excluded.content_hash,snapshot_id=excluded.snapshot_id;
                """;
            cmd.Parameters.AddWithValue("$path", export.PathHash);
            cmd.Parameters.AddWithValue("$hash", export.ContentHash);
            cmd.Parameters.AddWithValue("$id", snapshotId);
            cmd.ExecuteNonQuery();
        }
    }

    public InventorySnapshot? Current(string account)
    {
        InventoryParser.ValidateAccount(account);
        using var db = Open();
        return Current(db, null, account);
    }

    public (InventorySnapshot? Previous, InventorySnapshot? Current) ConfirmedPair(string account)
    {
        InventoryParser.ValidateAccount(account);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.payload,s.payload FROM current_snapshots c
            JOIN snapshots s ON s.account=c.account AND s.id=c.snapshot_id
            LEFT JOIN snapshots p ON p.account=c.account AND p.id=c.previous_id WHERE c.account=$account
            """;
        cmd.Parameters.AddWithValue("$account", account);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (null, null);
        return (reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<InventorySnapshot>(reader.GetString(0), SnapshotJson.Options),
            JsonSerializer.Deserialize<InventorySnapshot>(reader.GetString(1), SnapshotJson.Options));
    }

    public void SeedLinkedAccount(string provisional, string linked)
    {
        InventoryParser.ValidateAccount(provisional);
        InventoryParser.ValidateAccount(linked);
        if (provisional == linked) return;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var lookup = db.CreateCommand();
        lookup.Transaction = tx;
        using var rebind = db.CreateCommand();
        rebind.Transaction = tx;
        rebind.CommandText = "UPDATE inventory_actions SET inventory_account=$new WHERE inventory_account=$old AND owner=$old";
        rebind.Parameters.AddWithValue("$old", provisional);
        rebind.Parameters.AddWithValue("$new", linked);
        rebind.ExecuteNonQuery();
        lookup.CommandText = """
            SELECT previous_id,snapshot_id FROM current_snapshots WHERE account=$old
            AND NOT EXISTS (SELECT 1 FROM current_snapshots WHERE account=$new);
            """;
        lookup.Parameters.AddWithValue("$old", provisional);
        lookup.Parameters.AddWithValue("$new", linked);
        long? previous;
        long current;
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read())
            {
                reader.Close();
                SettleLinked();
                tx.Commit();
                return;
            }
            previous = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            current = reader.GetInt64(1);
        }
        long Copy(long original)
        {
            using var copy = db.CreateCommand();
            copy.Transaction = tx;
            copy.CommandText = """
                INSERT INTO snapshots(account,read_at,source_time,complete,payload)
                SELECT $new,read_at,source_time,complete,json_set(payload,'$.accountId',$new,'$.identityVerified',json('false'))
                FROM snapshots WHERE account=$old AND id=$id RETURNING id;
                """;
            copy.Parameters.AddWithValue("$old", provisional);
            copy.Parameters.AddWithValue("$new", linked);
            copy.Parameters.AddWithValue("$id", original);
            return (long)copy.ExecuteScalar()!;
        }
        using var save = db.CreateCommand();
        save.Transaction = tx;
        save.CommandText = "INSERT INTO current_snapshots(account,previous_id,snapshot_id) VALUES($account,$previous,$current)";
        save.Parameters.AddWithValue("$account", linked);
        save.Parameters.AddWithValue("$previous", previous is long id ? Copy(id) : DBNull.Value);
        save.Parameters.AddWithValue("$current", Copy(current));
        save.ExecuteNonQuery();
        SettleLinked();
        tx.Commit();

        void SettleLinked()
        {
            using var time = db.CreateCommand();
            time.Transaction = tx;
            time.CommandText = "SELECT s.source_time FROM current_snapshots c JOIN snapshots s ON c.snapshot_id=s.id WHERE c.account=$new";
            time.Parameters.AddWithValue("$new", linked);
            if (time.ExecuteScalar() is double synced) SettleActions(db, tx, linked, synced);
        }
    }

    private static InventorySnapshot? Current(SqliteConnection db, SqliteTransaction? tx, string account)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT s.payload FROM snapshots s JOIN current_snapshots c ON c.account=s.account AND c.snapshot_id=s.id WHERE c.account=$account";
        cmd.Parameters.AddWithValue("$account", account);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<InventorySnapshot>(json, SnapshotJson.Options) : null;
    }

    // Account lists only need these scalars, not every stack and equipment entry.
    public SnapshotMetadata? CurrentMetadata(string account)
    {
        InventoryParser.ValidateAccount(account);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json_extract(s.payload, '$.masteryRank'), s.source_time FROM snapshots s JOIN current_snapshots c ON c.account=s.account AND c.snapshot_id=s.id WHERE c.account=$account";
        cmd.Parameters.AddWithValue("$account", account);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetDouble(1)) : null;
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
        using var tx = db.BeginTransaction();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM current_snapshots WHERE account=$account; UPDATE inventory_actions SET settled=1,effects='[]' WHERE inventory_account=$account AND settled=0";
        cmd.Parameters.AddWithValue("$account", account); cmd.ExecuteNonQuery();
        tx.Commit();
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
