using LaLira.Core.Localization;
using Microsoft.AspNetCore.Identity;

namespace LaLira.Web.Services;

// Identity's messages that people can actually run into, in the page's language.
public sealed class LocalizedIdentityErrors(Texts T) : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) =>
        new() { Code = nameof(PasswordTooShort), Description = T["Use at least {0} characters for the password.", length] };

    public override IdentityError PasswordMismatch() =>
        new() { Code = nameof(PasswordMismatch), Description = T["The current password is wrong."] };

    public override IdentityError InvalidToken() =>
        new() { Code = nameof(InvalidToken), Description = T["This link has expired or was already used."] };

    public override IdentityError DuplicateUserName(string userName) =>
        new() { Code = nameof(DuplicateUserName), Description = T["Another user already signs in with that email address."] };

    public override IdentityError DuplicateEmail(string email) =>
        new() { Code = nameof(DuplicateEmail), Description = T["Another user already uses that email address."] };

    public override IdentityError InvalidEmail(string? email) =>
        new() { Code = nameof(InvalidEmail), Description = T["That doesn't look like an email address."] };

    public override IdentityError ConcurrencyFailure() =>
        new() { Code = nameof(ConcurrencyFailure), Description = T["Someone else changed this at the same time. Try again."] };
}
