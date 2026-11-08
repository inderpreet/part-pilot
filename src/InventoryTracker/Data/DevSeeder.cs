using System.Data.Common;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InventoryTracker.Data;

/// <summary>How much demo data <see cref="DevSeeder"/> creates. The defaults are the F06 performance targets.</summary>
public sealed record DemoSeedOptions(
    int Products = 10_000,
    int Suppliers = 200,
    int PurchaseOrders = 2_000,
    int Transactions = 1_000_000,
    int RandomSeed = 20260801);

/// <summary>What <see cref="DevSeeder.SeedAsync"/> did. <see cref="Message"/> explains a skip.</summary>
public sealed record DemoSeedResult(bool Seeded, string Message, int Products = 0, int Suppliers = 0, int PurchaseOrders = 0, int Transactions = 0);

/// <summary>
/// Fills an empty-ish development database with a believable, internally consistent data set (F06: 10k products, 200
/// suppliers, 2k purchase orders, 1M ledger rows) so list, dashboard and report queries can be measured. Development
/// only: the host calls it for <c>--seed-demo</c> and never in Production. Everything it creates is recognisable by a
/// <c>DEMO-</c> prefix, and running it twice does nothing the second time.
/// </summary>
/// <remarks>
/// The stock ledger is written with plain SQL in one transaction (a million EF entities would take minutes), so the
/// rules <c>StockService</c> normally enforces are kept here by construction: <c>QuantityAfter</c> follows posting
/// order, stock never goes negative, <c>Product.QuantityOnHand</c> equals the ledger sum, and purchase-order receipts
/// match <c>PurchaseOrderLine.QuantityReceived</c>.
/// </remarks>
public sealed class DevSeeder(AppDbContext db, TimeProvider? timeProvider = null)
{
    public const string Prefix = "DEMO-";

    private const int PreferredSupplierPercent = 80;
    private const int DeadProductPercent = 15;
    private const int LowProductPercent = 12;
    private const int HistoryDays = 540;
    private const int DeadDays = 120;

    private static readonly string[] Adjectives = ["Steel", "Brass", "Copper", "Nylon", "Rubber", "Zinc", "Carbon", "Titanium", "Ceramic", "Alloy"];
    private static readonly string[] Nouns = ["Bolt", "Washer", "Bracket", "Gasket", "Bearing", "Spring", "Valve", "Fitting", "Clip", "Sensor", "Relay", "Hose"];
    private static readonly int[] MinOrderQuantities = [1, 1, 1, 10, 25, 100];

    public async Task<DemoSeedResult> SeedAsync(DemoSeedOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DemoSeedOptions();
        if (options.Products < 1 || options.Suppliers < 1 || options.PurchaseOrders < 0 || options.Transactions < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Demo seed counts must be positive.");
        }

        var user = await db.Users.AsNoTracking().OrderBy(u => u.UserName).FirstOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return new DemoSeedResult(false, "Create the first account (open the app and finish setup), then start again with --seed-demo.");
        }

        if (await db.Products.AnyAsync(p => p.Sku.StartsWith(Prefix), cancellationToken))
        {
            return new DemoSeedResult(false, "Demo data is already there; delete the database to seed again.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var rng = new Random(options.RandomSeed);

        var wasDetecting = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var categoryIds = await AddCategoriesAsync(cancellationToken);
            var supplierIds = await AddSuppliersAsync(options.Suppliers, rng, cancellationToken);
            var products = await AddProductsAsync(options.Products, categoryIds, rng, cancellationToken);
            await AddSupplierLinksAsync(products, supplierIds, rng, cancellationToken);
            var receipts = await AddPurchaseOrdersAsync(options.PurchaseOrders, supplierIds, products, user.Id, now, rng, cancellationToken);
            var written = await AddLedgerAsync(options.Transactions, products, receipts, user.Id, now, rng, cancellationToken);

            return new DemoSeedResult(true, "Demo data created.", products.Count, supplierIds.Count, options.PurchaseOrders, written);
        }
        finally
        {
            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = wasDetecting;
        }
    }

    private async Task<List<int>> AddCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = Enumerable.Range(1, 20).Select(i => new Category { Name = $"Demo category {i:D2}" }).ToList();
        db.Categories.AddRange(categories);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return categories.Select(c => c.Id).ToList();
    }

    private async Task<List<int>> AddSuppliersAsync(int count, Random rng, CancellationToken cancellationToken)
    {
        var ids = new List<int>(count);
        foreach (var batch in Enumerable.Range(1, count).Chunk(500))
        {
            var suppliers = batch.Select(i => new Supplier
            {
                Name = $"Demo Supplier {i:D3}",
                Email = $"orders{i}@demo-supplier.example",
                IsActive = rng.Next(100) < 95,
            }).ToList();
            db.Suppliers.AddRange(suppliers);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            ids.AddRange(suppliers.Select(s => s.Id));
        }

        return ids;
    }

    private async Task<List<SeedProduct>> AddProductsAsync(int count, List<int> categoryIds, Random rng, CancellationToken cancellationToken)
    {
        var result = new List<SeedProduct>(count);
        foreach (var batch in Enumerable.Range(1, count).Chunk(1000))
        {
            var entities = new List<Product>(batch.Length);
            foreach (var i in batch)
            {
                var hasReorderPoint = rng.Next(100) < 70;
                var reorderPoint = hasReorderPoint ? rng.Next(5, 201) : 0;
                entities.Add(new Product
                {
                    Sku = $"{Prefix}{i:D5}",
                    Name = $"{Adjectives[rng.Next(Adjectives.Length)]} {Nouns[rng.Next(Nouns.Length)]} {i}",
                    CategoryId = categoryIds[rng.Next(categoryIds.Count)],
                    Unit = rng.Next(10) == 0 ? "box" : "each",
                    BinLocation = $"{(char)('A' + rng.Next(8))}-{rng.Next(1, 40):D2}",
                    ReorderPoint = reorderPoint,
                    ReorderQuantity = reorderPoint == 0 ? 0 : reorderPoint * 2,
                    UnitCostCents = rng.Next(50, 50_001),
                    IsActive = rng.Next(100) < 95,
                });
            }

            db.Products.AddRange(entities);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            result.AddRange(entities.Select(p => new SeedProduct(p.Id, p.ReorderPoint, p.UnitCostCents)));
        }

        return result;
    }

    private async Task AddSupplierLinksAsync(List<SeedProduct> products, List<int> supplierIds, Random rng, CancellationToken cancellationToken)
    {
        foreach (var batch in products.Chunk(1000))
        {
            var links = new List<ProductSupplier>();
            foreach (var product in batch)
            {
                var chosen = new HashSet<int>();
                var wanted = rng.Next(1, 4);
                while (chosen.Count < wanted)
                {
                    chosen.Add(supplierIds[rng.Next(supplierIds.Count)]);
                }

                var first = true;
                foreach (var supplierId in chosen)
                {
                    links.Add(new ProductSupplier
                    {
                        ProductId = product.Id,
                        SupplierId = supplierId,
                        UnitCostCents = Math.Max(1, product.UnitCostCents + rng.Next(-20, 21)),
                        LeadTimeDays = rng.Next(3, 46),
                        MinOrderQuantity = MinOrderQuantities[rng.Next(MinOrderQuantities.Length)],
                        IsPreferred = first && rng.Next(100) < PreferredSupplierPercent,
                    });
                    first = false;
                }
            }

            db.ProductSuppliers.AddRange(links);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Creates the orders and returns, per product, the receipts the ledger must contain for them.</summary>
    private async Task<Dictionary<int, List<SeedReceipt>>> AddPurchaseOrdersAsync(
        int count, List<int> supplierIds, List<SeedProduct> products, string userId, DateTime now, Random rng, CancellationToken cancellationToken)
    {
        var receipts = new Dictionary<int, List<SeedReceipt>>();
        var today = DateOnly.FromDateTime(now);

        foreach (var batch in Enumerable.Range(1, count).Chunk(500))
        {
            var orders = new List<PurchaseOrder>(batch.Length);
            var plans = new List<(PurchaseOrderLine Line, DateTime At)>();

            foreach (var i in batch)
            {
                var status = PickStatus(rng);
                var ageDays = rng.Next(1, 366);
                var orderDate = today.AddDays(-ageDays);
                var leadDays = rng.Next(7, 46);
                var submittedAt = status == PurchaseOrderStatus.Draft ? (DateTime?)null : now.AddDays(-ageDays);

                var order = new PurchaseOrder
                {
                    Number = $"{Prefix}PO-{i:D5}",
                    SupplierId = supplierIds[rng.Next(supplierIds.Count)],
                    Status = status,
                    OrderDate = orderDate,
                    ExpectedDate = rng.Next(10) == 0 ? null : orderDate.AddDays(leadDays),
                    SubmittedAt = submittedAt,
                    CreatedById = userId,
                };

                var lineCount = rng.Next(1, 7);
                var chosen = new HashSet<int>();
                while (chosen.Count < lineCount)
                {
                    chosen.Add(rng.Next(products.Count));
                }

                foreach (var index in chosen)
                {
                    var product = products[index];
                    var ordered = rng.Next(5, 501);
                    var received = status switch
                    {
                        PurchaseOrderStatus.Received => rng.Next(5) == 0 ? rng.Next(1, ordered + 1) : ordered,
                        PurchaseOrderStatus.PartiallyReceived => rng.Next(1, ordered),
                        _ => 0,
                    };
                    var line = new PurchaseOrderLine
                    {
                        ProductId = product.Id,
                        QuantityOrdered = ordered,
                        QuantityReceived = received,
                        UnitCostCents = product.UnitCostCents,
                    };
                    order.Lines.Add(line);
                    if (received > 0)
                    {
                        var at = submittedAt!.Value.AddDays(Math.Min(leadDays, ageDays) * rng.NextDouble());
                        plans.Add((line, at));
                    }
                }

                if (status is PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled)
                {
                    order.ClosedAt = submittedAt!.Value.AddDays(Math.Min(leadDays, ageDays));
                }

                orders.Add(order);
            }

            db.PurchaseOrders.AddRange(orders);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            foreach (var (line, at) in plans)
            {
                if (!receipts.TryGetValue(line.ProductId, out var list))
                {
                    receipts[line.ProductId] = list = [];
                }

                list.Add(new SeedReceipt(line.Id, line.QuantityReceived, line.UnitCostCents, at));
            }
        }

        return receipts;
    }

    private static PurchaseOrderStatus PickStatus(Random rng) => rng.Next(100) switch
    {
        < 5 => PurchaseOrderStatus.Draft,
        < 20 => PurchaseOrderStatus.Submitted,
        < 30 => PurchaseOrderStatus.PartiallyReceived,
        < 90 => PurchaseOrderStatus.Received,
        _ => PurchaseOrderStatus.Cancelled,
    };

    private async Task<int> AddLedgerAsync(
        int target, List<SeedProduct> products, Dictionary<int, List<SeedReceipt>> receipts, string userId, DateTime now, Random rng, CancellationToken cancellationToken)
    {
        var receiptTotal = receipts.Values.Sum(r => r.Count);
        var remaining = Math.Max(0, target - receiptTotal);
        var written = 0;
        var finalQuantities = new List<(int ProductId, int Quantity)>(products.Count);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        await using var insert = connection.CreateCommand();
        insert.Transaction = dbTransaction;
        insert.CommandText =
            """
            INSERT INTO "StockTransactions"
                ("ProductId", "Type", "QuantityDelta", "QuantityAfter", "UnitCostCents", "Reason", "Reference", "PurchaseOrderLineId", "UserId", "OccurredAt", "CreatedAt")
            VALUES ($product, $type, $delta, $after, $cost, $reason, $reference, $line, $user, $at, $at)
            """;
        var pProduct = Add(insert, "$product");
        var pType = Add(insert, "$type");
        var pDelta = Add(insert, "$delta");
        var pAfter = Add(insert, "$after");
        var pCost = Add(insert, "$cost");
        var pReason = Add(insert, "$reason");
        var pReference = Add(insert, "$reference");
        var pLine = Add(insert, "$line");
        var pUser = Add(insert, "$user");
        var pAt = Add(insert, "$at");
        pUser.Value = userId;
        await insert.PrepareAsync(cancellationToken);

        for (var k = 0; k < products.Count; k++)
        {
            var product = products[k];
            var left = products.Count - k;
            var average = remaining / left;
            var extra = left == 1 ? remaining : Math.Min(remaining, rng.Next(average / 2, average * 3 / 2 + 1));
            remaining -= extra;

            var dead = rng.Next(100) < DeadProductPercent;
            var drain = !dead && product.ReorderPoint > 0 && rng.Next(100) < LowProductPercent;
            var events = BuildEvents(receipts.GetValueOrDefault(product.Id), extra, dead, now, rng);

            var quantity = 0;
            for (var e = 0; e < events.Count; e++)
            {
                var ev = events[e];
                var type = ev.Type;
                int delta;
                string? reason = null;

                if (ev.Receipt is { } receipt)
                {
                    delta = receipt.Quantity;
                }
                else if (drain && e == events.Count - 1)
                {
                    // End the history below the reorder point so the low-stock and out-of-stock queries have rows.
                    var targetQuantity = rng.Next(4) == 0 ? 0 : rng.Next(0, product.ReorderPoint + 1);
                    if (targetQuantity == quantity)
                    {
                        targetQuantity = quantity + 1;
                    }

                    type = StockTransactionType.Count;
                    delta = targetQuantity - quantity;
                    reason = "Cycle count";
                }
                else
                {
                    (type, delta, reason) = GenericMove(quantity, rng);
                }

                quantity += delta;
                pProduct.Value = product.Id;
                pType.Value = type.ToString();
                pDelta.Value = delta;
                pAfter.Value = quantity;
                pCost.Value = type == StockTransactionType.Receive ? ev.Receipt?.UnitCostCents ?? product.UnitCostCents : DBNull.Value;
                pReason.Value = reason is null ? DBNull.Value : reason;
                pReference.Value = type == StockTransactionType.Issue ? $"JOB-{rng.Next(1000, 9999)}" : DBNull.Value;
                pLine.Value = ev.Receipt is { } r ? r.LineId : DBNull.Value;
                pAt.Value = ev.At;
                await insert.ExecuteNonQueryAsync(cancellationToken);
                written++;
            }

            finalQuantities.Add((product.Id, quantity));
        }

        await using var update = connection.CreateCommand();
        update.Transaction = dbTransaction;
        update.CommandText = "UPDATE \"Products\" SET \"QuantityOnHand\" = $quantity WHERE \"Id\" = $id";
        var uQuantity = Add(update, "$quantity");
        var uId = Add(update, "$id");
        foreach (var (productId, quantity) in finalQuantities)
        {
            uId.Value = productId;
            uQuantity.Value = quantity;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    private static List<SeedEvent> BuildEvents(List<SeedReceipt>? receipts, int extra, bool dead, DateTime now, Random rng)
    {
        var newest = dead ? now.AddDays(-DeadDays) : now;
        var oldest = now.AddDays(-HistoryDays);
        var span = (newest - oldest).TotalSeconds;

        var events = new List<SeedEvent>((receipts?.Count ?? 0) + extra);
        foreach (var receipt in receipts ?? [])
        {
            events.Add(new SeedEvent(StockTransactionType.Receive, receipt.At, receipt));
        }

        for (var i = 0; i < extra; i++)
        {
            events.Add(new SeedEvent(StockTransactionType.Issue, oldest.AddSeconds(span * rng.NextDouble()), null));
        }

        // Moves other than receipts are placeholders: their kind and size are chosen while writing, against the running quantity.
        events.Sort((a, b) => a.At.CompareTo(b.At));
        return events;
    }

    /// <summary>A believable next movement given the current on-hand quantity; never makes it negative, never has delta 0.</summary>
    private static (StockTransactionType Type, int Delta, string? Reason) GenericMove(int quantity, Random rng)
    {
        if (quantity <= 5)
        {
            return (StockTransactionType.Receive, rng.Next(50, 301), null);
        }

        var roll = rng.Next(100);
        if (roll < 60)
        {
            return (StockTransactionType.Issue, -rng.Next(1, Math.Min(quantity, 40) + 1), null);
        }

        if (roll < 80)
        {
            return (StockTransactionType.Receive, rng.Next(20, 201), null);
        }

        if (roll < 90)
        {
            return (StockTransactionType.Return, rng.Next(1, 6), "Returned unused");
        }

        if (roll < 95)
        {
            return rng.Next(2) == 0
                ? (StockTransactionType.Adjust, rng.Next(1, 6), "Found")
                : (StockTransactionType.Adjust, -rng.Next(1, Math.Min(quantity, 5) + 1), "Damaged");
        }

        var counted = Math.Max(0, quantity + rng.Next(-3, 4));
        return counted == quantity
            ? (StockTransactionType.Count, -1, "Cycle count")
            : (StockTransactionType.Count, counted - quantity, "Cycle count");
    }

    private static DbParameter Add(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private sealed record SeedProduct(int Id, int ReorderPoint, long UnitCostCents);

    private sealed record SeedReceipt(long LineId, int Quantity, long UnitCostCents, DateTime At);

    private sealed record SeedEvent(StockTransactionType Type, DateTime At, SeedReceipt? Receipt);
}
