using InventoryTracker.Services;

namespace InventoryTracker.Tests.Services;

/// <summary>The status math as a table, with no database (F10 FR-4).</summary>
public sealed class BomStatusCalculatorTests
{
    // perUnit, remaining, onHand, onOrder, inDraft  ->  needed, short, toOrder, state
    [Theory]
    [InlineData(2, 5, 10, 0, 0, 10, 0, 0, LineState.Ready)] // on hand covers exactly
    [InlineData(2, 5, 50, 0, 0, 10, 0, 0, LineState.Ready)] // plenty
    [InlineData(2, 5, 4, 6, 0, 10, 6, 0, LineState.Covered)] // on order covers the rest
    [InlineData(2, 5, 4, 10, 0, 10, 6, 0, LineState.Covered)] // more on order than needed
    [InlineData(2, 5, 4, 3, 0, 10, 6, 3, LineState.Short)] // partly covered by orders
    [InlineData(2, 5, 4, 3, 3, 10, 6, 3, LineState.InDraft)] // a draft covers the rest, but toOrder is unchanged
    [InlineData(2, 5, 4, 3, 2, 10, 6, 3, LineState.Short)] // a draft that covers only part
    [InlineData(2, 5, 0, 0, 0, 10, 10, 10, LineState.Short)] // nothing at all
    [InlineData(2, 5, 0, 0, 10, 10, 10, 10, LineState.InDraft)] // only a draft
    [InlineData(2, 5, 0, 0, 50, 10, 10, 10, LineState.InDraft)] // an oversized draft is still a draft, not Ready
    [InlineData(3, 0, 0, 0, 0, 0, 0, 0, LineState.Ready)] // nothing left to build
    [InlineData(1, 1, -2, 0, 0, 1, 1, 1, LineState.Short)] // negative on hand (allowed by settings) counts as none
    public void A_line_is_calculated_from_what_is_needed_and_where_it_stands(
        int perUnit, int remaining, int onHand, int onOrder, int inDraft, long needed, long shortBy, long toOrder, LineState state)
    {
        var calc = BomStatusCalculator.Line(perUnit, remaining, onHand, onOrder, inDraft);

        Assert.Equal(needed, calc.Needed);
        Assert.Equal(shortBy, calc.Short);
        Assert.Equal(toOrder, calc.ToOrder);
        Assert.Equal(state, calc.State);
    }

    [Fact]
    public void Drafts_never_reduce_what_is_left_to_order()
    {
        var without = BomStatusCalculator.Line(1, 10, 2, 3, 0);
        var with = BomStatusCalculator.Line(1, 10, 2, 3, 100);

        Assert.Equal(without.ToOrder, with.ToOrder);
        Assert.Equal(without.Short, with.Short);
    }

    [Fact]
    public void Big_quantities_do_not_overflow()
    {
        var calc = BomStatusCalculator.Line(1_000_000, 100_000, 5, 0, 0);

        Assert.Equal(100_000_000_000L, calc.Needed);
        Assert.Equal(LineState.Short, calc.State);
    }

    [Theory]
    [InlineData(10, 0, 10)]
    [InlineData(10, 4, 6)]
    [InlineData(10, 10, 0)]
    [InlineData(10, 14, 0)]
    public void Remaining_units_are_the_target_less_what_was_built_never_below_zero(int target, int built, int remaining)
        => Assert.Equal(remaining, BomStatusCalculator.RemainingUnits(target, built));

    [Fact]
    public void The_project_state_is_its_worst_line()
    {
        Assert.Equal(ProjectState.Ready, BomStatusCalculator.Project(2, 1, [LineState.Ready, LineState.Ready]));
        Assert.Equal(ProjectState.Covered, BomStatusCalculator.Project(2, 1, [LineState.Ready, LineState.Covered]));
        Assert.Equal(ProjectState.InDraft, BomStatusCalculator.Project(3, 1, [LineState.Ready, LineState.Covered, LineState.InDraft]));
        Assert.Equal(ProjectState.Short, BomStatusCalculator.Project(3, 1, [LineState.InDraft, LineState.Short, LineState.Ready]));
    }

    [Fact]
    public void A_project_with_no_lines_is_empty_and_one_with_nothing_remaining_is_built()
    {
        Assert.Equal(ProjectState.Empty, BomStatusCalculator.Project(0, 3, []));
        Assert.Equal(ProjectState.Built, BomStatusCalculator.Project(2, 0, [LineState.Ready, LineState.Ready]));
        Assert.Equal(ProjectState.Built, BomStatusCalculator.Project(2, 0, [LineState.Short, LineState.Short]));
    }
}

public sealed class MinOrderRoundingTests
{
    [Theory]
    [InlineData(3, 10, 10)]
    [InlineData(10, 10, 10)]
    [InlineData(25, 10, 25)]
    [InlineData(3, 1, 3)]
    [InlineData(3, 0, 3)]
    [InlineData(0, 10, 0)]
    public void A_quantity_is_raised_to_the_minimum_order_quantity(int quantity, int minimum, int expected)
        => Assert.Equal(expected, MinOrderRounding.RoundUp(quantity, minimum));

    [Fact]
    public void No_minimum_leaves_the_quantity_alone()
        => Assert.Equal(7, MinOrderRounding.RoundUp(7, null));
}
