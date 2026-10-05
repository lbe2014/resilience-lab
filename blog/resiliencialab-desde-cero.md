---
titulo: "ResilienceLab: construí una biblioteca de resiliencia para .NET 10"
slug: resiliencialab-dotnet-resiliencia-observabilidad
descripcion: "Retry, timeout, circuit breaker y trazas en una biblioteca propia para .NET 10: decisiones, ejemplos y límites de ResilienceLab."
fechaPublicacion: 2026-10-05T12:00:00-06:00
fechaActualizacion: 2026-10-05T12:00:00-06:00
destacado: false
autor: Leopoldo Benavente Cadena
etiquetas:
  - .NET
  - CSharp
  - Resiliencia
  - Observabilidad
  - OpenTelemetry
  - NuGet
tituloSeo: "ResilienceLab: resiliencia y observabilidad en .NET 10"
descripcionSeo: "Cómo funciona ResilienceLab 0.5.0: pipelines, retry, timeout, circuit breaker, IHttpClientFactory y trazas. Implementación propia bajo licencia MIT."
tldr: "ResilienceLab 0.5.0 reúne estrategias de resiliencia y observabilidad opcional para .NET 10. Su pipeline se configura de afuera hacia adentro; el orden cambia los presupuestos y cómo cuenta fallos el circuito. Los paquetes ya están en NuGet, con 194 pruebas verificadas y muestras HTTP locales. Es una biblioteca experimental: timeout y hedging necesitan cancelación cooperativa, y la validación local no sustituye pruebas en tu aplicación."
imagenPortada: /media/8c05a0b5d1024e67-960.webp
textoAlternativo: "Flujos de datos atraviesan puertas transparentes mientras una ruta ámbar evita un tramo roto, con gráficas de monitoreo al fondo"
---

Quería programar algo útil y terminé con una pregunta: ¿qué implica construir una biblioteca de resiliencia, además de volver a intentar una llamada que falló?

La respuesta fue [ResilienceLab](https://github.com/lbe2014/resilience-lab), una implementación propia para .NET 10 bajo licencia MIT. La [release experimental 0.5.0](https://github.com/lbe2014/resilience-lab/releases/tag/v0.5.0) ya se puede instalar desde NuGet. Incluye reintentos por excepciones y resultados, timeout, circuit breaker, fallback, rate limiting, hedging y un adaptador HTTP.

Lo desarrollé con ayuda de agentes de programación, revisando contratos y resultados de pruebas.

El proyecto es experimental. No comparte la API de Polly ni pretende demostrar la misma madurez. Me interesaba entender los contratos que debe cumplir cada estrategia: cuándo cancelar, quién libera una respuesta HTTP y qué sucede cuando varias operaciones terminan al mismo tiempo.

## El primer contrato: qué significa un reintento

Un servicio puede fallar de forma transitoria. Reintentar ayuda si seleccionamos los fallos adecuados y ponemos un presupuesto. En ResilienceLab, `MaxRetries` cuenta intentos **adicionales**: un valor de tres permite hasta cuatro ejecuciones.

```powershell
dotnet add package ResilienceLab --version 0.5.0
dotnet add package ResilienceLab.Http --version 0.5.0
```

La primera orden instala el [núcleo de estrategias](https://www.nuget.org/packages/ResilienceLab/0.5.0). La segunda agrega el [adaptador HTTP y su integración con la fábrica](https://www.nuget.org/packages/ResilienceLab.Http/0.5.0).

Este ejemplo usa una operación simulada y se puede colocar en el `Program.cs` de una consola .NET 10 después de instalar el núcleo:

```csharp
using System.Net.Http;
using ResilienceLab;

var intentos = 0;
var resultado = await Retry.ExecuteAsync(
    ct =>
    {
        ct.ThrowIfCancellationRequested();
        intentos++;
        return intentos < 3
            ? Task.FromException<string>(new HttpRequestException())
            : Task.FromResult("Servicio recuperado");
    },
    new RetryOptions
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.FromMilliseconds(100),
        MaxDelay = TimeSpan.FromSeconds(2),
        ShouldRetry = ex => ex is HttpRequestException
    });

Console.WriteLine($"{resultado}; intentos: {intentos}");
```

El resultado llega en el tercer intento. El backoff es exponencial, limitado por `MaxDelay`, con full jitter activado por defecto: el retraso se elige entre cero y el límite de ese intento. La biblioteca nunca reintenta `OperationCanceledException`.

También se pueden seleccionar resultados con `RetryOptions<T>`. Por ejemplo, un estado `-1` puede representar una respuesta temporalmente indisponible aunque no haya excepción. Al agotar el presupuesto, esta variante devuelve el último resultado; la aplicación sigue siendo responsable de interpretarlo.

## El orden del pipeline cambia el comportamiento

La siguiente mejora fue componer estrategias sin repetir delegados anidados. El builder registra las capas de **afuera hacia adentro**.

Este fragmento configura un pipeline de texto. `ConsultarApiAsync` representa el método de la aplicación, que debe aceptar y respetar el token:

```csharp
using System.Net.Http;
using ResilienceLab;

var pipeline = new ResiliencePipelineBuilder<string>()
    .WithTelemetry(new ResilienceTelemetry("catalogo-api"))
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
        ShouldHandle = ex => ex is HttpRequestException
            or TimeoutRejectedException
    })
    .AddRetry(new RetryOptions
    {
        MaxRetries = 3,
        ShouldRetry = ex => ex is HttpRequestException
            or TimeoutRejectedException
    })
    .AddTimeout(TimeSpan.FromSeconds(2))
    .Build();

var resultado = await pipeline.ExecuteAsync(
    ct => ConsultarApiAsync(ct), cancellationToken);
```

La ejecución atraviesa estas capas:

```text
Fallback
└─ Circuit breaker
   └─ Retry
      └─ Timeout de cada intento
         └─ Operación
```

Con este orden, el circuito cuenta una llamada fallida después de agotar sus reintentos. Cada intento tiene su propio timeout. El fallback proporciona la alternativa si sale una excepción seleccionada, incluido el rechazo por circuito abierto.

Si colocamos timeout antes de retry, el plazo incluye las esperas entre intentos. Si colocamos el circuito dentro de retry, verá fallos individuales. Esas diferencias cambian cuándo deja de enviarse tráfico a una dependencia.

Cada `Build()` crea un circuito independiente. Las llamadas al mismo pipeline comparten su circuito, mientras cada llamada conserva su presupuesto de reintentos. La [guía de pipelines](https://github.com/lbe2014/resilience-lab/blob/main/PIPELINES.md) también muestra el registro singleton por nombre y tipo en DI.

## HTTP: repetir la llamada también implica liberar recursos

El adaptador `ResilientHttpClient` solicita una `HttpRequestMessage` nueva en cada intento y libera solicitudes y respuestas descartadas. La respuesta final pertenece al consumidor.

También existe `AddResilientHttpClient`, respaldado por `IHttpClientFactory`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ResilienceLab.Http;

builder.Services.AddResilientHttpClient(
    client => client.BaseAddress = new Uri("https://example.com/"),
    provider => new HttpRetryOptions { MaxRetries = 3 });
```

`builder` es el builder de la aplicación ASP.NET Core. El registro crea un cliente tipado transitorio; la fábrica administra el pool de handlers. Su funcionamiento general está descrito en la [documentación de Microsoft](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory).

Por defecto, el adaptador permite reintentos para GET y HEAD. Selecciona errores de transporte y los códigos 408, 429, 502, 503 y 504. Una respuesta final fallida se devuelve para que la aplicación decida cómo manejarla; no llama automáticamente a `EnsureSuccessStatusCode`.

Hay dos detalles que conviene conocer antes de componerlo:

- El adaptador ya reintenta. Añadir otro retry alrededor puede multiplicar los envíos. La muestra consumidora desactiva el retry del adaptador cuando el pipeline administra ese presupuesto.
- Usa `ResponseHeadersRead`: el retry cubre envío y cabeceras, no la lectura posterior del cuerpo. Para proteger también esa lectura con timeout, hay que leer y liberar la respuesta dentro del delegado protegido y devolver texto o un DTO.

Activar reintentos para una escritura tampoco crea idempotencia. La aplicación debe resolver cómo evitar efectos duplicados.

## Las trazas explican cómo llegó el resultado

Un resultado exitoso puede esconder dos intentos fallidos y una recuperación. Por eso agregué observabilidad opcional: `ILogger`, métricas con `Meter` y trazas con `ActivitySource`.

`WithTelemetry` proporciona la configuración del pipeline y sus estrategias. Para crear spans también debe existir un listener que escuche la fuente `ResilienceLab`. La biblioteca no instala un exportador ni envía datos a un collector por sí misma.

La demo incluye un `ActivityListener` y muestra las trazas en consola:

```powershell
dotnet run --project samples/Demo -c Release
```

Los spans de cada intento comparten la traza de la operación y conservan su relación de padre e hijo. Un intento puede terminar con error mientras el retry exterior termina con éxito. También hay eventos para espera programada, activación del fallback y cambios del circuito.

La instrumentación propia registra tipos de error, estados y nombres de dependencia estables. No captura URLs, cabeceras, cuerpos, resultados ni mensajes de excepción. La instrumentación HTTP adicional que instale la aplicación tiene su propia política de datos.

La [guía de trazas del proyecto](https://github.com/lbe2014/resilience-lab/blob/main/TRACING.md) explica cómo escuchar esa fuente desde OpenTelemetry. El mecanismo usa la infraestructura de [trazas distribuidas de .NET](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs).

## Lo que más cuidado necesitó

Timeout es cooperativo. Al vencer el plazo solicita cancelación y espera a que termine la operación. Si el código ignora el token, puede seguir ejecutándose indefinidamente. No hay un hilo que lo detenga por la fuerza.

Hedging tiene una condición parecida: lanza alternativas escalonadas y acepta un resultado ganador, pero cancela y espera a todos los perdedores antes de devolverlo. Un perdedor que ignore la cancelación puede retrasar la respuesta. Además, los intentos paralelos aumentan carga y costes; sirven para lecturas o efectos idempotentes, con recursos independientes por intento.

Rate limiting comparte un limitador de `System.Threading.RateLimiting` dentro del proceso. La aplicación elige entre presupuesto por tiempo o concurrencia y administra su ciclo de vida. No hay coordinación automática entre varias instancias del servicio.

## Cómo lo comprobé

La versión 0.5.0 tiene **194 pruebas aprobadas en Release**: 137 del núcleo y 57 de HTTP. Cubren cancelación, límites, estados del circuito, composición, concurrencia, liberación de recursos y jerarquía de trazas.

Para las pruebas de tiempo usé `FakeTimeProvider`, aleatoriedad controlada y señales explícitas. Así se puede avanzar el reloj o liberar una tarea sin hacer que el resultado dependa de esperar exactamente unos milisegundos reales.

Además de las doce demos, una API Kestrel local verifica seis escenarios HTTP. Un consumidor independiente instala los paquetes mediante `PackageReference`, sin enlazar los proyectos fuente. También restauré ambos paquetes desde NuGet.org y comprobé sus escenarios de pipeline, fábrica HTTP y trazas.

El runner de carga local ejecuta ocho fases y se probó con perfiles de 256 solicitudes/concurrencia 32 y 512/concurrencia 64. Comprueba presupuestos de intentos, rechazo con circuito abierto, recuperación y finalización de tareas y spans. Estos ensayos son acotados y locales; no acreditan estabilidad prolongada, ausencia de fugas ni rendimiento en producción.

Los comandos, resultados y sus límites están en [VALIDATION.md](https://github.com/lbe2014/resilience-lab/blob/main/VALIDATION.md). GitHub Actions ejecuta pruebas, demos, verificaciones HTTP y empaquetado. La publicación NuGet usa Trusted Publishing con OIDC para obtener credenciales temporales, sin guardar una API key permanente.

## Probarlo en un problema pequeño

Empezaría con una lectura idempotente, un filtro de errores específico y un presupuesto de reintentos que pueda explicar. Después agregaría timeout y observaría las trazas antes de sumar más estrategias.

ResilienceLab me sirvió para convertir esas decisiones en código y pruebas. El siguiente trabajo es validarlas frente a dependencias reales y documentar lo que aparezca allí. El [repositorio](https://github.com/lbe2014/resilience-lab) reúne las muestras y las guías para reproducir los escenarios o explorar una composición distinta.
