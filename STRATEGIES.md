# Estrategias adicionales en 0.3.0

## Reintentos por resultados

```csharp
var result = await Retry.ExecuteAsync(
    ct => ConsultarEstadoAsync(ct),
    new RetryOptions<int>
    {
        MaxRetries = 3,
        ShouldRetryResult = estado => estado == -1,
        ShouldRetryException = ex => ex is HttpRequestException,
        Telemetry = telemetry
    }, cancellationToken);
```

RetryOptions<T> permite seleccionar resultados de cualquier tipo y, opcionalmente,
excepciones. ShouldRetryResult es obligatorio. Mantiene el backoff, jitter y la
cancelación del Retry original. MaxRetries cuenta intentos adicionales.

Si se agota el presupuesto, se **devuelve el último resultado**, aunque sea
rechazable. Una excepción final se propaga. El filtro de resultado no se evalúa
cuando no quedan reintentos. OnRetry recibe ResultRetryEvent<T> con número,
resultado/error y retraso; su excepción detiene la ejecución.

## Fallback

```csharp
var value = await Fallback.ExecuteAsync(
    ct => ConsultarServicioAsync(ct),
    (outcome, ct) => LeerCacheAsync(ct),
    new FallbackOptions<string>
    {
        ShouldHandleException = ex => ex is HttpRequestException,
        ShouldHandleResult = texto => string.IsNullOrEmpty(texto),
        Telemetry = telemetry
    }, cancellationToken);
```

Selecciona al menos un filtro. Sólo activa la alternativa ante un error o resultado
seleccionado. No captura cancelaciones ni aplica fallback recursivo a los errores de
la propia alternativa. El callback recibe ResilienceOutcome<T>: HasResult distingue
un resultado, incluso null, de una excepción.

Un callback que ignore el token y termine con éxito devuelve ese resultado, igual
que el Retry original. Para imponer un plazo, compón con Timeout cooperativo.

## Propiedad de los resultados

En Retry por resultado, fallback y hedging, cada operación debe devolver recursos
**nuevos y propios**, evitando objetos IDisposable compartidos entre intentos.
Los resultados descartados se liberan automáticamente: primero IAsyncDisposable,
o IDisposable si no implementan la primera interfaz.

OnDiscardResult permite sustituir esa liberación con un Func<T, ValueTask> para
recursos con otra gestión de propiedad. Si lo configuras, debes liberar el recurso
o decidir explícitamente cómo gestionarlo; no se ejecuta después el Dispose automático.

La devolución final pertenece al consumidor y no se libera automáticamente.
Fallback deja vivo el resultado rechazado mientras la alternativa lo inspecciona;
después lo libera, salvo que la alternativa devuelva esa misma instancia por referencia.
Las excepciones de los filtros de resultado y de limpieza se propagan.
Los filtros de excepciones deben ser puros: si lanzan en un catch filter, se preserva
la excepción original de la operación.

## Rate limiting

```csharp
using System.Threading.RateLimiting;

using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
{
    TokenLimit = 10,
    TokensPerPeriod = 10,
    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
    AutoReplenishment = true,
    QueueLimit = 20,
    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
});
var strategy = new RateLimit(limiter, telemetry);
var result = await strategy.ExecuteAsync(ct => ConsultarServicioAsync(ct), cancellationToken);
```

Reutiliza el limitador para llamadas que comparten presupuesto. La aplicación
administra su vida y lo libera al terminar; el wrapper no lo dispone.
La estrategia adquiere un permiso antes de ejecutar y libera el lease al finalizar,
también ante errores. Una cola llena provoca RateLimitRejectedException sin invocar
la operación. RetryAfter expone el retraso si el limitador lo proporciona.

Puedes usar los limitadores de System.Threading.RateLimiting: token bucket,
ventana fija, ventana deslizante o ConcurrencyLimiter. Un límite de concurrencia
limita operaciones activas; los demás regulan el presupuesto por tiempo.
Las esperas de cola respetan cancelación. No hay particionado automático por usuario
ni coordinación entre procesos: crea y comparte el limitador según tu aplicación.
Consulta la [API oficial de .NET](https://learn.microsoft.com/en-us/dotnet/api/system.threading.ratelimiting).

## Hedging

```csharp
var value = await Hedging.ExecuteAsync(
    (attempt, ct) => attempt == 0
        ? LeerReplicaPrincipalAsync(ct)
        : LeerReplicaAlternativaAsync(ct),
    new HedgingOptions<string>
    {
        MaxHedgedAttempts = 1,
        Delay = TimeSpan.FromMilliseconds(200),
        ShouldAcceptResult = texto => !string.IsNullOrEmpty(texto),
        Telemetry = telemetry
    }, cancellationToken);
```

El intento cero comienza inmediatamente; los adicionales se programan a Delay,
2*Delay, etc., desde el inicio. MaxHedgedAttempts admite de cero a 32 adicionales.
Delay cero permite lanzarlos inmediatamente. Si varias tareas ya completaron cuando
se selecciona, no se garantiza cuál de los resultados aceptables gana.

El primer resultado aceptado cancela los demás intentos. La estrategia espera su
finalización, observa sus excepciones y libera sus resultados descartados. Las
alternativas todavía esperando no ejecutan la operación después de cancelarse.
La cancelación del consumidor prevalece y también libera un ganador ya seleccionado.
Si ningún intento produce un resultado aceptado, se lanza HedgingRejectedException
con AggregateException interna cuando hubo errores. Un error de una alternativa
no vence a un resultado válido de otra.

Las operaciones deben ser asíncronas, no bloquear antes de devolver su Task y
cooperar con el token. Un perdedor que ignore cancelación puede retrasar indefinidamente
la devolución del ganador. Los errores de limpieza o de callbacks de cancelación
se informan mediante AggregateException y se libera también el ganador.

Usa hedging para lecturas o efectos idempotentes: puede duplicar solicitudes,
carga y costes. No revierte cambios que un intento cancelado ya haya producido.

## Composición

Fallback por fuera puede proporcionar una alternativa después de agotar Retry.
Para que **cada** intento consuma presupuesto, coloca RateLimit dentro del delegado
de Retry o Hedging. Si lo colocas por fuera, sólo limita la operación lógica completa.
Retry multiplicado por hedging puede multiplicar los envíos: calcula ese presupuesto.
Todas estas estrategias aceptan Telemetry y mantienen la biblioteca independiente de IA.
