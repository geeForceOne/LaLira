using System.Text.RegularExpressions;
using LaLira.Core.Backup;
using LaLira.Core.Localization;
using LaLira.Core.Services;

namespace LaLira.Tests;

public class MiscTests
{
    [Fact]
    public void BackupEncryptionRoundTrips()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("ledger");
        var encrypted = BackupCrypto.Encrypt(data, "secret");
        Assert.True(BackupCrypto.IsEncrypted(encrypted));
        Assert.Equal(data, BackupCrypto.Decrypt(encrypted, "secret"));
        Assert.Throws<LaLira.Core.InputException>(() => BackupCrypto.Decrypt(encrypted, "wrong"));
    }

    [Fact]
    public void CsvGuessesColumnsAndFormats()
    {
        var table = CsvService.Parse("Datum;Buchungstext;Betrag\n01.05.2009;Einzahlung Mai;250\n01.06.2009;\"Einzahlung; Juni\";-1'250.50\n");
        Assert.Equal(';', table.Delimiter);
        Assert.Equal("Einzahlung; Juni", table.Rows[2][1]);
        var map = CsvService.Guess(table);
        Assert.True(map.HasHeader);
        Assert.Equal((0, 1, 2), (map.DateColumn, map.DescriptionColumn, map.AmountColumn!.Value));
        Assert.Equal("dd.MM.yyyy", map.DateFormat);
    }

    [Fact]
    public void CsvWithoutHeaderUsesTheValues()
    {
        var map = CsvService.Guess(CsvService.Parse("2026-01-05,Coffee beans,-12.40\n2026-01-06,Salary,5200.00\n"));
        Assert.False(map.HasHeader);
        Assert.Equal((0, 1, 2), (map.DateColumn, map.DescriptionColumn, map.AmountColumn!.Value));
        Assert.Equal("yyyy-MM-dd", map.DateFormat);
    }

    [Fact]
    public void BrowserLanguageMapsToSettings()
    {
        Assert.Equal(("de", "de-CH"), Regions.FromBrowser("de-CH"));
        Assert.Equal(("de", "de-DE"), Regions.FromBrowser("de"));
        Assert.Equal(("en", "en-GB"), Regions.FromBrowser("en-GB"));
        Assert.Equal(("en", "fr-CH"), Regions.FromBrowser("fr-CH"));
    }

    [Fact]
    public void ZurichIsAlwaysOffered()
    {
        Assert.Contains("Europe/Zurich", Regions.TimeZoneIds);
        Assert.Contains("Europe/Berlin", Regions.TimeZoneIdsWith("Europe/Berlin"));
    }

    [Fact]
    public void CurrencyListIsSound()
    {
        Assert.Equal(Currencies.All.Count, Currencies.All.Select(c => c.Code).Distinct().Count());
        Assert.All(Currencies.All, c => Assert.Matches("^[A-Z]{3}$", c.Code));
        Assert.Equal(["CHF", "EUR", "USD", "GBP"], Currencies.Ordered.Take(4).Select(c => c.Code));
        Assert.Equal(Currencies.All.Count, Currencies.Ordered.Count());
        Assert.Equal("Schweizer Franken", Currencies.All.Single(c => c.Code == "CHF").Name("de"));
    }

    [Fact]
    public void AccountIconNamesAreTranslated() =>
        Assert.All(LaLira.Core.Models.AccountIcons.All, i => Assert.True(L.GermanDictionary.ContainsKey(i.Label), i.Label));

    // Every text the code shows has a German translation (keys are the English texts).
    [Fact]
    public void EveryTextHasAGermanTranslation()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
        const string str = "\"((?:[^\"\\\\]|\\\\.)*)\"";
        var patterns = new[]
        {
            new Regex(@"\b[Tt]\[\s*" + str),
            new Regex(@"\b[Tt]\.Plural\([^,]+,\s*" + str + @"\s*,\s*" + str),
            new Regex(@"L\.Text\([^,]+,\s*" + str),
            new Regex(@"(?:InputException|NotFoundException)\(\s*" + str),
        };
        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".razor")) && !f.Contains("obj") && !f.Contains("bin") && !f.Contains("Migrations"));
        var missing = new SortedSet<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var p in patterns)
                foreach (Match m in p.Matches(text))
                    foreach (var g in m.Groups.Cast<Group>().Skip(1).Where(g => g.Success))
                    {
                        var key = Regex.Unescape(g.Value);
                        if (key.Length > 0 && !L.GermanDictionary.ContainsKey(key))
                            missing.Add($"{Path.GetFileName(file)}: {key}");
                    }
        }
        Assert.Empty(missing);
    }
}
