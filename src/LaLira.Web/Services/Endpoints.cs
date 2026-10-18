using System.Globalization;
using LaLira.Core.Backup;
using LaLira.Core.Ledger;
using LaLira.Core.Models;
using LaLira.Core.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LaLira.Web.Services;

public static class Endpoints
{
    public static void MapLaLiraEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A form post (with antiforgery) rather than a link, so a stray GET can't sign anyone out.
        endpoints.MapPost("/account/logout", async (SignInManager<AppUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/account/login");
        });

        // Changing the password on the (interactive) Profile page rotates the security stamp, and an
        // interactive page can't set cookies, so it comes through here to get a fresh sign-in cookie.
        // Harmless as a GET: it only re-issues the caller's own cookie.
        endpoints.MapGet("/account/refresh-signin", async (HttpContext http, SignInManager<AppUser> signInManager,
            UserManager<AppUser> userManager, [FromQuery] string? returnUrl) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
                return Results.LocalRedirect("~/account/login");
            await signInManager.RefreshSignInAsync(user);
            return Results.LocalRedirect(IsLocal(returnUrl) ? returnUrl! : "~/profile");
        }).RequireAuthorization();

        // For a gethomepage.dev "customapi" widget: this month's money in and out, per currency,
        // for the person whose token is in the X-API-Key header. Numbers are plain decimals.
        endpoints.MapGet("/api/homepage/month", async (HttpContext http, SettingsService settings, OverviewService overview) =>
        {
            var userId = await settings.FindUserByHomepageTokenAsync(http.Request.Headers["X-API-Key"].FirstOrDefault(), http.RequestAborted);
            if (userId is null)
                return Results.Unauthorized();
            var (today, flows, total) = await overview.MonthFlowAsync(userId, http.RequestAborted);
            var result = new Dictionary<string, object>
            {
                ["month"] = today.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            };
            foreach (var f in flows)
                result[f.Currency] = new { @in = Money.ToDecimal(f.In), @out = Money.ToDecimal(-f.Out), net = Money.ToDecimal(f.In + f.Out) };
            // Everything in the main currency, when one is set in Settings.
            if (total is not null)
                result["total"] = new
                {
                    currency = total.Currency, @in = Money.ToDecimal(total.In), @out = Money.ToDecimal(-total.Out), net = Money.ToDecimal(total.In + total.Out),
                };
            return Results.Json(result);
        });

        // Downloads a stored backup; admins only.
        endpoints.MapGet("/admin/backups/file/{name}", (string name, BackupService backups) =>
            backups.PathOf(name) is { } path
                ? Results.File(path, "application/zip", name)
                : Results.NotFound())
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
}
