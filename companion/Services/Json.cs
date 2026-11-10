using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WarframeCompanion.Services;

public static class J
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static JsonNode? Get(JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;
    public static string? S(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    public static string? S(JsonNode? node, string key) => S(Get(node, key));
    public static long N(JsonNode? node, long fallback = 0) => node is JsonValue v &&
        (v.TryGetValue<long>(out var n) || long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ? n : fallback;
    public static long N(JsonNode? node, string key, long fallback = 0) => N(Get(node, key), fallback);
    public static double? D(JsonNode? node) => node is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    public static bool B(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static IEnumerable<JsonNode> A(JsonNode? node) => node is JsonArray a ? a.OfType<JsonNode>() : [];
    public static IEnumerable<JsonNode> A(JsonNode? node, string key) => A(Get(node, key));
    public static IEnumerable<KeyValuePair<string, JsonNode?>> O(JsonNode? node) => node is JsonObject o ? o : [];
    public static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!;
    public static JsonArray Array<T>(IEnumerable<T> value) => (JsonArray)Node(value);
    public static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
