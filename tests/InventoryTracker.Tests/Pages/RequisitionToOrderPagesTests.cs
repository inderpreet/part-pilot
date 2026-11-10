using System.Globalization;
using System.Net;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-13-3: the to-order list, links between requisitions and purchase orders, and cancelling an approved requisition.</summary>
public sealed partial class RequisitionPagesTests
{
    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A supplier and a part that prefers it.</summary>
    private static async Task<(int SupplierId, int ProductId)> SeedSupplierPartAsync(AppFactory factory, string sku, string supplierName = "Acme")
    {
        var productId = await SeedPartAsync(factory, sku);
        var supplierId = 0;
        await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Name == supplierName) ?? new Supplier { Name = supplierName };
            if (supplier.Id == 0)
            {
                db.Suppliers.Add(supplier);
                await db.SaveChangesAsync();
            }

            db.ProductSuppliers.Add(new ProductSupplier { ProductId = productId, SupplierId = supplier.Id, UnitCostCents = 300, IsPreferred = true });
            await db.SaveChangesAsync();
            supplierId = supplier.Id;
        });
        return (supplierId, productId);
    }

    /// <summary>Submitted by the Viewer and approved by the Manager: <paramref name="approved"/> per line, 0 rejects.</summary>
    private static async Task<RequisitionDetail> SeedApprovedAsync(AppFactory factory, int[] parts, int[] approved)
    {
        var submitted = await SeedSubmittedAsync(factory, TestAuth.Users.Viewer, parts);
        var manager = new RequisitionActor(await WebForms.UserIdAsync(factory, TestAuth.Users.Manager), CanSeeAll: true, CanApprove: true, CanOrder: true);
        RequisitionDetail? detail = null;
        await factory.WithScopeAsync(async sp =>
        {
            var decisions = submitted.Lines.Select((l, i) => approved[i] == 0
                ? new RequisitionLineDecisionInput(l.Id, RequisitionDecision.Rejected, null, "Not now")
                : new RequisitionLineDecisionInput(l.Id, RequisitionDecision.Approved, approved[i], approved[i] < l.QuantityRequested ? "Fewer" : null)).ToList();
            var result = await sp.GetRequiredService<IRequisitionService>().DecideAsync(submitted.Id, submitted.RowVersion, decisions, manager);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            detail = result.Value;
        });
        return detail!;
    }

    private static async Task<int> DraftOrderAsync(AppFactory factory, int supplierId)
    {
        var id = 0;
        var actor = new PurchaseOrderActor(await WebForms.UserIdAsync(factory, TestAuth.Users.Staff));
        await factory.WithScopeAsync(async sp => id = (await sp.GetRequiredService<IPurchaseOrderService>().CreateDraftAsync(supplierId, actor)).Value.Id);
        return id;
    }

    private static async Task<List<(int OrderId, string Number, int Quantity)>> OrderedLinesAsync(AppFactory factory)
    {
        var rows = new List<(int, string, int)>();
        await factory.WithScopeAsync(async sp => rows.AddRange((await sp.GetRequiredService<AppDbContext>().RequisitionLineOrders
                .Select(o => new { o.PurchaseOrderLine!.PurchaseOrderId, o.PurchaseOrderLine.PurchaseOrder!.Number, o.PurchaseOrderLine.QuantityOrdered })
                .ToListAsync())
            .Select(o => (o.PurchaseOrderId, o.Number, o.QuantityOrdered))));
        return rows;
    }

    [Fact]
    public async Task Staff_see_the_list_grouped_by_supplier_with_availability()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var (_, part) = await SeedSupplierPartAsync(factory, "ORD-1", "Bolt Co");
        var requisition = await SeedApprovedAsync(factory, [part], [4]);

        var html = await client.GetStringAsync("/Requisitions/ToOrder");

        Assert.Contains("Bolt Co", html);
        Assert.Contains(requisition.Number, html);
        Assert.Contains("In draft", html);
        Assert.Contains("New draft PO", html);
    }

    [Fact]
    public async Task An_empty_list_says_so()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");

        Assert.Contains("Nothing to order", await client.GetStringAsync("/Requisitions/ToOrder"));
    }

    [Fact]
    public async Task Viewers_are_forbidden_and_do_not_get_the_nav_entry()
    {
        using var factory = new AppFactory();
        using var viewer = factory.CreateClientAs("Viewer");
        using var staff = factory.CreateClientAs("Staff");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Requisitions/ToOrder")).StatusCode);
        Assert.DoesNotContain("href=\"/Requisitions/ToOrder\"", await viewer.GetStringAsync("/Requisitions"));
        Assert.Contains("href=\"/Requisitions/ToOrder\"", await staff.GetStringAsync("/Requisitions"));
    }

    [Fact]
    public async Task Staff_send_lines_to_a_new_draft_and_get_a_summary_with_links()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var (supplier, part) = await SeedSupplierPartAsync(factory, "ORD-2");
        var requisition = await SeedApprovedAsync(factory, [part], [3]);

        var response = await WebForms.PostFormAsync(client, "/Requisitions/ToOrder",
            [($"Selected_{supplier}", Invariant(requisition.Lines[0].Id)), ($"Targets[{supplier}]", "new")]);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ordered = Assert.Single(await OrderedLinesAsync(factory));
        Assert.Equal(3, ordered.Quantity);
        Assert.Contains($"/PurchaseOrders/{ordered.OrderId}", html);
        Assert.Contains("new draft", html);
        Assert.Contains("Nothing to order", html);
    }

    [Fact]
    public async Task Staff_send_lines_to_an_existing_draft()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var (supplier, part) = await SeedSupplierPartAsync(factory, "ORD-3");
        var draft = await DraftOrderAsync(factory, supplier);
        var requisition = await SeedApprovedAsync(factory, [part], [5]);

        Assert.Contains($"value=\"{draft}\"", await client.GetStringAsync("/Requisitions/ToOrder"));
        await WebForms.PostFormAsync(client, "/Requisitions/ToOrder",
            [($"Selected_{supplier}", Invariant(requisition.Lines[0].Id)), ($"Targets[{supplier}]", Invariant(draft))]);

        Assert.Equal(draft, Assert.Single(await OrderedLinesAsync(factory)).OrderId);
    }

    [Fact]
    public async Task A_line_with_no_supplier_needs_one_picked()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var (supplier, _) = await SeedSupplierPartAsync(factory, "ORD-4");
        var loose = await SeedPartAsync(factory, "LOOSE-1");
        var requisition = await SeedApprovedAsync(factory, [loose], [2]);
        var lineId = Invariant(requisition.Lines[0].Id);

        var missing = await WebForms.PostFormAsync(client, "/Requisitions/ToOrder", [("Selected_none", lineId)]);
        Assert.Contains("Choose a supplier for this line", await missing.Content.ReadAsStringAsync());
        Assert.Empty(await OrderedLinesAsync(factory));

        await WebForms.PostFormAsync(client, "/Requisitions/ToOrder", [("Selected_none", lineId), ($"SupplierFor[{lineId}]", Invariant(supplier))]);
        Assert.Single(await OrderedLinesAsync(factory));
    }

    [Fact]
    public async Task The_draft_order_page_links_to_the_list_for_that_order_and_shows_the_source()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var (supplier, part) = await SeedSupplierPartAsync(factory, "ORD-5");
        var draft = await DraftOrderAsync(factory, supplier);
        var requisition = await SeedApprovedAsync(factory, [part], [2]);

        Assert.Contains($"/Requisitions/ToOrder?purchaseOrderId={draft}", await client.GetStringAsync($"/PurchaseOrders/{draft}"));
        var forOrder = $"/Requisitions/ToOrder?purchaseOrderId={draft}";
        Assert.Contains("Add selected to", await client.GetStringAsync(forOrder));

        await WebForms.PostFormAsync(client, forOrder, [("PurchaseOrderId", Invariant(draft)), ($"Selected_{supplier}", Invariant(requisition.Lines[0].Id))]);

        Assert.Equal(draft, Assert.Single(await OrderedLinesAsync(factory)).OrderId);
        Assert.Contains($"/Requisitions/{requisition.Id}", await client.GetStringAsync($"/PurchaseOrders/{draft}"));
    }

    [Fact]
    public async Task The_full_path_raise_approve_part_convert_submit_and_the_requester_sees_the_order()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        using var viewer = factory.CreateClientAs("Viewer");
        var (supplier, a) = await SeedSupplierPartAsync(factory, "ORD-6");
        var (_, b) = await SeedSupplierPartAsync(factory, "ORD-7");
        var requisition = await SeedApprovedAsync(factory, [a, b], [2, 0]);

        await WebForms.PostFormAsync(staff, "/Requisitions/ToOrder", [($"Selected_{supplier}", Invariant(requisition.Lines[0].Id)), ($"Targets[{supplier}]", "new")]);
        var order = Assert.Single(await OrderedLinesAsync(factory));
        var page = $"/PurchaseOrders/{order.OrderId}";
        var version = System.Text.RegularExpressions.Regex.Match(await staff.GetStringAsync(page), "name=\"rowVersion\" value=\"(\\d+)\"").Groups[1].Value;
        var submit = await WebForms.PostFormAsync(staff, page + "?handler=Submit", [("rowVersion", version)], page);

        Assert.Equal(HttpStatusCode.Redirect, submit.StatusCode);
        var detail = await viewer.GetStringAsync($"/Requisitions/{requisition.Id}");
        Assert.Contains(order.Number, detail);
        Assert.Contains("Ordered", detail);
        Assert.Contains("Ordered", await viewer.GetStringAsync("/Requisitions?ordering=Ordered"));
    }

    [Fact]
    public async Task A_Manager_cancels_an_approved_requisition_with_nothing_ordered()
    {
        using var factory = new AppFactory();
        using var manager = factory.CreateClientAs("Manager");
        using var staff = factory.CreateClientAs("Staff");
        var (_, part) = await SeedSupplierPartAsync(factory, "ORD-8");
        var requisition = await SeedApprovedAsync(factory, [part], [2]);
        var page = $"/Requisitions/{requisition.Id}";
        var fields = new List<(string, string)> { ("rowVersion", Invariant(requisition.RowVersion)), ("reason", "Project dropped") };

        Assert.Equal(HttpStatusCode.Forbidden, (await WebForms.PostFormAsync(staff, page + "?handler=CancelApproved", fields, page)).StatusCode);
        Assert.Contains("handler=CancelApproved", await manager.GetStringAsync(page));

        await WebForms.PostFormAsync(manager, page + "?handler=CancelApproved", fields, page);

        Assert.Equal(RequisitionStatus.Cancelled, await StatusOfAsync(factory, requisition.Id));
        Assert.Contains("Nothing to order", await staff.GetStringAsync("/Requisitions/ToOrder"));
    }

    [Fact]
    public async Task A_purchase_order_that_is_not_a_draft_cannot_take_lines()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");

        var response = await client.GetAsync("/Requisitions/ToOrder?purchaseOrderId=9999");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
