using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-03-3 helpers: browser-time conversion, mapping service errors onto forms, the bin filter and the user filter list.</summary>
public class StockSupportTests : StockTestBase
{
    // ---- LocalTimeInput ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 9)]    // browser on UTC
    [InlineData(-120, 7)] // browser at UTC+2: getTimezoneOffset() is -120, local 09:00 is 07:00 UTC
    [InlineData(300, 14)] // browser at UTC-5
    public void A_browser_offset_is_added_to_local_time_to_get_utc(int offset, int expectedUtcHour)
    {
        var utc = LocalTimeInput.ToUtc(new DateTime(2026, 3, 10, 9, 0, 0), offset);

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 3, 10, expectedUtcHour, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void A_missing_or_absurd_offset_falls_back_to_the_server_zone()
    {
        var local = new DateTime(2026, 6, 10, 9, 0, 0);
        var expected = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);

        Assert.Equal(expected, LocalTimeInput.ToUtc(local, null));
        Assert.Equal(expected, LocalTimeInput.ToUtc(local, 99_999));
        Assert.Equal(DateTimeKind.Utc, LocalTimeInput.ToUtc(local, null).Kind);
    }

    [Fact]
    public void Dates_at_the_edge_of_the_range_do_not_throw()
    {
        var utc = LocalTimeInput.ToUtc(DateTime.MinValue, null);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(DateTimeKind.Utc, LocalTimeInput.ToUtc(DateTime.MaxValue, 0).Kind);
    }

    [Fact]
    public void A_day_range_is_inclusive_of_both_days_and_open_at_the_end()
    {
        // Browser at UTC+2 (offset -120): local midnight 10 Mar is 22:00 UTC on 9 Mar.
        var (from, to) = LocalTimeInput.DayRange(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 12), -120);

        Assert.Equal(new DateTime(2026, 3, 9, 22, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 3, 12, 22, 0, 0, DateTimeKind.Utc), to); // exclusive: start of 13 Mar local
    }

    [Fact]
    public void An_open_ended_day_range_stays_open()
    {
        Assert.Equal((null, null), LocalTimeInput.DayRange(null, null, 0));
        Assert.Null(LocalTimeInput.DayRange(null, DateOnly.MaxValue, 0).ToUtcExclusive);
    }

    // ---- AddStockErrors ------------------------------------------------------------------------------------------

    private static Result Failed(params (string Message, string? Field)[] errors)
        => Result.Failure([.. errors.Select(e => new Error("stock.test", e.Message, e.Field))]);

    private static string[] KeysOf(ModelStateDictionary state) => [.. state.Where(e => e.Value!.Errors.Count > 0).Select(e => e.Key)];

    [Fact]
    public void A_single_line_form_drops_the_line_index_and_maps_product_to_the_product_id_input()
    {
        var state = new ModelStateDictionary();
        state.AddStockErrors(Failed(("No.", StockFields.Line(0, StockFields.Product)), ("Too many.", StockFields.Line(0, StockFields.Quantity))), "Input");

        Assert.Equal(["Input.ProductId", "Input.Quantity"], KeysOf(state).Order());
    }

    [Fact]
    public void A_multi_line_form_keeps_the_index_and_labels_the_message()
    {
        var state = new ModelStateDictionary();
        state.AddStockErrors(Failed(("Too many.", StockFields.Line(2, StockFields.Quantity))), "Input", keepLineIndex: true, lineLabel: i => $"Line {i + 1}: ");

        Assert.Equal(["Input.Lines[2].Quantity"], KeysOf(state));
        Assert.Equal("Line 3: Too many.", state["Input.Lines[2].Quantity"]!.Errors.Single().ErrorMessage);
    }

    [Fact]
    public void Without_a_prefix_errors_are_form_level_but_still_labelled()
    {
        var state = new ModelStateDictionary();
        state.AddStockErrors(Failed(("Bad.", StockFields.Line(1, StockFields.Quantity)), ("General.", null)), prefix: null, lineLabel: i => $"Row {i}: ");

        var messages = state[string.Empty]!.Errors.Select(e => e.ErrorMessage).ToArray();
        Assert.Equal(["Row 1: Bad.", "General."], messages);
    }

    [Fact]
    public void An_unindexed_field_keeps_its_name_under_the_prefix()
    {
        var state = new ModelStateDictionary();
        state.AddStockErrors(Failed(("Stuck.", StockFields.Unindexed(StockFields.Reason))), "Input");

        Assert.Equal(["Input.Reason"], KeysOf(state));
    }

    // ---- movement users ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_users_who_posted_something_are_offered_as_a_filter()
    {
        var product = await NewProductAsync("U-1");
        var (context, stock) = NewStock();
        await using (context)
        {
            Assert.Empty(await stock.ListMovementUsersAsync());
            Assert.True((await stock.PostAsync([Receive(product, 1)], Who)).Succeeded);
        }

        var (context2, stock2) = NewStock();
        await using (context2)
        {
            var user = Assert.Single(await stock2.ListMovementUsersAsync());
            Assert.Equal(UserId, user.Id);
            Assert.Equal("Mo Ver", user.Name);
        }
    }

    // ---- bin filter and sort -------------------------------------------------------------------------------------

    private async Task<PagedResult<ProductListItem>> SearchAsync(ProductListQuery query)
    {
        var (context, products, _) = NewRequest();
        await using (context)
        {
            return await products.SearchAsync(query);
        }
    }

    [Fact]
    public async Task The_bin_filter_matches_part_of_a_bin_location_case_insensitively()
    {
        await CreateProductAsync(Input("B-1", bin: "A-01-02"));
        await CreateProductAsync(Input("B-2", bin: "a-01-09"));
        await CreateProductAsync(Input("B-3", bin: "C-07"));
        await CreateProductAsync(Input("B-4"));

        var page = await SearchAsync(new ProductListQuery { Bin = "A-01" });

        Assert.Equal(["B-1", "B-2"], page.Items.Select(p => p.Sku).Order());
    }

    [Fact]
    public async Task Sorting_by_bin_puts_unbinned_products_last_in_both_directions()
    {
        await CreateProductAsync(Input("S-1", name: "Zed", bin: "B-2"));
        await CreateProductAsync(Input("S-2", name: "Amy", bin: "A-1"));
        await CreateProductAsync(Input("S-3", name: "Bob"));

        var ascending = await SearchAsync(new ProductListQuery { Sort = "bin" });
        Assert.Equal(["S-2", "S-1", "S-3"], ascending.Items.Select(p => p.Sku));

        var descending = await SearchAsync(new ProductListQuery { Sort = "bin", Desc = true });
        Assert.Equal("S-3", descending.Items[^1].Sku);
        Assert.Equal(["S-1", "S-2"], descending.Items.Take(2).Select(p => p.Sku));
    }
}
