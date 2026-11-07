using InventoryTracker.Services;

namespace InventoryTracker.Tests.Services;

public class ResultTests
{
    [Fact]
    public void Success_has_no_errors()
    {
        var result = Result.Success();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Failure_carries_errors_with_optional_field()
    {
        var result = Result.Failure("sku.duplicate", "SKU already exists", "Sku");

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal(new Error("sku.duplicate", "SKU already exists", "Sku"), error);
    }

    [Fact]
    public void Generic_result_exposes_value_only_on_success()
    {
        var ok = Result.Success(42);
        var failed = Result.Failure<int>("nope", "No");

        Assert.Equal(42, ok.Value);
        Assert.False(failed.Succeeded);
        Assert.Throws<InvalidOperationException>(() => failed.Value);
    }

    [Fact]
    public void Failure_requires_at_least_one_error()
    {
        Assert.Throws<ArgumentException>(() => Result.Failure());
        Assert.Throws<ArgumentException>(() => Result.Failure<int>());
    }
}
