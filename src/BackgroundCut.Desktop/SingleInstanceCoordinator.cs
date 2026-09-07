using System.IO;
using System.IO.Pipes;
using System.Text;

namespace BackgroundCut.Desktop;

public sealed class SingleInstanceCoordinator : IDisposable
{
    public const string MutexName = "BackgroundCut.Desktop.SingleInstance.v1";
    public const string PipeName = "BackgroundCut.Desktop.Handoff.v1";
    private readonly Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    public bool IsFirstInstance { get; }
    public event EventHandler<string>? PathReceived;

    public SingleInstanceCoordinator()
    { _mutex = new(true, MutexName, out var created); IsFirstInstance = created; if (created) StartListener(); }
    public static bool TryHandoff(string path)
    { try { using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out); pipe.Connect(350); using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: false) { AutoFlush = true }; writer.WriteLine(path); return true; } catch (IOException) { return false; } catch (TimeoutException) { return false; } }
    private void StartListener()
    { _listenerCancellation = new(); _ = ListenAsync(_listenerCancellation.Token); }
    private async Task ListenAsync(CancellationToken token)
    { while (!token.IsCancellationRequested) { try { using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous); await pipe.WaitForConnectionAsync(token); using var reader = new StreamReader(pipe, Encoding.UTF8); var path = await reader.ReadLineAsync(token); if (!string.IsNullOrWhiteSpace(path)) PathReceived?.Invoke(this, path); } catch (OperationCanceledException) { break; } catch (IOException) { } } }
    public void Dispose() { _listenerCancellation?.Cancel(); _mutex?.Dispose(); }
}
