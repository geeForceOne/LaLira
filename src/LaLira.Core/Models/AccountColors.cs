namespace LaLira.Core.Models;

// The colors an account can have; app.css defines each as --c-<key> for the dark and the light
// theme. Labels are English texts (translated where shown). No red: a red account would read
// like a negative balance. Spread over hue and lightness so even the closest pair stays apart
// in a stacked chart, and ordered so the colors handed out first differ the most.
public static class AccountColors
{
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("blue", "Blue"),
        ("orange", "Orange"),
        ("green", "Green"),
        ("pink", "Pink"),
        ("sky", "Sky"),
        ("gold", "Gold"),
        ("purple", "Purple"),
        ("teal", "Teal"),
        ("brown", "Brown"),
        ("lime", "Lime"),
        ("indigo", "Indigo"),
        ("slate", "Slate"),
        ("mint", "Mint"),
        ("plum", "Plum"),
        ("sand", "Sand"),
        ("forest", "Forest"),
    ];

    // The neutral color of the accounts and portfolios added up as "Others" in the charts; not one
    // an account can have.
    public const string Others = "others";

    public static bool IsKnown(string? key) => All.Any(c => c.Key == key);

    // The first color none of the given ones uses; after all are taken, they repeat in order.
    public static string Next(IEnumerable<string?> used)
    {
        var taken = used.Where(IsKnown).ToList();
        var free = All.Select(c => c.Key).FirstOrDefault(k => !taken.Contains(k));
        return free ?? All[taken.Count % All.Count].Key;
    }

    // The same from the palette's end, for portfolios, so the next new account's color stays free.
    public static string NextFromEnd(IEnumerable<string?> used)
    {
        var taken = used.Where(IsKnown).ToList();
        var free = All.Select(c => c.Key).Reverse().FirstOrDefault(k => !taken.Contains(k));
        return free ?? All[All.Count - 1 - taken.Count % All.Count].Key;
    }
}
