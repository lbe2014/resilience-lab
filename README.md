# ResilienceLab 0.5.0

Nombre del proyecto: **ResilienceLab**. Licencia: [MIT](LICENSE).
Paquetes: `ResilienceLab` y `ResilienceLab.Http`.
Estado: **experimental**; valida su comportamiento en tu aplicación antes de adoptarla.
Paquetes 0.5.0 enviados a NuGet.org mediante Trusted Publishing:
[ResilienceLab](https://www.nuget.org/packages/ResilienceLab/0.5.0) y
[ResilienceLab.Http](https://www.nuget.org/packages/ResilienceLab.Http/0.5.0).
Consulta [preparación y publicación con OIDC](PUBLISHING.md).

Repositorio: [lbe2014/resilience-lab](https://github.com/lbe2014/resilience-lab).
Consulta [las ejecuciones de GitHub Actions](https://github.com/lbe2014/resilience-lab/actions)
para descargar resultados e informes y paquetes generados por CI.

Biblioteca educativa de resiliencia para .NET 10: Retry por errores y resultados,
fallback, rate limiting, hedging, timeout cooperativo, circuit breaker y HTTP.
Implementación propia; su API no es compatible
con Polly. Funciona sin IA, claves ni servicios externos.

Incluye observabilidad opcional con ILogger, métricas, eventos del circuito y ActivitySource.
Consulta [trazas, jerarquía de spans y OpenTelemetry](TRACING.md).
Consulta [activación, métricas y ejemplos](OBSERVABILITY.md).
Consulta [las estrategias adicionales y su composición](STRATEGIES.md).
Consulta [pipelines reutilizables, orden y registro por nombre en DI](PIPELINES.md).
Consulta [CI y carga manual con GitHub Actions](CI.md).
Incluye una [API consumidora independiente](examples/PackageConsumer/README.md)
que instala los .nupkg locales y verifica DI, pipeline y trazas sin ProjectReference.

## Ejecutar y empaquetar

Desde esta carpeta, con el SDK .NET 10 (global.json fija 10.0.401 y acepta parches):

~~~powershell
dotnet test -c Release
dotnet run --project samples/Demo -c Release
dotnet pack src/ResilienceLab -c Release -o artifacts
dotnet pack src/ResilienceLab.Http -c Release -o artifacts
~~~

La demo ejecuta doce escenarios, incluidas las estrategias adicionales, pipelines y el
cliente tipado registrado con IHttpClientFactory; usa HTTP simulado.
Los paquetes y sus DLL se generan localmente; nada se publica.

También incluye una [API local con fallos simulados](samples/FaultApi/README.md):
Kestrel, IHttpClientFactory y escenarios HTTP de retry, timeout, fallback y circuito.
Ejecuta `dotnet run --project samples/FaultApi -c Release` y abre
http://127.0.0.1:5099/. Su script `verify.ps1` comprueba seis llamadas reales a localhost.

Incluye [carga local reproducible](samples/LoadCheck/README.md) con ocho fases,
fallos sostenidos, recuperación, límites de concurrencia y limpieza de hedging.
Ejecuta `pwsh -File samples/LoadCheck/run.ps1`; genera informes en artifacts/load-check.

## Retry y eventos

~~~csharp
using ResilienceLab;

var resultado = await Retry.ExecuteAsync(
    ct => CallServiceAsync(ct),
    new RetryOptions
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.FromMilliseconds(200),
        MaxDelay = TimeSpan.FromSeconds(10),
        ShouldRetry = ex => ex is HttpRequestException,
        OnRetry = e => Console.WriteLine(
            $"Reintento {e.RetryNumber}: {e.Delay.TotalMilliseconds} ms")
    }, cancellationToken);
~~~

`CallServiceAsync` representa tu operación; la demo contiene ejemplos ejecutables.

- Tres reintentos permiten cuatro ejecuciones. Cada llamada tiene su propio contador.
- El límite del retraso es `min(MaxDelay, BaseDelay * 2^índice)`, con índice inicial cero.
  Full jitter, activado por defecto, elige un retraso entre cero y ese límite.
- `ShouldRetry` es obligatorio. Una `OperationCanceledException` nunca se reintenta.
- `OnRetry` recibe número desde uno, excepción y retraso, antes de la espera.
  Si lanza, ese error se propaga y no se vuelve a ejecutar la operación.
- No hay eventos de éxito, cancelación o agotamiento. El evento indica que se
  programó un reintento: una cancelación posterior puede impedir su ejecución.
- El filtro debe ser puro y no lanzar. Si `ShouldRetry` lanza dentro del filtro
  catch, se conserva la excepción original. El callback de eventos tiene otra semántica.
- Sin Telemetry no se registran datos automáticamente. Evita imprimir mensajes de excepción
  que contengan información sensible.
- Si el delegado ignora la cancelación y termina con éxito, Retry devuelve ese resultado.

## Timeout cooperativo

~~~csharp
using ResilienceTimeout = ResilienceLab.Timeout;

var resultado = await ResilienceTimeout.ExecuteAsync(
    ct => CallServiceAsync(ct),
    TimeSpan.FromSeconds(2),
    cancellationToken);
~~~

Se solicita cancelación al vencer el plazo y se espera a que termine la operación.
Si venció el plazo se lanza `TimeoutRejectedException`, incluso si la operación
terminó con éxito después. Si se canceló el consumidor, prevalece su cancelación.
Si la operación falló después del plazo, la excepción original queda como InnerException.

Una operación que ignore el token puede tardar indefinidamente. No se abandonan
tareas ni se detiene por la fuerza el código del usuario. Para reintentar timeouts,
selecciona explícitamente `TimeoutRejectedException` en el filtro.

## Circuit breaker

~~~csharp
var circuit = new CircuitBreaker(new CircuitBreakerOptions
{
    FailureThreshold = 5,
    BreakDuration = TimeSpan.FromSeconds(30),
    ShouldHandle = ex => ex is HttpRequestException or TimeoutRejectedException
});

// Reutiliza esta instancia para llamadas a la misma dependencia.
var resultado = await circuit.ExecuteAsync(ct => CallServiceAsync(ct), cancellationToken);
~~~

| Estado | Comportamiento |
|---|---|
| Closed | Admite llamadas; los fallos seleccionados incrementan el contador. |
| Open | Rechaza sin ejecutar la operación mediante BrokenCircuitException. |
| HalfOpen | Admite una sola prueba; éxito cierra y fallo seleccionado abre otra vez. |

Un éxito reinicia el contador. Cancelaciones y errores no seleccionados no lo
incrementan ni reinician; en HalfOpen liberan el turno de prueba sin cerrar.
Un filtro defectuoso conserva el error original y tampoco cuenta como fallo.
El filtro debe ser puro: se ejecuta fuera del bloqueo, igual que la operación.

La propiedad State y la admisión de nuevas llamadas actualizan Open a HalfOpen al
vencer BreakDuration; no hay un temporizador de fondo. Se usa tiempo monotónico.
Los resultados de llamadas admitidas antes de una transición no alteran el nuevo
estado. En concurrencia, los resultados se contabilizan en orden de finalización.
Una operación que termina con éxito prevalece sobre un token que ella decidió ignorar.

## HTTP

~~~csharp
using ResilienceLab.Http;

var resilient = new ResilientHttpClient(httpClient);
using var response = await resilient.SendAsync(
    () => new HttpRequestMessage(HttpMethod.Get, "https://example.com/api"),
    cancellationToken);

var body = await response.Content.ReadAsStringAsync(cancellationToken);
~~~

Al construir el adaptador manualmente, el consumidor administra la vida del
HttpClient. La respuesta final siempre pertenece al consumidor.
La fábrica debe construir solicitud **y contenido nuevos** en cada intento.
El adaptador libera todas las solicitudes y las respuestas que descarta.

Se reintentan 408, 429, 502, 503, 504 y HttpRequestException de transporte,
con tres reintentos máximos por defecto. La respuesta final fallida se devuelve
sin llamar a EnsureSuccessStatusCode. GET y HEAD permiten reintentos por defecto;
otros métodos requieren `AllowRetryForIdempotentOperations = true`.

Retry-After admite segundos o fecha UTC: se elige el mayor entre la cabecera y el
backoff, limitado por MaxDelay. Una cabecera inválida se ignora.
**Este límite puede esperar menos que lo solicitado por el servidor**; configura
MaxDelay de acuerdo con el contrato del servicio.

Se usa ResponseHeadersRead: la estrategia cubre el envío y las cabeceras, no la
lectura posterior del cuerpo. La cancelación del HttpClient, incluido su timeout,
no se reintenta. La solicitud referenciada por response.RequestMessage ya se liberó.

Reintentar escrituras sin idempotencia puede duplicar efectos. Activar la opción
no crea claves de idempotencia ni convierte automáticamente una escritura en segura.

## IHttpClientFactory

```csharp
using Microsoft.Extensions.DependencyInjection;
using ResilienceLab.Http;

builder.Services.AddResilientHttpClient(
    client => client.BaseAddress = new Uri("https://example.com/"),
    provider => new HttpRetryOptions { MaxRetries = 3 });
```

Puedes inyectar ResilientHttpClient en tu servicio. Se registra como cliente tipado
transitorio, respaldado por IHttpClientFactory. La extensión devuelve IHttpClientBuilder
para configurar handlers y su vida. El callback configureRetry puede resolver servicios
desde DI, incluidos loggers para Telemetry. La demo contiene un registro ejecutable.

El cliente tipado implementa IDisposable y libera su HttpClient al desecharse;
la fábrica administra el pool de handlers. Un adaptador manual no dispone del
HttpClient que recibió, aunque se llame a Dispose. Usa los clientes tipados en
servicios transitorios o scoped; evita capturarlos en un singleton.
El registro configura un único cliente tipado. Para varios backends, registra
clientes con nombre y crea adaptadores con los HttpClient obtenidos de la fábrica,
administrando esas instancias en el consumidor.
Consulta [la documentación de Microsoft](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory).

## Composición

El orden de la demo es `circuit breaker → retry → timeout por intento`.
Cada fallo definitivo, después de agotar los reintentos, cuenta una vez en el circuito.
El timeout interno limita cada intento; para limitar también las esperas, coloca
otro timeout cooperativo por fuera de Retry.

El circuit breaker genérico cuenta excepciones, no códigos HTTP. Si necesitas que
una respuesta final 503 cuente como fallo, libera esa respuesta y lanza una excepción
seleccionada dentro de la operación protegida. Evita envolver ResilientHttpClient
con otro Retry sin calcular el total de intentos, porque los reintentos se multiplican.

Al combinar timeout con HTTP, lee y libera la respuesta **dentro** del delegado y
devuelve texto o un DTO. Timeout descarta resultados tardíos y no puede asumir la
propiedad de recursos genéricos devueltos por el usuario:

~~~csharp
var body = await ResilienceLab.Timeout.ExecuteAsync(async ct =>
{
    using var response = await resilient.SendAsync(
        () => new HttpRequestMessage(HttpMethod.Get, "https://example.com/api"), ct);
    return await response.Content.ReadAsStringAsync(ct);
}, TimeSpan.FromSeconds(5), cancellationToken);
~~~

## Pruebas y siguientes pasos

Las pruebas usan tiempo simulado, aleatoriedad controlada y señales explícitas.
Cubren límites, eventos, cancelación, estados, carreras, composición y recursos HTTP.
Las esperas de cinco segundos en algunas pruebas son límites de detección de bloqueos,
no tiempos esperados de la estrategia.

[TimeProvider y FakeTimeProvider de .NET](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview)
permiten comprobar el paso del tiempo sin esperar los retrasos reales.

Abre la carpeta en Codex u OpenCode y pide que lea AGENTS.md y ROADMAP.md.
El agente propio del blog queda pendiente; puedes usar los ejemplos y pruebas como
material del primer artículo. Esta entrega no garantiza compatibilidad ni madurez
equivalente a Polly; aún requiere validación en tu aplicación real.
