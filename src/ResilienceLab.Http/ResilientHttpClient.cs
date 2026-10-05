using System.Net;

namespace ResilienceLab.Http;

/// <summary>Reintenta envíos HTTP sin adquirir la propiedad del HttpClient proporcionado.</summary>
public sealed class ResilientHttpClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly HttpRetryOptions _options;
    private readonly ResilienceRuntime _runtime;
    private readonly bool _ownsClient;

    public ResilientHttpClient(HttpClient client, HttpRetryOptions? options = null)
        : this(client, options, ResilienceRuntime.System)
    {
    }

    internal ResilientHttpClient(HttpClient client, HttpRetryOptions? options, ResilienceRuntime runtime, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(runtime);
        options ??= new HttpRetryOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);
        DelayValidation.Validate(options.BaseDelay, nameof(options.BaseDelay));
        DelayValidation.Validate(options.MaxDelay, nameof(options.MaxDelay));
        if (options.MaxDelay < options.BaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxDelay), "MaxDelay debe ser mayor o igual que BaseDelay.");
        }

        _client = client;
        _options = options;
        _runtime = runtime;
        _ownsClient = ownsClient;
    }

    /// <summary>Disposes only clients created by the typed factory registration.</summary>
    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    /// <summary>
    /// La fábrica debe crear una solicitud y contenido nuevos en cada llamada.
    /// Se liberan todas las solicitudes y las respuestas descartadas. El consumidor
    /// debe liberar la respuesta final, cuyo cuerpo se lee fuera de esta estrategia.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken = default) =>
        ResilienceTracing.ExecuteAsync(_options.Telemetry, "http_retry", () => SendCoreAsync(requestFactory, cancellationToken));

    private async Task<HttpResponseMessage> SendCoreAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        var retries = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            Exception? transportError = null;
            int? statusCode = null;
            // Terminar este bloque antes de esperar libera los recursos del intento.
            using (var request = requestFactory()
                ?? throw new InvalidOperationException("La fábrica devolvió una solicitud nula."))
            {
                var canRetry = retries < _options.MaxRetries &&
                    (_options.AllowRetryForIdempotentOperations ||
                     request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);

                HttpResponseMessage? response = null;
                try
                {
                    response = await ResilienceTracing.ExecuteAsync(_options.Telemetry, "http_retry.attempt", async () =>
                    {
                        var result = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                        ResilienceTracing.HttpResponse(_options.Telemetry, (int)result.StatusCode);
                        return result;
                    }, retries).ConfigureAwait(false);
                }
                catch (HttpRequestException error) when (canRetry && !cancellationToken.IsCancellationRequested)
                {
                    // Los errores de transporte seleccionados usan el backoff normal.
                    transportError = error;
                }

                if (response is not null)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!canRetry || !IsRetryable(response.StatusCode))
                        {
                            ResilienceTracing.HttpResponse(_options.Telemetry, (int)response.StatusCode);
                            return response;
                        }

                        delay = GetDelay(retries, response);
                        statusCode = (int)response.StatusCode;
                    }
                    catch
                    {
                        response.Dispose();
                        throw;
                    }

                    response.Dispose();
                }
                else
                {
                    delay = GetDelay(retries, null);
                }
            }

            retries++;
            cancellationToken.ThrowIfCancellationRequested();
            _options.Telemetry?.RetryScheduled("http_retry", retries, delay, transportError, statusCode);
            await _runtime.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan GetDelay(int retryIndex, HttpResponseMessage? response)
    {
        var delay = RetryDelay.Calculate(_options.BaseDelay, _options.MaxDelay,
            retryIndex, _options.UseJitter ? _runtime.NextDouble() : 1);
        var header = response?.Headers.RetryAfter;
        var retryAfter = header?.Delta ?? (header?.Date - _runtime.TimeProvider.GetUtcNow());

        if (retryAfter is { } requested && requested > delay)
        {
            delay = requested > _options.MaxDelay ? _options.MaxDelay : requested;
        }

        return delay;
    }

    private static bool IsRetryable(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;
}
