using LaLira.Web.Components.Shared;

namespace LaLira.Web.Services;

// The layout's shared dialogs, cascaded to every page so each one can await a confirmation or
// show a toast without declaring its own modal markup.
public sealed class AppUi
{
    public ConfirmDialog Confirm { get; internal set; } = default!;
    public PromptDialog Prompt { get; internal set; } = default!;
    public Toast Toast { get; internal set; } = default!;

    // The account list in the sidebar; pages refresh it after a change to accounts or balances.
    public AccountsNav? AccountsNav { get; internal set; }

    public Task RefreshAccountsAsync() => AccountsNav?.RefreshAsync() ?? Task.CompletedTask;
}
