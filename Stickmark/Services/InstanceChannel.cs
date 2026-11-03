using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stickmark.Services;

/// A channel that "passes a file path to an already running instance" in a single-instance app.
/// If you double-click .md on the desktop (or open it with 'Open with'), a second process will appear.
/// The process sends the path to the first instance as a named pipe and exits quietly.
public sealed class InstanceChannel : IDisposable
{
    private const string PipeName = "Stickmark.OpenFile.v1";

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// Called by a running instance — onPaths (multiple lines possible) is executed for each incoming path.
    public void StartServer(Action<string[]> onPaths)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ServerLoopAsync(onPaths, _cts.Token));
    }

    private static async Task ServerLoopAsync(Action<string[]> onPaths, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                var paths = payload
                    .Replace("\r\n", "\n")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (paths.Length > 0) onPaths(paths);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Ignore individual connection failures and continue receiving the next connection.
            }
        }
    }

    /// Called by the newly launched (second) process — passing the paths to the running instance. true if successful.
    public static bool TrySend(string[] paths, int timeoutMs = 1500)
    {
        if (paths.Length == 0) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join("\n", paths));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _loop?.Wait(500);
        }
        catch { /* nothing to do */ }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }
}
