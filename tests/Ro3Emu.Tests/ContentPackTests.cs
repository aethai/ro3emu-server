using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>Operator content packs (data/content/*.json) change, add and remove config rows without a re-export.</summary>
public sealed class ContentPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-content-" + Guid.NewGuid().ToString("N"));

    private void Pack(string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "content"));
        File.WriteAllText(Path.Combine(_root, "content", name), json);
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> Tables(string table, params (string Id, string Json)[] rows)
    {
        var map = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        var tableMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, json) in rows) tableMap[id] = JsonDocument.Parse(json).RootElement.Clone();
        map[table] = tableMap;
        return map;
    }

    private static string? Field(IReadOnlyDictionary<string, JsonElement> table, string id, string field) =>
        table.TryGetValue(id, out var row) && row.TryGetProperty(field, out var value) ? value.ToString() : null;

    [Fact]
    public void APackMergesFieldsKeepsTheOthersAndRemovesRows()
    {
        var tables = Tables("ItemConfig",
            ("1200100", """{"_iID":1200100,"_iSellPrice":500,"_kIcon":"item_1200100.png"}"""),
            ("1200101", """{"_iID":1200101,"_iSellPrice":700}"""));
        Pack("010-a.json", """
            { "name": "prices", "tables": { "ItemConfig": {
                "set": { "1200100": { "_iSellPrice": 0 } },
                "remove": ["1200101"] } } }
            """);

        var result = ContentPacks.Apply(_root, tables, NullLogger.Instance);

        Assert.Equal(1, result.Packs);
        Assert.Equal(1, result.Rows);
        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Problems);
        Assert.Equal("0", Field(tables["ItemConfig"], "1200100", "_iSellPrice"));
        Assert.Equal("item_1200100.png", Field(tables["ItemConfig"], "1200100", "_kIcon")); // untouched field kept
        Assert.Equal("1200100", Field(tables["ItemConfig"], "1200100", "_iID"));
        Assert.False(tables["ItemConfig"].ContainsKey("1200101"));
    }

    [Fact]
    public void LaterPacksWinAndNewRowsCanBeAdded()
    {
        var tables = Tables("TaskConfig", ("10105050", """{"_iID":10105050,"_iCompleteType":0}"""));
        Pack("010-a.json", """
            { "tables": { "TaskConfig": { "set": { "10105050": { "_iCompleteType": 1 } } } } }
            """);
        Pack("020-b.json", """
            { "tables": { "TaskConfig": {
                "set": {
                    "10105050": { "_iCompleteType": 2 },
                    "99999999": { "_iID": 99999999, "_iCompleteType": 3 } } } } }
            """);

        var result = ContentPacks.Apply(_root, tables, NullLogger.Instance);

        Assert.Equal(2, result.Packs);
        Assert.Empty(result.Problems);
        Assert.Equal("2", Field(tables["TaskConfig"], "10105050", "_iCompleteType"));
        Assert.Equal("3", Field(tables["TaskConfig"], "99999999", "_iCompleteType"));
    }

    [Fact]
    public void ABrokenPackIsSkippedWholeAndTheRestStillApply()
    {
        var tables = Tables("ItemConfig",
            ("1", """{"_iID":1,"_iSellPrice":10}"""),
            ("2", """{"_iID":2,"_iSellPrice":20}"""));
        Pack("010-broken.json", """{ "tables": { "ItemConfig": { "set": { "1": """);
        Pack("020-unknown.json", """
            { "tables": { "NoSuchTable": { "set": { "1": { "_iSellPrice": 0 } } } } }
            """);
        Pack("030-good.json", """
            { "tables": { "ItemConfig": { "set": { "2": { "_iSellPrice": 99 } } } } }
            """);

        var result = ContentPacks.Apply(_root, tables, NullLogger.Instance);

        Assert.Equal(1, result.Packs);                       // only the good pack
        Assert.Equal(2, result.Problems.Count); // invalid JSON + unknown table
        Assert.Equal("10", Field(tables["ItemConfig"], "1", "_iSellPrice")); // broken pack changed nothing
        Assert.Equal("99", Field(tables["ItemConfig"], "2", "_iSellPrice"));
    }

    [Fact]
    public void NoContentFolderIsNotAProblem()
    {
        var tables = Tables("ItemConfig", ("1", """{"_iID":1}"""));
        var result = ContentPacks.Apply(_root, tables, NullLogger.Instance);
        Assert.Equal(0, result.Packs);
        Assert.Empty(result.Problems);
        Assert.Single(tables["ItemConfig"]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
