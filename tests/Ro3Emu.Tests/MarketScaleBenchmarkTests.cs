using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>
/// Baseline measurement of current <c>market_record</c> full-collection capture and guard cost at
/// 1k / 3k / 10k trade listings (P3.2a benchmark before moving <c>trade_listing</c> to its own indexed table).
/// </summary>
public sealed class MarketScaleBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-market-bench-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void MeasureMarketRecordScalingAt1k3kAnd10kListings()
    {
        Directory.CreateDirectory(_dir);
        using var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));

        var measurements = new List<(int Listings, double ReadMs, int GuardBytes)>();
        int seeded = 0;
        foreach (int target in new[] { 1_000, 3_000, 10_000 })
        {
            SeedListings(accounts, seeded, target);
            seeded = target;

            // Warm read, then measure snapshot read + full-collection guard plan size.
            _ = accounts.ReadSync<MarketRecordSnapshot>(new MarketRecordSnapshotReadPlan(TradeService.Listings));
            var sw = Stopwatch.StartNew();
            var snapshot = accounts.ReadSync<MarketRecordSnapshot>(new MarketRecordSnapshotReadPlan(TradeService.Listings));
            sw.Stop();
            var guard = new MarketRecordGuardPlan(TradeService.Listings, null, snapshot);
            Assert.Equal(target, snapshot.Rows.Count);
            measurements.Add((target, sw.Elapsed.TotalMilliseconds, guard.Bytes));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"market_record listings={target}: snapshotRead={sw.Elapsed.TotalMilliseconds:F2} ms, guardBytes={guard.Bytes:N0} (writerMax={DbWriter.MaxDurableBytes:N0})"));
        }

        // Document the linear growth of full-collection market_record guard bytes with listing count.
        Assert.True(measurements[1].GuardBytes > measurements[0].GuardBytes * 2);
        Assert.True(measurements[2].GuardBytes > measurements[1].GuardBytes * 2);
        Assert.True(measurements[2].GuardBytes > DbWriter.MaxDurableBytes,
            $"Expected 10k full-collection guard ({measurements[2].GuardBytes:N0} B) to exceed writer durable limit ({DbWriter.MaxDurableBytes:N0} B).");
    }

    private static void SeedListings(AccountStore accounts, int fromExclusive, int toInclusive)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = accounts.DatabasePath, Pooling = false }.ToString());
        db.Open();
        using var tx = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO market_record(kind, id, data) VALUES($k, $i, $d)";
        var pKind = insert.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text);
        var pId = insert.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Text);
        var pData = insert.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Text);
        pKind.Value = TradeService.Listings;

        for (int i = fromExclusive + 1; i <= toInclusive; i++)
        {
            string sku = "sku-" + i.ToString("D6", CultureInfo.InvariantCulture);
            int entry = 1004000 + (i % 250);
            var listing = new TradeService.Listing
            {
                Sku = sku,
                Spu = "spu-" + entry.ToString(CultureInfo.InvariantCulture),
                SellerId = 100_000 + (i % 500),
                Entry = entry,
                Currency = 2,
                UnitPrice = 1000 + (i % 100) * 10,
                Quantity = 1 + (i % 5),
                SaleTimeMs = nowMs - i * 1000L,
                PublicUntilMs = 0,
                ExpiresAtMs = nowMs + 86_400_000L,
                Item = new InvItem { Guid = i, Entry = entry, Stack = 1 + (i % 5), Pack = 1, Sort = 1 },
            };
            pId.Value = sku;
            pData.Value = JsonSerializer.Serialize(listing);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
