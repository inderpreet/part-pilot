using InventoryTracker.Services;

namespace InventoryTracker.Pages.Shared;

/// <summary>How project and BOM line states are shown, so the list, project and product pages agree.</summary>
public static class ProjectDisplay
{
    public static string StateLabel(ProjectState state) => state switch
    {
        ProjectState.Empty => "No parts",
        ProjectState.Ready => "Ready to build",
        ProjectState.Covered => "Covered by orders",
        ProjectState.InDraft => "In draft PO",
        ProjectState.Short => "Short",
        ProjectState.Built => "Built",
        _ => state.ToString(),
    };

    /// <summary>The <c>badge-*</c> CSS class of a project state (site.css).</summary>
    public static string StateCss(ProjectState state) => state switch
    {
        ProjectState.Ready => "badge-ok",
        ProjectState.Covered => "badge-partial",
        ProjectState.InDraft => "badge-draft",
        ProjectState.Short => "badge-out",
        ProjectState.Built => "badge-received",
        _ => "badge-inactive",
    };

    public static string LineLabel(LineState state) => state switch
    {
        LineState.Ready => "Ready",
        LineState.Covered => "Covered",
        LineState.InDraft => "In draft",
        LineState.Short => "Short",
        _ => state.ToString(),
    };

    public static string LineCss(LineState state) => state switch
    {
        LineState.Ready => "badge-ok",
        LineState.Covered => "badge-partial",
        LineState.InDraft => "badge-draft",
        _ => "badge-out",
    };

    public static string TabLabel(ProjectTab tab) => tab switch
    {
        ProjectTab.Active => "Active",
        ProjectTab.Archived => "Archived",
        _ => "All",
    };

    /// <summary>"3 short, 2 in draft PO, 1 on order": only the groups that exist. Empty for a project without parts.</summary>
    public static string Summary(ProjectStatusSummary summary)
    {
        if (summary.State is ProjectState.Empty)
        {
            return "No parts in the BOM yet";
        }

        if (summary.State is ProjectState.Built)
        {
            return "All units built";
        }

        var parts = new List<string>(4);
        if (summary.Short > 0)
        {
            parts.Add($"{summary.Short} short");
        }

        if (summary.InDraft > 0)
        {
            parts.Add($"{summary.InDraft} in draft PO");
        }

        if (summary.Covered > 0)
        {
            parts.Add($"{summary.Covered} on order");
        }

        if (summary.Ready > 0)
        {
            parts.Add($"{summary.Ready} in stock");
        }

        return string.Join(", ", parts);
    }
}
