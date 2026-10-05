# Pipelines configurables

Un pipeline reutiliza la composición y el circuito. El orden de registro es
de **afuera hacia adentro**. Cada Build crea un circuito independiente; llamadas
al mismo pipeline comparten ese circuito, pero tienen presupuestos de retry propios.
El builder es para configuración secuencial; no lo modifiques concurrentemente.
Modificarlo después de Build no altera un pipeline ya construido.

```csharp
using ResilienceLab;

var pipeline = new ResiliencePipelineBuilder<string>()
    .AddFallback((_, _) => Task.FromResult("Dato de caché"),
        new FallbackOptions<string>
        {
            ShouldHandleException = ex => ex is HttpRequestException
                or TimeoutRejectedException or BrokenCircuitException
        })
    .AddCircuitBreaker(new CircuitBreakerOptions
    {
        FailureThreshold = 5,
        BreakDuration = TimeSpan.FromSeconds(30),
        ShouldHandle = ex => ex is HttpRequestException or TimeoutRejectedException
    })
    .AddRetry(new RetryOptions
    {
        MaxRetries = 3,
        ShouldRetry = ex => ex is HttpRequestException or TimeoutRejectedException
    })
    .AddTimeout(TimeSpan.FromSeconds(2))
    .Build();

// ConsultarApiAsync representa tu operación; Demo contiene un ejemplo ejecutable.
var resultado = await pipeline.ExecuteAsync(ct => ConsultarApiAsync(ct), cancellationToken);
```

Son hasta cuatro intentos; cada uno tiene dos segundos. El circuito cuenta un fallo
definitivo después del retry. Fallback captura el fallo seleccionado o circuito abierto.
El pipeline adapta las estrategias existentes: conserva cancelación, callbacks,
telemetría opcional y propiedad de resultados. Un pipeline vacío ejecuta directamente.
Las opciones se validan conforme a la estrategia: CircuitBreaker al construir,
las demás al ejecutar. Los argumentos nulos se rechazan al registrar.

## Timeout total o por intento

```csharp
// Timeout por intento.
new ResiliencePipelineBuilder<string>()
    .AddRetry(retryOptions)
    .AddTimeout(TimeSpan.FromSeconds(2))
    .Build();

// Timeout total, incluidas las esperas del retry.
new ResiliencePipelineBuilder<string>()
    .AddTimeout(TimeSpan.FromSeconds(5))
    .AddRetry(retryOptions)
    .Build();
```

El timeout es cooperativo: cancelar no detiene por la fuerza la operación.
Lee y libera respuestas HTTP dentro del delegado y devuelve un DTO o texto;
un resultado tardío descartado por Timeout requiere limpieza dentro de tu operación.

## Registro por nombre en DI

```csharp
using Microsoft.Extensions.DependencyInjection;
using ResilienceLab;

builder.Services.AddResiliencePipeline<string>("catalogo", pipeline => pipeline
    .AddRetry(new RetryOptions
    {
        MaxRetries = 3,
        ShouldRetry = ex => ex is HttpRequestException
    })
    .AddTimeout(TimeSpan.FromSeconds(2)));

// Resolver dentro del servicio consumidor, o inyectar con FromKeyedServices.
var pipeline = serviceProvider.GetRequiredKeyedService<ResiliencePipeline<string>>("catalogo");
```

En ASP.NET Core puedes inyectar:

```csharp
app.MapGet("/catalogo", async (
    [FromKeyedServices("catalogo")] ResiliencePipeline<string> pipeline,
    CatalogService catalog,
    CancellationToken ct) =>
    await pipeline.ExecuteAsync(token => catalog.GetAsync(token), ct));
```

CatalogService es tu servicio de aplicación. La muestra Demo registra y resuelve
un pipeline real. El registro es singleton por nombre y tipo de resultado;
dos nombres distintos son independientes. Repetir nombre y tipo lanza un error.
La configuración se ejecuta una vez al resolver por primera vez, no por solicitud.
La sobrecarga `(provider, pipeline) => ...` permite resolver loggers o limitadores.
Usa servicios singleton en esa configuración y callbacks: evita capturar servicios
scoped o clientes HTTP transitorios en un pipeline singleton. Pasa la operación
del servicio scoped a ExecuteAsync en cada llamada.

## Otras estrategias

También acepta AddRetry(RetryOptions<T>), AddRateLimit(limiter, telemetry) y
AddHedging(HedgingOptions<T>). AddTimeout acepta TimeoutOptions para telemetría.
Cada estrategia recibe Telemetry mediante sus opciones; no hay exportador automático.
WithTelemetry(telemetry) en el builder activa el span del pipeline y aporta Telemetry
por defecto a las estrategias; sus opciones explícitas prevalecen. Consulta TRACING.md.

Coloca RateLimit dentro de Retry o Hedging para limitar cada intento; por fuera
limita la llamada completa. El consumidor administra y libera el limitador.
Compartirlo entre pipelines comparte su presupuesto.

Hedging ejecuta el mismo delegado concurrentemente: úsalo con operaciones idempotentes,
recursos independientes por intento y soporte de cancelación. Cancela y espera a todos
los perdedores antes de devolver el ganador. Para elegir backends por número de intento,
la API Hedging.ExecuteAsync independiente sigue disponible.

ResilientHttpClient ya reintenta: envolverlo además con AddRetry multiplica intentos.
El pipeline no crea automáticamente un handler HTTP ni cambia el registro de la fábrica.
