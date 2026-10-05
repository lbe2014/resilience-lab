# Observabilidad en ResilienceLab 0.5.0

## Activar logs y métricas

También incluye [trazas con ActivitySource y guía de OpenTelemetry](TRACING.md).
WithTelemetry en el builder proporciona la configuración común a todas las estrategias.

Crea una instancia por dependencia y compártela entre sus estrategias:

```csharp
using Microsoft.Extensions.Logging;
using ResilienceLab;

var telemetry = new ResilienceTelemetry("inventario-api", logger);
var options = new RetryOptions
{
    ShouldRetry = ex => ex is HttpRequestException,
    Telemetry = telemetry
};
var result = await Retry.ExecuteAsync(ct => CallServiceAsync(ct), options, cancellationToken);
```

`logger` es un ILogger de tu aplicación y `CallServiceAsync` es tu operación.
En ASP.NET Core puedes obtener el logger mediante inyección de dependencias.
El núcleo depende sólo de Microsoft.Extensions.Logging.Abstractions; el proveedor
de consola, archivos o nube lo configura la aplicación. Puedes omitir el logger
para emitir únicamente métricas. Si omites Telemetry en las opciones, la estrategia
no emite estos logs ni métricas.

En HTTP usa `HttpRetryOptions.Telemetry`. En el circuito usa
`CircuitBreakerOptions.Telemetry`. Para timeout conserva el overload anterior
o usa las nuevas opciones:

```csharp
var result = await ResilienceLab.Timeout.ExecuteAsync(
    ct => CallServiceAsync(ct),
    new TimeoutOptions
    {
        TimeoutDuration = TimeSpan.FromSeconds(2),
        Telemetry = telemetry
    }, cancellationToken);
```

## Métricas disponibles

Meter: **ResilienceLab**, versión **0.5.0**. Usa System.Diagnostics.Metrics.

| Instrumento | Tipo | Qué mide |
|---|---|---|
| resilience.retry.count | Counter de long | Reintentos programados, en Retry y HTTP. |
| resilience.retry.delay | Histogram de double, segundos | Espera elegida para cada reintento. |
| resilience.timeout.count | Counter de long | Operaciones rechazadas por timeout después de terminar. |
| resilience.circuit.transition.count | Counter de long | Cambios de estado confirmados. |
| resilience.circuit.rejection.count | Counter de long | Llamadas rechazadas sin ejecutar la operación. |
| resilience.fallback.count | Counter de long | Alternativas activadas, aunque la alternativa falle después. |
| resilience.hedging.attempt.count | Counter de long | Intentos realmente iniciados, incluido el inicial. |
| resilience.hedging.win.count | Counter de long | Resultados aceptados devueltos después de limpiar perdedores. |
| resilience.rate_limit.acquisition.count | Counter de long | Adquisiciones completadas, exitosas o rechazadas. |
| resilience.rate_limit.wait | Histogram de double, segundos | Tiempo real dedicado a adquirir un permiso. |

Las etiquetas comunes son `dependency.name` y `resilience.strategy`.
Los valores de estrategia son `retry`, `result_retry`, `http_retry`, `timeout`,
`circuit_breaker`, `fallback`, `hedging` y `rate_limit`.
Los reintentos agregan `error.type` o `http.response.status_code` según su causa.
Las transiciones agregan `circuit.from` y `circuit.to`.
El limitador agrega `rate_limit.outcome`: acquired o rejected. La cancelación de
una adquisición no se contabiliza. Los reintentos por resultado no etiquetan ni
registran el contenido del resultado genérico.

El nombre de dependencia debe ser estable, de hasta 128 caracteres; usa por ejemplo
`inventario-api`. No uses URLs, identificadores de usuario o un nombre nuevo por llamada:
cada combinación de etiquetas puede producir una serie independiente.

El contador de reintentos mide programación, no garantiza una ejecución posterior:
una cancelación durante la espera puede impedirla. No cuenta el intento inicial.
El histograma mide la espera configurada, no cuánto tiempo tardó realmente la operación.
Los timeouts se registran al rechazar el resultado después de que la operación termine;
una operación que ignore cancelación y nunca termine no emite ese rechazo.

Los instrumentos no guardan históricos ni transmiten datos por sí solos.
Conecta un colector antes de ejecutar las operaciones. En una aplicación que ya
usa OpenTelemetry, agrega `AddMeter(ResilienceTelemetry.MeterName)` al builder de
métricas y configura el exporter del destino que uses.
Para observar un proceso en ejecución con dotnet-counters instalado:

```powershell
dotnet-counters monitor --process-id <PID> --counters ResilienceLab
```

La [guía de recolección de Microsoft](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-collection)
explica cómo conectar estas métricas a OpenTelemetry, Prometheus y Grafana.
Esta biblioteca emite las señales; la aplicación administra el colector y el almacenamiento.

## Eventos del circuito

```csharp
var circuit = new CircuitBreaker(new CircuitBreakerOptions
{
    ShouldHandle = ex => ex is HttpRequestException,
    Telemetry = telemetry,
    OnStateChanged = e => Console.WriteLine(
        $"{e.Sequence}: {e.PreviousState} -> {e.CurrentState} ({e.Timestamp:O})")
});
```

Cada transición se captura dentro del bloqueo y se notifica **fuera** de él.
El evento incluye secuencia, estado anterior, nuevo estado y fecha UTC.
No hay evento por consultas de State que no produzcan transición ni por completados
antiguos que el circuito ignore.

En concurrencia, los observadores pueden ejecutarse simultáneamente y recibir eventos
fuera de orden. Sequence permite reconstruir el orden por instancia; el estado del
evento es una instantánea confirmada y puede ser distinto del estado actual al recibirlo.
Los callbacks deben ser rápidos, seguros bajo concurrencia y dedicados a observación.

Las excepciones del logger, de los listeners de métricas y de OnStateChanged se aíslan
para conservar el resultado de la operación. El contrato de **Retry.OnRetry** sigue
siendo distinto: una excepción de ese callback aborta y se propaga.

## Logs estructurados

| EventId | Nombre | Nivel |
|---:|---|---|
| 1001 | RetryScheduled | Warning |
| 1002 | TimeoutRejected | Warning |
| 1003 | CircuitStateChanged | Information |
| 1004 | CircuitRejected | Warning |
| 1005 | FallbackActivated | Warning |
| 1006 | HedgeAttemptStarted | Information |
| 1007 | HedgeWon | Information |
| 1008 | RateLimitAcquisition | Debug al adquirir; Warning al rechazar |

Se registran nombre de dependencia, estrategia, número/retraso del reintento,
tipo de excepción, estado HTTP o transición, según corresponda.
La biblioteca no pasa la excepción al logger ni registra sus mensajes, stack traces,
URLs, cabeceras o cuerpos. El nombre de dependencia y los callbacks del consumidor
son responsabilidad de la aplicación.

## Verlo ahora

```powershell
dotnet run --project samples/Demo -c Release
```

La demo imprime logs de consola, eventos del circuito y mediciones mediante
MeterListener. Funciona con HTTP simulado y no necesita un servidor de observabilidad.
