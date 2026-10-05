# Trazas en ResilienceLab 0.5.0

La DLL usa ActivitySource `ResilienceLab`, accesible mediante
`ResilienceTelemetry.ActivitySourceName`. Es opt-in: configura Telemetry y un
ActivityListener o un collector OpenTelemetry que escuche esa fuente.
No se instala un exportador ni se envían datos desde la biblioteca.

## Activar una composición

```csharp
var pipeline = new ResiliencePipelineBuilder<string>()
    .WithTelemetry(new ResilienceTelemetry("catalogo-api", logger))
    .AddRetry(new RetryOptions
    {
        MaxRetries = 3,
        ShouldRetry = ex => ex is HttpRequestException
    })
    .AddTimeout(TimeSpan.FromSeconds(2))
    .Build();

var resultado = await pipeline.ExecuteAsync(ct => ConsultarApiAsync(ct), cancellationToken);
```

WithTelemetry activa el span del pipeline y provee Telemetry por defecto a sus
estrategias, incluidos logs y métricas. Una opción explícita de estrategia prevalece.
Build captura esa configuración. En las APIs independientes usa las propiedades
Telemetry existentes; en HTTP configura HttpRetryOptions.Telemetry.

## Ver las trazas localmente

La demo incluye un ActivityListener y muestra nombre, TraceId, SpanId, ParentSpanId,
estado, duración y nombres de eventos, sin requerir OpenTelemetry:

```powershell
dotnet run --project samples/Demo -c Release
```

La API de fallos puede mostrar trazas con:

```powershell
dotnet run --project samples/FaultApi -c Release -- --Tracing:Console=true
```

Los spans heredan Activity.Current de ASP.NET Core o del consumidor. Los intentos
comparten el TraceId y tienen SpanId propio; cada alternativa de hedging es un
hermano bajo el span hedging. Los spans finalizan después de esperar y limpiar
los perdedores. Una traza con pipeline, retry y timeout tiene esta estructura:

```text
request de ASP.NET Core
└─ resilience.pipeline
   └─ resilience.retry
      ├─ resilience.retry.attempt (0)
      │  └─ resilience.timeout
      ├─ evento retry.scheduled (número y espera programada)
      └─ resilience.retry.attempt (1)
         └─ resilience.timeout
```

## Exportar con OpenTelemetry

La aplicación puede instalar OpenTelemetry y OpenTelemetry.Exporter.Console y usar:

```csharp
using OpenTelemetry;
using OpenTelemetry.Trace;
using ResilienceLab;

using var provider = Sdk.CreateTracerProviderBuilder()
    .AddSource(ResilienceTelemetry.ActivitySourceName)
    .AddConsoleExporter()
    .Build();
```

Mantén el provider vivo mientras se ejecuta la aplicación. Para conectar request HTTP
de entrada y salida, configura además la instrumentación ASP.NET Core y HttpClient
en tu aplicación. La DLL sólo genera sus spans internos de resiliencia; el exporter
y sus destinos pertenecen al consumidor. Esta entrega verifica ActivityListener
local, no un collector OTLP externo.

El patrón sigue las guías de Microsoft de
[instrumentación](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs)
y [recolección con OpenTelemetry](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-collection-walkthroughs).

## Datos y estados

- Nombres estables: resilience.pipeline, retry, result_retry, timeout, circuit_breaker,
  fallback, fallback.execute, rate_limit, hedging, http_retry; cada nombre lleva el
  prefijo resilience. Retry, result_retry, hedging y http_retry incluyen spans .attempt.
- Tags: dependency.name, resilience.strategy, resilience.attempt (desde cero),
  resilience.outcome (success/error/canceled), error.type, http.response.status_code.
- Eventos: retry.scheduled, timeout.rejected, circuit.changed, circuit.rejected,
  fallback.activated, hedging.started, hedging.won, rate_limit.acquisition y http.response.
- Retry registra la espera **programada** en segundos; el span retry mide la ejecución
  completa, incluyendo esperas reales. Rate limiting registra su espera real.
- Una excepción que sale del span marca Error y sólo registra su tipo. Cancelación
  deja status Unset y outcome canceled. Éxito deja status Unset y outcome success.
- Los spans externos pueden terminar con éxito aunque un intento interno haya fallado
  y retry/fallback lo haya recuperado. En HTTP, una respuesta >=400 marca Error
  en su intento y, si es la final, en http_retry. Outcome success indica devolución
  normal de la Task, incluso cuando el resultado es una respuesta HTTP fallida.
- Predicados de resultados genéricos no serializan ni etiquetan el resultado: consulta
  los eventos de retry o fallback para identificar la decisión de resiliencia.
- No se capturan URL, cabeceras, cuerpo, resultados, mensajes, stack traces ni objetos
  de excepción. Otra instrumentación HTTP instalada por tu aplicación tiene su propia
  política de datos. Usa nombres de dependencia constantes, nunca identificadores de usuario.
- Sin listener o con sampling None no se crean spans. Los listeners con errores en
  sampling/start/stop se aíslan para preservar resultados, errores y Activity.Current.
- Los cambios del circuito observados fuera de una ejecución (por ejemplo State)
  siguen emitiendo logs/métricas, pero no crean una traza aislada automáticamente.
