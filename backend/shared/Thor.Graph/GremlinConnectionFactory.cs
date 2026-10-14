using System.Net.WebSockets;
using Gremlin.Net.Driver;
using Gremlin.Net.Structure.IO.GraphSON;

namespace Thor.Graph;

/// <summary>
/// Owns the single <see cref="IGremlinClient"/> connection to the shared Neptune cluster,
/// authenticated via IAM/SigV4 (see <see cref="NeptuneSigV4Signer"/>). Internal — nothing outside
/// this library ever touches a raw Gremlin client, only <see cref="IGraphVertexStore"/>/
/// <see cref="IGraphEdgeStore"/>.
///
/// <para>Speaks GraphSON 3, which Neptune supports alongside GraphSON 2: <see cref="GraphBatchWriter"/>'s
/// <c>mergeV</c>/<c>mergeE</c> maps are keyed by <c>T.id</c>, <c>T.label</c> and <c>Direction</c>, and
/// GraphSON 2 maps take string keys only. One format for every client, so readers and writers see the
/// same result shapes. <paramref name="poolSettings"/> is for <see cref="GraphBatchWriter"/>, which
/// sends requests in parallel; everything else keeps the driver's default pool.</para>
/// </summary>
internal sealed class GremlinConnectionFactory(NeptuneOptions options, ConnectionPoolSettings? poolSettings = null) : IDisposable
{
    private readonly Lazy<IGremlinClient> _client = new(() => CreateClient(options, poolSettings));

    public IGremlinClient Client => _client.Value;

    private static IGremlinClient CreateClient(NeptuneOptions options, ConnectionPoolSettings? poolSettings)
    {
        var server = new GremlinServer(options.Endpoint, options.Port, enableSsl: options.EnableSsl);
        var serializer = new GraphSON3MessageSerializer(new GraphSON3Reader(), new GraphSON3Writer());
        return new GremlinClient(
            server, serializer, connectionPoolSettings: poolSettings, webSocketConfiguration: ws => ConfigureWebSocket(ws, options));
    }

    // Gremlin.Net invokes this once per underlying WebSocket connection it opens (not per
    // traversal call), so every connection/reconnect gets a fresh signature and timestamp.
    private static void ConfigureWebSocket(ClientWebSocketOptions webSocketOptions, NeptuneOptions options)
    {
        var scheme = options.EnableSsl ? "https" : "http";
        var endpoint = new Uri($"{scheme}://{options.Endpoint}:{options.Port}");
        var headers = NeptuneSigV4Signer.SignRequest("GET", endpoint, "/gremlin", options.Region);

        foreach (var (name, value) in headers)
        {
            // Host is sent automatically by ClientWebSocket from the connection URI (which
            // already matches what was signed above) and can't be overridden here.
            if (string.Equals(name, "host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            webSocketOptions.SetRequestHeader(name, value);
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
