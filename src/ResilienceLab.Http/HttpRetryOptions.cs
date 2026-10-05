namespace ResilienceLab.Http;

/// <summary>Configuración inmutable de reintentos HTTP.</summary>
public sealed record HttpRetryOptions
{
    /// <summary>Intentos adicionales a la primera solicitud.</summary>
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);
    public bool UseJitter { get; init; } = true;
    public ResilienceTelemetry? Telemetry { get; init; }

    /// <summary>
    /// Permite reintentar métodos distintos de GET y HEAD. El consumidor debe
    /// garantizar la idempotencia y recrear también el contenido en cada intento.
    /// </summary>
    public bool AllowRetryForIdempotentOperations { get; init; }
}
