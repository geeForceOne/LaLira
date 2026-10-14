namespace LaLira.Core;

// Thrown when something doesn't exist or belongs to someone else. The message is user-facing
// English and is translated where it's shown.
public sealed class NotFoundException(string message = "That no longer exists.") : Exception(message);

// A problem with what the person entered, in user-facing English (translated where it's shown).
// Args fill {0}, {1}... after translation.
public sealed class InputException(string message, params object?[] args) : Exception(message)
{
    public object?[] Args { get; } = args;
}
