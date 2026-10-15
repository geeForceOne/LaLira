namespace LaLira.Core.Models;

// The icons an account can have; Icon.razor draws them by key. Labels are English texts
// (translated where shown).
public static class AccountIcons
{
    public const string Default = "bank";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("bank", "Bank"),
        ("piggy", "Savings"),
        ("wallet", "Wallet"),
        ("card", "Credit card"),
        ("coin", "Cash"),
        ("house", "Home"),
        ("car", "Car"),
        ("chart", "Investments"),
        ("umbrella", "Pension and insurance"),
        ("tax", "Tax"),
        ("briefcase", "Work"),
        ("cart", "Shopping"),
        ("plane", "Travel"),
        ("cap", "Education"),
        ("heart", "Health"),
        ("gift", "Gifts"),
        ("child", "Children"),
    ];

    public static string Normalize(string? key) => All.Any(i => i.Key == key) ? key! : Default;
}
