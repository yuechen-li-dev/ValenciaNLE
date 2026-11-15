using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Valencia.Obs;

/// <summary>Owned WebSocket 5/RPC1 connection. Requests are never replayed after a disconnect.</summary>
public sealed class ObsClient : IAsyncDisposable
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "GetVersion", "GetRecordStatus", "GetRecordDirectory", "GetSceneCollectionList", "GetCurrentProgramScene", "GetSceneList",
        "GetInputList", "GetInputAudioTracks", "GetProfileList", "GetProfileParameter", "GetVideoSettings",
        "StartRecord", "StopRecord", "PauseRecord", "ResumeRecord", "GetReplayBufferStatus", "SaveReplayBuffer"
    };
    private readonly ObsConnectionOptions options;
    private readonly string? password;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly SemaphoreSlim requestSlots = new(32, 32);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Channel<ObsEvent> events = Channel.CreateBounded<ObsEvent>(new BoundedChannelOptions(128)
    {
        SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly TaskCompletionSource firstConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientWebSocket? socket;
    private Task? supervisor;
    private ObsConnectionStatus status = new(ObsConnectionState.Disconnected, 0);
    private ObsVersion? version;
    private long generation;
    public ObsConnectionStatus Status => Volatile.Read(ref status);
    public ObsVersion? Version => Volatile.Read(ref version);
    public ChannelReader<ObsEvent> Events => events.Reader;

    public ObsClient(ObsConnectionOptions options, string? password = null)
    {
        options.Validate(!string.IsNullOrEmpty(password));
        this.options = options;
        this.password = password;
    }

    public async Task ConnectAsync(CancellationToken cancellation = default)
    {
        if (supervisor is null) supervisor = Task.Run(SuperviseAsync);
        await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation).ConfigureAwait(false);
    }

    public async Task<JsonElement> RequestAsync(string type, object? data = null, CancellationToken cancellation = default)
    {
        if (!Allowed.Contains(type)) throw new NotSupportedException("This connector does not expose streaming, scene changes or configuration writes.");
        if (Status.State != ObsConnectionState.Connected) throw new IOException("OBS is disconnected; query status after reconnect before retrying an operation.");
        if (Version is { } capabilities && !capabilities.Supports(type)) throw new NotSupportedException("OBS did not advertise " + type);
        await requestSlots.WaitAsync(cancellation).ConfigureAwait(false);
        string id = Guid.NewGuid().ToString("N");
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = response;
        try
        {
            await sending.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var current = socket ?? throw new IOException("OBS connection was lost.");
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { op = 6, d = new { requestType = type, requestId = id, requestData = data } });
                await current.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false);
            }
            finally { sending.Release(); }
            var message = await response.Task.WaitAsync(TimeSpan.FromSeconds(options.RequestTimeoutSeconds), cancellation).ConfigureAwait(false);
            var result = message.GetProperty("requestStatus");
            if (!result.GetProperty("result").GetBoolean())
                throw new ObsRequestException(type, result.GetProperty("code").GetInt32(), result.TryGetProperty("comment", out var comment) ? comment.GetString()![..Math.Min(512, comment.GetString()!.Length)] : "Request rejected.");
            return message.TryGetProperty("responseData", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(new { });
        }
        finally { pending.TryRemove(id, out _); requestSlots.Release(); }
    }

    private async Task SuperviseAsync()
    {
        int attempt = 0;
        bool connectedBefore = false;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                ClientWebSocket? connection = null;
                Task? reading = null;
                try
                {
                    Volatile.Write(ref status, new(connectedBefore ? ObsConnectionState.Reconnecting : ObsConnectionState.Connecting, attempt));
                    connection = new();
                    connection.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    handshake.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                    await connection.ConnectAsync(options.Endpoint, handshake.Token).ConfigureAwait(false);
                    var hello = await ReceiveAsync(connection, handshake.Token).ConfigureAwait(false);
                    if (hello.GetProperty("op").GetInt32() != 0) throw new InvalidDataException("OBS WebSocket 5 Hello required.");
                    var greeting = hello.GetProperty("d");
                    if (greeting.GetProperty("rpcVersion").GetInt32() < 1) throw new NotSupportedException("OBS RPC1 required.");
                    string? authentication = null;
                    if (greeting.TryGetProperty("authentication", out var challenge))
                    {
                        if (string.IsNullOrEmpty(password)) throw new UnauthorizedAccessException("OBS authentication is enabled; configure a password in Valencia's protected user settings.");
                        authentication = Authenticate(password, challenge.GetProperty("salt").GetString()!, challenge.GetProperty("challenge").GetString()!);
                    }
                    byte[] identify = JsonSerializer.SerializeToUtf8Bytes(new { op = 1, d = new { rpcVersion = 1, authentication, eventSubscriptions = 79 } });
                    await connection.SendAsync(identify, WebSocketMessageType.Text, true, handshake.Token).ConfigureAwait(false);
                    var identified = await ReceiveAsync(connection, handshake.Token).ConfigureAwait(false);
                    if (identified.GetProperty("op").GetInt32() != 2 || identified.GetProperty("d").GetProperty("negotiatedRpcVersion").GetInt32() != 1)
                        throw new InvalidDataException("OBS identification failed or negotiated an unsupported RPC version.");
                    socket = connection;
                    Interlocked.Increment(ref generation);
                    Volatile.Write(ref version, null);
                    Volatile.Write(ref status, new(ObsConnectionState.Connected, 0));
                    reading = ReadLoopAsync(connection, stopping.Token);
                    var info = await RequestAsync("GetVersion", cancellation: stopping.Token).ConfigureAwait(false);
                    var discovered = new ObsVersion(info.GetProperty("obsVersion").GetString()!, info.GetProperty("obsWebSocketVersion").GetString()!,
                        info.GetProperty("rpcVersion").GetInt32(), [.. info.GetProperty("availableRequests").EnumerateArray().Select(r => r.GetString()!)]);
                    Volatile.Write(ref version, discovered);
                    Publish("Connected", JsonSerializer.SerializeToElement(new { reconnected = connectedBefore }));
                    firstConnection.TrySetResult();
                    connectedBefore = true;
                    attempt = 0;
                    await reading.ConfigureAwait(false);
                    throw new IOException("OBS closed its connection.");
                }
                catch (Exception error) when (!stopping.IsCancellationRequested)
                {
                    bool permanent = error is UnauthorizedAccessException or NotSupportedException;
                    attempt++;
                    Volatile.Write(ref status, new(permanent || attempt > 5 ? ObsConnectionState.Faulted : ObsConnectionState.Reconnecting, attempt, error.Message));
                    // No credentials or authentication frames are logged or included in these events.
                    if (!events.Writer.TryWrite(new("Disconnected", JsonSerializer.SerializeToElement(new { continuityLost = true }), DateTimeOffset.UtcNow, generation)))
                        permanent = true;
                    if (permanent || attempt > 5) { firstConnection.TrySetException(error); return; }
                }
                finally
                {
                    socket = null;
                    connection?.Abort();
                    if (reading is not null) { try { await reading.ConfigureAwait(false); } catch (Exception) { } }
                    connection?.Dispose();
                    foreach (var request in pending.Values) request.TrySetException(new IOException("OBS connection lost; operation outcome may be unknown. Requests are not replayed."));
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(4000, 250 * (1 << Math.Min(attempt - 1, 4)))), stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            events.Writer.TryComplete();
            firstConnection.TrySetCanceled(stopping.Token);
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket connection, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var message = await ReceiveAsync(connection, cancellation).ConfigureAwait(false);
            var data = message.GetProperty("d");
            switch (message.GetProperty("op").GetInt32())
            {
                case 7:
                    if (pending.TryGetValue(data.GetProperty("requestId").GetString()!, out var request)) request.TrySetResult(data.Clone());
                    break;
                case 5:
                    Publish(data.GetProperty("eventType").GetString()!, data.GetProperty("eventData"));
                    break;
                default: throw new InvalidDataException("Unexpected OBS message opcode.");
            }
        }
    }

    private void Publish(string type, JsonElement data)
    {
        if (!events.Writer.TryWrite(new(type, data.Clone(), DateTimeOffset.UtcNow, generation)))
            throw new IOException("OBS event buffer overflow: recording continuity must be reconciled; no events were silently dropped.");
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket connection, CancellationToken cancellation)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            var result = await connection.ReceiveAsync(buffer, cancellation).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if ((int?)result.CloseStatus == 4009) throw new UnauthorizedAccessException("OBS rejected authentication.");
                throw new IOException("OBS closed the WebSocket (" + result.CloseStatus + ").");
            }
            if (result.MessageType != WebSocketMessageType.Text || bytes.Length + result.Count > 1_048_576)
                throw new InvalidDataException("OBS requires bounded JSON text messages (maximum 1 MiB).");
            bytes.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
            return document.RootElement.Clone();
        }
    }

    public static string Authenticate(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        socket?.Abort();
        if (supervisor is not null) await supervisor.ConfigureAwait(false);
        Volatile.Write(ref status, new(ObsConnectionState.Closed, 0));
        stopping.Dispose(); sending.Dispose(); requestSlots.Dispose();
        // Deliberately no StopRecord/StopStream on disposal.
    }
}
