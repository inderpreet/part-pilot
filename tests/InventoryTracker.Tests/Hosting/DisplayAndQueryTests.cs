using InventoryTracker.Data;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Hosting;

public class MoneyFormatterTests
{
    [Theory]
    [InlineData(0L, "$0.00")]
    [InlineData(5L, "$0.05")]
    [InlineData(100L, "$1.00")]
    [InlineData(123456L, "$1,234.56")]
    [InlineData(100000000L, "$1,000,000.00")]
    [InlineData(-250099L, "-$2,500.99")]
    [InlineData(-1L, "-$0.01")]
    public void Formats_cents_with_symbol_grouping_and_sign(long cents, string expected)
        => Assert.Equal(expected, MoneyFormatter.Format(cents, "$"));

    [Fact]
    public void Uses_the_configured_symbol()
        => Assert.Equal("€12.00", MoneyFormatter.Format(1200, "€"));

    [Fact]
    public void Handles_extreme_values_without_overflow()
        => Assert.StartsWith("-", MoneyFormatter.Format(long.MinValue, "$"));
}

public class ListQueryTests
{
    [Fact]
    public void Defaults_are_sane()
    {
        var q = new ListQuery();

        Assert.Equal(1, q.SafePage);
        Assert.Equal(ListQuery.DefaultPageSize, q.SafePageSize);
        Assert.Equal(0, q.Skip);
        Assert.Null(q.SearchTerm);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    public void Page_is_clamped_to_at_least_one(int page, int expected)
        => Assert.Equal(expected, new ListQuery { Page = page }.SafePage);

    [Theory]
    [InlineData(0, ListQuery.DefaultPageSize)]
    [InlineData(-1, ListQuery.DefaultPageSize)]
    [InlineData(50, 50)]
    [InlineData(100000, ListQuery.MaxPageSize)]
    public void Page_size_is_clamped(int pageSize, int expected)
        => Assert.Equal(expected, new ListQuery { PageSize = pageSize }.SafePageSize);

    [Fact]
    public void Skip_and_search_term_are_derived()
    {
        var q = new ListQuery { Page = 3, PageSize = 10, Search = "  bolt " };

        Assert.Equal(20, q.Skip);
        Assert.Equal("bolt", q.SearchTerm);
    }

    [Fact]
    public void Sort_comparison_is_case_insensitive()
        => Assert.True(new ListQuery { Sort = "Name" }.IsSortedBy("name"));
}

public class PagedResultTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;

    public async Task InitializeAsync() => _db = await SqliteTestDb.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // The migrations seed AppSettings rows (6 now; the newest is left out to keep the arithmetic at 5), which is all the data these paging tests need.
    private IQueryable<string> Keys(AppDbContext context) => context.AppSettings.Where(s => s.Key != SettingKeys.ProjectNumberPrefix).OrderBy(s => s.Key).Select(s => s.Key);

    [Fact]
    public async Task Returns_the_requested_page_and_totals()
    {
        await using var context = _db.CreateContext();

        var result = await PagedResult<string>.CreateAsync(Keys(context), new ListQuery { Page = 2, PageSize = 2 });

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(5, result.TotalCount);
        Assert.Equal(3, result.TotalPages);
        Assert.Equal(2, result.Page);
        Assert.True(result.HasPrevious);
        Assert.True(result.HasNext);
        Assert.Equal(3, result.FirstItem);
        Assert.Equal(4, result.LastItem);
    }

    [Fact]
    public async Task Last_page_may_be_partial()
    {
        await using var context = _db.CreateContext();

        var result = await PagedResult<string>.CreateAsync(Keys(context), new ListQuery { Page = 3, PageSize = 2 });

        Assert.Single(result.Items);
        Assert.False(result.HasNext);
    }

    [Fact]
    public async Task Page_past_the_end_snaps_to_the_last_page()
    {
        await using var context = _db.CreateContext();

        var result = await PagedResult<string>.CreateAsync(Keys(context), new ListQuery { Page = 99, PageSize = 2 });

        Assert.Equal(3, result.Page);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Empty_result_has_no_items_and_zero_ranges()
    {
        await using var context = _db.CreateContext();

        var result = await PagedResult<string>.CreateAsync(Keys(context).Where(k => k == "nope"), new ListQuery());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.TotalPages);
        Assert.Equal(1, result.Page);
        Assert.Equal(0, result.FirstItem);
        Assert.Equal(0, result.LastItem);
    }
}

public class HtmxExtensionsTests
{
    [Fact]
    public void IsHtmx_checks_the_HX_Request_header()
    {
        var plain = new DefaultHttpContext();
        var htmx = new DefaultHttpContext();
        htmx.Request.Headers["HX-Request"] = "true";

        Assert.False(plain.Request.IsHtmx());
        Assert.True(htmx.Request.IsHtmx());
    }

    [Fact]
    public void HxTrigger_serializes_event_with_detail()
    {
        var context = new DefaultHttpContext();

        context.Response.HxTrigger("flash", "Saved");

        Assert.Equal("{\"flash\":\"Saved\"}", context.Response.Headers["HX-Trigger"].ToString());
    }

    [Fact]
    public void HxTrigger_without_detail_sends_true_and_merges_multiple_events()
    {
        var context = new DefaultHttpContext();

        context.Response.HxTrigger("flash", "Saved");
        context.Response.HxTrigger("closeModal");

        var header = context.Response.Headers["HX-Trigger"].ToString();
        Assert.Contains("\"flash\":\"Saved\"", header);
        Assert.Contains("\"closeModal\":true", header);
    }
}
