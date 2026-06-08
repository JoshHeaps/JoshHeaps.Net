using JoshHeaps.Net.Services.Interfaces;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// Middleman wrapper over the native custom chess engine (chess_engine.dll / libchess_engine.so).
/// Shares <see cref="IChessEngine"/> with <see cref="Stockfish"/> so the two are swappable.
/// The boundary contract is FEN string in, UCI move string out — identical to Stockfish.
/// </summary>
public sealed partial class CustomChessEngine : IChessEngine
{
    private readonly EngineSafeHandle _handle;

    public int Skill { get; }

    public CustomChessEngine(int skill = 20)
    {
        Skill = skill;

        var handle = NativeMethods.engine_create($"skill={skill}");

        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Native chess engine failed to initialize (engine_create returned null).");

        _handle = new EngineSafeHandle(handle);
    }

    public Task<string> GetBestMoveAsync(string fen) => Task.Run(() => GetBestMove(fen));

    private unsafe string GetBestMove(string fen)
    {
        const int bufferLength = 16;            // longest UCI move is 5 chars ("e7e8q") + NUL
        byte* buffer = stackalloc byte[bufferLength];

        var added = false;

        try
        {
            _handle.DangerousAddRef(ref added);
            var code = NativeMethods.engine_best_move(_handle.DangerousGetHandle(), fen, buffer, bufferLength);

            if (code != 0)
                throw new InvalidOperationException($"Native chess engine failed to produce a move for FEN '{fen}' (engine_best_move returned {code}).");

            return Marshal.PtrToStringUTF8((IntPtr)buffer)
                ?? throw new InvalidOperationException("Native chess engine returned an empty move.");
        }
        finally
        {
            if (added)
                _handle.DangerousRelease();
        }
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Guarantees the native handle is released exactly once via engine_destroy.</summary>
    private sealed class EngineSafeHandle : SafeHandle
    {
        public EngineSafeHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            NativeMethods.engine_destroy(handle);
            return true;
        }
    }

    /// <summary>
    /// P/Invoke surface for chess_engine.(dll|so). The resolver maps the logical name
    /// "chess_engine" to the platform binary in the Resources folder (mirrors Stockfish).
    /// </summary>
    private static partial class NativeMethods
    {
        private const string LibName = "chess_engine";

        static NativeMethods() =>
            NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != LibName)
                return IntPtr.Zero;

            var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "chess_engine.dll"
                : "libchess_engine.so";

            var path = Path.Combine(AppContext.BaseDirectory, "Resources", fileName);

            if (NativeLibrary.TryLoad(path, out var handle))
                return handle;

            return NativeLibrary.Load(libraryName, assembly, searchPath);
        }

        [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial IntPtr engine_create(string? options);

        [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int engine_best_move(IntPtr engine, string fen, byte* outBuffer, int outLength);

        [LibraryImport(LibName)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void engine_destroy(IntPtr engine);
    }
}
