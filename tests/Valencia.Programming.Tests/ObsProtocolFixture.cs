using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Valencia.Obs;

namespace Valencia.Programming.Tests;

/// <summary>A real loopback WebSocket test peer, not a substituted ObsClient method.</summary>
internal sealed class ObsProtocolFixture : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Task accepting;
    private readonly ConcurrentBag<Task> connections = [];
    public string Password { get; } = Guid.NewGuid().ToString("N");
    public Uri Endpoint { get; }
    public string Output { get; set; } = Path.GetFullPath("fixture.mkv");
    public bool Active { get; private set; }
    public bool Paused { get; private set; }
    public bool FailStart { get; set; }
    public bool DropNextStatus { get; set; }
    public bool IgnoreStatus { get; set; }
    public int StopDrainMs { get; set; }
    public int StopStatusLagResponses { get; set; }
    private int remainingStatusLag;
    public int Connections;
    public ConcurrentQueue<string> Requests { get; } = [];
    public decimal DurationMs { get; set; } = 2500;
    public string? SplitPath { get; set; }

    public ObsProtocolFixture()
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        Endpoint = new($"ws://127.0.0.1:{number}/");
        listener.Prefixes.Add($"http://127.0.0.1:{number}/");
        listener.Start();
        accepting = Task.Run(AcceptAsync);
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(stopping.Token);
                var upgraded = await context.AcceptWebSocketAsync(null);
                connections.Add(ServeAsync(upgraded.WebSocket));
            }
        }
        catch (Exception error) when (stopping.IsCancellationRequested && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(WebSocket socket)
    {
        using (socket)
        {
            try
            {
                Interlocked.Increment(ref Connections);
                await Send(socket, new { op = 0, d = new { rpcVersion = 1, obsWebSocketVersion = "5.7.3", authentication = new { salt = "test salt", challenge = "test challenge" } } });
                var identify = await Read(socket);
                if (identify.GetProperty("op").GetInt32() != 1 || identify.GetProperty("d").GetProperty("authentication").GetString() != ObsClient.Authenticate(Password, "test salt", "test challenge"))
                {
                    await socket.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed", stopping.Token);
                    return;
                }
                await Send(socket, new { op = 2, d = new { negotiatedRpcVersion = 1 } });
                while (!stopping.IsCancellationRequested)
                {
                    var message = await Read(socket);
                    var request = message.GetProperty("d");
                    string name = request.GetProperty("requestType").GetString()!;
                    Requests.Enqueue(name);
                    if (name == "GetRecordStatus" && DropNextStatus)
                    {
                        DropNextStatus = false;
                        socket.Abort();
                        return;
                    }
                    if (name == "GetRecordStatus" && IgnoreStatus) continue;
                    object data = name switch
                    {
                        "GetVersion" => new { obsVersion = "32.1.2", obsWebSocketVersion = "5.7.3", rpcVersion = 1,
                            availableRequests = new[] { "GetVersion", "GetRecordStatus", "GetRecordDirectory", "GetSceneList", "GetSceneCollectionList", "GetInputList", "GetInputAudioTracks", "GetProfileParameter", "GetVideoSettings", "StartRecord", "StopRecord", "PauseRecord", "ResumeRecord" } },
                        "GetRecordStatus" => new { outputActive = StatusActive(), outputPaused = Paused, outputDuration = DurationMs, outputTimecode = "00:00:02.500", outputBytes = 1024 },
                        "GetRecordDirectory" => new { recordDirectory = Path.GetDirectoryName(Output) },
                        "GetSceneList" => new { currentProgramSceneName = "Fixture", scenes = new[] { new { sceneName = "Fixture" } } },
                        "GetSceneCollectionList" => new { currentSceneCollectionName = "Fixture" },
                        "GetInputList" => new { inputs = new[] { new { inputName = "Microphone", inputKind = "fixture" } } },
                        "GetInputAudioTracks" => new { inputAudioTracks = new Dictionary<string, bool> { ["1"] = true, ["2"] = false } },
                        "GetProfileParameter" => new { parameterValue = "mkv" },
                        "GetVideoSettings" => new { baseWidth = 1920, baseHeight = 1080, outputWidth = 1920, outputHeight = 1080, fpsNumerator = 60, fpsDenominator = 1 },
                        "StopRecord" => new { outputPath = Output },
                        _ => new { }
                    };
                    if (name == "StartRecord" && !FailStart)
                    {
                        Active = true;
                        await Event(socket, "RecordStateChanged", new { outputState = "OBS_WEBSOCKET_OUTPUT_STARTED", outputActive = true, outputPath = Output });
                        if (SplitPath is { } split) await Event(socket, "RecordFileChanged", new { newOutputPath = split });
                    }
                    if (name == "StopRecord")
                    {
                        if (StopDrainMs > 0)
                        {
                            await Send(socket, new { op = 7, d = new { requestType = name, requestId = request.GetProperty("requestId").GetString(),
                                requestStatus = new { result = true, code = 100 }, responseData = new { outputPath = Output } } });
                            await Task.Delay(StopDrainMs, stopping.Token);
                            Active = false;
                            remainingStatusLag = StopStatusLagResponses;
                            await Event(socket, "RecordStateChanged", new { outputState = "OBS_WEBSOCKET_OUTPUT_STOPPED", outputActive = false, outputPath = Output });
                            continue;
                        }
                        Active = false;
                        remainingStatusLag = StopStatusLagResponses;
                        await Event(socket, "RecordStateChanged", new { outputState = "OBS_WEBSOCKET_OUTPUT_STOPPED", outputActive = false, outputPath = Output });
                    }
                    if (name is "PauseRecord" or "ResumeRecord") Paused = name == "PauseRecord";
                    await Send(socket, new { op = 7, d = new { requestType = name, requestId = request.GetProperty("requestId").GetString(),
                        requestStatus = new { result = name != "StartRecord" || !FailStart, code = FailStart && name == "StartRecord" ? 500 : 100 }, responseData = data } });
                }
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }
    private bool StatusActive()
    {
        if (remainingStatusLag > 0) { remainingStatusLag--; return true; }
        return Active;
    }
    private Task Event(WebSocket socket, string type, object data) => Send(socket, new { op = 5, d = new { eventType = type, eventIntent = 64, eventData = data } });
    private async Task Send(WebSocket socket, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        // Fragment every message to exercise real receive aggregation.
        int middle = bytes.Length / 2;
        await socket.SendAsync(bytes.AsMemory(0, middle), WebSocketMessageType.Text, false, stopping.Token);
        await socket.SendAsync(bytes.AsMemory(middle), WebSocketMessageType.Text, true, stopping.Token);
    }
    private async Task<JsonElement> Read(WebSocket socket)
    {
        using var memory = new MemoryStream();
        byte[] buffer = new byte[16384];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, stopping.Token);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Peer closed");
            memory.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonDocument.Parse(memory.ToArray()).RootElement.Clone();
        }
    }
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Close();
        await accepting;
        await Task.WhenAll(connections);
        stopping.Dispose();
    }
}
