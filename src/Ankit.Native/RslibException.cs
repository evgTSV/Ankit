using Anki.Backend;

namespace Ankit.Native;

/// <summary>
/// The native library failed to complete a call: rslib panicked, or the call was malformed.
/// </summary>
public class RslibException(string message) : Exception(message);

/// <summary>
/// rslib rejected a call, see <see cref="Error"/> for the details it reported.
/// </summary>
public sealed class BackendException(BackendError error) : RslibException(error.Message)
{
    public BackendError Error { get; } = error;

    public BackendError.Types.Kind Kind => Error.Kind;
}
