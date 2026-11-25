using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed record TradeListingPage(List<string> Rows, int TotalCount);

internal sealed record TradeListingSummary(int Entry, int SpecialType, long Quantity, int MinPrice, int MaxPrice);

internal sealed record TradeListingDueSnapshot(List<(string Sku, string Data)> Matured,
    List<(string Sku, string Data)> Expired, long NextDueMs);

internal static class TradeListingSql
{
    internal static void CreateSchema(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS trade_listing (
                sku TEXT PRIMARY KEY,
                spu TEXT NOT NULL,
                seller_id INTEGER NOT NULL,
                entry INTEGER NOT NULL,
                unit_price INTEGER NOT NULL,
                quantity INTEGER NOT NULL,
                sale_time_ms INTEGER NOT NULL,
                public_until_ms INTEGER NOT NULL,
                expires_at_ms INTEGER NOT NULL,
                special_type INTEGER NOT NULL,
                data TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS trade_listing_spu ON trade_listing(spu, unit_price, sku);
            CREATE INDEX IF NOT EXISTS trade_listing_entry ON trade_listing(entry, unit_price, sale_time_ms, sku);
            CREATE INDEX IF NOT EXISTS trade_listing_seller ON trade_listing(seller_id, sku);
            CREATE INDEX IF NOT EXISTS trade_listing_expires ON trade_listing(expires_at_ms);
            CREATE INDEX IF NOT EXISTS trade_listing_public ON trade_listing(public_until_ms);
            """);
    }

    internal static void Upsert(SqliteConnection db, SqliteTransaction tx, string sku, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string spu = root.TryGetProperty("Spu", out var spuProp) ? spuProp.GetString() ?? "" : "";
        long sellerId = root.TryGetProperty("SellerId", out var sellerProp) ? sellerProp.GetInt64() : 0;
        int entry = root.TryGetProperty("Entry", out var entryProp) ? entryProp.GetInt32() : 0;
        int unitPrice = root.TryGetProperty("UnitPrice", out var priceProp) ? priceProp.GetInt32() : 0;
        int quantity = root.TryGetProperty("Quantity", out var qtyProp) ? qtyProp.GetInt32() : 0;
        long saleTimeMs = root.TryGetProperty("SaleTimeMs", out var saleProp) ? saleProp.GetInt64() : 0;
        long publicUntilMs = root.TryGetProperty("PublicUntilMs", out var pubProp) ? pubProp.GetInt64() : 0;
        long expiresAtMs = root.TryGetProperty("ExpiresAtMs", out var expProp) ? expProp.GetInt64() : 0;
        int specialType = root.TryGetProperty("SpecialType", out var specProp) ? specProp.GetInt32() : 0;

        using var cmd = DbSql.Command(db, tx, """
            INSERT INTO trade_listing(sku, spu, seller_id, entry, unit_price, quantity, sale_time_ms,
                public_until_ms, expires_at_ms, special_type, data)
            VALUES($sku, $spu, $seller, $entry, $price, $qty, $sale, $pub, $exp, $spec, $data)
            ON CONFLICT(sku) DO UPDATE SET
                spu=excluded.spu, seller_id=excluded.seller_id, entry=excluded.entry,
                unit_price=excluded.unit_price, quantity=excluded.quantity, sale_time_ms=excluded.sale_time_ms,
                public_until_ms=excluded.public_until_ms, expires_at_ms=excluded.expires_at_ms,
                special_type=excluded.special_type, data=excluded.data
            """);
        cmd.Parameters.AddWithValue("$sku", sku);
        cmd.Parameters.AddWithValue("$spu", spu);
        cmd.Parameters.AddWithValue("$seller", sellerId);
        cmd.Parameters.AddWithValue("$entry", entry);
        cmd.Parameters.AddWithValue("$price", unitPrice);
        cmd.Parameters.AddWithValue("$qty", quantity);
        cmd.Parameters.AddWithValue("$sale", saleTimeMs);
        cmd.Parameters.AddWithValue("$pub", publicUntilMs);
        cmd.Parameters.AddWithValue("$exp", expiresAtMs);
        cmd.Parameters.AddWithValue("$spec", specialType);
        cmd.Parameters.AddWithValue("$data", json);
        cmd.ExecuteNonQuery();
    }

    internal static void MigrateFromMarketRecord(SqliteConnection db, SqliteTransaction tx)
    {
        CreateSchema(db, tx);
        var legacy = new List<(string Sku, string Data)>();
        using (var read = DbSql.Command(db, tx, "SELECT id, data FROM market_record WHERE kind='trade_listing' ORDER BY id"))
        using (var row = read.ExecuteReader())
            while (row.Read()) legacy.Add((row.GetString(0), row.GetString(1)));
        if (legacy.Count == 0) return;

        foreach (var (sku, data) in legacy)
            Upsert(db, tx, sku, data);

        using var del = DbSql.Command(db, tx, "DELETE FROM market_record WHERE kind='trade_listing'");
        del.ExecuteNonQuery();
    }
}

internal sealed class TradeListingBySellerReadPlan(long sellerId) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "trade/read-by-seller";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "SELECT sku, data FROM trade_listing WHERE seller_id=$s AND quantity>0 ORDER BY sale_time_ms, sku");
        cmd.Parameters.AddWithValue("$s", sellerId);
        using var row = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (row.Read()) list.Add((row.GetString(0), row.GetString(1)));
        return list;
    }
}

internal sealed class TradeListingSellerCountReadPlan(long sellerId) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "trade/count-by-seller";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "SELECT COUNT(*) FROM trade_listing WHERE seller_id=$s AND quantity>0");
        cmd.Parameters.AddWithValue("$s", sellerId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

internal sealed class TradeListingSellerCountGuardPlan(long sellerId, int expectedCount) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "trade/guard-seller-count";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "SELECT COUNT(*) FROM trade_listing WHERE seller_id=$s AND quantity>0");
        cmd.Parameters.AddWithValue("$s", sellerId);
        int actual = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (actual != expectedCount)
            throw new InvalidOperationException("Seller trade listing count changed during transaction capture.");
        return true;
    }
}

internal sealed class TradeListingBySpuReadPlan(string spu, int? unitPrice = null) : DbReadPlan
{
    internal override int Bytes => 128 + spu.Length * 2;
    internal override string Label => "trade/read-by-spu";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, unitPrice.HasValue
            ? "SELECT sku, data FROM trade_listing WHERE spu=$spu AND unit_price=$p AND quantity>0 ORDER BY sale_time_ms, sku"
            : "SELECT sku, data FROM trade_listing WHERE spu=$spu AND quantity>0 ORDER BY unit_price, sale_time_ms, sku");
        cmd.Parameters.AddWithValue("$spu", spu);
        if (unitPrice.HasValue) cmd.Parameters.AddWithValue("$p", unitPrice.Value);
        using var row = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (row.Read()) list.Add((row.GetString(0), row.GetString(1)));
        return list;
    }
}

internal sealed class TradeListingBySkusReadPlan(string[] skus) : DbReadPlan
{
    internal override int Bytes => 128 + skus.Sum(s => s.Length * 2 + 8);
    internal override string Label => "trade/read-by-skus";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var list = new List<(string, string)>();
        if (skus.Length == 0) return list;
        foreach (var chunk in skus.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            string placeholders = string.Join(',', chunk.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)));
            using var cmd = DbSql.Command(db, tx,
                $"SELECT sku, data FROM trade_listing WHERE sku IN ({placeholders}) AND quantity>0 ORDER BY sku");
            for (int i = 0; i < chunk.Length; i++)
                cmd.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), chunk[i]);
            using var row = cmd.ExecuteReader();
            while (row.Read()) list.Add((row.GetString(0), row.GetString(1)));
        }
        return list;
    }
}

internal sealed class TradeListingPageReadPlan(int? entry, int? specialType, long? sellerId, string? sku,
    bool publicity, long nowMs, bool priceDesc, int pageNo, int pageSize) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "trade/read-page";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        List<string> clauses =
        [
            "quantity>0",
            publicity ? "public_until_ms>$now" : "public_until_ms<=$now",
        ];
        if (entry is > 0) clauses.Add("entry=$entry");
        if (specialType is > 0) clauses.Add("special_type=$spec");
        if (sellerId.HasValue) clauses.Add("seller_id=$seller");
        if (!string.IsNullOrEmpty(sku)) clauses.Add("sku=$sku");
        string where = string.Join(" AND ", clauses);

        int total;
        using (var countCmd = DbSql.Command(db, tx, $"SELECT COUNT(*) FROM trade_listing WHERE {where}"))
        {
            BindFilters(countCmd);
            total = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var rows = new List<string>();
        if (total > 0)
        {
            string order = priceDesc ? "unit_price DESC, sale_time_ms ASC, sku ASC" : "unit_price ASC, sale_time_ms ASC, sku ASC";
            using var select = DbSql.Command(db, tx,
                $"SELECT data FROM trade_listing WHERE {where} ORDER BY {order} LIMIT $limit OFFSET $offset");
            BindFilters(select);
            select.Parameters.AddWithValue("$limit", pageSize);
            select.Parameters.AddWithValue("$offset", (pageNo - 1) * pageSize);
            using var reader = select.ExecuteReader();
            while (reader.Read()) rows.Add(reader.GetString(0));
        }
        return new TradeListingPage(rows, total);
    }

    private void BindFilters(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$now", nowMs);
        if (entry is > 0) cmd.Parameters.AddWithValue("$entry", entry.Value);
        if (specialType is > 0) cmd.Parameters.AddWithValue("$spec", specialType.Value);
        if (sellerId.HasValue) cmd.Parameters.AddWithValue("$seller", sellerId.Value);
        if (!string.IsNullOrEmpty(sku)) cmd.Parameters.AddWithValue("$sku", sku);
    }
}

internal sealed class TradeListingInventoryReadPlan(int[] entries, int? specialType, bool publicity, long nowMs) : DbReadPlan
{
    internal override int Bytes => 128 + entries.Length * 4;
    internal override string Label => "trade/read-inventory";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var summaries = new Dictionary<(int Entry, int SpecialType), TradeListingSummary>();
        if (entries.Length == 0) return summaries;
        string pubClause = publicity ? "public_until_ms>$now" : "public_until_ms<=$now";
        string specClause = specialType is > 0 ? " AND special_type=$spec" : "";
        foreach (var chunk in entries.Distinct().Chunk(500))
        {
            string placeholders = string.Join(',', chunk.Select((_, i) => "$e" + i.ToString(CultureInfo.InvariantCulture)));
            using var cmd = DbSql.Command(db, tx, $"""
                SELECT entry, {(specialType is > 0 ? "special_type" : "0")}, SUM(quantity), MIN(unit_price), MAX(unit_price)
                FROM trade_listing
                WHERE entry IN ({placeholders}) AND quantity>0 AND {pubClause}{specClause}
                GROUP BY entry{(specialType is > 0 ? ", special_type" : "")}
                """);
            cmd.Parameters.AddWithValue("$now", nowMs);
            if (specialType is > 0) cmd.Parameters.AddWithValue("$spec", specialType.Value);
            for (int i = 0; i < chunk.Length; i++)
                cmd.Parameters.AddWithValue("$e" + i.ToString(CultureInfo.InvariantCulture), chunk[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int e = reader.GetInt32(0);
                int s = reader.GetInt32(1);
                summaries[(e, s)] = new TradeListingSummary(e, s, reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4));
            }
        }
        return summaries;
    }
}

internal sealed class TradeListingDueReadPlan(long nowMs, int limit = 256) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "trade/read-due";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var matured = new List<(string Sku, string Data)>();
        using (var cmd = DbSql.Command(db, tx, """
            SELECT sku, data FROM trade_listing
            WHERE public_until_ms>0 AND public_until_ms<=$now AND quantity>0
            ORDER BY public_until_ms, sku LIMIT $limit
            """))
        {
            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var row = cmd.ExecuteReader();
            while (row.Read()) matured.Add((row.GetString(0), row.GetString(1)));
        }

        var expired = new List<(string Sku, string Data)>();
        using (var cmd = DbSql.Command(db, tx, """
            SELECT sku, data FROM trade_listing
            WHERE expires_at_ms>0 AND expires_at_ms<=$now AND quantity>0
            ORDER BY expires_at_ms, sku LIMIT $limit
            """))
        {
            cmd.Parameters.AddWithValue("$now", nowMs);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var row = cmd.ExecuteReader();
            while (row.Read()) expired.Add((row.GetString(0), row.GetString(1)));
        }

        long nextDue = long.MaxValue;
        using (var nextPub = DbSql.Command(db, tx,
            "SELECT MIN(public_until_ms) FROM trade_listing WHERE public_until_ms>$now AND quantity>0"))
        {
            nextPub.Parameters.AddWithValue("$now", nowMs);
            if (nextPub.ExecuteScalar() is long p && p > 0) nextDue = Math.Min(nextDue, p);
        }
        using (var nextExp = DbSql.Command(db, tx,
            "SELECT MIN(expires_at_ms) FROM trade_listing WHERE expires_at_ms>$now AND quantity>0"))
        {
            nextExp.Parameters.AddWithValue("$now", nowMs);
            if (nextExp.ExecuteScalar() is long e && e > 0) nextDue = Math.Min(nextDue, e);
        }
        if (matured.Count == limit || expired.Count == limit) nextDue = nowMs;
        return new TradeListingDueSnapshot(matured, expired, nextDue);
    }
}

internal sealed class TradeHistoryPageReadPlan(long? buyerId, long? sellerId, int[] entries, int pageNo, int pageSize) : DbReadPlan
{
    internal override int Bytes => 256 + entries.Length * 4;
    internal override string Label => "trade/read-history-page";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        List<string> clauses = ["kind='trade_history'"];
        if (buyerId.HasValue) clauses.Add("CAST(json_extract(data, '$.BuyerId') AS INTEGER)=$buyer");
        if (sellerId.HasValue) clauses.Add("CAST(json_extract(data, '$.SellerId') AS INTEGER)=$seller");
        if (entries.Length > 0)
        {
            string placeholders = string.Join(',', entries.Select((_, i) => "$e" + i.ToString(CultureInfo.InvariantCulture)));
            clauses.Add($"CAST(json_extract(data, '$.Entry') AS INTEGER) IN ({placeholders})");
        }
        string where = string.Join(" AND ", clauses);
        using var cmd = DbSql.Command(db, tx, $"""
            SELECT data FROM market_record
            WHERE {where}
            ORDER BY CAST(json_extract(data, '$.TimeMs') AS INTEGER) DESC, id DESC
            LIMIT $limit OFFSET $offset
            """);
        if (buyerId.HasValue) cmd.Parameters.AddWithValue("$buyer", buyerId.Value);
        if (sellerId.HasValue) cmd.Parameters.AddWithValue("$seller", sellerId.Value);
        for (int i = 0; i < entries.Length; i++)
            cmd.Parameters.AddWithValue("$e" + i.ToString(CultureInfo.InvariantCulture), entries[i]);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (pageNo - 1) * pageSize);
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }
}
