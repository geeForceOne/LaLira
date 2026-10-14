using System.Globalization;
using System.Text;
using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

public sealed record CsvTable(char Delimiter, IReadOnlyList<string[]> Rows)
{
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Length);
}

// Which column holds what. Amount is one signed column, or In and Out as two.
public sealed record CsvMapping(bool HasHeader, int DateColumn, int DescriptionColumn, int? AmountColumn, int? InColumn, int? OutColumn, string? DateFormat, int? NoteColumn = null);

public enum CsvRowStatus { Ok, Duplicate, Error }

public sealed record CsvPreviewRow(int Line, DateOnly? Date, string Description, long? Amount, CsvRowStatus Status, string? Error, string? Note = null);

// CSV import (with a preview before anything is booked) and export of an account's positions.
public sealed class CsvService(IDbContextFactory<AppDbContext> dbFactory, AccountService accounts, LedgerService ledger)
{
    public const int MaxRows = 20_000;

    // Date formats tried when reading, in this order; the first that reads every value wins.
    public static readonly IReadOnlyList<string> DateFormats =
        ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "M/d/yyyy", "dd.MM.yy", "d.M.yy", "yyyy/MM/dd", "dd-MM-yyyy"];

    public static CsvTable Parse(string content)
    {
        content = content.TrimStart('﻿');
        var firstLines = content.Split('\n').Take(10).ToList();
        var delimiter = new[] { ';', ',', '\t', '|' }
            .OrderByDescending(d => firstLines.Sum(l => l.Count(c => c == d)))
            .First();

        var rows = new List<string[]>();
        var field = new StringBuilder();
        var row = new List<string>();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(field.ToString().Trim()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                row.Add(field.ToString().Trim());
                field.Clear();
                if (row.Any(f => f.Length > 0)) rows.Add([.. row]);
                row.Clear();
                if (rows.Count > MaxRows) throw new InputException("The file has more than {0} lines. Split it into smaller files.", MaxRows);
            }
            else field.Append(c);
        }
        row.Add(field.ToString().Trim());
        if (row.Any(f => f.Length > 0)) rows.Add([.. row]);
        return new CsvTable(delimiter, rows);
    }

    // A first guess at the columns, from header names (English and German) or from the values.
    public static CsvMapping Guess(CsvTable table)
    {
        if (table.Rows.Count == 0)
            return new CsvMapping(false, 0, 1, 2, null, null, null);
        var header = table.Rows[0].Select(h => h.ToLowerInvariant()).ToArray();
        int? Find(params string[] words) =>
            header.Select((h, i) => (h, i)).Where(x => words.Any(w => x.h.Contains(w))).Select(x => (int?)x.i).FirstOrDefault();

        var date = Find("date", "datum", "valuta");
        var description = Find("description", "beschreibung", "text", "buchung", "details", "memo", "zweck");
        var amount = Find("amount", "betrag", "value", "wert");
        var @in = Find("credit", "gutschrift", "einnahme", "eingang", "in");
        var @out = Find("debit", "belastung", "ausgabe", "ausgang", "lastschrift", "out");
        var note = Find("note", "notiz", "bemerkung", "kommentar", "comment");
        var hasHeader = date is not null || description is not null || amount is not null;

        var data = table.Rows.Skip(hasHeader ? 1 : 0).Take(50).ToList();
        int columns = table.ColumnCount;
        bool All(int col, Func<string, bool> test) => data.Count > 0 && data.All(r => col < r.Length && (r[col].Length == 0 || test(r[col])));

        date ??= Enumerable.Range(0, columns).Cast<int?>().FirstOrDefault(c => All(c!.Value, v => DetectDateFormat([v]) is not null));
        amount ??= @in is null || @out is null || @in == @out
            ? Enumerable.Range(0, columns).Where(c => c != date).Cast<int?>().LastOrDefault(c => All(c!.Value, v => Money.TryParse(v, out _)))
            : null;
        description ??= Enumerable.Range(0, columns).Where(c => c != date && c != amount)
            .OrderByDescending(c => data.Sum(r => c < r.Length ? r[c].Length : 0)).Cast<int?>().FirstOrDefault();

        var dateCol = date ?? 0;
        var format = DetectDateFormat(data.Where(r => dateCol < r.Length).Select(r => r[dateCol]));
        if (note == description || note == dateCol || note == amount) note = null;
        return amount is not null
            ? new CsvMapping(hasHeader, dateCol, description ?? 1, amount, null, null, format, note)
            : new CsvMapping(hasHeader, dateCol, description ?? 1, null, @in, @out, format, note);
    }

    public static string? DetectDateFormat(IEnumerable<string> values)
    {
        var list = values.Where(v => v.Length > 0).Take(200).ToList();
        if (list.Count == 0) return null;
        return DateFormats.FirstOrDefault(f => list.All(v => DateOnly.TryParseExact(v, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)));
    }

    // What importing would book, row by row, with duplicates (same date, amount and description as
    // an existing position or an earlier row) and errors marked.
    public async Task<IReadOnlyList<CsvPreviewRow>> PreviewAsync(string userId, int accountId, CsvTable table, CsvMapping map, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(userId, accountId, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = (await db.Positions.AsNoTracking().Where(p => p.AccountId == accountId)
            .Select(p => new { p.Date, p.Amount, p.Description }).ToListAsync(ct))
            .Select(p => Key(p.Date, p.Amount, p.Description)).ToHashSet();

        var result = new List<CsvPreviewRow>();
        var seen = new HashSet<string>();
        var start = map.HasHeader ? 1 : 0;
        for (var i = start; i < table.Rows.Count; i++)
        {
            var r = table.Rows[i];
            string Cell(int? col) => col is { } c && c < r.Length ? r[c] : "";

            var line = i + 1;
            var description = Cell(map.DescriptionColumn).Trim();
            var note = map.NoteColumn is null ? null : Cell(map.NoteColumn).Trim() is { Length: > 0 } n ? n : null;
            DateOnly? date = DateOnly.TryParseExact(Cell(map.DateColumn), map.DateFormat ?? "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            long? amount = null;
            if (map.AmountColumn is not null)
            {
                if (Money.TryParse(Cell(map.AmountColumn), out var a)) amount = a;
            }
            else
            {
                var inText = Cell(map.InColumn);
                var outText = Cell(map.OutColumn);
                long inValue = 0, outValue = 0;
                var ok = (inText.Length == 0 || Money.TryParse(inText, out inValue)) && (outText.Length == 0 || Money.TryParse(outText, out outValue));
                if (ok && (inText.Length > 0 || outText.Length > 0)) amount = Math.Abs(inValue) - Math.Abs(outValue);
            }

            string? error = date is null ? "The date can't be read."
                : amount is null ? "The amount can't be read."
                : amount == 0 ? "The amount is zero."
                : description.Length == 0 ? "The description is empty."
                : description.Length > Position.MaxDescriptionLength ? "The description is too long."
                : date < account.OpeningDate ? "The date is before the account's opening date."
                : note is { Length: > Position.MaxNoteLength } ? "The note is too long."
                : null;
            if (error is not null)
            {
                result.Add(new CsvPreviewRow(line, date, description, amount, CsvRowStatus.Error, error, note));
                continue;
            }
            var key = Key(date!.Value, amount!.Value, description);
            var duplicate = existing.Contains(key) || !seen.Add(key);
            result.Add(new CsvPreviewRow(line, date, description, amount, duplicate ? CsvRowStatus.Duplicate : CsvRowStatus.Ok, null, note));
        }
        return result;
    }

    public Task<int> ImportAsync(string userId, int accountId, IEnumerable<CsvPreviewRow> rows, CancellationToken ct = default) =>
        ledger.AddManyAsync(userId, accountId,
            rows.Where(r => r.Status != CsvRowStatus.Error).Select(r => new PositionInput(r.Date!.Value, r.Amount!.Value, r.Description, r.Note)).ToList(), ct);

    // The account's positions, oldest first, with the running balance: semicolons, the person's
    // date format and decimal mark, no group separators, UTF-8 with BOM (so Excel reads umlauts).
    public async Task<(string FileName, byte[] Content)> ExportAsync(string userId, int accountId, Texts t, CancellationToken ct = default)
    {
        var view = await ledger.GetAsync(userId, accountId, t.Language, ct);
        var rows = view.Years.SelectMany(y => y.Rows).Reverse().ToList();
        var sb = new StringBuilder();
        sb.Append(string.Join(';', t["Date"], t["Description"], t["Amount"], t["Balance"], t["Note"])).Append("\r\n");
        foreach (var r in rows)
        {
            sb.Append(r.Date.ToString("d", t.Format)).Append(';')
              .Append(Quote(r.Description)).Append(';')
              .Append(r.IsOpening ? "" : Money.FormatForInput(r.Amount, t.Format)).Append(';')
              .Append(Money.FormatForInput(r.Balance, t.Format)).Append(';')
              .Append(r.Note is null ? "" : Quote(r.Note)).Append("\r\n");
        }
        var safeName = new string(view.Account.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var name = $"{(safeName.Length == 0 ? "account" : safeName)}-{view.Today:yyyy-MM-dd}.csv";
        return (name, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
    }

    private static string Quote(string s) =>
        s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string Key(DateOnly date, long amount, string description) =>
        $"{date:yyyy-MM-dd}|{amount}|{description.Trim().ToLowerInvariant()}";
}
