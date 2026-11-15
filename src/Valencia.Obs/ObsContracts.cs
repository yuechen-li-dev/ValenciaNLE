using System.Collections.Immutable;
using System.Net;
using System.Text.Json;

namespace Valencia.Obs;

public sealed record ObsConnectionOptions(Uri Endpoint, bool AllowRemote = false, int RequestTimeoutSeconds = 10)
{
    public void Validate(bool hasPassword)
    {
        if (!Endpoint.IsAbsoluteUri || Endpoint.Scheme is not ("ws" or "wss") || Endpoint.UserInfo.Length != 0
            || Endpoint.Query.Length != 0 || Endpoint.Fragment.Length != 0 || RequestTimeoutSeconds is < 1 or > 60)
            throw new ArgumentException("Use a ws/wss endpoint without embedded credentials, query or fragment.");
        bool local = Endpoint.IsLoopback || IPAddress.TryParse(Endpoint.Host, out var address) && IPAddress.IsLoopback(address);
        if (!local && (!AllowRemote || Endpoint.Scheme != "wss" || !hasPassword))
            throw new ArgumentException("Remote OBS requires explicit opt-in, authenticated wss and normal certificate validation.");
    }
}

public enum ObsConnectionState { Disconnected, Connecting, Connected, Reconnecting, Faulted, Closed }
public sealed record ObsConnectionStatus(ObsConnectionState State, int Retry, string? Error = null);
public sealed record ObsVersion(string ObsVersionString, string WebSocketVersion, int RpcVersion, ImmutableArray<string> AvailableRequests)
{
    public bool Supports(string request) => AvailableRequests.Contains(request);
    public bool SupportsFileChanges => Version.TryParse(WebSocketVersion, out var version) && version >= new Version(5, 5);
}
public sealed record ObsEvent(string Type, JsonElement Data, DateTimeOffset ReceivedAt, long Generation);
public sealed record ObsRecordStatus(bool Active, bool Paused, decimal DurationMs, string Timecode, long Bytes);
public sealed record ObsInput(string Name, string Kind, ImmutableArray<int> AudioTracks, bool RoutingObserved);
public sealed record ObsDiscovery(ObsVersion Version, ObsRecordStatus Recording, string Directory, string SceneCollection,
    string ProgramScene, ImmutableArray<string> Scenes, ImmutableArray<ObsInput> Inputs, JsonElement? VideoSettings,
    ImmutableDictionary<string, string?> RecordingSettings);
public sealed record ObsPreflight(string Severity, string Code, string Message);
public sealed record ObsPreset(string Id, string Purpose, string SuggestedContainer, string SuggestedVideo,
    string AudioGuidance, string EncoderGuidance);

public sealed class ObsRequestException(string request, int code, string message)
    : InvalidOperationException($"OBS {request} failed ({code}): {message}")
{
    public int Code { get; } = code;
}
