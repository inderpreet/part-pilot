using System.Net;
using System.Text.RegularExpressions;
using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

public partial class LayoutAndComponentTests(AppFactory factory) : IClassFixture<AppFactory>
{
    [GeneratedRegex("hx-headers='\\{\"RequestVerificationToken\": \"([^\"]+)\"\\}'")]
    private static partial Regex TokenPattern();

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/");
        var match = TokenPattern().Match(html);
        Assert.True(match.Success, "Layout must set hx-headers with the antiforgery token on <body>.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Fact]
    public async Task Home_renders_inside_the_shared_layout()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("name=\"viewport\"", html);
        Assert.Contains("/lib/htmx.min.js", html);
        Assert.Contains("/lib/pico.min.css", html);
        Assert.Contains("/css/site.css", html);
        Assert.Contains("/js/site.js", html);
        Assert.Contains("id=\"flash\"", html);
        Assert.Contains("<dialog id=\"modal\"", html);
        Assert.Contains("<h1 class=\"page-title\">Dashboard</h1>", html);
        Assert.Contains("Dashboard coming in F06", html);
    }

    [Fact]
    public async Task Layout_shows_the_company_name_from_settings()
    {
        var settings = factory.Services.GetRequiredService<ISettingsService>();
        await settings.SetAsync(SettingKeys.CompanyName, "Acme <Tools>");
        try
        {
            using var client = factory.CreateClient();
            var html = await client.GetStringAsync("/");

            Assert.Contains("<strong>Acme &lt;Tools&gt;</strong>", html);
            // The separator is HTML-encoded by Razor (&#xB7;), so match around it.
            Assert.Matches("<title>Dashboard (·|&#xB7;) Acme &lt;Tools&gt;</title>", html);
        }
        finally
        {
            await settings.SetAsync(SettingKeys.CompanyName, SettingKeys.Defaults[SettingKeys.CompanyName]);
        }
    }

    [Fact]
    public async Task Main_nav_lists_built_sections_as_links_and_unbuilt_ones_as_inert_labels()
    {
        using var client = factory.CreateClient(); // default test role: Admin

        var html = await client.GetStringAsync("/");

        // Built sections are links; Admin is visible because the test user is an administrator.
        foreach (var label in new[] { "Dashboard", "Products", "Stock", "Suppliers", "Purchase Orders", "Admin" })
        {
            Assert.Contains($">{label}</a>", html);
        }

        // Sections whose pages do not exist yet are labelled, never linked (they would 404).
        foreach (var label in new[] { "Reports" })
        {
            Assert.DoesNotContain($">{label}</a>", html);
            Assert.Contains($"aria-disabled=\"true\" title=\"Coming soon\">{label}</span>", html);
        }

        Assert.Matches("<a href=\"/\" aria-current=\"page\">Dashboard</a>", html);
    }

    [Theory]
    [InlineData("/lib/htmx.min.js", "htmx")]
    [InlineData("/lib/pico.min.css", "Pico CSS")]
    [InlineData("/css/site.css", ".badge-low")]
    [InlineData("/js/site.js", "closeModal")]
    public async Task Vendored_and_app_assets_are_served(string url, string marker)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(marker, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Hx_post_with_the_layout_token_passes_antiforgery()
    {
        using var client = factory.CreateClient();
        var token = await GetTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Dev/Components?handler=Echo");
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("posted", await response.Content.ReadAsStringAsync());
        Assert.Equal("{\"flash\":\"Saved\"}", response.Headers.GetValues("HX-Trigger").Single());
    }

    [Fact]
    public async Task Hx_post_without_the_token_is_rejected()
    {
        using var client = factory.CreateClient();
        await GetTokenAsync(client); // establishes the antiforgery cookie, but we deliberately omit the header
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Dev/Components?handler=Echo");
        request.Headers.Add("HX-Request", "true");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The htmx error partial retargets to #flash and gives a useful message for a rejected request.
        Assert.Equal("#flash", response.Headers.GetValues("HX-Retarget").Single());
        Assert.Contains("Request rejected", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Modal_fragment_is_a_partial_and_closing_posts_send_flash_and_closeModal()
    {
        using var client = factory.CreateClient();
        var token = await GetTokenAsync(client);

        var modal = await client.GetStringAsync("/Dev/Components?handler=Modal");
        Assert.DoesNotContain("<html", modal);
        Assert.Contains("data-close-modal", modal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Dev/Components?handler=SaveModal");
        request.Headers.Add("RequestVerificationToken", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var trigger = response.Headers.GetValues("HX-Trigger").Single();
        Assert.Contains("\"flash\":\"Saved from the modal\"", trigger);
        Assert.Contains("\"closeModal\":true", trigger);
    }

    [Fact]
    public async Task Service_errors_render_through_the_validation_summary_with_422()
    {
        using var client = factory.CreateClient();
        var token = await GetTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Dev/Components?handler=Invalid");
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("SKU &#x27;ABC-1&#x27; already exists.", html);
        Assert.Contains("Unit cost cannot be negative.", html);
    }

    [Fact]
    public async Task Style_guide_renders_all_partials()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Dev/Components?sort=name&desc=false&search=bolt&page=3");

        Assert.Contains("$1,234.56", html);                       // Html.Money
        Assert.Contains("-$2,500.99", html);
        Assert.Contains("data-local=\"datetime\"", html);          // Html.LocalTime
        Assert.Contains("class=\"badge badge-low\"", html);
        Assert.Contains("hx-confirm=\"Deactivate this product?\"", html);
        Assert.Contains("No products yet", html);                  // _EmptyState
        Assert.Contains("aria-sort=\"ascending\"", html);          // _SortHeader on the active column
        Assert.Contains("aria-sort=\"none\"", html);
    }

    [Fact]
    public async Task Pager_links_keep_the_query_string_and_replace_only_page()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Dev/Components?search=bolt&sort=name&page=3");

        // 240 rows at 25 per page = 10 pages; current page 3 links to 2 and 4.
        Assert.Contains("Showing 51–75 of 240", html);
        Assert.Matches("href=\"/Dev/Components\\?search=bolt&amp;sort=name&amp;page=4\"", html);
        Assert.Matches("href=\"/Dev/Components\\?search=bolt&amp;sort=name&amp;page=2\"", html);
        Assert.Contains("hx-push-url=\"true\"", html);
        Assert.Contains("aria-current=\"page\" aria-label=\"Page 3\"", html);
    }

    [Fact]
    public async Task Sort_header_toggles_direction_and_resets_paging()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Dev/Components?sort=sku&desc=false&page=4");

        // Already ascending on sku, so the next click is descending, back on page 1 (page param dropped).
        Assert.Matches("href=\"/Dev/Components\\?sort=sku&amp;desc=true\"", html);
    }

    [Fact]
    public async Task Style_guide_is_not_available_outside_development_and_testing()
    {
        using var production = new AppFactory { EnvironmentName = "Production" };
        using var client = production.CreateClient();

        var response = await client.GetAsync("/Dev/Components");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Error_pages_use_the_layout_and_a_status_specific_message()
    {
        using var client = factory.CreateClient();

        var forbidden = await client.GetAsync("/Error/403");
        var html = await forbidden.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("Access denied", html);
        Assert.Contains("id=\"flash\"", html); // rendered inside _Layout
        Assert.Contains("You don't have permission to do that.".Replace("'", "&#x27;"), html);
    }

    [Fact]
    public async Task Pages_vary_on_the_htmx_request_header_so_caches_do_not_mix_partials_and_full_pages()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Contains("HX-Request", response.Headers.Vary);
    }
}
