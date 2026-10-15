using System.Globalization;
using LaLira.Core;
using LaLira.Core.Backup;
using LaLira.Core.Data;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Web.Components;
using LaLira.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Everything persistent lives under one data directory (a Docker volume in production): the
// SQLite database, the Data Protection key ring and the backups. Each path can also be
// overridden on its own through Storage__DatabasePath, Storage__KeysPath, Storage__BackupsPath.
var storage = builder.Configuration.GetSection("Storage");
var paths = StoragePaths.From(storage["DataDirectory"] ?? "data", storage["DatabasePath"], storage["KeysPath"], storage["BackupsPath"],
    builder.Environment.ContentRootPath);
paths.CreateDirectories();

// A restore prepared on the Backups page is swapped in now, before anything opens the files.
using (var startupLog = LoggerFactory.Create(b => b.AddConsole()))
    BackupService.ApplyPendingRestore(paths, startupLog.CreateLogger("Restore"));

builder.Services.AddLaLiraCore(paths);

builder.Services.AddDataProtection()
    // Not the product name: changing it would make the stored SMTP password unreadable.
    .SetApplicationName("LaLira")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysPath));

// Identity: email + password now. External logins (Google, Apple, Facebook) plug in later as
// additional authentication handlers on top of the same Identity cookies.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    // Its own name: cookies ignore the port, so with the default name another ASP.NET Identity app
    // on the same host (Recall, for example) would overwrite this one and sign people out.
    options.Cookie.Name = ".LaLira.Auth";
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.Cookie.Name = ".LaLira.Antiforgery");

builder.Services.AddIdentityCore<AppUser>(options =>
    {
        // Uniqueness of emails is checked in UserAdminService instead.
        options.User.RequireUniqueEmail = false;
        // The email is the UserName, and an email may contain characters beyond the default set.
        options.User.AllowedUserNameCharacters = "";
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireDigit = false;
        options.Lockout.MaxFailedAccessAttempts = 10;
    })
    .AddRoles<IdentityRole>()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<LocalizedIdentityErrors>()
    .AddTokenProvider<InviteTokenProvider>(AccountMailService.InviteProvider);
// Password reset links are short-lived; invite links get their own, longer lifespan.
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = AccountMailService.ResetLifespan);

// Language from the user's settings (or the browser when signed out); region format separately.
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.SupportedCultures = Regions.RegionFormats.Select(CultureInfo.GetCultureInfo).ToList();
    o.SupportedUICultures = L.Languages.Select(CultureInfo.GetCultureInfo).ToList();
    o.DefaultRequestCulture = new RequestCulture(Regions.DefaultRegion, L.English);
    o.FallBackToParentCultures = true;
    o.FallBackToParentUICultures = true;
    o.RequestCultureProviders = [new UserCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
});

builder.Services.AddSingleton<UserAdminService>();
builder.Services.AddScoped<CircuitHandler, ActivityCircuitHandler>();
builder.Services.AddSingleton<AccountMailService>();
builder.Services.AddSingleton<RequestLimiter>();
builder.Services.AddHostedService<SchedulerHost>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment())
    // Room for CSV and backup uploads, which arrive over the circuit.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();

    // Before an existing database gets a newer schema (an update of the app), keep a copy of it as
    // it is. Without that copy the update doesn't run: the data stays untouched, and the log says why.
    if ((await db.Database.GetAppliedMigrationsAsync()).Any() && (await db.Database.GetPendingMigrationsAsync()).Any())
    {
        var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            var safety = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateBeforeUpdateAsync();
            log.LogWarning("The database schema is being updated; a backup of the previous state is in {Backup}", safety.Name);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Couldn't make the backup that comes before a database update, so the update was not applied and the app stops. Fix the cause (for example free disk space in {Folder}) and start it again.", paths.BackupsPath);
            Environment.ExitCode = 1;
            return;
        }
    }
    await db.Database.MigrateAsync();

    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(Roles.Admin))
        await roles.CreateAsync(new IdentityRole(Roles.Admin));
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
// After authentication, so the signed-in person's own language and formats apply.
app.UseRequestLocalization();
app.UseAuthorization();
// After authentication, so tokens are validated against the signed-in user they were issued for.
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapLaLiraEndpoints();

app.Run();
