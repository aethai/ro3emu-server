using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Ro3Emu.Server.Data;

/// <summary>
/// Operator content packs: JSON files in <c>data/content/*.json</c> that change rows of the client's config tables
/// before the frozen snapshot is built, so a quest, event, guild, skill, drop, spawn or map row can be added, changed
/// or removed without re-exporting the client data or rebuilding the server.
/// <para>
/// A pack names tables and, per table, the rows to set and the ids to remove:
/// </para>
/// <code>
/// {
///   "name": "spring-event",
///   "tables": {
///     "ItemConfig":  { "set": { "1200100": { "_iSellPrice": 0 } }, "remove": ["1234567"] },
///     "TaskConfig":  { "set": { "10105050": { "_iCompleteType": 2 } } }
///   }
/// }
/// </code>
/// <para>
/// A row in <c>set</c> is merged field by field into the existing row (fields the pack does not name keep their client
/// value); an id the table does not have yet is created from the pack's object, so a pack can add a row as long as it
/// carries every field the server validates. Packs apply in file-name order, so <c>010-a.json</c> is overridden by
/// <c>020-b.json</c>. A pack that is not valid JSON, or that names a table this data set does not have, is reported and
/// skipped as a whole - the server still starts, with the other packs and the client's own values.
/// </para>
/// </summary>
public static class ContentPacks
{
    /// <param name="Documents">The documents the applied rows point into; the caller must keep them alive (a
    /// JsonElement is only valid while its document lives).</param>
    public sealed record Result(int Packs, int Rows, int Removed, IReadOnlyList<string> Problems, IReadOnlyList<JsonDocument> Documents);

    /// <summary>
    /// Applies <paramref name="root"/>/content/*.json to <paramref name="tables"/> (table name -> id -> row), which
    /// must be the map the server will read from.
    /// </summary>
    public static Result Apply(string root, IDictionary<string, Dictionary<string, JsonElement>> tables, ILogger log)
    {
        var problems = new List<string>();
        var documents = new List<JsonDocument>();
        var directory = Path.Combine(root, "content");
        if (!Directory.Exists(directory)) return new Result(0, 0, 0, problems, documents);

        var files = Directory.GetFiles(directory, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        int packs = 0, rows = 0, removed = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            JsonDocument document;
            try { document = JsonDocument.Parse(File.ReadAllBytes(file)); }
            catch (JsonException e)
            {
                problems.Add($"{name}: not valid JSON ({e.Message})");
                continue;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("tables", out var specs) || specs.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"{name}: no \"tables\" object");
                    continue;
                }

                // Build everything first so a pack is applied completely or not at all.
                var plan = new List<(string Table, string Id, JsonElement? Row, bool Remove)>();
                bool bad = false;
                foreach (var spec in specs.EnumerateObject())
                {
                    if (!tables.TryGetValue(spec.Name, out var table))
                    {
                        problems.Add($"{name}: no table {spec.Name} in this data set");
                        bad = true;
                        continue;
                    }
                    if (spec.Value.ValueKind != JsonValueKind.Object)
                    {
                        problems.Add($"{name}: table {spec.Name} is not an object");
                        bad = true;
                        continue;
                    }
                    if (spec.Value.TryGetProperty("remove", out var remove))
                    {
                        if (remove.ValueKind != JsonValueKind.Array)
                        {
                            problems.Add($"{name}: {spec.Name}.remove is not an array");
                            bad = true;
                        }
                        else
                        {
                            foreach (var id in remove.EnumerateArray())
                            {
                                if (id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                                    plan.Add((spec.Name, id.ToString(), null, true));
                                else { problems.Add($"{name}: {spec.Name}.remove has a non-string id"); bad = true; }
                            }
                        }
                    }
                    if (!spec.Value.TryGetProperty("set", out var set)) continue;
                    if (set.ValueKind != JsonValueKind.Object)
                    {
                        problems.Add($"{name}: {spec.Name}.set is not an object");
                        bad = true;
                        continue;
                    }
                    foreach (var entry in set.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.Object)
                        {
                            problems.Add($"{name}: {spec.Name} row {entry.Name} is not an object");
                            bad = true;
                            continue;
                        }
                        plan.Add((spec.Name, entry.Name, entry.Value.Clone(), false));
                    }
                }
                if (bad) continue;

                foreach (var (tableName, id, row, isRemove) in plan)
                {
                    var table = tables[tableName];
                    if (isRemove)
                    {
                        if (table.Remove(id)) removed++;
                        continue;
                    }
                    JsonObject merged;
                    if (table.TryGetValue(id, out var existing) && JsonNode.Parse(existing.GetRawText()) is JsonObject current) merged = current;
                    else merged = [];
                    foreach (var field in row!.Value.EnumerateObject())
                        merged[field.Name] = field.Value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(field.Value.GetRawText());
                    var applied = JsonDocument.Parse(merged.ToJsonString());
                    documents.Add(applied);
                    table[id] = applied.RootElement;
                    rows++;
                }
                packs++;
            }
        }

        foreach (var problem in problems) log.LogWarning("Content pack {Problem}", problem);
        return new Result(packs, rows, removed, problems, documents);
    }
}
