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
        Assert.Contains("<a class=\"skip-link\" href=\"#content\">", html);
        Assert.Contains("<main id=\"content\"", html);
        Assert.Contains("id=\"dashboard\"", html);
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

            Assert.Contains("class=\"brand-name\">Acme &lt;Tools&gt;</strong>", html);
            // The tab title carries the product name, not the company (see BrandingPagesTests).
            // The separator is HTML-encoded by Razor (&#xB7;), so match around it.
            Assert.Matches("<title>Dashboard (·|&#xB7;) Part Pilot</title>", html);
            Assert.Matches("<small>Acme &lt;Tools&gt; (·|&#xB7;) Part Pilot v", html);
        }
        finally
        {
            await settings.SetAsync(SettingKeys.CompanyName, SettingKeys.Defaults[SettingKeys.CompanyName]);
        }
    }

    [Fact]
    public async Task Rail_lists_built_sections_as_links_and_unbuilt_ones_as_inert_labels()
    {
        using var client = factory.CreateClient(); // default test role: Admin

        var html = await client.GetStringAsync("/");

        // Built sections are links; Admin is visible because the test user is an administrator.
        foreach (var label in new[] { "Dashboard", "Parts", "Stock", "Suppliers", "Purchase Orders", "BOMs", "Reports", "Import", "Export", "Admin" })
        {
            Assert.Contains($"<span>{label}</span></a>", html);
        }

        // Every section is built now, so none is an inert "Soon" label (the mechanism stays for future sections).
        Assert.DoesNotContain("title=\"Coming soon\"", html);
        Assert.Matches("<a href=\"/Reports/LowStock\"[^>]*><svg[^>]*>.*?</svg><span>Low stock</span>", html);

        Assert.Matches("<a href=\"/\" aria-current=\"page\"><svg[^>]*>.*?</svg><span>Dashboard</span></a>", html);
        Assert.Contains("class=\"rail-label\">Inventory</p>", html);
        Assert.Contains("class=\"rail-label\">Insights</p>", html);
    }

    [Fact]
    public async Task Rail_marks_the_section_that_contains_the_page_as_current()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/PurchaseOrders");

        Assert.Matches("<a href=\"/PurchaseOrders\" aria-current=\"page\">", html);
        Assert.DoesNotContain("<a href=\"/\" aria-current=\"page\">", html);
        Assert.Matches("<div class=\"crumbs\">\\s*<span aria-current=\"page\">Purchase Orders</span>", html);
    }

    [Fact]
    public async Task Rail_foot_shows_the_user_card_with_a_post_only_sign_out()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("class=\"user-card\"", html);
        Assert.Contains("action=\"/Account/Logout\"", html);
        Assert.Contains("aria-label=\"Sign out\"", html);
        Assert.Contains("href=\"/Account/ChangePassword\"", html);
        Assert.Contains("data-rail-open", html);
        Assert.Contains("aria-controls=\"rail\"", html);
    }

    [Fact]
    public async Task Viewer_does_not_see_the_admin_link()
    {
        using var client = factory.CreateClientAs("Viewer");

        var html = await client.GetStringAsync("/");

        Assert.DoesNotContain("<span>Admin</span>", html);
        Assert.Contains("<span>Parts</span></a>", html);
    }

    [Fact]
    public async Task Sign_in_page_has_no_rail_or_top_bar_but_keeps_the_skip_link_and_company_name()
    {
        using var client = factory.CreateAnonymousClient();

        var html = await client.GetStringAsync("/Account/Login");

        Assert.DoesNotContain("class=\"rail\"", html);
        Assert.DoesNotContain("class=\"topbar\"", html);
        Assert.DoesNotContain("class=\"app\"", html);
        Assert.Contains("class=\"skip-link\"", html);
        Assert.Contains("<main id=\"content\"", html);
        Assert.Contains("class=\"brand\"", html);
    }

    [Theory]
    [InlineData("/PurchaseOrders", "New purchase order", "Staff")]
    [InlineData("/Products", "New part", "Staff")]
    [InlineData("/Suppliers", "New supplier", "Staff")]
    [InlineData("/Stock/Movements", "Bulk movement", "Staff")]
    [InlineData("/Admin/Users", "New user", "Admin")]
    public async Task List_pages_put_the_primary_action_in_the_page_header(string url, string label, string role)
    {
        using var client = factory.CreateClientAs(role);

        var html = await client.GetStringAsync(url);

        Assert.Contains("class=\"page-head\"", html);
        var actions = html[html.IndexOf("class=\"page-actions\"", StringComparison.Ordinal)..];
        Assert.Contains(label, actions[..actions.IndexOf("</div>", StringComparison.Ordinal)]);
        // and no longer in the filter row
        var toolbar = html[html.IndexOf("class=\"toolbar", StringComparison.Ordinal)..html.IndexOf("id=\"results\"", StringComparison.Ordinal)];
        Assert.DoesNotContain(label, toolbar);
    }

    [Theory]
    [InlineData("/PurchaseOrders")]
    [InlineData("/Products")]
    [InlineData("/Suppliers")]
    [InlineData("/Stock/Movements")]
    [InlineData("/Admin/Users")]
    public async Task List_pages_render_one_list_card_with_a_load_bar_and_live_filters_that_show_it(string url)
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(url);

        Assert.Contains("class=\"card list-card\"", html);
        Assert.Contains("<div class=\"load-bar\" role=\"status\" aria-label=\"Updating\"></div>", html);
        Assert.Contains("hx-indicator=\".load-bar\"", html);
        Assert.Contains("id=\"results\"", html);
    }

    [Theory]
    [InlineData("/PurchaseOrders")]
    [InlineData("/Products")]
    [InlineData("/Suppliers")]
    [InlineData("/Stock/Movements")]
    [InlineData("/Admin/Users")]
    public async Task Htmx_requests_for_a_list_page_return_only_the_results_not_a_second_card(string url)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("HX-Request", "true");

        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("list-card", html);
        Assert.DoesNotContain("<html", html);
        Assert.DoesNotContain("load-bar", html);
    }

    [Theory]
    [InlineData("/PurchaseOrders", "New purchase order")]
    [InlineData("/Products", "New part")]
    [InlineData("/Suppliers", "New supplier")]
    [InlineData("/Stock/Movements", "Bulk movement")]
    public async Task Viewers_do_not_get_the_primary_action(string url, string label)
    {
        using var viewer = factory.CreateClientAs("Viewer");

        Assert.DoesNotContain(label, await viewer.GetStringAsync(url));
    }
    [Fact]
    public async Task Plain_header_pages_render_the_title_alone_without_a_page_head()
    {
        using var client = factory.CreateClient();

        var changePassword = await client.GetStringAsync("/Account/ChangePassword");

        Assert.Contains("<h1 class=\"page-title\">Change password</h1>", changePassword);
        Assert.DoesNotContain("class=\"page-head\"", changePassword);

        using var anonymous = factory.CreateAnonymousClient();
        var login = await anonymous.GetStringAsync("/Account/Login");
        Assert.Contains("<h1 class=\"page-title\">Sign in</h1>", login);
        Assert.DoesNotContain("class=\"page-head\"", login);
    }

    [Fact]
    public async Task Site_js_wires_the_rail_drawer()
    {
        using var client = factory.CreateClient();

        var js = await client.GetStringAsync("/js/site.js");

        Assert.Contains("data-rail-open", js);
        Assert.Contains("rail-open", js);
    }

    [Theory]
    [InlineData("/css/site.css")]
    [InlineData("/js/site.js")]
    [InlineData("/lib/htmx.min.js")]
    public async Task Static_assets_are_always_revalidated_so_an_upgrade_never_pairs_new_markup_with_old_css(string url)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache, "Static files must send Cache-Control: no-cache.");
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Site_js_marks_the_section_nav_link_for_the_current_hash()
    {
        using var client = factory.CreateClient();

        var js = await client.GetStringAsync("/js/site.js");

        Assert.Contains("markSectionNav", js);
        Assert.Contains("hashchange", js);
        Assert.Contains("'.section-nav'", js);
        Assert.Contains("aria-current", js);
    }

    [Fact]
    public async Task Site_js_replaces_the_native_confirm_with_the_app_dialog_and_closes_menus()
    {
        using var client = factory.CreateClient();

        var js = await client.GetStringAsync("/js/site.js");

        Assert.Contains("htmx:confirm", js);
        Assert.Contains("issueRequest", js);
        Assert.Contains("details.menu[open]", js);
    }

    [Fact]
    public async Task Signed_in_pages_have_a_confirm_dialog_that_is_separate_from_the_form_modal_and_sign_in_has_none()
    {
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/Products");

        Assert.Contains("<dialog id=\"modal\"", html);
        Assert.Contains("<dialog id=\"confirm\"", html);
        Assert.Contains("id=\"confirm-ok\"", html);
        Assert.Contains("id=\"confirm-cancel\"", html);

        using var anonymous = factory.CreateAnonymousClient();
        var login = await anonymous.GetStringAsync("/Account/Login");
        Assert.DoesNotContain("id=\"confirm\"", login);
    }

    [Fact]
    public async Task Site_css_defines_the_danger_text_role_the_overflow_menu_and_the_confirm_dialog()
    {
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains(".text.danger", css);
        Assert.Contains(".menu-item.danger", css);
        Assert.Contains("details.menu", css);
        Assert.Contains("dialog#confirm", css);
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
    public async Task Site_css_pins_the_root_font_size_and_defines_the_overhaul_tokens()
    {
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains("--pico-font-size: 100%", css);
        Assert.Contains("--it-text-sm: 0.875rem", css);
        Assert.Contains("--it-accent: #4f46e5", css);
        Assert.Contains(".badge-overdue", css);
        Assert.Contains(".card-head", css);
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
        Assert.Contains("hx-confirm=\"Deactivate this part?\"", html);
        Assert.Contains("No parts yet", html);                  // _EmptyState
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
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Dev/Overhaul")).StatusCode);
    }

    [Fact]
    public async Task The_style_guide_shows_each_shared_pattern_on_the_real_stylesheet()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Dev/Components");

        Assert.Contains("/css/site.css", html);
        Assert.DoesNotContain("overhaul.css", html);
        foreach (var marker in new[]
        {
            "class=\"text danger\"", "class=\"secondary outline\"", "class=\"danger\"",   // button roles
            "<details class=\"menu\">", "hx-confirm=", "id=\"confirm\"",                  // menu + confirm dialog
            "badge-overdue", "badge-draft", "badge-cancelled",                            // chips
            "class=\"card stat", "class=\"timeline\"", "class=\"kv\"",                    // cards, stat, timeline
            "class=\"tabs\"", "class=\"section-nav\"",                                    // tabs
            "alert alert-error", "class=\"field-error\"",                                 // alert, field error
        })
        {
            Assert.Contains(marker, html);
        }
    }

    [Fact]
    public async Task The_prototype_page_and_its_stylesheet_are_gone_so_site_css_is_the_only_app_stylesheet()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Dev/Overhaul")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/css/overhaul.css")).StatusCode);
        foreach (var page in new[] { "/", "/Products", "/PurchaseOrders", "/Dev/Components" })
        {
            Assert.DoesNotContain("overhaul.css", await client.GetStringAsync(page));
        }
    }

    [Fact]
    public async Task Site_css_carries_the_narrow_screen_touch_and_print_rules()
    {
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains("@media (max-width: 64rem)", css);   // rail becomes a drawer
        Assert.Contains("@media (max-width: 80rem)", css);   // side columns drop under the main card
        Assert.Contains("@media (max-width: 40rem)", css);   // header actions stack, filters full width
        Assert.Contains("@media (pointer: coarse)", css);
        Assert.Contains("min-height: 2.75rem", css);
        Assert.Contains("position: sticky; left: 0", css);   // first column stays in view in a scrolling table

        var printStart = css.IndexOf("@media print {\n  .rail", StringComparison.Ordinal);
        if (printStart < 0)
        {
            printStart = css.IndexOf("@media print {\r\n  .rail", StringComparison.Ordinal);
        }

        Assert.True(printStart >= 0, "a print block hides the app chrome");
        var hide = css[printStart..css.IndexOf("display: none !important", printStart, StringComparison.Ordinal)];
        foreach (var hidden in new[] { ".rail", ".topbar", "#flash", "#modal", "#confirm", "details.menu", "button", ".toolbar", ".tabs", ".section-nav", ".page-actions" })
        {
            Assert.Contains(hidden, hide);
        }
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
