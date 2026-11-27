using System.Text.Json;
using Microsoft.Data.Sqlite;
using WarframeCompanion.Core;

namespace WarframeCompanion.Persistence;

public sealed record InventoryEffect(string Kind, string Uid, long Change, double? Done = null, string? InstanceId = null);
public sealed record InventoryAction(string Token, double Time, string Uid, InventoryEffect[] Effects);
public sealed record WorkingInventory(long Baseline, InventorySnapshot? Snapshot, List<InventoryAction> Pending);

public sealed partial class SnapshotStore
{
    private static void SettleActions(SqliteConnection db, SqliteTransaction tx, string account, double? through)
    {
        if (through == null) return;
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE inventory_actions SET settled=1,effects='[]' WHERE inventory_account=$account AND settled=0 AND event_time<=$through";
        cmd.Parameters.AddWithValue("$account", account);
        cmd.Parameters.AddWithValue("$through", through);
        cmd.ExecuteNonQuery();
    }

    public WorkingInventory Working(string owner, string account)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT s.id,s.payload FROM current_snapshots c JOIN snapshots s ON c.snapshot_id=s.id WHERE c.account=$account";
        cmd.Parameters.AddWithValue("$account", account);
        long baseline = 0;
        InventorySnapshot? snapshot = null;
        using (var row = cmd.ExecuteReader())
            if (row.Read())
            {
                baseline = row.GetInt64(0);
                snapshot = JsonSerializer.Deserialize<InventorySnapshot>(row.GetString(1), SnapshotJson.Options);
            }
        cmd.CommandText = "SELECT token,event_time,uid,effects FROM inventory_actions WHERE owner=$owner AND inventory_account=$account AND settled=0 AND uid IS NOT NULL ORDER BY event_time,rowid";
        cmd.Parameters.AddWithValue("$owner", owner);
        var actions = new List<InventoryAction>();
        using var rows = cmd.ExecuteReader();
        while (rows.Read()) actions.Add(new(rows.GetString(0), rows.GetDouble(1), rows.GetString(2),
            JsonSerializer.Deserialize<InventoryEffect[]>(rows.GetString(3))!));
        return new(baseline, snapshot, actions);
    }

    public bool HasAction(string owner, string token)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM inventory_actions WHERE owner=$owner AND token=$token";
        cmd.Parameters.AddWithValue("$owner", owner);
        cmd.Parameters.AddWithValue("$token", token);
        return cmd.ExecuteScalar() != null;
    }

    public bool HasPending(string owner)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM inventory_actions WHERE owner=$owner AND settled=0 AND uid IS NOT NULL LIMIT 1";
        cmd.Parameters.AddWithValue("$owner", owner);
        return cmd.ExecuteScalar() != null;
    }

    public Dictionary<string,string> Picks(string owner, string[] ids)
    {
        var picks = new Dictionary<string,string>();
        if (ids.Length == 0) return picks;
        using var db = Open();
        using var cmd = db.CreateCommand();
        var parameters = ids.Select((id, i) => cmd.Parameters.AddWithValue("$p" + i, "pick:" + id).ParameterName).ToArray();
        cmd.CommandText = $"SELECT token,uid FROM inventory_actions WHERE owner=$owner AND uid IS NOT NULL AND token IN ({string.Join(',', parameters)})";
        cmd.Parameters.AddWithValue("$owner", owner);
        using var rows = cmd.ExecuteReader();
        while (rows.Read()) picks[rows.GetString(0)[5..]] = rows.GetString(1);
        return picks;
    }

    public void SaveAction(string owner, string account, string token, double time, string? uid,
        InventoryEffect[] effects, long? expectedBaseline = null, bool checkPick = false, string? expectedPick = null)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$owner", owner);
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$account", account);
        if (checkPick)
        {
            cmd.CommandText = "SELECT uid FROM inventory_actions WHERE owner=$owner AND token=$token";
            var picked = cmd.ExecuteScalar() as string;
            if (picked == uid) return;
            if (picked != expectedPick) throw new InventoryException("This reward choice changed. Reopen the reward history.");
        }
        if (expectedBaseline.HasValue)
        {
            cmd.CommandText = "SELECT 1 FROM inventory_actions WHERE owner=$owner AND token=$token";
            if (cmd.ExecuteScalar() != null) return;
            cmd.CommandText = "SELECT snapshot_id FROM current_snapshots WHERE account=$account";
            if (cmd.ExecuteScalar() is not long id || id != expectedBaseline)
                throw new InventoryException("Inventory changed. Reopen the item before marking the build.");
        }
        cmd.Parameters.AddWithValue("$t", time);
        cmd.Parameters.AddWithValue("$historical", expectedBaseline == null);
        cmd.Parameters.AddWithValue("$uid", (object?)uid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$effects", JsonSerializer.Serialize(effects));
        cmd.CommandText = """
            INSERT INTO inventory_actions(owner,inventory_account,token,event_time,uid,settled,effects)
            VALUES ($owner,$account,$token,$t,$uid,
                $historical AND EXISTS(SELECT 1 FROM current_snapshots c JOIN snapshots s ON s.id=c.snapshot_id WHERE c.account=$account AND s.source_time >= $t),$effects)
            ON CONFLICT(owner,token) DO UPDATE SET uid=excluded.uid,
                settled=MAX(settled,excluded.settled),effects=CASE WHEN MAX(settled,excluded.settled)=1 THEN '[]' ELSE excluded.effects END;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
