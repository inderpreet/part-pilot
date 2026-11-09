using System.Net;
using System.Net.Http.Headers;
using System.Text;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

public class ImportExportPagesTests
{
    [Fact]
    public async Task Admin_can_open_import_export_and_backup()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Import")).StatusCode);
        var export = await client.GetAsync("/Export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var exportHtml = await export.Content.ReadAsStringAsync();
        Assert.Contains("report-links", exportHtml);
        Assert.Contains("report-card", exportHtml);
        Assert.Contains("Download CSV", exportHtml);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Backup")).StatusCode);
        var importHtml = await client.GetStringAsync("/Import");
        Assert.Contains("Import", importHtml);
        Assert.Contains("Download a template", importHtml);
    }

    [Fact]
    public async Task Viewer_is_forbidden_on_import_and_export_until_granted()
    {
        using var factory = new AppFactory();
        using var viewer = factory.CreateClientAs("Viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Import")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Export")).StatusCode);
        var home = await viewer.GetStringAsync("/");
        Assert.DoesNotContain("href=\"/Import\"", home);
        Assert.DoesNotContain("href=\"/Export\"", home);

        await factory.WithScopeAsync(async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            Assert.True((await settings.SetAsync(SettingKeys.Permission(DataPermission.ExportData, Roles.Viewer), true)).Succeeded);
        });

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/Export")).StatusCode);
        Assert.Contains("href=\"/Export\"", await viewer.GetStringAsync("/"));
    }

    [Fact]
    public async Task Staff_may_export_by_default_and_cannot_import()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/Import")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/Export")).StatusCode);
        var csv = await staff.GetAsync("/Export?handler=Products");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var body = await csv.Content.ReadAsStringAsync();
        Assert.Contains("sku", body);
    }

    [Fact]
    public async Task Invalid_csv_shows_errors_and_writes_nothing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var response = await UploadAsync(client, "/Import?handler=Upload", "sku,name\n,No sku\n", extra: new() { ["Kind"] = "Products" });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("SKU is required", html);
        Assert.Contains("badge-out", html);
    }

    [Fact]
    public async Task Template_download_is_csv()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var response = await client.GetAsync("/Import?handler=Template&kind=Boms");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("project_name", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Opening_stock_import_from_the_page_sets_on_hand()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await factory.WithScopeAsync(async sp =>
        {
            var products = sp.GetRequiredService<IProductService>();
            Assert.True((await products.CreateAsync(new ProductInput("WID-1", "Widget", null, null, "each", null, null, 0, 0, 0))).Succeeded);
        });

        var preview = await UploadAsync(client, "/Import?handler=Upload", "sku,quantity,note\nWID-1,40,count\n", extra: new() { ["Kind"] = "OpeningStock" });
        var html = await preview.Content.ReadAsStringAsync();
        Assert.Contains("WID-1", html);
        var token = TokenFrom(html);
        var commit = await WebForms.PostFormAsync(client, "/Import?handler=Commit", [("Token", token), ("Kind", "OpeningStock")]);
        Assert.True((int)commit.StatusCode is 200 or 302, commit.StatusCode.ToString());

        await factory.WithScopeAsync(async sp =>
        {
            var product = await sp.GetRequiredService<IProductService>().FindByCodeAsync("WID-1");
            Assert.Equal(40, product!.QuantityOnHand);
        });
    }

    [Fact]
    public async Task Bom_csv_for_three_projects_round_trips()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await factory.WithScopeAsync(async sp =>
        {
            var products = sp.GetRequiredService<IProductService>();
            Assert.True((await products.CreateAsync(new ProductInput("P-1", "Part 1", null, null, "each", null, null, 0, 0, 100))).Succeeded);
        });

        var csv = """
            project_number,project_name,target_units,description,notes,sku,quantity_per_unit,note,part_name,part_unit,part_unit_cost
            ,Alpha,1,,,P-1,1,,,
            ,Beta,1,,,P-1,2,,,
            ,Gamma,1,,,P-1,3,,,
            """;
        var preview = await UploadAsync(client, "/Import?handler=Upload", csv, extra: new() { ["Kind"] = "Boms", ["Mode"] = "Merge" });
        var html = await preview.Content.ReadAsStringAsync();
        Assert.DoesNotContain("badge-out", html);
        var token = TokenFrom(html);
        var commit = await WebForms.PostFormAsync(client, "/Import?handler=Commit", [("Token", token), ("Kind", "Boms")]);
        Assert.True((int)commit.StatusCode is 200 or 302);

        var export = await client.GetAsync("/Export?handler=Boms");
        var exported = await export.Content.ReadAsStringAsync();
        Assert.Contains("Alpha", exported);
        Assert.Contains("Beta", exported);
        Assert.Contains("Gamma", exported);
    }

    [Fact]
    public async Task Settings_permissions_card_grants_staff_import()
    {
        using var factory = new AppFactory();
        using var admin = factory.CreateClientAs("Admin");
        using var staff = factory.CreateClientAs("Staff");
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/Import")).StatusCode);

        var html = await admin.GetStringAsync("/Admin/Settings");
        Assert.Contains("Data permissions", html);
        var response = await WebForms.PostFormAsync(admin, "/Admin/Settings?handler=Permissions",
        [
            ("Permissions.StaffImportData", "true"),
            ("Permissions.StaffImportData", "false"),
            ("Permissions.ViewerImportData", "false"),
            ("Permissions.StaffImportBoms", "false"),
            ("Permissions.ViewerImportBoms", "false"),
            ("Permissions.StaffExportData", "true"),
            ("Permissions.StaffExportData", "false"),
            ("Permissions.ViewerExportData", "false"),
            ("Permissions.StaffExportBoms", "true"),
            ("Permissions.StaffExportBoms", "false"),
            ("Permissions.ViewerExportBoms", "false"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/Import")).StatusCode);
    }

    [Fact]
    public async Task Backup_download_is_a_sqlite_file()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var response = await WebForms.PostFormAsync(client, "/Admin/Backup", []);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sqlite", response.Content.Headers.ContentType?.MediaType ?? "");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100);
        Assert.Equal("SQLite format 3", Encoding.ASCII.GetString(bytes, 0, 15).TrimEnd('\0'));
    }

    private const string BomHeader = "project_number,project_name,target_units,description,notes,sku,quantity_per_unit,note,part_name,part_unit,part_unit_cost\n";

    private static async Task AddPartsAsync(AppFactory factory, params string[] skus) => await factory.WithScopeAsync(async sp =>
    {
        var products = sp.GetRequiredService<IProductService>();
        foreach (var sku in skus)
        {
            Assert.True((await products.CreateAsync(new ProductInput(sku, "Part " + sku, null, null, "each", null, null, 0, 0, 100))).Succeeded);
        }
    });

    private static async Task<HttpResponseMessage> CommitAsync(HttpClient client, string token, string kind)
        => await WebForms.PostFormAsync(client, "/Import?handler=Commit", [("Token", token), ("Kind", kind)]);

    [Fact]
    public async Task Every_allowed_kind_has_a_template_link()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var html = await client.GetStringAsync("/Import");
        foreach (var kind in new[] { "Products", "Suppliers", "ProductSuppliers", "OpeningStock", "Boms" })
        {
            Assert.Contains($"kind={kind}", html);
        }

        Assert.Contains("handler=Template", html);
    }

    [Fact]
    public async Task Bom_merge_then_replace_end_to_end()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await AddPartsAsync(factory, "A-1", "B-1");

        var merge = await UploadAsync(client, "/Import?handler=Upload", BomHeader + "PRJ-7,Robot,1,,,A-1,1,,,,\nPRJ-7,Robot,1,,,B-1,2,,,,\n", extra: new() { ["Kind"] = "Boms", ["Mode"] = "Merge" });
        var mergeHtml = await merge.Content.ReadAsStringAsync();
        Assert.Contains("class=\"import-group\"", mergeHtml);
        Assert.Contains("Robot</th>", mergeHtml);
        Assert.DoesNotContain("hx-confirm=\"Replace", mergeHtml);
        Assert.Equal(HttpStatusCode.Redirect, (await CommitAsync(client, TokenFrom(mergeHtml), "Boms")).StatusCode);

        var replace = await UploadAsync(client, "/Import?handler=Upload", BomHeader + "PRJ-7,Robot,1,,,A-1,1,,,,\n", extra: new() { ["Kind"] = "Boms", ["Mode"] = "Replace" });
        var replaceHtml = await replace.Content.ReadAsStringAsync();
        Assert.Contains("hx-confirm=\"Replace will remove 1 BOM line(s)", replaceHtml);
        Assert.Contains("B-1", replaceHtml);
        Assert.Equal(HttpStatusCode.Redirect, (await CommitAsync(client, TokenFrom(replaceHtml), "Boms")).StatusCode);

        var exported = await client.GetStringAsync("/Export?handler=Boms");
        Assert.Contains("A-1", exported);
        Assert.DoesNotContain("B-1", exported);

        // Exporting and importing again changes nothing.
        var again = await UploadAsync(client, "/Import?handler=Upload", exported, extra: new() { ["Kind"] = "Boms", ["Mode"] = "Replace" });
        var againHtml = await again.Content.ReadAsStringAsync();
        Assert.Contains("1 skip", againHtml);
        Assert.Contains("0 create", againHtml);
        Assert.Contains("0 update", againHtml);
    }

    [Fact]
    public async Task Commit_is_refused_when_the_staged_kinds_permission_is_revoked()
    {
        using var factory = new AppFactory();
        using var staff = factory.CreateClientAs("Staff");
        await AddPartsAsync(factory, "A-1");

        async Task SetAsync(DataPermission permission, bool value) => await factory.WithScopeAsync(async sp =>
            Assert.True((await sp.GetRequiredService<ISettingsService>().SetAsync(SettingKeys.Permission(permission, Roles.Staff), value)).Succeeded));

        await SetAsync(DataPermission.ImportData, true);
        await SetAsync(DataPermission.ImportBoms, true);
        var preview = await UploadAsync(staff, "/Import?handler=Upload", BomHeader + ",Robot,1,,,A-1,1,,,,\n", extra: new() { ["Kind"] = "Boms", ["Mode"] = "Merge" });
        var token = TokenFrom(await preview.Content.ReadAsStringAsync());

        await SetAsync(DataPermission.ImportBoms, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await CommitAsync(staff, token, "Boms")).StatusCode);

        await factory.WithScopeAsync(async sp =>
            Assert.False(await sp.GetRequiredService<AppDbContext>().Projects.AnyAsync()));
    }

    [Fact]
    public async Task Malformed_csv_shows_an_error_instead_of_failing()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        var response = await UploadAsync(client, "/Import?handler=Upload", "sku,name\nA-1,\"open\nA-2,Two\n", extra: new() { ["Kind"] = "Products" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("not closed properly", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Summary_links_to_the_batch_and_its_stock_movements()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await AddPartsAsync(factory, "WID-1");

        var preview = await UploadAsync(client, "/Import?handler=Upload", "sku,quantity,note\nWID-1,40,count\n", extra: new() { ["Kind"] = "OpeningStock" });
        var commit = await CommitAsync(client, TokenFrom(await preview.Content.ReadAsStringAsync()), "OpeningStock");
        Assert.Equal(HttpStatusCode.Redirect, commit.StatusCode);
        var batchUrl = commit.Headers.Location!.ToString();
        Assert.Contains("batch=", batchUrl);
        var batchId = batchUrl[(batchUrl.IndexOf('=', StringComparison.Ordinal) + 1)..];

        var summary = await client.GetStringAsync(batchUrl);
        Assert.Contains($"Import #{batchId} finished", summary);
        Assert.Contains($"importBatchId={batchId}", summary);
        Assert.Contains($"batch={batchId}", summary); // the history row links back to the batch

        Assert.Contains("WID-1", await client.GetStringAsync($"/Stock/Movements?importBatchId={batchId}"));
        Assert.Contains("No movements match", await client.GetStringAsync("/Stock/Movements?importBatchId=9999"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Import?batch=9999")).StatusCode);
    }

    [Fact]
    public async Task Transaction_export_reads_the_dates_in_the_users_time_zone()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await AddPartsAsync(factory, "TZ-1");
        await factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var user = await db.Users.Select(u => u.Id).FirstAsync();
            var product = await db.Products.Select(p => p.Id).SingleAsync();
            var posted = await sp.GetRequiredService<IStockService>().PostAsync(
                [new StockMovement(product, StockTransactionType.Receive, 5, OccurredAt: new DateTime(2026, 1, 10, 2, 0, 0, DateTimeKind.Utc))],
                new StockContext(user));
            Assert.True(posted.Succeeded);
        });

        // 02:00 UTC on the 10th is still the 9th at UTC-5.
        Assert.Contains("TZ-1", await client.GetStringAsync("/Export?handler=Transactions&from=2026-01-09&to=2026-01-09&tz=300"));
        Assert.DoesNotContain("TZ-1", await client.GetStringAsync("/Export?handler=Transactions&from=2026-01-10&to=2026-01-10&tz=300"));
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string url, string csv, Dictionary<string, string>? extra = null)
    {
        var token = await WebForms.TokenAsync(client, "/Import");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(token), "__RequestVerificationToken");
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                content.Add(new StringContent(value), key);
            }
        }

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "upload.csv");
        return await client.PostAsync(url, content);
    }

    private static string TokenFrom(string html)
    {
        const string marker = "name=\"Token\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Preview did not include a token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }
}
