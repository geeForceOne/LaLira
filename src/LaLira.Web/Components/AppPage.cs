using System.Security.Claims;
using LaLira.Core;
using LaLira.Core.Email;
using LaLira.Core.Localization;
using LaLira.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace LaLira.Web.Components;

// Base for signed-in pages: the current user's id (every Core call is scoped by it), the page's
// texts, the shared dialogs, and running an action with its error shown as a toast.
public abstract class AppPage : ComponentBase, IDisposable
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    [CascadingParameter]
    protected AppUi Ui { get; set; } = default!;

    [Inject]
    protected NavigationManager Nav { get; set; } = default!;

    [Inject]
    protected Texts T { get; set; } = default!;

    private string? _userId;
    private readonly CancellationTokenSource _pageCts = new();

    // Canceled when the user leaves the page.
    protected CancellationToken PageToken => _pageCts.Token;

    // True while RunAsync is working, to disable buttons.
    protected bool Busy { get; private set; }

    protected async Task<string> UserIdAsync()
    {
        if (_userId is not null)
            return _userId;

        var state = AuthState is null ? null : await AuthState;
        _userId = state?.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Not signed in.");
        return _userId;
    }

    protected async Task<bool> IsAdminAsync()
    {
        var state = AuthState is null ? null : await AuthState;
        return state?.User.IsInRole(Roles.Admin) == true;
    }

    // Runs an action; a problem the person can fix (or something that's gone) shows as a toast.
    // Returns false when it didn't work.
    protected async Task<bool> RunAsync(Func<Task> work)
    {
        if (Busy) return false;
        Busy = true;
        StateHasChanged();
        try
        {
            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InputException or NotFoundException or EmailException or InvalidOperationException)
        {
            Ui.Toast.Error(ErrorText(ex));
            return false;
        }
        finally
        {
            Busy = false;
            StateHasChanged();
        }
    }

    // A Core error in the page's language.
    protected string ErrorText(Exception ex) => ex switch
    {
        InputException input => T[input.Message, input.Args],
        NotFoundException => T[ex.Message],
        EmailException => ex.Message,
        _ => T[ex.Message],
    };

    public virtual void Dispose()
    {
        _pageCts.Cancel();
        _pageCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
