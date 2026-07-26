using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MecchaCamouflage.Controller;

public sealed record BridgeReply(
    bool Ok,
    bool Success,
    string Stage,
    string Message,
    string Raw,
    int? ProcessId = null,
    Guid? InstanceId = null,
    string? BridgeHash = null,
    uint? ProtocolVersion = null);

/// <summary>
/// A one-command TCP client. Every connection is authenticated with the direct bridge's
/// unguessable instance token before the existing paint protocol is sent.
/// </summary>
public sealed class BridgeClient
{
    private readonly BridgeEndpoint endpoint;
    private readonly int expectedProcessId;
    private readonly TimeSpan? timeout;

    public BridgeClient(BridgeEndpoint endpoint, int expectedProcessId, TimeSpan? timeout = null)
    {
        if (expectedProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedProcessId));
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        this.expectedProcessId = expectedProcessId;
        this.timeout = timeout;
    }

    public async Task<BridgeReply> RequestAsync(string jsonLine, CancellationToken cancellationToken = default, TimeSpan? timeoutOverride = null)
    {
        try
        {
            using var timeoutCts = CreateTimeoutToken(cancellationToken, timeoutOverride ?? timeout);
            var token = timeoutCts?.Token ?? cancellationToken;
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            await WriteLineAsync(stream, BridgeProtocolV1.CreateHello(endpoint), token);
            var helloRaw = await reader.ReadLineAsync(token);
            if (string.IsNullOrWhiteSpace(helloRaw))
                return new BridgeReply(false, false, "hello_error", "Bridge returned no bootstrap response.", "");
            if (!BridgeProtocolV1.TryValidateHelloReply(helloRaw, endpoint, expectedProcessId, out var identity, out var helloError))
                return new BridgeReply(false, false, "hello_error", helloError, helloRaw);

            await WriteLineAsync(stream, jsonLine, token);
            var response = await reader.ReadToEndAsync(token);
            return Parse(response, identity);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BridgeReply(false, false, "transport_error", "Bridge request timed out.", "");
        }
        catch (OperationCanceledException)
        {
            return new BridgeReply(false, false, "transport_error", "Bridge request canceled.", "");
        }
        catch (Exception ex)
        {
            return new BridgeReply(false, false, "transport_error", ex.Message, "");
        }
    }

    public Task<BridgeReply> PingAsync(CancellationToken cancellationToken = default, TimeSpan? timeoutOverride = null) =>
        RequestAsync("{\"type\":\"ping\"}", cancellationToken, timeoutOverride ?? TimeSpan.FromSeconds(2.5));

    public Task<BridgeReply> CancelPaintAsync(CancellationToken cancellationToken = default) =>
        RequestAsync("{\"type\":\"cancel_paint\"}", cancellationToken, TimeSpan.FromSeconds(5));

    public Task<BridgeReply> ShutdownAsync(CancellationToken cancellationToken = default) =>
        RequestAsync("{\"type\":\"shutdown\"}", cancellationToken, TimeSpan.FromSeconds(10));

    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken cancellationToken)
    {
        var value = line.EndsWith('\n') ? line : line + "\n";
        var bytes = Encoding.UTF8.GetBytes(value);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static CancellationTokenSource? CreateTimeoutToken(CancellationToken cancellationToken, TimeSpan? timeout)
    {
        if (timeout is null || timeout.Value <= TimeSpan.Zero)
            return null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout.Value);
        return cts;
    }

    private static BridgeReply Parse(string raw, BridgeHelloIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new BridgeReply(false, false, "empty_response", "Bridge returned no response.", raw, identity.ProcessId, identity.InstanceId, identity.BridgeHash, identity.ProtocolVersion);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
            var stage = root.TryGetProperty("stage", out var stageProp) ? stageProp.GetString() ?? "" : "";
            var message = root.TryGetProperty("message", out var messageProp) ? messageProp.GetString() ?? "" : "";
            var processId = identity.ProcessId;
            if (root.TryGetProperty("metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("pid", out var pidProp) &&
                pidProp.TryGetInt32(out var responsePid) &&
                responsePid != identity.ProcessId)
            {
                return new BridgeReply(false, false, "identity_error", "Bridge response PID did not match its authenticated hello.", raw, identity.ProcessId, identity.InstanceId, identity.BridgeHash, identity.ProtocolVersion);
            }
            return new BridgeReply(true, success, stage, message, raw, processId, identity.InstanceId, identity.BridgeHash, identity.ProtocolVersion);
        }
        catch (Exception ex)
        {
            return new BridgeReply(false, false, "parse_error", ex.Message, raw, identity.ProcessId, identity.InstanceId, identity.BridgeHash, identity.ProtocolVersion);
        }
    }
}
