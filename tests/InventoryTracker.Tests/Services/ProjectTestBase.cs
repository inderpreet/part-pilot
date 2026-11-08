using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>Collects every project event; optionally throws, to prove a failing handler cannot break the operation.</summary>
public sealed class RecordingProjectEvents : IProjectEvents
{
    public List<ProjectEvent> Events { get; } = [];

    public bool Throw { get; set; }

    public Task HandleAsync(ProjectEvent projectEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(projectEvent);
        return Throw ? throw new InvalidOperationException("handler failed") : Task.CompletedTask;
    }
}

/// <summary>Counts the SELECTs a context runs, to show a page does not query per row.</summary>
public sealed class CountingInterceptor(Action onQuery) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        onQuery();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>Purchase order test database plus project helpers. One request = one context shared by every service.</summary>
public abstract class ProjectTestBase : PurchaseOrderTestBase
{
    protected RecordingProjectEvents ProjectEvents { get; } = new();

    protected ProjectActor Staff => new(UserId);

    protected ProjectActor Admin => new(UserId, IsAdmin: true);

    protected (AppDbContext Context, ProjectService Projects, PurchaseOrderService Orders) NewProjects(params IInterceptor[] extra)
    {
        var (context, products, _) = NewRequest(extra);
        var stock = new StockService(context, Settings, Clock, NullLogger<StockService>.Instance);
        var orders = new PurchaseOrderService(context, stock, Settings, Clock, new PurchaseOrderEventPublisher([Events], NullLogger<PurchaseOrderEventPublisher>.Instance));
        var projects = new ProjectService(context, stock, orders, products, Settings, Clock, new ProjectEventPublisher([ProjectEvents], NullLogger<ProjectEventPublisher>.Instance));
        return (context, projects, orders);
    }

    protected async Task<T> WithProjectsAsync<T>(Func<ProjectService, Task<T>> action)
    {
        var (context, projects, _) = NewProjects();
        await using (context)
        {
            return await action(projects);
        }
    }

    protected static ProjectInput ProjectInputOf(string name = "Robot", int target = 1, string? number = null, string? description = null, string? notes = null)
        => new(name, number, description, target, notes);

    protected async Task<ProjectDetail> NewProjectAsync(string name = "Robot", int target = 1, string? number = null)
    {
        var result = await WithProjectsAsync(p => p.CreateAsync(ProjectInputOf(name, target, number), Staff));
        Assert.True(result.Succeeded, Messages(result));
        return result.Value;
    }

    protected async Task<BomLineItem> AddPartAsync(int projectId, int productId, int quantityPerUnit = 1, string? note = null)
    {
        var result = await WithProjectsAsync(p => p.AddLineAsync(projectId, new BomLineInput(productId, quantityPerUnit, note), Staff));
        Assert.True(result.Succeeded, Messages(result));
        return result.Value;
    }

    protected async Task<BomStatus> StatusAsync(int projectId)
        => (await WithProjectsAsync(p => p.GetStatusAsync(projectId)))!;

    protected async Task<ProjectDetail> DetailAsync(int projectId)
        => (await WithProjectsAsync(p => p.GetAsync(projectId)))!;

    /// <summary>A product with the given stock, and (optionally) cost.</summary>
    protected async Task<int> PartAsync(string sku, int onHand = 0, long costCents = 0)
    {
        var id = (await CreateProductAsync(Input(sku: sku, name: "Part " + sku, costCents: costCents))).Id;
        if (onHand > 0)
        {
            Assert.True((await PostAsync(Receive(id, onHand))).Succeeded);
        }

        return id;
    }

    protected async Task<PurchaseOrderDetail> DraftWithAsync(int supplierId, params (int ProductId, int Quantity)[] lines)
        => await DraftAsync(supplierId, null, [.. lines.Select(l => new PurchaseOrderLineInput(l.ProductId, l.Quantity))]);

    protected async Task<PurchaseOrderDetail> OpenOrderAsync(int supplierId, params (int ProductId, int Quantity)[] lines)
        => await SubmittedAsync(supplierId, [.. lines.Select(l => new PurchaseOrderLineInput(l.ProductId, l.Quantity))]);

    protected async Task<List<ProjectBuild>> BuildRowsAsync(int projectId)
    {
        await using var context = Db.CreateContext();
        return await context.ProjectBuilds.AsNoTracking().Where(b => b.ProjectId == projectId).OrderBy(b => b.Id).ToListAsync();
    }
}
