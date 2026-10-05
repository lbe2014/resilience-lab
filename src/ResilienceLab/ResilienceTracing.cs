using System.Diagnostics;

namespace ResilienceLab;

internal static class ResilienceTracing
{
    private static readonly ActivitySource Source = new(ResilienceTelemetry.ActivitySourceName, "0.5.0");

    internal static Task<T> ExecuteAsync<T>(ResilienceTelemetry? telemetry, string strategy,
        Func<Task<T>> operation, int? attempt = null) => telemetry is null
        ? operation() : TraceAsync(telemetry, strategy, operation, attempt);

    private static async Task<T> TraceAsync<T>(ResilienceTelemetry telemetry, string strategy,
        Func<Task<T>> operation, int? attempt)
    {
        var parent = Activity.Current;
        Activity? activity = null;
        try
        {
            var tags = new ActivityTagsCollection
            {
                { "dependency.name", telemetry.DependencyName }, { "resilience.strategy", strategy }
            };
            if (attempt is not null) tags.Add("resilience.attempt", attempt.Value);
            activity = Source.StartActivity("resilience." + strategy, ActivityKind.Internal, default(ActivityContext), tags);
        }
        catch (Exception)
        {
            var interrupted = Activity.Current;
            if (interrupted != parent && interrupted?.Source == Source)
                ResilienceTelemetry.Observe(() => interrupted.Dispose());
            Activity.Current = parent;
        }
        try
        {
            var result = await operation().ConfigureAwait(false);
            ResilienceTelemetry.Observe(() => activity?.SetTag("resilience.outcome", "success"));
            return result;
        }
        catch (OperationCanceledException)
        {
            ResilienceTelemetry.Observe(() => activity?.SetTag("resilience.outcome", "canceled"));
            throw;
        }
        catch (Exception error)
        {
            ResilienceTelemetry.Observe(() =>
            {
                activity?.SetTag("resilience.outcome", "error");
                activity?.SetTag("error.type", error.GetType().FullName);
                activity?.SetStatus(ActivityStatusCode.Error);
            });
            throw;
        }
        finally
        {
            ResilienceTelemetry.Observe(() => activity?.Dispose());
            Activity.Current = parent;
        }
    }

    internal static void Event(ResilienceTelemetry telemetry, string name, params (string Key, object? Value)[] tags)
    {
        ResilienceTelemetry.Observe(() =>
        {
            var current = Activity.Current;
            if (current?.Source != Source || !Equals(current.GetTagItem("dependency.name"), telemetry.DependencyName)) return;
            var values = new ActivityTagsCollection();
            foreach (var tag in tags) if (tag.Value is not null) values.Add(tag.Key, tag.Value);
            current.AddEvent(new ActivityEvent(name, tags: values));
        });
    }

    internal static void HttpResponse(ResilienceTelemetry? telemetry, int statusCode)
    {
        if (telemetry is null) return;
        Event(telemetry, "http.response", ("http.response.status_code", statusCode));
        ResilienceTelemetry.Observe(() =>
        {
            var current = Activity.Current;
            if (current?.Source != Source || !Equals(current.GetTagItem("dependency.name"), telemetry.DependencyName)) return;
            current.SetTag("http.response.status_code", statusCode);
            if (statusCode >= 400) current.SetStatus(ActivityStatusCode.Error);
        });
    }
}
