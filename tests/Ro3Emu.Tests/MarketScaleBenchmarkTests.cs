using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void MigrationV4MovesLegacyMarketRecordListingsIntoIndexedTradeListingTable()
    {
        Directory.CreateDirectory(_dir);
        string dbPath;
        using (var first = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir })))
            dbPath = first.DatabasePath;

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            db.Open();
            using var tx = db.BeginTransaction();
            using (var drop = db.CreateCommand())
            {
                drop.Transaction = tx;
                drop.CommandText = "DROP TABLE IF EXISTS trade_listing; PRAGMA user_version = 3;";
                drop.ExecuteNonQuery();
            }
            using (var insert = db.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO market_record(kind, id, data) VALUES($k, $i, $d)";
                var pKind = insert.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text);
                var pId = insert.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Text);
                var pData = insert.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Text);
                pKind.Value = TradeService.Listings;
                for (int i = 1; i <= 25; i++)
                {
                    var listing = MakeListing(i, nowMs);
                    pId.Value = listing.Sku;
                    pData.Value = JsonSerializer.Serialize(listing);
                    insert.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }

        using var migrated = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        var page = migrated.ReadSync<TradeListingPage>(new TradeListingPageReadPlan(
            entry: null, specialType: null, sellerId: null, sku: null, publicity: false, nowMs: nowMs, priceDesc: false, pageNo: 1, pageSize: 10));
        Assert.Equal(25, page.TotalCount);
        Assert.Equal(10, page.Rows.Count);

        using var verify = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        verify.Open();
        using var versionCmd = verify.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version";
        Assert.Equal(DatabaseMigrations.CurrentVersion, Convert.ToInt32(versionCmd.ExecuteScalar(), CultureInfo.InvariantCulture));
        using var legacyCmd = verify.CreateCommand();
        legacyCmd.CommandText = "SELECT COUNT(*) FROM market_record WHERE kind='trade_listing'";
        Assert.Equal(0, Convert.ToInt32(legacyCmd.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task IndexedTradeListingTableScalesTo20kWithConstantPlanSizeAndFastLoopP99()
    {
        Directory.CreateDirectory(_dir);
        using var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SeedIndexedListings(accounts.DatabasePath, 20_000, nowMs);

        // Per-row and per-seller guards stay O(1) at 20,000 listings, far below DbWriter.MaxDurableBytes.
        const long sellerId = 100_005;
        int sellerCount = accounts.ReadSync<int>(new TradeListingSellerCountReadPlan(sellerId));
        Assert.Equal(40, sellerCount);
        var countGuard = new TradeListingSellerCountGuardPlan(sellerId, sellerCount);
        Assert.Equal(128, countGuard.Bytes);

        var singleSnap = accounts.ReadSync<MarketRecordSnapshot>(new MarketRecordSnapshotReadPlan(TradeService.Listings, "sku-000001"));
        Assert.Single(singleSnap.Rows);
        var singleGuard = new MarketRecordGuardPlan(TradeService.Listings, "sku-000001", singleSnap);
        Assert.True(singleGuard.Bytes < 4096, $"Expected single-row guard < 4 KB, got {singleGuard.Bytes} B.");

        // Measure indexed page/seller/spu/inventory/due queries on a GameLoop at 20,000 rows.
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        await loop.StartAsync(CancellationToken.None);
        var samplesMs = new List<double>();
        try
        {
            // Warm up SQLite prepared statements and page cache once before measuring p50/p99.
            _ = accounts.ReadSync<TradeListingDueSnapshot>(new TradeListingDueReadPlan(nowMs));
            for (int i = 0; i < 60; i++)
            {
                double elapsed = 0;
                int index = i;
                await loop.InvokeAsync(() =>
                {
                    var sw = Stopwatch.StartNew();
                    switch (index % 5)
                    {
                        case 0:
                            {
                                var page = accounts.ReadSync<TradeListingPage>(new TradeListingPageReadPlan(
                                    entry: 1004005, specialType: null, sellerId: null, sku: null,
                                    publicity: false, nowMs: nowMs, priceDesc: false, pageNo: 1, pageSize: 20));
                                Assert.Equal(80, page.TotalCount);
                                Assert.Equal(20, page.Rows.Count);
                                break;
                            }
                        case 1:
                            {
                                var rows = accounts.ReadSync<List<(string Sku, string Data)>>(new TradeListingBySellerReadPlan(sellerId));
                                Assert.Equal(40, rows.Count);
                                break;
                            }
                        case 2:
                            {
                                var rows = accounts.ReadSync<List<(string Sku, string Data)>>(new TradeListingBySpuReadPlan("spu-1004005", 1050));
                                Assert.NotEmpty(rows);
                                break;
                            }
                        case 3:
                            {
                                var inv = accounts.ReadSync<Dictionary<(int Entry, int SpecialType), TradeListingSummary>>(
                                    new TradeListingInventoryReadPlan([1004001, 1004005, 1004010], null, false, nowMs));
                                Assert.Equal(3, inv.Count);
                                break;
                            }
                        default:
                            {
                                var due = accounts.ReadSync<TradeListingDueSnapshot>(new TradeListingDueReadPlan(nowMs));
                                Assert.Empty(due.Matured);
                                Assert.Empty(due.Expired);
                                Assert.True(due.NextDueMs > nowMs);
                                break;
                            }
                    }
                    sw.Stop();
                    elapsed = sw.Elapsed.TotalMilliseconds;
                    return Task.CompletedTask;
                });
                samplesMs.Add(elapsed);
            }
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }

        samplesMs.Sort();
        double p50 = samplesMs[samplesMs.Count / 2];
        double p99 = samplesMs[(int)Math.Ceiling(samplesMs.Count * 0.99) - 1];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"trade_listing 20k: countGuard={countGuard.Bytes} B, singleGuard={singleGuard.Bytes} B, loop p50={p50:F2} ms, p99={p99:F2} ms"));
        Assert.True(p99 < 50.0, $"Expected 20k indexed query loop p99 < 50 ms, got {p99:F2} ms.");
    }

    private static TradeService.Listing MakeListing(int i, long nowMs)
    {
        string sku = "sku-" + i.ToString("D6", CultureInfo.InvariantCulture);
        int entry = 1004000 + (i % 250);
        return new TradeService.Listing
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
    }

    private static void SeedIndexedListings(string dbPath, int count, long nowMs)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        db.Open();
        using var tx = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO trade_listing(sku, spu, seller_id, entry, unit_price, quantity, sale_time_ms,
                public_until_ms, expires_at_ms, special_type, data)
            VALUES($sku, $spu, $seller, $entry, $price, $qty, $sale, $pub, $exp, $spec, $data)
            """;
        var pSku = insert.Parameters.Add("$sku", Microsoft.Data.Sqlite.SqliteType.Text);
        var pSpu = insert.Parameters.Add("$spu", Microsoft.Data.Sqlite.SqliteType.Text);
        var pSeller = insert.Parameters.Add("$seller", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pEntry = insert.Parameters.Add("$entry", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pPrice = insert.Parameters.Add("$price", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pQty = insert.Parameters.Add("$qty", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pSale = insert.Parameters.Add("$sale", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pPub = insert.Parameters.Add("$pub", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pExp = insert.Parameters.Add("$exp", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pSpec = insert.Parameters.Add("$spec", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pData = insert.Parameters.Add("$data", Microsoft.Data.Sqlite.SqliteType.Text);

        for (int i = 1; i <= count; i++)
        {
            var listing = MakeListing(i, nowMs);
            pSku.Value = listing.Sku;
            pSpu.Value = listing.Spu;
            pSeller.Value = listing.SellerId;
            pEntry.Value = listing.Entry;
            pPrice.Value = listing.UnitPrice;
            pQty.Value = listing.Quantity;
            pSale.Value = listing.SaleTimeMs;
            pPub.Value = listing.PublicUntilMs;
            pExp.Value = listing.ExpiresAtMs;
            pSpec.Value = listing.SpecialType;
            pData.Value = JsonSerializer.Serialize(listing);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
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
