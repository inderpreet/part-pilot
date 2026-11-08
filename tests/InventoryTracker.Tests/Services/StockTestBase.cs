using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>Settings stand-in: only the negative-stock switch matters to <see cref="StockService"/>.</summary>
public sealed class FakeSettings : ISettingsService
{
    public bool AllowNegative { get; set; }

    public Task<bool> GetBoolAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(key == SettingKeys.StockAllowNegative && AllowNegative);

    public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> GetIntAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> SetAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> SetAsync(string key, bool value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> SetAsync(string key, int value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<BusinessSettings> GetBusinessSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<BusinessSettings>> UpdateBusinessSettingsAsync(BusinessSettings settings, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>Catalog test database plus a user to post as, a controllable clock, and stock helpers.</summary>
public abstract class StockTestBase : CatalogTestBase, IAsyncLifetime
{
    protected string UserId { get; private set; } = null!;

    protected FakeSettings Settings { get; } = new();

    protected TestClock Clock { get; } = new(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc));

    protected StockContext Who => new(UserId);

    async Task IAsyncLifetime.InitializeAsync()
    {
        await InitializeAsync();
        await using var context = Db.CreateContext();
        var user = new ApplicationUser { UserName = "mover", DisplayName = "Mo Ver" };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        UserId = user.Id;
    }

    /// <summary>A new context + stock service, like one web request.</summary>
    protected (AppDbContext Context, StockService Stock) NewStock()
    {
        var (context, _, _) = NewRequest();
        return (context, new StockService(context, Settings, Clock, NullLogger<StockService>.Instance));
    }

    protected async Task<Result<IReadOnlyList<PostedMovement>>> PostAsync(params StockMovement[] lines)
    {
        var (context, stock) = NewStock();
        await using (context)
        {
            return await stock.PostAsync(lines, Who);
        }
    }

    protected async Task<Result<PostedMovement>> ReverseAsync(long id, string? note = null)
    {
        var (context, stock) = NewStock();
        await using (context)
        {
            return await stock.ReverseAsync(id, Who, note);
        }
    }

    protected async Task<int> NewProductAsync(string sku, int onHand = 0, bool archived = false)
    {
        var id = (await CreateProductAsync(Input(sku: sku, name: "Product " + sku))).Id;
        if (onHand != 0)
        {
            Assert.True((await PostAsync(Receive(id, onHand))).Succeeded);
        }

        if (archived)
        {
            await ArchiveAsync(id);
        }

        return id;
    }

    protected static StockMovement Receive(int productId, int quantity, string? reference = null)
        => new(productId, StockTransactionType.Receive, quantity, Reference: reference);

    protected static StockMovement Issue(int productId, int quantity)
        => new(productId, StockTransactionType.Issue, quantity);

    protected static StockMovement Adjust(int productId, int signedQuantity, string? reason = "Damaged")
        => new(productId, StockTransactionType.Adjust, signedQuantity, Reason: reason);

    protected static StockMovement Count(int productId, int counted)
        => new(productId, StockTransactionType.Count, counted);

    protected async Task<int> OnHandAsync(int productId)
    {
        await using var context = Db.CreateContext();
        return await context.Products.Where(p => p.Id == productId).Select(p => p.QuantityOnHand).SingleAsync();
    }

    protected async Task<List<StockTransaction>> LedgerAsync(int? productId = null)
    {
        await using var context = Db.CreateContext();
        var rows = context.StockTransactions.AsNoTracking().AsQueryable();
        if (productId is { } id)
        {
            rows = rows.Where(t => t.ProductId == id);
        }

        return await rows.OrderBy(t => t.Id).ToListAsync();
    }

    /// <summary>Invariants 1 and 2: the cache is the ledger sum, and each row's QuantityAfter is the running total in posting order.</summary>
    protected async Task AssertConsistentAsync(int productId)
    {
        var running = 0;
        foreach (var row in await LedgerAsync(productId))
        {
            running += row.QuantityDelta;
            Assert.Equal(running, row.QuantityAfter);
        }

        Assert.Equal(running, await OnHandAsync(productId));
    }

    protected static void AssertFailed(Result result, string code, string? field = null)
    {
        Assert.False(result.Succeeded, "Expected a failure.");
        var error = Assert.Single(result.Errors, e => e.Code == code);
        if (field is not null)
        {
            Assert.Equal(field, error.Field);
        }
    }
}
