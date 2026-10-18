using System.Runtime.InteropServices;
using System.Text;
using Anki.Backend;
using Ankit.Native.Interop;
using Google.Protobuf;

namespace Ankit.Native;

/// <summary>
/// An instance of Anki's Rust backend. Calls are synchronous and may be made from several threads at once;
/// rslib serializes access to the open collection internally.
/// </summary>
/// <remarks>
/// The typed methods, one per rslib backend method, are generated into
/// <c>Generated/RslibBackend.Methods.g.cs</c> by <c>rslib-bindings/csharp-gen</c>.
/// </remarks>
public sealed unsafe partial class RslibBackend : IDisposable
{
    private readonly BackendHandle _handle;

    private RslibBackend(BackendHandle handle) => _handle = handle;

    /// <summary>
    /// Build hash of the linked rslib, empty when rslib was built outside of Anki's build system.
    /// </summary>
    public static string BuildHash => Encoding.UTF8.GetString(
        MemoryMarshal.CreateReadOnlySpanFromNullTerminated(NativeMethods.BuildHash()));

    /// <summary>
    /// Sets up rslib's global logger, writing to stderr and, if given, to <paramref name="path"/>.
    /// Can be called once per process.
    /// </summary>
    public static void InitializeLogging(string? path = null)
    {
        int status;
        AnkitBuffer error;
        if (path is null)
        {
            status = NativeMethods.InitializeLogging(null, 0, out error);
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            fixed (byte* data = bytes)
            {
                status = NativeMethods.InitializeLogging(data, (nuint)bytes.Length, out error);
            }
        }

        var message = error.TakeBytes();
        if (status != NativeMethods.Ok)
        {
            throw new RslibException(Encoding.UTF8.GetString(message));
        }
    }

    public static RslibBackend Open(BackendInit init)
    {
        var bytes = init.ToByteArray();
        int status;
        BackendHandle handle;
        AnkitBuffer error;
        fixed (byte* data = bytes)
        {
            status = NativeMethods.BackendOpen(data, (nuint)bytes.Length, out handle, out error);
        }

        var message = error.TakeBytes();
        if (status == NativeMethods.Ok)
        {
            return new RslibBackend(handle);
        }

        handle.Dispose();
        throw new RslibException(Encoding.UTF8.GetString(message));
    }

    /// <summary>
    /// Opens a backend whose user-facing messages use the first of <paramref name="preferredLanguages"/>
    /// rslib has translations for, falling back to English.
    /// </summary>
    public static RslibBackend Open(params IEnumerable<string> preferredLanguages) =>
        Open(new BackendInit { PreferredLangs = { preferredLanguages } });

    /// <summary>
    /// Calls a rslib method with an encoded request and returns the encoded response.
    /// </summary>
    /// <exception cref="BackendException">rslib reported an error.</exception>
    public byte[] Command(uint service, uint method, ReadOnlySpan<byte> input)
    {
        int status;
        AnkitBuffer output;
        fixed (byte* data = input)
        {
            status = NativeMethods.BackendCommand(_handle, service, method, data, (nuint)input.Length, out output);
        }

        return Check(status, output.TakeBytes());
    }

    /// <summary>
    /// Runs a raw database command against the open collection, taking and returning JSON.
    /// </summary>
    /// <exception cref="BackendException">rslib reported an error.</exception>
    public byte[] DbCommand(ReadOnlySpan<byte> json)
    {
        int status;
        AnkitBuffer output;
        fixed (byte* data = json)
        {
            status = NativeMethods.BackendDbCommand(_handle, data, (nuint)json.Length, out output);
        }

        return Check(status, output.TakeBytes());
    }

    public void Dispose() => _handle.Dispose();

    private void Call(uint service, uint method, IMessage? request) =>
        Command(service, method, request is null ? [] : request.ToByteArray());

    private TResponse Call<TResponse>(uint service, uint method, IMessage? request,
        MessageParser<TResponse> parser) where TResponse : IMessage<TResponse> =>
        parser.ParseFrom(Command(service, method, request is null ? [] : request.ToByteArray()));

    private static byte[] Check(int status, byte[] output) =>
        status switch
        {
            NativeMethods.Ok => output,
            NativeMethods.BackendError => throw new BackendException(BackendError.Parser.ParseFrom(output)),
            _ => throw new RslibException(Encoding.UTF8.GetString(output)),
        };
}
