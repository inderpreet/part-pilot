using System.Globalization;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface IProjectService
{
    // ---- queries -----------------------------------------------------------------------------------------------

    /// <summary>Projects (matched on name or number) with their parts count, built units, BOM cost and state.</summary>
    Task<PagedResult<ProjectListItem>> SearchAsync(ProjectListQuery query, CancellationToken cancellationToken = default);

    Task<ProjectDetail?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Recorded builds, newest first.</summary>
    Task<IReadOnlyList<ProjectBuildItem>> GetBuildsAsync(int projectId, CancellationToken cancellationToken = default);

    /// <summary>The BOM checked against stock, submitted orders and Draft orders; <c>null</c> when the project does not exist.</summary>
    Task<BomStatus?> GetStatusAsync(int projectId, CancellationToken cancellationToken = default);

    /// <summary>The projects whose BOM lists this product (the product page's "Used in projects"), archived ones last.</summary>
    Task<IReadOnlyList<ProductProjectUse>> GetProjectsUsingProductAsync(int productId, CancellationToken cancellationToken = default);
    /// <summary>Line-state counts for several projects at once (the list page): a fixed number of queries, not one per project.</summary>
    Task<IReadOnlyDictionary<int, ProjectStatusSummary>> GetStatusSummariesAsync(IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken = default);

    // ---- numbering ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Issues the next project number for <paramref name="year"/> (<c>PRJ-2026-0001</c>). Inside the caller's transaction when
    /// one is open, so a failed save hands the number back; a committed number is never issued again.
    /// </summary>
    Task<string> NextNumberAsync(int year, CancellationToken cancellationToken = default);

    // ---- projects ----------------------------------------------------------------------------------------------

    /// <summary>Creates a project. A blank number is generated; a typed one is kept as is and does not use the sequence.</summary>
    Task<Result<ProjectDetail>> CreateAsync(ProjectInput input, ProjectActor actor, CancellationToken cancellationToken = default);

    /// <summary>Saves the edit if <paramref name="rowVersion"/> is still current. Never changes the number.</summary>
    Task<Result<ProjectDetail>> UpdateAsync(int id, long rowVersion, ProjectInput input, CancellationToken cancellationToken = default);

    /// <summary>Admin only. Outside systems may refer to the old number, so this is a separate, deliberate action.</summary>
    Task<Result<ProjectDetail>> ChangeNumberAsync(int id, long rowVersion, string? newNumber, ProjectActor actor, CancellationToken cancellationToken = default);

    Task<Result> SetActiveAsync(int id, bool isActive, long? rowVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a project with its BOM. Refused once it has builds (archive it instead). Idempotent for a project that is already gone.</summary>
    Task<Result> DeleteAsync(int id, long? rowVersion = null, CancellationToken cancellationToken = default);

    // ---- BOM ---------------------------------------------------------------------------------------------------

    Task<Result<BomLineItem>> AddLineAsync(int projectId, BomLineInput input, ProjectActor actor, CancellationToken cancellationToken = default);

    Task<Result<BomLineItem>> UpdateLineAsync(int projectId, long lineId, int quantityPerUnit, string? note, ProjectActor actor, CancellationToken cancellationToken = default);

    /// <summary>Removes a line. Idempotent for a line that is already gone.</summary>
    Task<Result> RemoveLineAsync(int projectId, long lineId, ProjectActor actor, CancellationToken cancellationToken = default);

    /// <summary>Creates a catalog product and adds it to the BOM in one transaction; a failure leaves neither.</summary>
    Task<Result<BomLineItem>> CreatePartAndAddAsync(int projectId, NewPartInput part, int quantityPerUnit, ProjectActor actor, CancellationToken cancellationToken = default);

    // ---- ordering shortages ------------------------------------------------------------------------------------

    /// <summary>
    /// Applies the user's decision for each short line: <see cref="ShortageAction.Add"/> puts it on <paramref name="target"/>
    /// (an existing Draft, or a new draft for the chosen supplier; needed only when some decision adds),
    /// <see cref="ShortageAction.UpdateQuantity"/> sets the quantity of the line on the named draft, <see cref="ShortageAction.Skip"/>
    /// does nothing. Quantities are raised to the supplier's minimum order quantity. All or nothing: a draft that is no
    /// longer a draft refuses the whole call. Parts that cannot be added are reported as skipped.
    /// </summary>
    Task<Result<AddShortagesResult>> AddShortagesToOrderAsync(
        int projectId,
        IReadOnlyList<ShortageDecision> decisions,
        ShortageTarget? target,
        PurchaseOrderActor actor,
        CancellationToken cancellationToken = default);

    // ---- build -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds <paramref name="units"/> units: re-reads stock, refuses with one error per short part when anything is short,
    /// otherwise issues every part through the stock service and records the build, all in one database transaction.
    /// </summary>
    Task<Result<BuildResult>> BuildAsync(int projectId, int units, string? note, StockContext context, CancellationToken cancellationToken = default);
}

public sealed class ProjectService(
    AppDbContext db,
    IStockService stock,
    IPurchaseOrderService purchaseOrders,
    IProductService products,
    ISettingsService settings,
    IClock clock,
    ProjectEventPublisher events) : IProjectService
{
    /// <summary>Name of the numbering series in <see cref="DocumentSequence"/>.</summary>
    public const string SequenceName = "Project";

    // ---- queries -----------------------------------------------------------------------------------------------

    public async Task<PagedResult<ProjectListItem>> SearchAsync(ProjectListQuery query, CancellationToken cancellationToken = default)
    {
        var projects = db.Projects.AsNoTracking().AsQueryable();

        projects = query.Tab switch
        {
            ProjectTab.Active => projects.Where(p => p.IsActive),
            ProjectTab.Archived => projects.Where(p => !p.IsActive),
            _ => projects,
        };

        if (query.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            projects = projects.Where(p => EF.Functions.Like(p.Name, pattern, LikePattern.Escape) || EF.Functions.Like(p.Number, pattern, LikePattern.Escape));
        }

        // Every ordering ends in Id so paging is stable when the sort column has ties.
        var ordered = (query.Sort?.ToLowerInvariant(), query.Desc) switch
        {
            ("number", false) => projects.OrderBy(p => p.Number),
            ("number", true) => projects.OrderByDescending(p => p.Number),
            ("target", false) => projects.OrderBy(p => p.TargetUnits).ThenBy(p => p.Name),
            ("target", true) => projects.OrderByDescending(p => p.TargetUnits).ThenBy(p => p.Name),
            ("updated", false) => projects.OrderBy(p => p.UpdatedAt),
            ("updated", true) => projects.OrderByDescending(p => p.UpdatedAt),
            ("name", true) => projects.OrderByDescending(p => p.Name),
            _ => projects.OrderBy(p => p.Name),
        };

        var page = await PagedResult<ProjectRow>.CreateAsync(
            ordered.ThenBy(p => p.Id).Select(p => new ProjectRow(
                p.Id,
                p.Name,
                p.Number,
                p.TargetUnits,
                p.Builds.Sum(b => b.Units),
                p.IsActive,
                p.Lines.Count,
                p.Lines.Sum(l => (long)l.QuantityPerUnit * l.Product!.UnitCostCents),
                p.UpdatedAt)),
            query,
            cancellationToken);

        var summaries = await GetStatusSummariesAsync([.. page.Items.Select(r => r.Id)], cancellationToken);
        var items = page.Items
            .Select(r => new ProjectListItem(
                r.Id, r.Name, r.Number, r.TargetUnits, r.BuiltUnits, r.IsActive, r.PartsCount, r.BomCostCents,
                summaries.TryGetValue(r.Id, out var summary) ? summary : ProjectStatusSummary.Empty,
                r.UpdatedAt))
            .ToList();

        return new PagedResult<ProjectListItem>(items, page.Page, page.PageSize, page.TotalCount);
    }

    private sealed record ProjectRow(int Id, string Name, string Number, int TargetUnits, int BuiltUnits, bool IsActive, int PartsCount, long BomCostCents, DateTime UpdatedAt);

    public async Task<ProjectDetail?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var header = await db.Projects.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Number,
                p.Description,
                p.TargetUnits,
                p.Notes,
                p.IsActive,
                p.RowVersion,
                p.CreatedAt,
                p.UpdatedAt,
                BuiltUnits = p.Builds.Sum(b => b.Units),
                BuildCount = p.Builds.Count,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var lines = await LineItems(db.BomLines.AsNoTracking().Where(l => l.ProjectId == id).OrderBy(l => l.Id)).ToListAsync(cancellationToken);

        return new ProjectDetail(
            header.Id, header.Name, header.Number, header.Description, header.TargetUnits, header.Notes, header.IsActive,
            header.RowVersion, header.CreatedAt, header.UpdatedAt, header.BuiltUnits, header.BuildCount, lines);
    }

    public async Task<IReadOnlyList<ProjectBuildItem>> GetBuildsAsync(int projectId, CancellationToken cancellationToken = default)
        => await db.ProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.Id)
            .Select(b => new ProjectBuildItem(b.Id, b.Units, b.User!.DisplayName, b.Note, b.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductProjectUse>> GetProjectsUsingProductAsync(int productId, CancellationToken cancellationToken = default)
        => await db.BomLines.AsNoTracking()
            .Where(l => l.ProductId == productId)
            .OrderByDescending(l => l.Project!.IsActive).ThenBy(l => l.Project!.Name)
            .Select(l => new ProductProjectUse(l.ProjectId, l.Project!.Number, l.Project.Name, l.Project.IsActive, l.QuantityPerUnit))
            .ToListAsync(cancellationToken);
    public async Task<BomStatus?> GetStatusAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var all = await ComputeStatusAsync([projectId], cancellationToken);
        return all.TryGetValue(projectId, out var status) ? status : null;
    }

    public async Task<IReadOnlyDictionary<int, ProjectStatusSummary>> GetStatusSummariesAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken = default)
    {
        var all = await ComputeStatusAsync(projectIds, cancellationToken);
        return all.ToDictionary(p => p.Key, p => p.Value.Summary);
    }

    /// <summary>Loads the data for the projects in a fixed number of queries and runs the pure status math over it.</summary>
    private async Task<Dictionary<int, BomStatus>> ComputeStatusAsync(IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, BomStatus>();
        if (projectIds.Count == 0)
        {
            return result;
        }

        var ids = projectIds.Distinct().ToList();
        var targets = await db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.TargetUnits }).ToListAsync(cancellationToken);
        var built = (await db.ProjectBuilds.AsNoTracking()
                .Where(b => ids.Contains(b.ProjectId))
                .GroupBy(b => b.ProjectId)
                .Select(g => new { ProjectId = g.Key, Units = g.Sum(b => b.Units) })
                .ToListAsync(cancellationToken))
            .ToDictionary(b => b.ProjectId, b => b.Units);

        var lines = await db.BomLines.AsNoTracking()
            .Where(l => ids.Contains(l.ProjectId))
            .OrderBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.ProjectId,
                l.ProductId,
                l.Product!.Sku,
                ProductName = l.Product.Name,
                l.Product.Unit,
                ProductIsActive = l.Product.IsActive,
                l.QuantityPerUnit,
                l.Note,
                l.Product.UnitCostCents,
                l.Product.QuantityOnHand,
            })
            .ToListAsync(cancellationToken);

        var productIds = lines.Select(l => l.ProductId).Distinct().ToList();
        var onOrder = productIds.Count == 0 ? new Dictionary<int, int>() : (await purchaseOrders.GetOnOrderByProductAsync(cancellationToken)).ToDictionary(p => p.Key, p => p.Value);
        var inDraft = await purchaseOrders.GetInDraftByProductAsync(productIds, cancellationToken);

        foreach (var project in targets)
        {
            var builtUnits = built.GetValueOrDefault(project.Id);
            var remaining = BomStatusCalculator.RemainingUnits(project.TargetUnits, builtUnits);
            var statuses = lines
                .Where(l => l.ProjectId == project.Id)
                .Select(l =>
                {
                    var drafts = inDraft.TryGetValue(l.ProductId, out var d) ? d : null;
                    var onOrderQuantity = onOrder.GetValueOrDefault(l.ProductId);
                    var calc = BomStatusCalculator.Line(l.QuantityPerUnit, remaining, l.QuantityOnHand, onOrderQuantity, drafts?.Total ?? 0);
                    return new BomLineStatus(
                        l.Id, l.ProductId, l.Sku, l.ProductName, l.Unit, l.ProductIsActive, l.QuantityPerUnit, l.Note, l.UnitCostCents,
                        calc.Needed, l.QuantityOnHand, onOrderQuantity, drafts?.Total ?? 0, drafts?.Orders ?? [], calc.Short, calc.ToOrder, calc.State);
                })
                .ToList();

            result[project.Id] = new BomStatus(
                project.Id,
                project.TargetUnits,
                builtUnits,
                remaining,
                BomStatusCalculator.Project(statuses.Count, remaining, statuses.Select(s => s.State)),
                statuses);
        }

        return result;
    }

    // ---- numbering ---------------------------------------------------------------------------------------------

    public async Task<string> NextNumberAsync(int year, CancellationToken cancellationToken = default)
    {
        var prefix = await settings.GetStringAsync(SettingKeys.ProjectNumberPrefix, cancellationToken);
        if (db.Database.CurrentTransaction is not null)
        {
            return await IssueNumberAsync(prefix, year, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var number = await IssueNumberAsync(prefix, year, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return number;
    }

    /// <summary>
    /// Takes the next value of the year's counter. The increment is a single UPDATE so it holds the write lock before the value
    /// is read back; the first number of a year inserts the counter row.
    /// </summary>
    private async Task<string> IssueNumberAsync(string prefix, int year, CancellationToken cancellationToken)
    {
        var bumped = await db.DocumentSequences
            .Where(s => s.Name == SequenceName && s.Year == year)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextValue, x => x.NextValue + 1), cancellationToken);

        int value;
        if (bumped == 0)
        {
            var sequence = new DocumentSequence { Name = SequenceName, Year = year, NextValue = 2 };
            db.DocumentSequences.Add(sequence);
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(sequence).State = EntityState.Detached;
            value = 1;
        }
        else
        {
            value = await db.DocumentSequences
                .Where(s => s.Name == SequenceName && s.Year == year)
                .Select(s => s.NextValue)
                .SingleAsync(cancellationToken) - 1;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{year:D4}-{value:D4}");
    }

    // ---- projects ----------------------------------------------------------------------------------------------

    public async Task<Result<ProjectDetail>> CreateAsync(ProjectInput input, ProjectActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);

        var name = Clean(input.Name);
        var typedNumber = Clean(input.Number);
        var errors = ValidateProject(name, input.Description, input.TargetUnits, input.Notes);
        if (typedNumber is { Length: > Project.NumberMaxLength })
        {
            errors.Add(new Error("project.number_too_long", $"BOM number can be at most {Project.NumberMaxLength} characters.", ProjectFields.Number));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<ProjectDetail>([.. errors]);
        }

        // Read before the write transaction opens: the settings service uses its own connection.
        var prefix = await settings.GetStringAsync(SettingKeys.ProjectNumberPrefix, cancellationToken);
        var year = clock.UtcNow.ToLocalTime().Year;

        var created = await InTransactionAsync<Result<int>>(
            async () =>
            {
                if (await db.Projects.AnyAsync(p => p.Name == name, cancellationToken))
                {
                    return Result.Failure<int>(NameTaken(name!));
                }

                string number;
                if (typedNumber is not null)
                {
                    if (await db.Projects.AnyAsync(p => p.Number == typedNumber, cancellationToken))
                    {
                        return Result.Failure<int>(NumberTaken(typedNumber));
                    }

                    number = typedNumber;
                }
                else
                {
                    // A number someone typed earlier can collide with the sequence; skip past it.
                    number = await IssueNumberAsync(prefix, year, cancellationToken);
                    for (var attempt = 0; attempt < 1000 && await db.Projects.AnyAsync(p => p.Number == number, cancellationToken); attempt++)
                    {
                        number = await IssueNumberAsync(prefix, year, cancellationToken);
                    }
                }

                var project = new Project
                {
                    Name = name!,
                    Number = number,
                    Description = Clean(input.Description),
                    TargetUnits = input.TargetUnits,
                    Notes = Clean(input.Notes),
                };
                db.Projects.Add(project);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success(project.Id);
            },
            Fail<int>,
            cancellationToken);

        if (!created.Succeeded)
        {
            return Result.Failure<ProjectDetail>([.. created.Errors]);
        }

        var detail = (await GetAsync(created.Value, cancellationToken))!;
        await events.PublishAsync(Event(ProjectEventType.Created, detail.Id, detail.Number, actor, detail.Name), cancellationToken);
        return Result.Success(detail);
    }

    public async Task<Result<ProjectDetail>> UpdateAsync(int id, long rowVersion, ProjectInput input, CancellationToken cancellationToken = default)
    {
        var name = Clean(input.Name);
        var errors = ValidateProject(name, input.Description, input.TargetUnits, input.Notes);
        if (errors.Count > 0)
        {
            return Result.Failure<ProjectDetail>([.. errors]);
        }

        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
                if (project is null)
                {
                    return Result.Failure(NotFound());
                }

                if (project.RowVersion != rowVersion)
                {
                    return Result.Failure(ConflictError());
                }

                if (await db.Projects.AnyAsync(p => p.Id != id && p.Name == name, cancellationToken))
                {
                    return Result.Failure(NameTaken(name!));
                }

                project.Name = name!;
                project.Description = Clean(input.Description);
                project.TargetUnits = input.TargetUnits;
                project.Notes = Clean(input.Notes);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        return result.Succeeded
            ? Result.Success((await GetAsync(id, cancellationToken))!)
            : Result.Failure<ProjectDetail>([.. result.Errors]);
    }

    public async Task<Result<ProjectDetail>> ChangeNumberAsync(
        int id, long rowVersion, string? newNumber, ProjectActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!actor.IsAdmin)
        {
            return Result.Failure<ProjectDetail>("project.number_admin_only", "Only an administrator can change a BOM number.", ProjectFields.Number);
        }

        var number = Clean(newNumber);
        if (number is null)
        {
            return Result.Failure<ProjectDetail>("project.number_required", "Enter a BOM number.", ProjectFields.Number);
        }

        if (number.Length > Project.NumberMaxLength)
        {
            return Result.Failure<ProjectDetail>("project.number_too_long", $"BOM number can be at most {Project.NumberMaxLength} characters.", ProjectFields.Number);
        }

        string? oldNumber = null;
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
                if (project is null)
                {
                    return Result.Failure(NotFound());
                }

                if (project.RowVersion != rowVersion)
                {
                    return Result.Failure(ConflictError());
                }

                if (string.Equals(project.Number, number, StringComparison.Ordinal))
                {
                    return Result.Success(); // Nothing to change.
                }

                if (await db.Projects.AnyAsync(p => p.Id != id && p.Number == number, cancellationToken))
                {
                    return Result.Failure(NumberTaken(number));
                }

                oldNumber = project.Number;
                project.Number = number;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        if (!result.Succeeded)
        {
            return Result.Failure<ProjectDetail>([.. result.Errors]);
        }

        var detail = (await GetAsync(id, cancellationToken))!;
        if (oldNumber is not null)
        {
            await events.PublishAsync(Event(ProjectEventType.NumberChanged, id, detail.Number, actor, $"{oldNumber} -> {detail.Number}"), cancellationToken);
        }

        return Result.Success(detail);
    }

    public Task<Result> SetActiveAsync(int id, bool isActive, long? rowVersion = null, CancellationToken cancellationToken = default)
        => InTransactionAsync<Result>(
            async () =>
            {
                var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
                if (project is null)
                {
                    return Result.Failure(NotFound());
                }

                if (rowVersion is { } expected && project.RowVersion != expected)
                {
                    return Result.Failure(ConflictError());
                }

                if (project.IsActive == isActive)
                {
                    return Result.Success();
                }

                project.IsActive = isActive;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

    public Task<Result> DeleteAsync(int id, long? rowVersion = null, CancellationToken cancellationToken = default)
        => InTransactionAsync<Result>(
            async () =>
            {
                var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
                if (project is null)
                {
                    return Result.Success(); // Already gone (a double click).
                }

                if (rowVersion is { } expected && project.RowVersion != expected)
                {
                    return Result.Failure(ConflictError());
                }

                if (await db.ProjectBuilds.AnyAsync(b => b.ProjectId == id, cancellationToken))
                {
                    return Result.Failure("project.has_builds", $"{project.Name} has recorded builds, so it cannot be deleted. Archive it instead.");
                }

                db.Projects.Remove(project); // The BOM lines go with it (cascade).
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

    // ---- BOM ---------------------------------------------------------------------------------------------------

    public async Task<Result<BomLineItem>> AddLineAsync(int projectId, BomLineInput input, ProjectActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var note = Clean(input.Note);
        var errors = new List<Error>();
        ValidateLine(input.QuantityPerUnit, note, errors);
        if (errors.Count > 0)
        {
            return Result.Failure<BomLineItem>([.. errors]);
        }

        string? number = null;
        var result = await InTransactionAsync<Result<long>>(
            async () =>
            {
                var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => new { p.Number }).FirstOrDefaultAsync(cancellationToken);
                if (project is null)
                {
                    return Result.Failure<long>(NotFound());
                }

                number = project.Number;
                return await AddLineCoreAsync(projectId, input.ProductId, input.QuantityPerUnit, note, cancellationToken);
            },
            Fail<long>,
            cancellationToken);

        if (!result.Succeeded)
        {
            return Result.Failure<BomLineItem>([.. result.Errors]);
        }

        var line = (await GetLineAsync(result.Value, cancellationToken))!;
        await events.PublishAsync(Event(ProjectEventType.BomChanged, projectId, number!, actor, $"added {line.Sku} x{line.QuantityPerUnit}"), cancellationToken);
        return Result.Success(line);
    }

    public async Task<Result<BomLineItem>> UpdateLineAsync(
        int projectId, long lineId, int quantityPerUnit, string? note, ProjectActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var cleanNote = Clean(note);
        var errors = new List<Error>();
        ValidateLine(quantityPerUnit, cleanNote, errors);
        if (errors.Count > 0)
        {
            return Result.Failure<BomLineItem>([.. errors]);
        }

        string? number = null;
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var line = await db.BomLines.Include(l => l.Project).FirstOrDefaultAsync(l => l.Id == lineId && l.ProjectId == projectId, cancellationToken);
                if (line is null)
                {
                    return Result.Failure(LineMissing());
                }

                number = line.Project!.Number;
                line.QuantityPerUnit = quantityPerUnit;
                line.Note = cleanNote;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        if (!result.Succeeded)
        {
            return Result.Failure<BomLineItem>([.. result.Errors]);
        }

        var item = (await GetLineAsync(lineId, cancellationToken))!;
        await events.PublishAsync(Event(ProjectEventType.BomChanged, projectId, number!, actor, $"changed {item.Sku} to x{item.QuantityPerUnit}"), cancellationToken);
        return Result.Success(item);
    }

    public async Task<Result> RemoveLineAsync(int projectId, long lineId, ProjectActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        string? number = null;
        string? sku = null;
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var line = await db.BomLines.Include(l => l.Project).Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == lineId && l.ProjectId == projectId, cancellationToken);
                if (line is null)
                {
                    return Result.Success();
                }

                number = line.Project!.Number;
                sku = line.Product!.Sku;
                db.BomLines.Remove(line);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        if (result.Succeeded && number is not null)
        {
            await events.PublishAsync(Event(ProjectEventType.BomChanged, projectId, number, actor, $"removed {sku}"), cancellationToken);
        }

        return result;
    }

    public async Task<Result<BomLineItem>> CreatePartAndAddAsync(
        int projectId, NewPartInput part, int quantityPerUnit, ProjectActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var note = Clean(part.LineNote);
        var errors = new List<Error>();
        ValidateLine(quantityPerUnit, note, errors);
        if (errors.Count > 0)
        {
            return Result.Failure<BomLineItem>([.. errors]);
        }

        string? number = null;
        var result = await InTransactionAsync<Result<long>>(
            async () =>
            {
                var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => new { p.Number }).FirstOrDefaultAsync(cancellationToken);
                if (project is null)
                {
                    return Result.Failure<long>(NotFound());
                }

                number = project.Number;

                // Joins this transaction: if the line cannot be added the product goes with it.
                var created = await products.CreateAsync(
                    new ProductInput(part.Sku, part.Name, null, null, part.Unit, null, null, 0, 0, part.UnitCostCents),
                    cancellationToken);
                if (!created.Succeeded)
                {
                    return Result.Failure<long>([.. created.Errors]);
                }

                return await AddLineCoreAsync(projectId, created.Value.Id, quantityPerUnit, note, cancellationToken);
            },
            Fail<long>,
            cancellationToken);

        if (!result.Succeeded)
        {
            return Result.Failure<BomLineItem>([.. result.Errors]);
        }

        var line = (await GetLineAsync(result.Value, cancellationToken))!;
        await events.PublishAsync(Event(ProjectEventType.BomChanged, projectId, number!, actor, $"added new part {line.Sku} x{line.QuantityPerUnit}"), cancellationToken);
        return Result.Success(line);
    }

    /// <summary>Adds the line inside the caller's transaction. The product must exist and be active; one line per product.</summary>
    private async Task<Result<long>> AddLineCoreAsync(int projectId, int productId, int quantityPerUnit, string? note, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => new { p.Sku, p.IsActive }).FirstOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return Result.Failure<long>("project.product_missing", "That part no longer exists.", ProjectFields.ProductId);
        }

        if (!product.IsActive)
        {
            return Result.Failure<long>("project.product_archived", $"{product.Sku} is archived. Restore it before adding it to a BOM.", ProjectFields.ProductId);
        }

        var existing = await db.BomLines.AsNoTracking().Where(l => l.ProjectId == projectId && l.ProductId == productId).Select(l => (int?)l.QuantityPerUnit).FirstOrDefaultAsync(cancellationToken);
        if (existing is { } quantity)
        {
            return Result.Failure<long>(LineDuplicate(product.Sku, quantity));
        }

        var line = new BomLine { ProjectId = projectId, ProductId = productId, QuantityPerUnit = quantityPerUnit, Note = note };
        db.BomLines.Add(line);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(line.Id);
    }

    // ---- ordering shortages ------------------------------------------------------------------------------------

    public async Task<Result<AddShortagesResult>> AddShortagesToOrderAsync(
        int projectId,
        IReadOnlyList<ShortageDecision> decisions,
        ShortageTarget? target,
        PurchaseOrderActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(actor);

        var errors = new List<Error>();
        if (decisions.GroupBy(d => d.BomLineId).Any(g => g.Count() > 1))
        {
            errors.Add(new Error("project.shortage_duplicate", "A part can appear only once in an order request.", ProjectFields.Lines));
        }

        if (decisions.Any(d => d.Action != ShortageAction.Skip && d.Quantity < 1))
        {
            errors.Add(new Error("project.shortage_quantity", "Enter a quantity of at least 1.", ProjectFields.Lines));
        }

        if (decisions.Any(d => d.Action == ShortageAction.UpdateQuantity && d.PurchaseOrderId is null))
        {
            errors.Add(new Error("project.shortage_draft_required", "Choose which draft order to update.", ProjectFields.Lines));
        }

        var needsTarget = decisions.Any(d => d.Action == ShortageAction.Add);
        if (needsTarget && (target is null || target is { PurchaseOrderId: null, SupplierId: null }))
        {
            errors.Add(new Error("project.target_required", "Choose a draft order or a supplier to add the parts to.", ProjectFields.Target));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<AddShortagesResult>([.. errors]);
        }

        return await InTransactionAsync<Result<AddShortagesResult>>(
            async () =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
                {
                    return Result.Failure<AddShortagesResult>(NotFound());
                }

                var lineIds = decisions.Select(d => d.BomLineId).ToList();
                var lines = await db.BomLines.AsNoTracking()
                    .Where(l => l.ProjectId == projectId && lineIds.Contains(l.Id))
                    .Select(l => new { l.Id, l.ProductId, l.Product!.Sku })
                    .ToDictionaryAsync(l => l.Id, cancellationToken);
                if (decisions.Any(d => !lines.ContainsKey(d.BomLineId)))
                {
                    return Result.Failure<AddShortagesResult>(LineMissing(ProjectFields.Lines));
                }

                // The supplier whose minimum order quantity applies is the supplier of the draft the part ends up on.
                int? createdId = null;
                string? createdNumber = null;
                int? addOrderId = null;
                if (needsTarget)
                {
                    if (target!.PurchaseOrderId is { } existingId)
                    {
                        addOrderId = existingId;
                    }
                    else
                    {
                        var draft = await purchaseOrders.CreateDraftAsync(target.SupplierId!.Value, actor, null, null, cancellationToken);
                        if (!draft.Succeeded)
                        {
                            return Result.Failure<AddShortagesResult>([.. draft.Errors.Select(e => e with { Field = e.Field == PurchaseOrderFields.SupplierId ? ProjectFields.Target : e.Field })]);
                        }

                        addOrderId = createdId = draft.Value.Id;
                        createdNumber = draft.Value.Number;
                    }
                }

                var orderIds = decisions.Where(d => d.Action == ShortageAction.UpdateQuantity).Select(d => d.PurchaseOrderId!.Value).ToList();
                if (addOrderId is { } add)
                {
                    orderIds.Add(add);
                }

                var suppliers = await db.PurchaseOrders.AsNoTracking()
                    .Where(o => orderIds.Contains(o.Id))
                    .Select(o => new { o.Id, o.Number, o.SupplierId })
                    .ToDictionaryAsync(o => o.Id, cancellationToken);
                var productIds = lines.Values.Select(l => l.ProductId).ToList();
                var supplierIds = suppliers.Values.Select(s => s.SupplierId).Distinct().ToList();
                var minimums = await db.ProductSuppliers.AsNoTracking()
                    .Where(l => productIds.Contains(l.ProductId) && supplierIds.Contains(l.SupplierId))
                    .ToDictionaryAsync(l => (l.SupplierId, l.ProductId), l => l.MinOrderQuantity, cancellationToken);

                var changes = new List<DraftLineChange>();
                var changeOwners = new List<ShortageDecision>();
                foreach (var decision in decisions.Where(d => d.Action != ShortageAction.Skip))
                {
                    var line = lines[decision.BomLineId];
                    var orderId = decision.Action == ShortageAction.Add ? addOrderId!.Value : decision.PurchaseOrderId!.Value;
                    var supplierId = suppliers.TryGetValue(orderId, out var order) ? order.SupplierId : (int?)null;
                    var quantity = supplierId is { } s && minimums.TryGetValue((s, line.ProductId), out var min)
                        ? MinOrderRounding.RoundUp(decision.Quantity, min)
                        : decision.Quantity;
                    changes.Add(new DraftLineChange(orderId, line.ProductId, decision.Action == ShortageAction.Add ? DraftChangeKind.Add : DraftChangeKind.SetQuantity, quantity));
                    changeOwners.Add(decision);
                }

                var applied = await purchaseOrders.ApplyDraftChangesAsync(changes, cancellationToken);
                if (!applied.Succeeded)
                {
                    return Result.Failure<AddShortagesResult>([.. applied.Errors]);
                }

                var outcomes = new List<ShortageOutcome>(decisions.Count);
                foreach (var decision in decisions)
                {
                    var line = lines[decision.BomLineId];
                    var index = changeOwners.IndexOf(decision);
                    if (index < 0)
                    {
                        outcomes.Add(new ShortageOutcome(decision.BomLineId, line.Sku, ShortageOutcomeKind.Skipped, 0, null, null, null, "Skipped."));
                        continue;
                    }

                    var change = changes[index];
                    var outcome = applied.Value[index];
                    var number = suppliers.TryGetValue(change.PurchaseOrderId, out var po) ? po.Number : null;
                    var kind = outcome.Status switch
                    {
                        DraftChangeStatus.Added => ShortageOutcomeKind.Added,
                        DraftChangeStatus.Updated => ShortageOutcomeKind.Updated,
                        _ => ShortageOutcomeKind.Skipped,
                    };
                    outcomes.Add(new ShortageOutcome(decision.BomLineId, line.Sku, kind, change.Quantity, outcome.PreviousQuantity, change.PurchaseOrderId, number, outcome.Reason));
                }

                return Result.Success(new AddShortagesResult(outcomes, createdId, createdNumber));
            },
            Fail<AddShortagesResult>,
            cancellationToken);
    }

    // ---- build -------------------------------------------------------------------------------------------------

    public async Task<Result<BuildResult>> BuildAsync(int projectId, int units, string? note, StockContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.UserId))
        {
            throw new ArgumentException("A build needs the id of the user who made it.", nameof(context));
        }

        var cleanNote = Clean(note);
        var errors = new List<Error>();
        if (units < 1)
        {
            errors.Add(new Error("project.units_positive", "Enter how many units to build (at least 1).", ProjectFields.Units));
        }
        else if (units > Project.MaxTargetUnits)
        {
            errors.Add(new Error("project.units_too_large", $"A build can be at most {Project.MaxTargetUnits:N0} units.", ProjectFields.Units));
        }

        var noteLimit = Math.Min(ProjectBuild.NoteMaxLength, StockTransaction.NoteMaxLength);
        if (cleanNote is { Length: > 0 } && cleanNote.Length > noteLimit)
        {
            errors.Add(new Error("project.note_too_long", $"Note can be at most {noteLimit} characters.", ProjectFields.Note));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<BuildResult>([.. errors]);
        }

        string number = string.Empty;
        var result = await InTransactionAsync<Result<BuildResult>>(
            async () =>
            {
                var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => new { p.Name, p.Number, p.TargetUnits }).FirstOrDefaultAsync(cancellationToken);
                if (project is null)
                {
                    return Result.Failure<BuildResult>(NotFound());
                }

                number = project.Number;

                // On hand is judged here, on rows read inside the transaction, not on what the page showed.
                var lines = await db.BomLines.AsNoTracking()
                    .Where(l => l.ProjectId == projectId)
                    .OrderBy(l => l.Id)
                    .Select(l => new { l.ProductId, l.Product!.Sku, l.QuantityPerUnit, l.Product.QuantityOnHand, l.Product.IsActive })
                    .ToListAsync(cancellationToken);
                if (lines.Count == 0)
                {
                    return Result.Failure<BuildResult>("project.no_lines", "Add parts to the BOM before building.", ProjectFields.Lines);
                }

                var problems = new List<Error>();
                foreach (var line in lines)
                {
                    var needed = (long)line.QuantityPerUnit * units;
                    if (!line.IsActive)
                    {
                        problems.Add(new Error("project.part_archived", $"{line.Sku} is archived. Restore it or remove it from the BOM before building.", ProjectFields.Lines));
                    }
                    else if (needed > line.QuantityOnHand)
                    {
                        problems.Add(new Error(
                            "project.build_short",
                            line.QuantityOnHand <= 0
                                ? $"{line.Sku}: need {needed:N0}, none on hand."
                                : $"{line.Sku}: need {needed:N0}, only {line.QuantityOnHand:N0} on hand.",
                            ProjectFields.Lines));
                    }
                }

                if (problems.Count > 0)
                {
                    return Result.Failure<BuildResult>([.. problems]);
                }

                var build = new ProjectBuild { ProjectId = projectId, Units = units, UserId = context.UserId, Note = cleanNote };
                db.ProjectBuilds.Add(build);
                await db.SaveChangesAsync(cancellationToken); // Gives the build its id for the reference text.

                var reference = ReferenceFor(project.Name, build.Id);
                var movements = lines
                    .Select(l => new StockMovement(
                        l.ProductId,
                        StockTransactionType.Issue,
                        l.QuantityPerUnit * units,
                        Reason: "Build",
                        Reference: reference,
                        Note: cleanNote))
                    .ToList();

                // Joins this transaction, so a refusal leaves neither the issues nor the build row behind.
                var posted = await stock.PostAsync(movements, context, cancellationToken);
                if (!posted.Succeeded)
                {
                    db.ChangeTracker.Clear();
                    return Result.Failure<BuildResult>([.. posted.Errors.Select(e => e with { Field = ProjectFields.Lines })]);
                }

                var built = await db.ProjectBuilds.Where(b => b.ProjectId == projectId).SumAsync(b => b.Units, cancellationToken);
                return Result.Success(new BuildResult(build.Id, units, posted.Value, BomStatusCalculator.RemainingUnits(project.TargetUnits, built)));
            },
            Fail<BuildResult>,
            cancellationToken);

        if (result.Succeeded)
        {
            await events.PublishAsync(
                Event(ProjectEventType.Built, projectId, number, context.UserId, $"{result.Value.Units} unit(s), build #{result.Value.BuildId}"),
                cancellationToken);
        }

        return result;
    }

    /// <summary>The ledger reference of a build's issue rows. The project name is shortened to fit the column.</summary>
    /// <summary>The reference text on the issue rows of a build ("Robot build #3"); the stock history can be searched for it.</summary>
    public static string ReferenceFor(string projectName, long buildId)
    {
        var suffix = string.Create(CultureInfo.InvariantCulture, $" build #{buildId}");
        var room = StockTransaction.ReferenceMaxLength - suffix.Length;
        var name = projectName.Length <= room ? projectName : projectName[..Math.Max(0, room - 1)] + "…";
        return name + suffix;
    }

    // ---- internals ---------------------------------------------------------------------------------------------

    private IQueryable<BomLineItem> LineItems(IQueryable<BomLine> lines)
        => lines.Select(l => new BomLineItem(
            l.Id,
            l.ProductId,
            l.Product!.Sku,
            l.Product.Name,
            l.Product.Unit,
            l.Product.IsActive,
            l.QuantityPerUnit,
            l.Note,
            l.Product.UnitCostCents));

    private Task<BomLineItem?> GetLineAsync(long lineId, CancellationToken cancellationToken)
        => LineItems(db.BomLines.AsNoTracking().Where(l => l.Id == lineId)).FirstOrDefaultAsync(cancellationToken);

    internal static List<Error> ValidateProject(string? name, string? description, int targetUnits, string? notes)
    {
        var errors = new List<Error>();
        if (name is null)
        {
            errors.Add(new Error("project.name_required", "BOM name is required.", ProjectFields.Name));
        }
        else if (name.Length > Project.NameMaxLength)
        {
            errors.Add(new Error("project.name_too_long", $"Name can be at most {Project.NameMaxLength} characters.", ProjectFields.Name));
        }

        if (Clean(description) is { Length: > Project.DescriptionMaxLength })
        {
            errors.Add(new Error("project.description_too_long", $"Description can be at most {Project.DescriptionMaxLength} characters.", ProjectFields.Description));
        }

        if (Clean(notes) is { Length: > Project.NotesMaxLength })
        {
            errors.Add(new Error("project.notes_too_long", $"Notes can be at most {Project.NotesMaxLength} characters.", ProjectFields.Notes));
        }

        if (targetUnits < 1)
        {
            errors.Add(new Error("project.target_positive", "Target units must be at least 1.", ProjectFields.TargetUnits));
        }
        else if (targetUnits > Project.MaxTargetUnits)
        {
            errors.Add(new Error("project.target_too_large", $"Target units can be at most {Project.MaxTargetUnits:N0}.", ProjectFields.TargetUnits));
        }

        return errors;
    }

    internal static void ValidateLine(int quantityPerUnit, string? note, List<Error> errors)
    {
        if (quantityPerUnit < 1)
        {
            errors.Add(new Error("project.quantity_positive", "Quantity per unit must be at least 1.", ProjectFields.QuantityPerUnit));
        }
        else if (quantityPerUnit > BomLine.MaxQuantityPerUnit)
        {
            errors.Add(new Error("project.quantity_too_large", $"Quantity per unit can be at most {BomLine.MaxQuantityPerUnit:N0}.", ProjectFields.QuantityPerUnit));
        }

        if (note is { Length: > BomLine.NoteMaxLength })
        {
            errors.Add(new Error("project.note_too_long", $"Note can be at most {BomLine.NoteMaxLength} characters.", ProjectFields.Note));
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (joining one that is already open, which is then the caller's to
    /// commit) and commits when it succeeds. A lost race (optimistic concurrency, a unique index) becomes an error result.
    /// </summary>
    private async Task<TResult> InTransactionAsync<TResult>(Func<Task<TResult>> work, Func<Error, TResult> failure, CancellationToken cancellationToken)
        where TResult : Result
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var result = await work();
            if (own is not null && result.Succeeded)
            {
                await own.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || DbErrors.IsUniqueViolation(ex, out _))
        {
            db.ChangeTracker.Clear();
            return failure(ConflictFor(ex));
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync(); // An uncommitted transaction rolls back.
            }
        }
    }

    private static Error ConflictFor(DbUpdateException ex)
    {
        if (ex is not DbUpdateConcurrencyException && DbErrors.IsUniqueViolation(ex, out var target))
        {
            if (target.Contains("Projects.Name", StringComparison.Ordinal))
            {
                return NameTaken(null);
            }

            if (target.Contains("Projects.Number", StringComparison.Ordinal))
            {
                return NumberTaken(null);
            }

            if (target.Contains("BomLines.", StringComparison.Ordinal))
            {
                return new Error("project.line_duplicate", "That part is already in this BOM.", ProjectFields.ProductId);
            }
        }

        return ConflictError();
    }

    private static Result Fail(Error error) => Result.Failure(error);

    private static Result<T> Fail<T>(Error error) => Result.Failure<T>(error);

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void RequireActor(ProjectActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new ArgumentException("A project action needs the id of the user who made it.", nameof(actor));
        }
    }

    private ProjectEvent Event(ProjectEventType type, int projectId, string number, ProjectActor actor, string? detail)
        => Event(type, projectId, number, actor.UserId, detail);

    private ProjectEvent Event(ProjectEventType type, int projectId, string number, string userId, string? detail)
        => new(type, projectId, number, userId, detail, clock.UtcNow);

    // ---- errors ------------------------------------------------------------------------------------------------

    private static Error NotFound() => new("project.not_found", "That BOM no longer exists.");

    private static Error ConflictError() => new("project.conflict", "Someone else changed this BOM. Reload it and try again.");

    private static Error NameTaken(string? name)
        => new("project.name_taken", name is null ? "A BOM with that name already exists." : $"A BOM named \"{name}\" already exists.", ProjectFields.Name);

    private static Error NumberTaken(string? number)
        => new("project.number_taken", number is null ? "That BOM number is already used." : $"The BOM number {number} is already used.", ProjectFields.Number);

    private static Error LineMissing(string? field = null) => new("project.line_missing", "That part is not in this BOM any more.", field);

    private static Error LineDuplicate(string sku, int quantity)
        => new("project.line_duplicate", $"{sku} is already in this BOM ({quantity} per unit). Change its quantity instead.", ProjectFields.ProductId);
}
