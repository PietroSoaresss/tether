using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Orchestration.Core.Ipc;

namespace Orchestration.App.Services;

public sealed class TetherServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<TetherRequest, Task<TetherResponse>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    public TetherServer(string pipeName, Func<TetherRequest, Task<TetherResponse>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
        _acceptLoop = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                _ = Handle(pipe);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
        }
    }

    private async Task Handle(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                string? line = await reader.ReadLineAsync(_stop.Token);
                var request = line is null
                    ? null
                    : JsonSerializer.Deserialize<TetherRequest>(line, IpcJson.Options);
                if (request is not null)
                {
                    request.Args ??= new Dictionary<string, string>();
                    request.Chain ??= new List<Guid>();
                }
                TetherResponse response = request is null
                    ? TetherResponse.Failure("invalid request")
                    : await _handler(request);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcJson.Options));
            }
            catch (Exception e) when (e is IOException or JsonException or OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
