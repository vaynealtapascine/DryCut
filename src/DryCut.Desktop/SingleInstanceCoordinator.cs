using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DryCut.Desktop;

public sealed class SingleInstanceCoordinator : IDisposable
{
    public const string MutexName = "DryCut.Desktop.SingleInstance.v1";
    public const string PipeName = "DryCut.Desktop.Handoff.v1";
    private readonly Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    private bool _disposed;
    public bool IsFirstInstance { get; }
    public event EventHandler<string>? PathReceived;

    public SingleInstanceCoordinator()
    { _mutex = new(true, MutexName, out var created); IsFirstInstance = created; if (created) StartListener(); }
    public static bool TryHandoff(string path)
    { try { using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out); pipe.Connect(350); using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: false) { AutoFlush = true }; writer.WriteLine(path); return true; } catch (IOException) { return false; } catch (TimeoutException) { return false; } }
    private void StartListener()
    { _listenerCancellation = new(); _ = ListenAsync(_listenerCancellation.Token); }
    private async Task ListenAsync(CancellationToken token)
    { while (!token.IsCancellationRequested) { try { using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous); await pipe.WaitForConnectionAsync(token); using var reader = new StreamReader(pipe, Encoding.UTF8); var path = await reader.ReadLineAsync(token); if (!string.IsNullOrWhiteSpace(path)) { try { PathReceived?.Invoke(this, path); } catch { /* a subscriber (e.g. a shutting-down UI) may fail to handle this; keep listening */ } } } catch (OperationCanceledException) { break; } catch (IOException) { try { await Task.Delay(250, token); } catch (OperationCanceledException) { break; } } } }
    // Dispose must stay idempotent: App.Dispose() is public and also runs from OnExit, and both
    // ReleaseMutex() on an already-released mutex and any call on a disposed one throw.
    public void Dispose()
    { if (_disposed) return; _disposed = true; _listenerCancellation?.Cancel(); _listenerCancellation?.Dispose(); if (IsFirstInstance) { try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* not held by this thread; process exit reclaims it */ } } _mutex?.Dispose(); }
}
