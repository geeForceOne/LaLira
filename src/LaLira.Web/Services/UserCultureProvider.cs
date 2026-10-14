using System.Security.Claims;
using LaLira.Core.Services;
using Microsoft.AspNetCore.Localization;

namespace LaLira.Web.Services;

// A signed-in person's language (UI culture) and region format (culture) come from their
// settings; signed-out pages fall through to the browser's Accept-Language. The interactive
// circuit keeps the cultures of the request that opened it, so a settings change reloads the page.
public sealed class UserCultureProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext http)
    {
        if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
            return null;
        var settings = http.RequestServices.GetRequiredService<SettingsService>();
        var s = await settings.GetAsync(userId, http.RequestAborted);
        return new ProviderCultureResult(s.RegionFormat, s.Language);
    }
}
