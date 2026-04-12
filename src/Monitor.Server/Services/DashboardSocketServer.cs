using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Monitor.Server.Models;
using Monitor.Server.Services.Audio;

namespace Monitor.Server.Services;

public sealed class DashboardSocketServer(
    DashboardStateStore stateStore,
    DashboardSnapshotBuilder snapshotBuilder,
    AudioMixerService audioMixerService)
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = [];
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _audioRefreshLock = new();
    private CancellationTokenSource? _audioRefreshCts;

    public async Task AcceptAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid();
        _clients[clientId] = socket;

        await SendSnapshotAsync(socket, stateStore.Current, context.RequestAborted);
        await ReceiveLoopAsync(clientId, socket, context.RequestAborted);
    }

    public async Task BroadcastSnapshotAsync(DashboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        var disconnected = new List<Guid>();
        foreach (var entry in _clients)
        {
            if (entry.Value.State != WebSocketState.Open)
            {
                disconnected.Add(entry.Key);
                continue;
            }

            try
            {
                await SendSnapshotAsync(entry.Value, snapshot, cancellationToken);
            }
            catch
            {
                disconnected.Add(entry.Key);
            }
        }

        foreach (var clientId in disconnected)
        {
            _clients.TryRemove(clientId, out _);
        }
    }

    private async Task ReceiveLoopAsync(Guid clientId, WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var segment = new ArraySegment<byte>(buffer);

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(segment, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                        return;
                    }

                    stream.Write(segment.Array!, segment.Offset, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(stream.ToArray());
                var command = JsonSerializer.Deserialize<DashboardCommand>(json, _jsonOptions);
                if (command is null)
                {
                    continue;
                }

                switch (command.Type)
                {
                    case "setVolume" when !string.IsNullOrWhiteSpace(command.SessionId) && command.Value.HasValue:
                    {
                        if (audioMixerService.TrySetVolume(command.SessionId, command.Value.Value))
                        {
                            var snapshot = await snapshotBuilder.RefreshAudioAsync(cancellationToken);
                            await BroadcastSnapshotAsync(snapshot, cancellationToken);
                        }

                        break;
                    }
                    case "setMute" when !string.IsNullOrWhiteSpace(command.SessionId) && command.Value.HasValue:
                    {
                        if (audioMixerService.TrySetMute(command.SessionId, command.Value.Value >= 0.5d))
                        {
                            QueueAudioRefreshSequence(cancellationToken);
                        }

                        break;
                    }
                    case "setAllInputMute" when command.Value.HasValue:
                    {
                        if (audioMixerService.TrySetAllInputMute(command.Value.Value >= 0.5d))
                        {
                            QueueAudioRefreshSequence(cancellationToken);
                        }

                        break;
                    }
                    case "setDiscordOutputsMute" when command.Value.HasValue:
                    {
                        if (audioMixerService.TrySetDiscordOutputsMute(command.Value.Value >= 0.5d))
                        {
                            QueueAudioRefreshSequence(cancellationToken);
                        }

                        break;
                    }
                    case "setDiscordPrivacyMode" when command.Value.HasValue:
                    {
                        var nextMuted = command.Value.Value >= 0.5d;
                        var changedOutputs = audioMixerService.TrySetDiscordOutputsMute(nextMuted);
                        var changedInputs = audioMixerService.TrySetAllInputMute(nextMuted);
                        if (changedOutputs || changedInputs)
                        {
                            QueueAudioRefreshSequence(cancellationToken);
                        }

                        break;
                    }
                }
            }
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
        }
    }

    private void QueueAudioRefreshSequence(CancellationToken requestCancellationToken)
    {
        CancellationTokenSource cts;
        lock (_audioRefreshLock)
        {
            _audioRefreshCts?.Cancel();
            _audioRefreshCts?.Dispose();
            _audioRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
            cts = _audioRefreshCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delayMs in new[] { 40, 120, 260 })
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cts.Token);
                    }

                    var snapshot = await snapshotBuilder.RefreshAudioAsync(cts.Token);
                    await BroadcastSnapshotAsync(snapshot, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private async Task SendSnapshotAsync(WebSocket socket, DashboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "snapshot", payload = snapshot }, _jsonOptions);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }
}
