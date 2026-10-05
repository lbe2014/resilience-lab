# ResilienceLab.Http

Biblioteca educativa propia para .NET 10: reintentos HTTP con espera exponencial
acotada, full jitter, `Retry-After` y cancelación.

```csharp
using ResilienceLab.Http;

// Reutiliza este HttpClient durante la vida del servicio.
var http = new HttpClient();
var resilient = new ResilientHttpClient(http);
using var response = await resilient.SendAsync(
    () => new HttpRequestMessage(HttpMethod.Get, "https://example.com/api/status"),
    cancellationToken);
var body = await response.Content.ReadAsStringAsync(cancellationToken);
```

Por defecto hay tres reintentos adicionales (cuatro envíos como máximo), retraso
base de 200 ms, máximo de 10 s y jitter. Sólo se reintentan `GET` y `HEAD` ante
`HttpRequestException` o respuestas `408`, `429`, `502`, `503` y `504`.
`OperationCanceledException` nunca se reintenta, incluido el timeout propio de
`HttpClient`. Una respuesta fallida final se devuelve sin llamar automáticamente
a `EnsureSuccessStatusCode()`; un error de transporte final se propaga.

Para otros métodos, configura `AllowRetryForIdempotentOperations = true` únicamente
cuando tu servicio garantice la idempotencia (por ejemplo, con una clave de
idempotencia). La fábrica debe crear una solicitud **y un contenido nuevos** en
cada intento. No reutilices streams consumidos. Los errores de la fábrica no se
reintentan. Cada llamada debe representar la misma operación lógica.

`Retry-After` admite segundos y fecha: se espera el mayor valor entre el backoff
y la cabecera, limitado siempre por `MaxDelay`. Por ello un valor superior al máximo
configurado puede producir un reintento anterior al solicitado por el servidor.
Una cabecera inválida o una fecha pasada no incrementan la espera.

El wrapper manual no libera el `HttpClient`. Sí libera cada solicitud y cada respuesta
descartada antes de esperar; **el consumidor libera la respuesta final**. Su
`RequestMessage`, si existe, ya estará liberado; no reutilices su contenido.
Se usa `ResponseHeadersRead`: los reintentos terminan al recibir las cabeceras.
La lectura posterior del cuerpo no se reintenta y debe recibir su propio token.
Consulta el comportamiento de finalización de
[HttpClient.SendAsync en Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-10.0).

Las pruebas usan un handler simulado y tiempo controlado: no llaman a Internet.
El paquete se genera localmente como versión `0.5.0`.

Puedes configurar `HttpRetryOptions.Telemetry` con una instancia de
`ResilienceLab.ResilienceTelemetry` para emitir logs y métricas de reintentos.
Consulta [la guía de observabilidad](OBSERVABILITY.md).

## IHttpClientFactory

```csharp
using Microsoft.Extensions.DependencyInjection;
builder.Services.AddResilientHttpClient(
    client => client.BaseAddress = new Uri("https://example.com/"),
    provider => new HttpRetryOptions { MaxRetries = 3 });
```

Inyecta ResilientHttpClient como cliente tipado transitorio. El registro devuelve
IHttpClientBuilder para configurar handlers. Al disponer este cliente tipado,
se libera su HttpClient; la fábrica conserva la administración del pool de handlers.
Evita capturar clientes tipados en un singleton. El adaptador manual conserva
la propiedad externa del HttpClient y no lo dispone.
Consulta [las estrategias adicionales](STRATEGIES.md) para combinar con fallback,
rate limiting, hedging y reintentos por resultados.
