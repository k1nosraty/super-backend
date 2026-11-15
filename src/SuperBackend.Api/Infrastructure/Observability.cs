using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SuperBackend.Api;

internal static class Observability
{
    public static void AddObservability(this WebApplicationBuilder builder)
    {
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        }

        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName;
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(traces => traces.AddAspNetCoreInstrumentation().AddProcessor(new RemoveQueryTag()).AddOtlpExporter())
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMeter("System.Runtime").AddOtlpExporter());
    }

    private sealed class RemoveQueryTag : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => activity.SetTag("url.query", null);
    }
}
