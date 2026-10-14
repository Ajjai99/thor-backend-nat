using Thor.Graph;

namespace Thor.Workflows.Pai.Composition;

/// <summary>
/// Builds the graph reader from the environment. PAI only reads the graph, so
/// <c>THOR_NEPTUNE_ENDPOINT</c> is the cluster's reader endpoint (see
/// <c>infra/src/workflow_definitions.tf</c>), keeping PAI's traversals off the writer that
/// graph-load writes through.
/// </summary>
internal static class PaiGraph
{
    public static GraphReader FromEnvironment() => new(new NeptuneOptions
    {
        Endpoint = Environment.GetEnvironmentVariable("THOR_NEPTUNE_ENDPOINT")
            ?? throw new InvalidOperationException("THOR_NEPTUNE_ENDPOINT is required."),
        Port = int.TryParse(Environment.GetEnvironmentVariable("THOR_NEPTUNE_PORT"), out var port) ? port : 8182,
        EnableSsl = !bool.TryParse(Environment.GetEnvironmentVariable("THOR_NEPTUNE_ENABLESSL"), out var ssl) || ssl,
        Region = Environment.GetEnvironmentVariable("THOR_AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? throw new InvalidOperationException("THOR_AWS_REGION or AWS_REGION is required."),
    });
}
