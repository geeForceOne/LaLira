using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

// MonthEnds: the balance at the end of every month from the opening to today (today's balance
// for the current month), oldest first, for the chart.
public sealed record LedgerView(
    AccountInfo Account, DateOnly Today, long BalanceToday, long BalanceWithUpcoming, int UpcomingCount, IReadOnlyList<LedgerYear> Years,
    IReadOnlyList<(DateOnly Month, long Balance)> MonthEnds);

public sealed record PositionInput(DateOnly Date, long Amount, string Description, string? Note = null);

public sealed record PositionView(int Id, int AccountId, DateOnly Date, long Amount, string Description, int? RecurringRuleId, string? Note = null);

// An account's positions: the list with running balances, and adding, changing and deleting.
public sealed class LedgerService(IDbContextFactory<AppDbContext> dbFactory, AccountService accounts, UserClock clock)
{
    public async Task<LedgerView> GetAsync(string userId, int accountId, string language, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(userId, accountId, ct);
        var today = await clock.TodayAsync(userId, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var positions = await db.Positions.AsNoTracking().Where(p => p.AccountId == accountId)
            .Select(p => new LedgerPosition(p.Id, p.Date, p.Amount, p.Description, p.RecurringRuleId, p.Note))
            .ToListAsync(ct);
        var built = LedgerBuilder.Build(account.OpeningBalance, account.OpeningDate, positions, today, L.Text(language, "Opening balance"));
        return new LedgerView(account, today, built.BalanceToday, built.BalanceWithUpcoming, built.UpcomingCount, built.Years,
            LedgerBuilder.MonthEnds(account.OpeningBalance, account.OpeningDate, positions, today));
    }

    public async Task<PositionView> GetPositionAsync(string userId, int positionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Positions.AsNoTracking().Where(p => p.Id == positionId && p.Account!.UserId == userId)
            .Select(p => new PositionView(p.Id, p.AccountId, p.Date, p.Amount, p.Description, p.RecurringRuleId, p.Note))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("That position no longer exists.");
    }

    public async Task<PositionView> AddAsync(string userId, int accountId, PositionInput input, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(userId, accountId, ct);
        var clean = Validate(input, account);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = new Position { AccountId = accountId, Date = clean.Date, Amount = clean.Amount, Description = clean.Description, Note = clean.Note };
        db.Positions.Add(p);
        await db.SaveChangesAsync(ct);
        return new PositionView(p.Id, p.AccountId, p.Date, p.Amount, p.Description, null, p.Note);
    }

    // Adds many at once (CSV import), in one transaction.
    public async Task<int> AddManyAsync(string userId, int accountId, IReadOnlyList<PositionInput> inputs, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(userId, accountId, ct);
        var clean = inputs.Select(i => Validate(i, account)).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Positions.AddRange(clean.Select(i => new Position { AccountId = accountId, Date = i.Date, Amount = i.Amount, Description = i.Description, Note = i.Note }));
        await db.SaveChangesAsync(ct);
        return clean.Count;
    }

    public async Task<PositionView> UpdateAsync(string userId, int positionId, PositionInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Positions.Include(x => x.Account).FirstOrDefaultAsync(x => x.Id == positionId && x.Account!.UserId == userId, ct)
            ?? throw new NotFoundException("That position no longer exists.");
        var clean = Validate(input, await accounts.GetAsync(userId, p.AccountId, ct));
        p.Date = clean.Date;
        p.Amount = clean.Amount;
        p.Description = clean.Description;
        p.Note = clean.Note;
        await db.SaveChangesAsync(ct);
        return new PositionView(p.Id, p.AccountId, p.Date, p.Amount, p.Description, p.RecurringRuleId, p.Note);
    }

    // Changes only the note (from the note icon in the list); empty removes it.
    public async Task SetNoteAsync(string userId, int positionId, string? note, CancellationToken ct = default)
    {
        var clean = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (clean is { Length: > Position.MaxNoteLength })
            throw new InputException("Keep the note under {0} characters.", Position.MaxNoteLength);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var updated = await db.Positions.Where(p => p.Id == positionId && p.Account!.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Note, clean), ct);
        if (updated == 0)
            throw new NotFoundException("That position no longer exists.");
    }

    public async Task DeleteAsync(string userId, int positionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.Positions.Where(p => p.Id == positionId && p.Account!.UserId == userId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw new NotFoundException("That position no longer exists.");
    }

    internal static PositionInput Validate(PositionInput input, AccountInfo account)
    {
        var description = input.Description.Trim();
        if (description.Length == 0)
            throw new InputException("Enter a description.");
        if (description.Length > Position.MaxDescriptionLength)
            throw new InputException("Keep the description under {0} characters.", Position.MaxDescriptionLength);
        if (input.Amount == 0)
            throw new InputException("Enter an amount other than zero.");
        if (Math.Abs(input.Amount) > Money.MaxAbsolute)
            throw new InputException("That amount is too large.");
        if (input.Date < account.OpeningDate)
            throw new InputException("The date is before the account's opening date ({0}).", account.OpeningDate);
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note is { Length: > Position.MaxNoteLength })
            throw new InputException("Keep the note under {0} characters.", Position.MaxNoteLength);
        return input with { Description = description, Note = note };
    }
}
