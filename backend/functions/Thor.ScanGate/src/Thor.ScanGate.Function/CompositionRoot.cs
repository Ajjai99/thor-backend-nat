using Amazon.S3;
using Amazon.StepFunctions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thor.DataConnectionManager;
using Thor.DataConnectionManager.Caching;
using Thor.DataConnectionManager.Routing;
using Thor.DataConnectionManager.Validation;
using Thor.DataLayer.Auth;
using Thor.DataLayer.Data;
using Thor.ScanGate.Core;

namespace Thor.ScanGate.Function;

/// <summary>Wires Core services for the Lambda runtime.</summary>
internal static class CompositionRoot
{
    public static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddLambdaLogger());

        // Both the Master and tenant legs authenticate with an RDS IAM token through the proxy
        // (ADR §6.2/§6.3) — there is no password auth path.
        var tokenProvider = new RdsIamTokenProvider();

        var masterConnectionInfo = new MasterConnectionInfo(
            Host: RequireEnv("THOR_MASTERDB_HOST"),
            Database: RequireEnv("THOR_MASTERDB_DATABASE"),
            Username: RequireEnv("THOR_MASTERDB_USER"),
            Region: RequireEnv("THOR_MASTERDB_REGION"),
            Port: int.TryParse(Environment.GetEnvironmentVariable("THOR_MASTERDB_PORT"), out var masterPort) ? masterPort : 5432,
            UseSsl: bool.TryParse(Environment.GetEnvironmentVariable("THOR_MASTERDB_USESSL"), out var masterSsl) ? masterSsl : true);

        var masterDbContextFactory = new MasterDbContextFactory(tokenProvider);
        var routingResolver = new TenantRoutingResolver(masterDbContextFactory, masterConnectionInfo);
        var tenantConnectionManager = new TenantConnectionManager(
            routingResolver, tokenProvider, new TenantConnectionValidator(), new TenantConnectionCache(), new TenantDbContextFactory());

        services.AddSingleton<ITenantConnectionManager>(tenantConnectionManager);
        services.AddSingleton<IScanGateStore, TenantScanGateStore>();

        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
        services.AddSingleton(new UploadsBucketOptions(RequireEnv("THOR_UPLOADS_BUCKET")));
        services.AddSingleton<IUploadObjectProbe, S3UploadObjectProbe>();

        services.AddSingleton<IAmazonStepFunctions>(_ => new AmazonStepFunctionsClient());
        // Empty where the environment does not run reconcile: the gate goes straight to the graph load.
        var reconcileStateMachineArn = Environment.GetEnvironmentVariable("THOR_RECONCILE_STATE_MACHINE_ARN");
        if (string.IsNullOrEmpty(reconcileStateMachineArn))
        {
            services.AddSingleton<IReconcileStarter, DisabledReconcileStarter>();
        }
        else
        {
            services.AddSingleton(new ReconcileStateMachineOptions(reconcileStateMachineArn));
            services.AddSingleton<IReconcileStarter, StepFunctionsReconcileStarter>();
        }

        services.AddSingleton(new GraphLoadStateMachineOptions(RequireEnv("THOR_GRAPH_LOAD_STATE_MACHINE_ARN")));
        services.AddSingleton<IGraphLoadStarter, StepFunctionsGraphLoadStarter>();

        // Empty where the environment does not run PAI: the gate still starts graph loads there.
        var paiStateMachineArn = Environment.GetEnvironmentVariable("THOR_PAI_STATE_MACHINE_ARN");
        if (string.IsNullOrEmpty(paiStateMachineArn))
        {
            services.AddSingleton<IPaiStarter, DisabledPaiStarter>();
        }
        else
        {
            services.AddSingleton(new PaiStateMachineOptions(paiStateMachineArn));
            services.AddSingleton<IPaiStarter, StepFunctionsPaiStarter>();
        }

        services.AddSingleton(new ScanGateOptions(UploadGrace: TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("THOR_SCAN_GATE_UPLOAD_GRACE_SECONDS"), out var graceSeconds) ? graceSeconds : 900)));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ScanGateEvaluator>();
        services.AddSingleton<ScanGateBatchProcessor>();

        return services.BuildServiceProvider();
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
}
