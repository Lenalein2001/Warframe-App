using System.Text.Json;
using WarframeCompanion.Core;
using WarframeCompanion.Persistence;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("""
        Warframe Companion C# inventory engine — migration preview, not desktop parity

        validate --file inventory.json --account ACCOUNT [--complete]
        import   --file inventory.json --account ACCOUNT [--complete] [--data-dir DIR | --portable]
        current  --account ACCOUNT [--data-dir DIR | --portable]
        history  --account ACCOUNT [--data-dir DIR | --portable]
        export   --account ACCOUNT --output snapshot.json [--data-dir DIR | --portable]
        backup   --output backup.sqlite [--data-dir DIR | --portable]
        check    [--data-dir DIR | --portable]

        Imports are partial by default. --complete explicitly asserts a full snapshot.
        Partial, older or unorderable observations never replace confirmed inventory.
        Exports are normalized version-1 snapshots, not raw Warframe import payloads.
        Existing Rust app settings and account files are never automatically changed.
        """);
    return 0;
}

try
{
    var command = args[0];
    var allowed = command switch
    {
        "validate" => new[] { "--file", "--account", "--complete" },
        "import" => ["--file", "--account", "--complete", "--data-dir", "--portable"],
        "current" or "history" => ["--account", "--data-dir", "--portable"],
        "export" => ["--account", "--output", "--data-dir", "--portable"],
        "backup" => ["--output", "--data-dir", "--portable"],
        "check" => ["--data-dir", "--portable"],
        _ => throw new InventoryException("Unknown command. Use --help.")
    };
    var options = new Dictionary<string, string?>();
    for (var i = 1; i < args.Length; i++)
    {
        var key = args[i];
        if (!allowed.Contains(key) || options.ContainsKey(key)) throw new InventoryException("Unknown or duplicate option. Use --help.");
        if (key is "--complete" or "--portable") options.Add(key, null);
        else
        {
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new InventoryException("Option requires a value.");
            options.Add(key, args[i]);
        }
    }
    string Required(string key) => options.GetValueOrDefault(key) ?? throw new InventoryException($"{key} is required.");
    void Print(object? value) => Console.WriteLine(JsonSerializer.Serialize(value, SnapshotJson.Options));
    InventorySnapshot? snapshot = null;
    if (command is "validate" or "import")
    {
        var account = Required("--account");
        var input = Required("--file");
        // Bound the actual read, not just the file's initial length (which can change).
        using var stream = File.OpenRead(input);
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + count > InventoryParser.MaxBytes) throw new InventoryException("Inventory exceeds the 32 MiB limit.");
            buffer.Write(chunk, 0, count);
        }
        snapshot = InventoryParser.Parse(buffer.ToArray(), account, options.ContainsKey("--complete"));
        if (command == "validate") { Print(snapshot); return 0; }
    }
    if (command is "current" or "history" or "export") InventoryParser.ValidateAccount(Required("--account"));
    if (command is "export" or "backup") _ = Required("--output");
    if (options.ContainsKey("--portable") && options.ContainsKey("--data-dir")) throw new InventoryException("Choose --portable or --data-dir, not both.");
    var directory = options.ContainsKey("--portable") ? Path.Combine(AppContext.BaseDirectory, "data") :
        options.GetValueOrDefault("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarframeCompanionPreview");
    var store = new SnapshotStore(Path.Combine(Path.GetFullPath(directory), "companion.sqlite"));
    switch (command)
    {
        case "import": Print(store.Commit(snapshot!)); break;
        case "current": Print(store.Current(Required("--account")) ?? throw new InventoryException("No confirmed snapshot for this account.")); break;
        case "history": Print(store.History(Required("--account"))); break;
        case "export":
            var current = store.Current(Required("--account")) ?? throw new InventoryException("No confirmed snapshot for this account.");
            using (var output = new FileStream(Required("--output"), FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(output, current, SnapshotJson.Options);
                output.Flush(true);
            }
            Print(new { exported = true, schemaVersion = 1 });
            break;
        case "backup": store.Backup(Required("--output")); Print(new { backedUp = true }); break;
        case "check":
            var integrity = store.IntegrityCheck();
            Print(new { integrity });
            return integrity == "ok" ? 0 : 1;
    }
    return 0;
}
catch (InventoryException e) { Console.Error.WriteLine(e.Message); return 2; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
{
    // Paths, SQL and raw payloads are deliberately not emitted in routine diagnostics.
    Console.Error.WriteLine("Storage operation failed. Check the selected directory, permissions, free space and database health. No fallback directory was used.");
    return 1;
}
