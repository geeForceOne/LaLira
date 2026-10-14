using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

public sealed record AccountInfo(
    int Id, string Name, string Currency, long OpeningBalance, DateOnly OpeningDate,
    bool ShowDecimals, bool IncludeInReports, int SortOrder, string Icon, string? Iban = null);

public sealed record AccountInput(
    string Name, string Currency, long OpeningBalance, DateOnly OpeningDate, bool ShowDecimals, bool IncludeInReports, string Icon, string? Iban = null);

// Ledger accounts: create, change, delete. Every call is scoped to the person who owns them.
public sealed class AccountService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<IReadOnlyList<AccountInfo>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Accounts.AsNoTracking().Where(a => a.UserId == userId)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => Info(a)).ToListAsync(ct);
    }

    public async Task<AccountInfo> GetAsync(string userId, int accountId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Accounts.AsNoTracking().Where(a => a.Id == accountId && a.UserId == userId)
            .Select(a => Info(a)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("That account no longer exists.");
    }

    public async Task<AccountInfo> CreateAsync(string userId, AccountInput input, CancellationToken ct = default)
    {
        var clean = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var order = await db.Accounts.Where(a => a.UserId == userId).Select(a => (int?)a.SortOrder).MaxAsync(ct) ?? 0;
        var account = new Account
        {
            UserId = userId,
            Name = clean.Name,
            Currency = clean.Currency,
            OpeningBalance = clean.OpeningBalance,
            OpeningDate = clean.OpeningDate,
            ShowDecimals = clean.ShowDecimals,
            IncludeInReports = clean.IncludeInReports,
            Icon = clean.Icon,
            Iban = clean.Iban,
            SortOrder = order + 1,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return Info(account);
    }

    public async Task<AccountInfo> UpdateAsync(string userId, int accountId, AccountInput input, CancellationToken ct = default)
    {
        var clean = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId, ct)
            ?? throw new NotFoundException("That account no longer exists.");

        if (clean.OpeningDate > account.OpeningDate)
        {
            if (await db.Positions.AnyAsync(p => p.AccountId == accountId && p.Date < clean.OpeningDate, ct))
                throw new InputException("Some positions are dated before the new opening date. Move or delete them first.");
            if (await db.RecurringRules.AnyAsync(r => r.AccountId == accountId && r.StartDate < clean.OpeningDate, ct))
                throw new InputException("A recurring position starts before the new opening date. Change its start date first.");
        }

        account.Name = clean.Name;
        account.Currency = clean.Currency;
        account.OpeningBalance = clean.OpeningBalance;
        account.OpeningDate = clean.OpeningDate;
        account.ShowDecimals = clean.ShowDecimals;
        account.IncludeInReports = clean.IncludeInReports;
        account.Icon = clean.Icon;
        account.Iban = clean.Iban;
        await db.SaveChangesAsync(ct);
        return Info(account);
    }

    // Deletes the account with all its positions and recurring positions.
    public async Task DeleteAsync(string userId, int accountId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.Accounts.Where(a => a.Id == accountId && a.UserId == userId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw new NotFoundException("That account no longer exists.");
    }

    // Saves a new order; ids not belonging to the person are ignored.
    public async Task ReorderAsync(string userId, IReadOnlyList<int> accountIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.Accounts.Where(a => a.UserId == userId).ToListAsync(ct);
        for (var i = 0; i < accountIds.Count; i++)
            if (accounts.FirstOrDefault(a => a.Id == accountIds[i]) is { } a)
                a.SortOrder = i + 1;
        await db.SaveChangesAsync(ct);
    }

    private static AccountInput Validate(AccountInput input)
    {
        var name = input.Name.Trim();
        if (name.Length == 0)
            throw new InputException("Enter a name for the account.");
        if (name.Length > Account.MaxNameLength)
            throw new InputException("Keep the name under {0} characters.", Account.MaxNameLength);
        var currency = input.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw new InputException("Enter the currency as a three-letter code, like CHF or EUR.");
        if (Math.Abs(input.OpeningBalance) > Money.MaxAbsolute)
            throw new InputException("That amount is too large.");
        if (input.OpeningDate.Year < 1900)
            throw new InputException("Enter an opening date after 1900.");
        var iban = Ledger.Iban.Normalize(input.Iban);
        if (iban.Length > 0 && !Ledger.Iban.IsValid(iban))
            throw new InputException("That IBAN isn't valid. Check it for typos.");
        return input with { Name = name, Currency = currency, Icon = AccountIcons.Normalize(input.Icon), Iban = iban.Length == 0 ? null : iban };
    }

    private static AccountInfo Info(Account a) =>
        new(a.Id, a.Name, a.Currency, a.OpeningBalance, a.OpeningDate, a.ShowDecimals, a.IncludeInReports, a.SortOrder, a.Icon, a.Iban);
}
