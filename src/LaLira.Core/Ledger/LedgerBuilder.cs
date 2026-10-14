namespace LaLira.Core.Ledger;

// One line of an account's list: a position, or the opening balance.
public sealed record LedgerRow(
    int? PositionId, DateOnly Date, long Amount, string Description, long Balance,
    bool IsUpcoming, bool IsOpening, int? RecurringRuleId, string? Note = null);

// A calendar year of an account. Past years are shown as one "31.12." line with EndBalance and
// open on request; Rows are newest first.
public sealed record LedgerYear(int Year, long StartBalance, long EndBalance, long In, long Out, bool IsPast, IReadOnlyList<LedgerRow> Rows)
{
    public DateOnly YearEnd => new(Year, 12, 31);
    public int PositionCount => Rows.Count(r => !r.IsOpening);
}

public sealed record LedgerPosition(int Id, DateOnly Date, long Amount, string Description, int? RecurringRuleId, string? Note = null);

// Builds the account's list with running balances. Positions on the same day keep the order they
// were entered in (by id). Anything dated after today is "upcoming": listed, but not in today's balance.
public static class LedgerBuilder
{
    public sealed record Result(long BalanceToday, long BalanceWithUpcoming, int UpcomingCount, IReadOnlyList<LedgerYear> Years);

    public static Result Build(long openingBalance, DateOnly openingDate, IEnumerable<LedgerPosition> positions, DateOnly today, string openingText)
    {
        var ordered = positions.OrderBy(p => p.Date).ThenBy(p => p.Id).ToList();
        var rows = new List<LedgerRow>(ordered.Count + 1)
        {
            new(null, openingDate, openingBalance, openingText, openingBalance, openingDate > today, true, null),
        };
        var balance = openingBalance;
        var balanceToday = openingDate > today ? 0 : openingBalance;
        foreach (var p in ordered)
        {
            balance += p.Amount;
            if (p.Date <= today)
                balanceToday += p.Amount;
            rows.Add(new LedgerRow(p.Id, p.Date, p.Amount, p.Description, balance, p.Date > today, false, p.RecurringRuleId, p.Note));
        }

        var firstYear = openingDate.Year;
        var lastYear = Math.Max(today.Year, rows[^1].Date.Year);
        var years = new List<LedgerYear>();
        var running = 0L;
        for (var year = firstYear; year <= lastYear; year++)
        {
            var inYear = rows.Where(r => r.Date.Year == year).ToList();
            var start = running;
            if (inYear.Count > 0)
                running = inYear[^1].Balance;
            var moves = inYear.Where(r => !r.IsOpening).ToList();
            inYear.Reverse();
            years.Add(new LedgerYear(year, start, running,
                moves.Where(r => r.Amount > 0).Sum(r => r.Amount),
                moves.Where(r => r.Amount < 0).Sum(r => r.Amount),
                year < today.Year, inYear));
        }
        years.Reverse();

        return new Result(balanceToday, balance, rows.Count(r => r.IsUpcoming && !r.IsOpening), years);
    }

    // The balance at the end of each month from the opening month to today's month (that one at
    // today), oldest first. Upcoming positions don't count.
    public static IReadOnlyList<(DateOnly Month, long Balance)> MonthEnds(long openingBalance, DateOnly openingDate, IEnumerable<LedgerPosition> positions, DateOnly today)
    {
        var ordered = positions.Where(p => p.Date <= today).OrderBy(p => p.Date).ToList();
        var result = new List<(DateOnly, long)>();
        var balance = openingBalance;
        var i = 0;
        for (var month = new DateOnly(openingDate.Year, openingDate.Month, 1); month <= today; month = month.AddMonths(1))
        {
            var end = month.AddMonths(1).AddDays(-1);
            if (end > today) end = today;
            while (i < ordered.Count && ordered[i].Date <= end)
                balance += ordered[i++].Amount;
            result.Add((month, balance));
        }
        return result;
    }

    // The balance at the end of `date`.
    public static long BalanceAt(long openingBalance, DateOnly openingDate, IEnumerable<(DateOnly Date, long Amount)> positions, DateOnly date) =>
        date < openingDate ? 0 : openingBalance + positions.Where(p => p.Date <= date).Sum(p => p.Amount);
}
