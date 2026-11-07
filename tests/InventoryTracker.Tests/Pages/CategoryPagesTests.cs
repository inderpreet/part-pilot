using System.Net;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>WI-02-3: the categories page with inline htmx create / edit / archive.</summary>
public class CategoryPagesTests
{
    private static async Task<HttpResponseMessage> HtmxPostAsync(
        HttpClient client, string url, IEnumerable<(string, string)> fields, string tokenPage = "/Categories")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("RequestVerificationToken", await WebForms.TokenAsync(client, tokenPage));
        request.Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage HtmxGet(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");
        return request;
    }

    private static async Task<CategoryListItem> SeedAsync(AppFactory factory, string name, string? description = null)
    {
        CategoryListItem? created = null;
        await factory.WithScopeAsync(async sp =>
        {
            var result = await sp.GetRequiredService<ICategoryService>().CreateAsync(new CategoryInput(name, description));
            Assert.True(result.Succeeded);
            created = result.Value;
        });
        return created!;
    }

    private static async Task<IReadOnlyList<CategoryListItem>> AllAsync(AppFactory factory)
    {
        IReadOnlyList<CategoryListItem> all = [];
        await factory.WithScopeAsync(async sp => all = await sp.GetRequiredService<ICategoryService>().ListAsync());
        return all;
    }

    [Fact]
    public async Task The_list_shows_categories_with_product_counts_linking_to_the_filtered_product_list()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var tools = await SeedAsync(factory, "Tools", "Hand tools");
        await factory.WithScopeAsync(async sp => Assert.True((await sp.GetRequiredService<IProductService>()
            .CreateAsync(new ProductInput("T-1", "Hammer", null, tools.Id, null, null, null, 0, 0, 0))).Succeeded));

        var html = await client.GetStringAsync("/Categories");

        Assert.Contains("<html", html);
        Assert.Contains("Tools", html);
        Assert.Contains("Hand tools", html);
        Assert.Contains($"/Products?categoryId={tools.Id}&amp;status=All", html);
    }

    [Fact]
    public async Task An_empty_list_shows_an_empty_state()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");

        Assert.Contains("No categories yet", await client.GetStringAsync("/Categories"));
    }

    [Fact]
    public async Task Viewers_see_the_list_without_any_edit_controls()
    {
        using var factory = new AppFactory();
        await SeedAsync(factory, "Tools");
        using var viewer = factory.CreateClientAs("Viewer");
        using var staff = factory.CreateClientAs("Staff");

        var viewerHtml = await viewer.GetStringAsync("/Categories");
        Assert.Contains("Tools", viewerHtml);
        Assert.DoesNotContain("New category", viewerHtml);
        Assert.DoesNotContain("handler=Edit", viewerHtml);
        Assert.DoesNotContain("handler=SetActive", viewerHtml);

        var staffHtml = await staff.GetStringAsync("/Categories");
        Assert.Contains("New category", staffHtml);
        Assert.Contains("handler=Edit", staffHtml);
        Assert.Contains("handler=SetActive", staffHtml);
    }

    [Fact]
    public async Task The_new_and_edit_forms_load_as_fragments()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var tools = await SeedAsync(factory, "Tools", "Hand tools");

        var newForm = await (await client.SendAsync(HtmxGet("/Categories?handler=New"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", newForm);
        Assert.Contains("name=\"name\"", newForm);
        Assert.Contains("Create category", newForm);

        var editRow = await (await client.SendAsync(HtmxGet($"/Categories?handler=Edit&id={tools.Id}"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", editRow);
        Assert.StartsWith("<tr", editRow.TrimStart());
        Assert.Contains("value=\"Tools\"", editRow);
        Assert.Contains("value=\"Hand tools\"", editRow);

        var plainRow = await (await client.SendAsync(HtmxGet($"/Categories?handler=Row&id={tools.Id}"))).Content.ReadAsStringAsync();
        Assert.StartsWith("<tr", plainRow.TrimStart());
        Assert.DoesNotContain("<form", plainRow);

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HtmxGet("/Categories?handler=Edit&id=999"))).StatusCode);
    }

    [Fact]
    public async Task Creating_a_category_returns_the_refreshed_section_and_a_flash_trigger()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        await SeedAsync(factory, "Zebra");

        var response = await HtmxPostAsync(client, "/Categories?handler=Save", [("name", "Anvils"), ("description", "Heavy")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("#category-section", response.Headers.GetValues("HX-Retarget").Single());
        Assert.Equal("innerHTML", response.Headers.GetValues("HX-Reswap").Single());
        Assert.Contains("flash", string.Join(" ", response.Headers.GetValues("HX-Trigger")));

        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", html);
        Assert.Contains("Anvils", html);
        Assert.Contains("Zebra", html);
        Assert.True(html.IndexOf("Anvils", StringComparison.Ordinal) < html.IndexOf("Zebra", StringComparison.Ordinal)); // sorted

        Assert.Contains(await AllAsync(factory), c => c.Name == "Anvils" && c.Description == "Heavy");
    }

    [Theory]
    [InlineData("", "Name is required")]
    [InlineData("tools", "already exists")]
    public async Task Invalid_names_come_back_as_422_with_the_form_and_errors(string name, string expected)
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await SeedAsync(factory, "Tools");

        var response = await HtmxPostAsync(client, "/Categories?handler=Save", [("name", name), ("description", "")]);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(expected, html);
        Assert.Contains("<form", html);
        Assert.False(response.Headers.Contains("HX-Retarget"));
        Assert.Single(await AllAsync(factory));
    }

    [Fact]
    public async Task Saving_an_edit_returns_just_the_updated_row()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        var tools = await SeedAsync(factory, "Tools");

        var response = await HtmxPostAsync(client, "/Categories?handler=Save", [("id", tools.Id.ToString()), ("name", "Hardware"), ("description", "Nuts")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("HX-Retarget"));
        var html = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<tr", html.TrimStart());
        Assert.Contains("Hardware", html);
        Assert.Equal("Hardware", (await AllAsync(factory)).Single().Name);
    }

    [Fact]
    public async Task A_rename_that_collides_with_another_category_is_422_and_keeps_the_editor_open()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Admin");
        await SeedAsync(factory, "Tools");
        var other = await SeedAsync(factory, "Other");

        var response = await HtmxPostAsync(client, "/Categories?handler=Save", [("id", other.Id.ToString()), ("name", "TOOLS"), ("description", "")]);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<tr", html.TrimStart());
        Assert.Contains("already exists", html);
        Assert.Contains("<form", html);
    }

    [Fact]
    public async Task Archiving_and_restoring_swap_the_row_and_keep_the_category()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClientAs("Staff");
        var tools = await SeedAsync(factory, "Tools");

        var archived = await HtmxPostAsync(client, $"/Categories?handler=SetActive&id={tools.Id}&active=false", []);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var archivedHtml = await archived.Content.ReadAsStringAsync();
        Assert.StartsWith("<tr", archivedHtml.TrimStart());
        Assert.Contains("Archived", archivedHtml);
        Assert.Contains("Restore", archivedHtml);
        Assert.False((await AllAsync(factory)).Single().IsActive);

        var restored = await HtmxPostAsync(client, $"/Categories?handler=SetActive&id={tools.Id}&active=true", []);
        var restoredHtml = await restored.Content.ReadAsStringAsync();
        Assert.Contains("Active", restoredHtml);
        Assert.Contains("Archive", restoredHtml);
        Assert.True((await AllAsync(factory)).Single().IsActive);

        Assert.Equal(HttpStatusCode.NotFound,
            (await HtmxPostAsync(client, "/Categories?handler=SetActive&id=999&active=false", [])).StatusCode);
    }

    [Fact]
    public async Task Viewers_cannot_create_edit_or_archive_even_with_a_valid_token()
    {
        using var factory = new AppFactory();
        var tools = await SeedAsync(factory, "Tools");
        using var viewer = factory.CreateClientAs("Viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(HtmxGet("/Categories?handler=New"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(HtmxGet($"/Categories?handler=Edit&id={tools.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await HtmxPostAsync(viewer, "/Categories?handler=Save", [("name", "Sneaky")])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await HtmxPostAsync(viewer, $"/Categories?handler=Save", [("id", tools.Id.ToString()), ("name", "Renamed")])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await HtmxPostAsync(viewer, $"/Categories?handler=SetActive&id={tools.Id}&active=false", [])).StatusCode);

        var only = Assert.Single(await AllAsync(factory));
        Assert.Equal("Tools", only.Name);
        Assert.True(only.IsActive);
    }
}
