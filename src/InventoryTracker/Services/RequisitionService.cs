using System.Globalization;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface IRequisitionService
{
    /// <summary>One page of requisitions. An actor who cannot see everyone's always gets only their own, whatever the query asks.</summary>
    Task<PagedResult<RequisitionListItem>> SearchAsync(RequisitionListQuery query, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>The requisition, or <c>null</c> when it does not exist or <paramref name="actor"/> may not see it.</summary>
    Task<RequisitionDetail?> GetAsync(int id, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Everyone who has raised a requisition, for the filter. Empty for an actor who only sees their own.</summary>
    Task<IReadOnlyList<RequisitionRequester>> GetRequestersAsync(RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Active parts matching <paramref name="term"/> with on hand and on order, flagging those already on the requisition.</summary>
    Task<IReadOnlyList<RequisitionProductChoice>> LookupProductsAsync(int requisitionId, string? term, RequisitionActor actor, CancellationToken cancellationToken = default);

    Task<Result<RequisitionDetail>> CreateDraftAsync(RequisitionHeaderInput input, RequisitionActor actor, CancellationToken cancellationToken = default);

    Task<Result<RequisitionDetail>> UpdateHeaderAsync(int id, long rowVersion, RequisitionHeaderInput input, RequisitionActor actor, CancellationToken cancellationToken = default);

    Task<Result<RequisitionLineItem>> AddLineAsync(int id, RequisitionLineInput input, RequisitionActor actor, CancellationToken cancellationToken = default);

    Task<Result<RequisitionLineItem>> UpdateLineAsync(int id, long lineId, int quantity, string? note, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Removes a line. Idempotent for a line that is already gone.</summary>
    Task<Result> RemoveLineAsync(int id, long lineId, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Draft to Submitted. Needs a reason and at least one line, all on active parts.</summary>
    Task<Result<RequisitionDetail>> SubmitAsync(int id, long? rowVersion, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Submitted back to Draft, while no decision has been made.</summary>
    Task<Result<RequisitionDetail>> WithdrawAsync(int id, long? rowVersion, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a draft and its lines. Idempotent for a requisition that is already gone.</summary>
    Task<Result> DeleteDraftAsync(int id, long? rowVersion, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Submitted requisitions, oldest first. Empty for an actor who cannot decide.</summary>
    Task<PagedResult<ApprovalQueueItem>> GetApprovalQueueAsync(ListQuery query, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>How many requisitions are waiting for a decision.</summary>
    Task<int> CountAwaitingDecisionAsync(CancellationToken cancellationToken = default);

    /// <summary>Decides every line in one transaction. Approved when at least one line is approved, otherwise Rejected. Final.</summary>
    Task<Result<RequisitionDetail>> DecideAsync(int id, long rowVersion, IReadOnlyList<RequisitionLineDecisionInput> decisions, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Cancels an approved requisition while none of its lines is on a purchase order (F13 FR-10). Needs a reason.</summary>
    Task<Result<RequisitionDetail>> CancelApprovedAsync(int id, long rowVersion, string? reason, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Cancels a submitted requisition instead of deciding it. Needs a reason.</summary>
    Task<Result<RequisitionDetail>> CancelSubmittedAsync(int id, long rowVersion, string? reason, RequisitionActor actor, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every requisition rule (F11). Reads stock and purchase order figures only to show availability; writes neither.
/// A change to a line also bumps the requisition's <c>RowVersion</c>, so a page showing older lines cannot submit them.
/// </summary>
public sealed class RequisitionService(
    AppDbContext db,
    IProductService products,
    ISettingsService settings,
    IClock clock,
    RequisitionEventPublisher events) : IRequisitionService
{
    /// <summary>Name of the numbering series in <see cref="DocumentSequence"/>.</summary>
    public const string SequenceName = "Requisition";

    // ---- queries -----------------------------------------------------------------------------------------------

    public async Task<PagedResult<RequisitionListItem>> SearchAsync(RequisitionListQuery query, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var requisitions = db.Requisitions.AsNoTracking().AsQueryable();

        if (!actor.SeesEveryone || query.Tab == RequisitionTab.Mine)
        {
            requisitions = requisitions.Where(r => r.RequestedById == actor.UserId);
        }
        else if (!string.IsNullOrEmpty(query.RequesterId))
        {
            requisitions = requisitions.Where(r => r.RequestedById == query.RequesterId);
        }

        if (query.Status is { } status)
        {
            requisitions = requisitions.Where(r => r.Status == status);
        }

        if (query.Ordering is { } ordering)
        {
            var approved = requisitions.Where(r => r.Status == RequisitionStatus.Approved);
            requisitions = ordering switch
            {
                RequisitionOrderState.NotOrdered => approved.Where(r => !r.Lines.Any(l => l.Order != null)),
                RequisitionOrderState.PartlyOrdered => approved.Where(r => r.Lines.Any(l => l.Order != null) && r.Lines.Any(l => l.Order == null && l.Decision != null && l.Decision.Decision == RequisitionDecision.Approved)),
                _ => approved.Where(r => r.Lines.Any(l => l.Order != null) && !r.Lines.Any(l => l.Order == null && l.Decision != null && l.Decision.Decision == RequisitionDecision.Approved)),
            };
        }

        if (query.FromUtc is { } from)
        {
            requisitions = requisitions.Where(r => r.CreatedAt >= from);
        }

        if (query.ToUtcExclusive is { } to)
        {
            requisitions = requisitions.Where(r => r.CreatedAt < to);
        }

        if (query.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            requisitions = requisitions.Where(r =>
                EF.Functions.Like(r.Number, pattern, LikePattern.Escape)
                || r.Lines.Any(l => EF.Functions.Like(l.Product!.Sku, pattern, LikePattern.Escape) || EF.Functions.Like(l.Product!.Name, pattern, LikePattern.Escape)));
        }

        // Every ordering ends in Id so paging is stable when the sort column has ties; newest first by default.
        var ordered = (query.Sort?.ToLowerInvariant(), query.Desc) switch
        {
            ("number", false) => requisitions.OrderBy(r => r.Number),
            ("number", true) => requisitions.OrderByDescending(r => r.Number),
            ("status", false) => requisitions.OrderBy(r => r.Status),
            ("status", true) => requisitions.OrderByDescending(r => r.Status),
            ("requester", false) => requisitions.OrderBy(r => r.RequestedBy!.DisplayName),
            ("requester", true) => requisitions.OrderByDescending(r => r.RequestedBy!.DisplayName),
            ("needed", false) => requisitions.OrderBy(r => r.NeededBy),
            ("needed", true) => requisitions.OrderByDescending(r => r.NeededBy),
            ("created", false) => requisitions.OrderBy(r => r.CreatedAt),
            _ => requisitions.OrderByDescending(r => r.CreatedAt),
        };

        return await PagedResult<RequisitionListItem>.CreateAsync(
            ordered.ThenByDescending(r => r.Id).Select(r => new RequisitionListItem(
                r.Id,
                r.Number,
                r.Status,
                r.RequestedById,
                r.RequestedBy!.DisplayName,
                r.NeededBy,
                r.Lines.Count,
                r.CreatedAt,
                r.SubmittedAt,
                r.Lines.Count(l => l.Decision != null && l.Decision.Decision == RequisitionDecision.Approved),
                r.Lines.Count(l => l.Order != null))),
            query,
            cancellationToken);
    }

    public async Task<RequisitionDetail?> GetAsync(int id, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var header = await db.Requisitions.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new
            {
                r.Id,
                r.Number,
                r.Status,
                r.RequestedById,
                RequestedByName = r.RequestedBy!.DisplayName,
                r.NeededBy,
                r.Reason,
                r.SubmittedAt,
                SubmittedByName = r.SubmittedBy == null ? null : r.SubmittedBy.DisplayName,
                r.DecidedAt,
                r.RowVersion,
                r.CreatedAt,
                r.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (header is null || !RequisitionRules.CanSee(actor, header.RequestedById))
        {
            return null;
        }

        var lines = await LineRows(db.RequisitionLines.AsNoTracking().Where(l => l.RequisitionId == id).OrderBy(l => l.Id)).ToListAsync(cancellationToken);
        var onOrder = await OnOrderAsync([.. lines.Select(l => l.ProductId)], cancellationToken);

        Dictionary<long, RequisitionLineOutcome> outcomes = [];
        if (header.DecidedAt is not null)
        {
            outcomes = await db.RequisitionLineDecisions.AsNoTracking()
                .Where(d => d.RequisitionLine!.RequisitionId == id)
                .Select(d => new
                {
                    d.RequisitionLineId,
                    Outcome = new RequisitionLineOutcome(d.Decision, d.QuantityApproved, d.Reason, d.DecidedById, d.DecidedBy!.DisplayName, d.DecidedAt),
                })
                .ToDictionaryAsync(d => d.RequisitionLineId, d => d.Outcome, cancellationToken);
        }

        Dictionary<long, LineOrderInfo> orders = [];
        if (header.DecidedAt is not null)
        {
            orders = await db.RequisitionLineOrders.AsNoTracking()
                .Where(o => o.RequisitionLine!.RequisitionId == id)
                .Select(o => new { o.RequisitionLineId, Info = new LineOrderInfo(o.PurchaseOrderLine!.PurchaseOrderId, o.PurchaseOrderLine.PurchaseOrder!.Number, o.QuantityOrdered) })
                .ToDictionaryAsync(o => o.RequisitionLineId, o => o.Info, cancellationToken);
        }

        var cancellation = header.Status != RequisitionStatus.Cancelled
            ? null
            : await db.RequisitionCancellations.AsNoTracking()
                .Where(c => c.RequisitionId == id)
                .Select(c => new RequisitionCancellationInfo(c.Reason, c.CancelledBy!.DisplayName, c.CancelledAt))
                .FirstOrDefaultAsync(cancellationToken);

        return new RequisitionDetail(
            header.Id, header.Number, header.Status, header.RequestedById, header.RequestedByName, header.NeededBy, header.Reason,
            header.SubmittedAt, header.SubmittedByName, header.DecidedAt, header.RowVersion, header.CreatedAt, header.UpdatedAt,
            [.. lines.Select(l => l.ToItem(onOrder.GetValueOrDefault(l.ProductId)) with { Outcome = outcomes.GetValueOrDefault(l.Id), OrderedOn = orders.GetValueOrDefault(l.Id) })],
            RequisitionRules.CanChange(actor, header.RequestedById),
            actor.CanApprove,
            outcomes.Values.FirstOrDefault()?.DecidedByName,
            outcomes.Values.Any(o => o.DecidedById == header.RequestedById),
            cancellation);
    }

    public async Task<IReadOnlyList<RequisitionRequester>> GetRequestersAsync(RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!actor.SeesEveryone)
        {
            return [];
        }

        return await db.Requisitions.AsNoTracking()
            .Select(r => new { r.RequestedById, r.RequestedBy!.DisplayName })
            .Distinct()
            .OrderBy(r => r.DisplayName)
            .Select(r => new RequisitionRequester(r.RequestedById, r.DisplayName))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RequisitionProductChoice>> LookupProductsAsync(int requisitionId, string? term, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var requesterId = await db.Requisitions.AsNoTracking().Where(r => r.Id == requisitionId).Select(r => r.RequestedById).FirstOrDefaultAsync(cancellationToken);
        if (requesterId is null || !RequisitionRules.CanSee(actor, requesterId))
        {
            return [];
        }

        var found = await products.LookupAsync(term, activeOnly: true, cancellationToken: cancellationToken);
        var ids = found.Select(p => p.Id).ToList();
        var onOrder = await OnOrderAsync(ids, cancellationToken);
        var present = await db.RequisitionLines.AsNoTracking()
            .Where(l => l.RequisitionId == requisitionId && ids.Contains(l.ProductId))
            .Select(l => l.ProductId)
            .ToListAsync(cancellationToken);

        return [.. found.Select(p => new RequisitionProductChoice(
            p.Id, p.Sku, p.Name, p.Unit, p.Barcode, p.QuantityOnHand, onOrder.GetValueOrDefault(p.Id), present.Contains(p.Id)))];
    }

    private sealed record LineRow(long Id, int ProductId, string Sku, string ProductName, string Unit, bool ProductIsActive, int QuantityRequested, string? Note, int OnHand)
    {
        public RequisitionLineItem ToItem(int onOrder)
            => new(Id, ProductId, Sku, ProductName, Unit, ProductIsActive, QuantityRequested, Note, OnHand, onOrder);
    }

    private static IQueryable<LineRow> LineRows(IQueryable<RequisitionLine> lines)
        => lines.Select(l => new LineRow(l.Id, l.ProductId, l.Product!.Sku, l.Product.Name, l.Product.Unit, l.Product.IsActive, l.QuantityRequested, l.Note, l.Product.QuantityOnHand));

    private async Task<Dictionary<int, int>> OnOrderAsync(List<int> productIds, CancellationToken cancellationToken)
        => productIds.Count == 0
            ? []
            : await db.OnOrder().Where(o => productIds.Contains(o.ProductId)).ToDictionaryAsync(o => o.ProductId, o => o.Quantity, cancellationToken);

    private async Task<RequisitionLineItem> GetLineAsync(long lineId, CancellationToken cancellationToken)
    {
        var row = await LineRows(db.RequisitionLines.AsNoTracking().Where(l => l.Id == lineId)).SingleAsync(cancellationToken);
        var onOrder = await OnOrderAsync([row.ProductId], cancellationToken);
        return row.ToItem(onOrder.GetValueOrDefault(row.ProductId));
    }

    // ---- drafts ------------------------------------------------------------------------------------------------

    public async Task<Result<RequisitionDetail>> CreateDraftAsync(RequisitionHeaderInput input, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var reason = Clean(input.Reason);
        var errors = ValidateHeader(reason);
        if (errors.Count > 0)
        {
            return Result.Failure<RequisitionDetail>([.. errors]);
        }

        // Read before the write transaction opens: the settings service uses its own connection.
        var prefix = await settings.GetStringAsync(SettingKeys.RequisitionNumberPrefix, cancellationToken);
        var year = clock.UtcNow.ToLocalTime().Year;

        var created = await InTransactionAsync<Result<int>>(
            async () =>
            {
                // A counter reset by hand could collide with an issued number; skip past it.
                var number = await IssueNumberAsync(prefix, year, cancellationToken);
                for (var attempt = 0; attempt < 1000 && await db.Requisitions.AnyAsync(r => r.Number == number, cancellationToken); attempt++)
                {
                    number = await IssueNumberAsync(prefix, year, cancellationToken);
                }

                var requisition = new Requisition { Number = number, RequestedById = actor.UserId, NeededBy = input.NeededBy, Reason = reason };
                db.Requisitions.Add(requisition);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success(requisition.Id);
            },
            Fail<int>,
            cancellationToken);

        if (!created.Succeeded)
        {
            return Result.Failure<RequisitionDetail>([.. created.Errors]);
        }

        var detail = (await GetAsync(created.Value, actor, cancellationToken))!;
        await PublishAsync(RequisitionEventType.Created, detail, actor, cancellationToken);
        return Result.Success(detail);
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

    public async Task<Result<RequisitionDetail>> UpdateHeaderAsync(
        int id, long rowVersion, RequisitionHeaderInput input, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var reason = Clean(input.Reason);
        var errors = ValidateHeader(reason);
        if (errors.Count > 0)
        {
            return Result.Failure<RequisitionDetail>([.. errors]);
        }

        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var (requisition, error) = await LoadDraftAsync(id, rowVersion, actor, cancellationToken);
                if (error is not null)
                {
                    return Result.Failure(error);
                }

                requisition!.NeededBy = input.NeededBy;
                requisition.Reason = reason;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        return await DetailOrFailureAsync(result, id, actor, cancellationToken);
    }

    public async Task<Result<RequisitionLineItem>> AddLineAsync(int id, RequisitionLineInput input, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var note = Clean(input.Note);
        var errors = ValidateLine(input.Quantity, note);
        if (errors.Count > 0)
        {
            return Result.Failure<RequisitionLineItem>([.. errors]);
        }

        var result = await InTransactionAsync<Result<long>>(
            async () =>
            {
                var (requisition, error) = await LoadDraftAsync(id, null, actor, cancellationToken);
                if (error is not null)
                {
                    return Result.Failure<long>(error);
                }

                var product = await db.Products.AsNoTracking().Where(p => p.Id == input.ProductId).Select(p => new { p.Sku, p.IsActive }).FirstOrDefaultAsync(cancellationToken);
                if (product is null)
                {
                    return Result.Failure<long>("requisition.product_missing", "Choose a part.", RequisitionFields.ProductId);
                }

                if (!product.IsActive)
                {
                    return Result.Failure<long>(PartArchived(product.Sku, RequisitionFields.ProductId));
                }

                if (await db.RequisitionLines.AnyAsync(l => l.RequisitionId == id && l.ProductId == input.ProductId, cancellationToken))
                {
                    return Result.Failure<long>(LineDuplicate(product.Sku));
                }

                var line = new RequisitionLine { RequisitionId = id, ProductId = input.ProductId, QuantityRequested = input.Quantity, Note = note };
                db.RequisitionLines.Add(line);
                Touch(requisition!);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success(line.Id);
            },
            Fail<long>,
            cancellationToken);

        return result.Succeeded
            ? Result.Success(await GetLineAsync(result.Value, cancellationToken))
            : Result.Failure<RequisitionLineItem>([.. result.Errors]);
    }

    public async Task<Result<RequisitionLineItem>> UpdateLineAsync(int id, long lineId, int quantity, string? note, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var cleanNote = Clean(note);
        var errors = ValidateLine(quantity, cleanNote);
        if (errors.Count > 0)
        {
            return Result.Failure<RequisitionLineItem>([.. errors]);
        }

        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var (requisition, error) = await LoadDraftAsync(id, null, actor, cancellationToken);
                if (error is not null)
                {
                    return Result.Failure(error);
                }

                var line = await db.RequisitionLines.FirstOrDefaultAsync(l => l.Id == lineId && l.RequisitionId == id, cancellationToken);
                if (line is null)
                {
                    return Result.Failure("requisition.line_missing", "That part is not on this requisition any more.");
                }

                line.QuantityRequested = quantity;
                line.Note = cleanNote;
                Touch(requisition!);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        return result.Succeeded
            ? Result.Success(await GetLineAsync(lineId, cancellationToken))
            : Result.Failure<RequisitionLineItem>([.. result.Errors]);
    }

    public Task<Result> RemoveLineAsync(int id, long lineId, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        return InTransactionAsync<Result>(
            async () =>
            {
                var (requisition, error) = await LoadDraftAsync(id, null, actor, cancellationToken);
                if (error is not null)
                {
                    return Result.Failure(error);
                }

                var line = await db.RequisitionLines.FirstOrDefaultAsync(l => l.Id == lineId && l.RequisitionId == id, cancellationToken);
                if (line is null)
                {
                    return Result.Success();
                }

                db.RequisitionLines.Remove(line);
                Touch(requisition!);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);
    }

    // ---- transitions -------------------------------------------------------------------------------------------

    public async Task<Result<RequisitionDetail>> SubmitAsync(int id, long? rowVersion, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var (requisition, error) = await LoadDraftAsync(id, rowVersion, actor, cancellationToken);
                if (error is not null)
                {
                    return Result.Failure(error);
                }

                var lines = await db.RequisitionLines.AsNoTracking()
                    .Where(l => l.RequisitionId == id)
                    .Select(l => new { l.Product!.Sku, l.Product.IsActive })
                    .ToListAsync(cancellationToken);

                var errors = new List<Error>();
                if (requisition!.Reason is null)
                {
                    errors.Add(new Error("requisition.reason_required", "Say why these parts are needed before submitting.", RequisitionFields.Reason));
                }

                if (lines.Count == 0)
                {
                    errors.Add(new Error("requisition.no_lines", "Add at least one part before submitting.", RequisitionFields.Lines));
                }

                errors.AddRange(lines.Where(l => !l.IsActive).Select(l => PartArchived(l.Sku, RequisitionFields.Lines)));
                if (errors.Count > 0)
                {
                    return Result.Failure([.. errors]);
                }

                requisition.Status = RequisitionStatus.Submitted;
                requisition.SubmittedAt = clock.UtcNow;
                requisition.SubmittedById = actor.UserId;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        return await TransitionedAsync(result, RequisitionEventType.Submitted, id, actor, cancellationToken);
    }

    public async Task<Result<RequisitionDetail>> WithdrawAsync(int id, long? rowVersion, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var (requisition, error) = await LoadAsync(id, rowVersion, actor, cancellationToken);
                if (error is not null)
                {
                    return Result.Failure(error);
                }

                if (!RequisitionRules.CanWithdraw(requisition!.Status))
                {
                    return Result.Failure("requisition.not_submitted", $"{requisition.Number} is {StatusText(requisition.Status)}, so it cannot be withdrawn.");
                }

                requisition.Status = RequisitionStatus.Draft;
                requisition.SubmittedAt = null;
                requisition.SubmittedById = null;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        return await TransitionedAsync(result, RequisitionEventType.Withdrawn, id, actor, cancellationToken);
    }

    public Task<Result> DeleteDraftAsync(int id, long? rowVersion, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        return InTransactionAsync<Result>(
            async () =>
            {
                if (!await db.Requisitions.AnyAsync(r => r.Id == id, cancellationToken))
                {
                    return Result.Success(); // Already gone (a double click).
                }

                var (requisition, error) = await LoadDraftAsync(id, rowVersion, actor, cancellationToken);
                if (error is not null)
                {
                    return Result.Failure(error);
                }

                db.Requisitions.Remove(requisition!); // The lines go with it (cascade).
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);
    }

    // ---- approvals (F12) ---------------------------------------------------------------------------------------

    public async Task<PagedResult<ApprovalQueueItem>> GetApprovalQueueAsync(ListQuery query, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!actor.CanApprove)
        {
            return new PagedResult<ApprovalQueueItem>([], 1, ListQuery.DefaultPageSize, 0);
        }

        return await PagedResult<ApprovalQueueItem>.CreateAsync(
            db.Requisitions.AsNoTracking()
                .Where(r => r.Status == RequisitionStatus.Submitted)
                .OrderBy(r => r.SubmittedAt)
                .ThenBy(r => r.Id)
                .Select(r => new ApprovalQueueItem(
                    r.Id,
                    r.Number,
                    r.RequestedBy!.DisplayName,
                    r.NeededBy,
                    r.SubmittedAt,
                    r.Lines.Count,
                    r.Lines.Sum(l => l.QuantityRequested * l.Product!.UnitCostCents),
                    r.RequestedById == actor.UserId)),
            query,
            cancellationToken);
    }

    public Task<int> CountAwaitingDecisionAsync(CancellationToken cancellationToken = default)
        => db.Requisitions.AsNoTracking().CountAsync(r => r.Status == RequisitionStatus.Submitted, cancellationToken);

    public async Task<Result<RequisitionDetail>> DecideAsync(
        int id, long rowVersion, IReadOnlyList<RequisitionLineDecisionInput> decisions, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(decisions);
        if (!actor.CanApprove)
        {
            return Result.Failure<RequisitionDetail>(NotApprover());
        }

        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var (requisition, error) = await LoadInStatusAsync(id, rowVersion, RequisitionStatus.Submitted, "decided", cancellationToken);
                if (error is not null)
                {
                    return Result.Failure(error);
                }

                var lines = await db.RequisitionLines.AsNoTracking()
                    .Where(l => l.RequisitionId == id)
                    .Select(l => new DecidableLine(l.Id, l.QuantityRequested, l.Product!.Sku))
                    .ToListAsync(cancellationToken);

                var errors = ValidateDecisions(lines, decisions);
                if (errors.Count > 0)
                {
                    return Result.Failure([.. errors]);
                }

                var now = clock.UtcNow;
                db.RequisitionLineDecisions.AddRange(decisions.Select(d => new RequisitionLineDecision
                {
                    RequisitionLineId = d.LineId,
                    Decision = d.Decision!.Value,
                    QuantityApproved = d.Decision == RequisitionDecision.Approved ? d.QuantityApproved!.Value : 0,
                    Reason = Clean(d.Reason),
                    DecidedById = actor.UserId,
                    DecidedAt = now,
                }));

                requisition!.Status = decisions.Any(d => d.Decision == RequisitionDecision.Approved) ? RequisitionStatus.Approved : RequisitionStatus.Rejected;
                requisition.DecidedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        return await TransitionedAsync(result, RequisitionEventType.Decided, id, actor, cancellationToken);
    }

    public Task<Result<RequisitionDetail>> CancelSubmittedAsync(int id, long rowVersion, string? reason, RequisitionActor actor, CancellationToken cancellationToken = default)
        => CancelAsync(id, rowVersion, reason, RequisitionStatus.Submitted, actor, cancellationToken);

    public Task<Result<RequisitionDetail>> CancelApprovedAsync(int id, long rowVersion, string? reason, RequisitionActor actor, CancellationToken cancellationToken = default)
        => CancelAsync(id, rowVersion, reason, RequisitionStatus.Approved, actor, cancellationToken);

    private async Task<Result<RequisitionDetail>> CancelAsync(int id, long rowVersion, string? reason, RequisitionStatus from, RequisitionActor actor, CancellationToken cancellationToken)
    {
        RequireActor(actor);
        if (!actor.CanApprove)
        {
            return Result.Failure<RequisitionDetail>(NotApprover());
        }

        var cleaned = Clean(reason);
        if (cleaned is null)
        {
            return Result.Failure<RequisitionDetail>(new Error("requisition.cancel_reason_required", "Say why this requisition is being cancelled.", RequisitionFields.Reason));
        }

        if (cleaned.Length > RequisitionCancellation.ReasonMaxLength)
        {
            return Result.Failure<RequisitionDetail>(new Error("requisition.cancel_reason_too_long", $"Reason can be at most {RequisitionCancellation.ReasonMaxLength} characters.", RequisitionFields.Reason));
        }

        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var (requisition, error) = await LoadInStatusAsync(id, rowVersion, from, "cancelled", cancellationToken);
                if (error is not null)
                {
                    return Result.Failure(error);
                }

                if (from == RequisitionStatus.Approved && await db.RequisitionLineOrders.AnyAsync(o => o.RequisitionLine!.RequisitionId == id, cancellationToken))
                {
                    return Result.Failure(new Error("requisition.has_orders", $"Some of {requisition!.Number} is already on a purchase order. Remove those lines from the order first."));
                }

                db.RequisitionCancellations.Add(new RequisitionCancellation { RequisitionId = id, Reason = cleaned, CancelledById = actor.UserId, CancelledAt = clock.UtcNow });
                requisition!.Status = RequisitionStatus.Cancelled;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Fail,
            cancellationToken);

        return await TransitionedAsync(result, RequisitionEventType.Cancelled, id, actor, cancellationToken);
    }

    private sealed record DecidableLine(long Id, int QuantityRequested, string Sku);

    /// <summary>Loads inside the write transaction, so of two approvers only the first finds it still in that status at its version.</summary>
    private async Task<(Requisition? Requisition, Error? Error)> LoadInStatusAsync(int id, long rowVersion, RequisitionStatus status, string verb, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (requisition is null)
        {
            return (null, NotFound());
        }

        if (requisition.RowVersion != rowVersion)
        {
            return (null, ConflictError());
        }

        return requisition.Status == status
            ? (requisition, null)
            : (null, new Error(status == RequisitionStatus.Submitted ? "requisition.not_submitted" : "requisition.not_approved", $"{requisition.Number} is {StatusText(requisition.Status)}, so it cannot be {verb}."));
    }

    private static List<Error> ValidateDecisions(List<DecidableLine> lines, IReadOnlyList<RequisitionLineDecisionInput> decisions)
    {
        var errors = new List<Error>();
        var byId = lines.ToDictionary(l => l.Id);
        var seen = new HashSet<long>();

        for (var i = 0; i < decisions.Count; i++)
        {
            var input = decisions[i];
            if (!byId.TryGetValue(input.LineId, out var line) || !seen.Add(input.LineId))
            {
                errors.Add(new Error("requisition.line_missing", "That part is not on this requisition any more. Reload it and try again."));
                continue;
            }

            if (input.Decision is null)
            {
                errors.Add(new Error("requisition.decision_required", $"Approve or reject {line.Sku}.", RequisitionFields.DecisionRow(i, RequisitionFields.Decision)));
                continue;
            }

            var reason = Clean(input.Reason);
            var reduced = false;
            if (input.Decision == RequisitionDecision.Approved)
            {
                if (input.QuantityApproved is not { } quantity || quantity < 1 || quantity > line.QuantityRequested)
                {
                    errors.Add(new Error("requisition.approved_quantity_invalid", $"Approve between 1 and {line.QuantityRequested} of {line.Sku}.", RequisitionFields.DecisionRow(i, RequisitionFields.QuantityApproved)));
                    continue;
                }

                reduced = quantity < line.QuantityRequested;
            }

            if (reason is { Length: > RequisitionLineDecision.ReasonMaxLength })
            {
                errors.Add(new Error("requisition.decision_reason_too_long", $"Reason can be at most {RequisitionLineDecision.ReasonMaxLength} characters.", RequisitionFields.DecisionRow(i, RequisitionFields.Reason)));
            }
            else if (reason is null && (reduced || input.Decision == RequisitionDecision.Rejected))
            {
                errors.Add(new Error(
                    "requisition.decision_reason_required",
                    reduced ? $"Say why {line.Sku} was reduced." : $"Say why {line.Sku} was rejected.",
                    RequisitionFields.DecisionRow(i, RequisitionFields.Reason)));
            }
        }

        errors.AddRange(lines.Where(l => !seen.Contains(l.Id)).Select(l => new Error("requisition.line_undecided", $"{l.Sku} has no decision yet. Every part must be decided.")));
        return errors;
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>Loads a requisition the actor may change. Someone who may not even see it is told it does not exist.</summary>
    private async Task<(Requisition? Requisition, Error? Error)> LoadAsync(int id, long? rowVersion, RequisitionActor actor, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (requisition is null || !RequisitionRules.CanSee(actor, requisition.RequestedById))
        {
            return (null, NotFound());
        }

        if (!RequisitionRules.CanChange(actor, requisition.RequestedById))
        {
            return (null, new Error("requisition.not_owner", "Only the person who raised this requisition or an administrator can change it."));
        }

        return rowVersion is { } expected && requisition.RowVersion != expected ? (null, ConflictError()) : (requisition, null);
    }

    private async Task<(Requisition? Requisition, Error? Error)> LoadDraftAsync(int id, long? rowVersion, RequisitionActor actor, CancellationToken cancellationToken)
    {
        var (requisition, error) = await LoadAsync(id, rowVersion, actor, cancellationToken);
        if (error is not null)
        {
            return (null, error);
        }

        return RequisitionRules.CanEditContent(requisition!.Status)
            ? (requisition, null)
            : (null, new Error("requisition.locked", $"{requisition.Number} is {StatusText(requisition.Status)}, so it can no longer be changed."));
    }

    /// <summary>A line changed: the header counts as changed too, which moves its <c>RowVersion</c> on.</summary>
    private void Touch(Requisition requisition) => db.Entry(requisition).Property(r => r.RowVersion).IsModified = true;

    private async Task<Result<RequisitionDetail>> DetailOrFailureAsync(Result result, int id, RequisitionActor actor, CancellationToken cancellationToken)
        => result.Succeeded
            ? Result.Success((await GetAsync(id, actor, cancellationToken))!)
            : Result.Failure<RequisitionDetail>([.. result.Errors]);

    private async Task<Result<RequisitionDetail>> TransitionedAsync(Result result, RequisitionEventType type, int id, RequisitionActor actor, CancellationToken cancellationToken)
    {
        var outcome = await DetailOrFailureAsync(result, id, actor, cancellationToken);
        if (outcome.Succeeded)
        {
            await PublishAsync(type, outcome.Value, actor, cancellationToken);
        }

        return outcome;
    }

    private Task PublishAsync(RequisitionEventType type, RequisitionDetail detail, RequisitionActor actor, CancellationToken cancellationToken)
        => events.PublishAsync(new RequisitionEvent(type, detail.Id, detail.Number, actor.UserId, clock.UtcNow), cancellationToken);

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, Func<Error, T> failure, CancellationToken cancellationToken)
        where T : Result
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
        => ex is not DbUpdateConcurrencyException && DbErrors.IsUniqueViolation(ex, out var target) && target.Contains("RequisitionLines.", StringComparison.Ordinal)
            ? LineDuplicate(null)
            : ConflictError();

    private static Result Fail(Error error) => Result.Failure(error);

    private static Result<T> Fail<T>(Error error) => Result.Failure<T>(error);

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string StatusText(RequisitionStatus status) => status.ToString().ToLowerInvariant();

    private static List<Error> ValidateHeader(string? reason)
    {
        var errors = new List<Error>();
        if (reason is { Length: > Requisition.ReasonMaxLength })
        {
            errors.Add(new Error("requisition.reason_too_long", $"Reason can be at most {Requisition.ReasonMaxLength} characters.", RequisitionFields.Reason));
        }

        return errors;
    }

    private static List<Error> ValidateLine(int quantity, string? note)
    {
        var errors = new List<Error>();
        if (quantity < 1)
        {
            errors.Add(new Error("requisition.quantity_invalid", "Quantity must be a whole number, 1 or more.", RequisitionFields.Quantity));
        }
        else if (quantity > RequisitionLine.MaxQuantity)
        {
            errors.Add(new Error("requisition.quantity_too_large", $"Quantity cannot exceed {RequisitionLine.MaxQuantity:N0}.", RequisitionFields.Quantity));
        }

        if (note is { Length: > RequisitionLine.NoteMaxLength })
        {
            errors.Add(new Error("requisition.note_too_long", $"Note can be at most {RequisitionLine.NoteMaxLength} characters.", RequisitionFields.Note));
        }

        return errors;
    }

    private static void RequireActor(RequisitionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new ArgumentException("A requisition action needs the id of the user who made it.", nameof(actor));
        }
    }

    private static Error NotFound() => new("requisition.not_found", "That requisition no longer exists.");

    private static Error NotApprover() => new("requisition.not_approver", "Only a Manager or an administrator can decide requisitions.");

    private static Error ConflictError() => new("requisition.conflict", "This requisition was changed since you opened it. Reload it and try again.");

    private static Error PartArchived(string sku, string field) => new("requisition.part_archived", $"{sku} is archived and cannot be requested.", field);

    private static Error LineDuplicate(string? sku)
        => new("requisition.line_duplicate", sku is null ? "That part is already on this requisition." : $"{sku} is already on this requisition. Change its quantity instead.", RequisitionFields.ProductId);
}
