# API de catálogo con ResilienceLab

ASP.NET Core .NET 10, independiente de la solución y usando ambos paquetes
públicos ResilienceLab 0.5.0. Consulta [DummyJSON](https://dummyjson.com/docs/products),
un proveedor externo de productos ficticios que no requiere claves.

## Ejecutar

Desde la raíz del repositorio:

```powershell
cd examples/CatalogApi
dotnet run -c Release
```

Consulta `http://127.0.0.1:5110/catalog`. `/health` comprueba que el proceso está
activo, no la disponibilidad del proveedor. Por defecto escucha sólo en localhost.

La respuesta contiene `products`, `total`, `fetchedAt` e `isStale`. Consulta fija:
hasta diez productos, campos id/title/price. Guarda una sola instantánea exitosa
en memoria. Ante fallos seleccionados devuelve esa instantánea con `isStale:true`
durante cinco minutos desde su obtención. Reutilizarla no renueva su edad. La
caché no persiste al reiniciar ni se comparte entre instancias.

Una caída sin caché válida devuelve `503` con ProblemDetails. Un `404` del
proveedor o JSON inválido devuelve `502`, sin retry ni fallback. No transmite
mensajes de excepción al cliente. No hay datos de respaldo inventados.

## Pipeline

`fallback → circuit breaker → retry → timeout → envío y lectura HTTP`

- Dos reintentos adicionales: hasta tres envíos por llamada admitida.
- Timeout cooperativo de dos segundos por intento, incluida lectura del cuerpo.
  Libera las respuestas dentro del delegado; limita el cuerpo a 1 MiB.
- Selecciona errores de transporte, 408, 429, 502, 503, 504 y timeout de la
  estrategia. No reintenta cancelación del consumidor.
- Circuito compartido: abre tras dos llamadas definitivamente fallidas; una
  prueba HalfOpen después de quince segundos.
- Caché singleton acotada y edad con reloj monotónico. El pipeline no captura
  el servicio ni el cliente HTTP transitorios. IHttpClientFactory administra handlers.

El adaptador HTTP tiene MaxRetries=0: sólo el pipeline administra el presupuesto.
El backoff genérico de este ejemplo no interpreta Retry-After; adapta la política
al contrato del proveedor antes de integrar un servicio que lo exija. Timeout
no detiene por la fuerza operaciones que ignoren cancelación.

Configuración `Catalog`: BaseUrl (HTTP/S terminada en `/`),
AttemptTimeoutMilliseconds, MaxRetries, FailureThreshold, BreakSeconds y
MaxStaleSeconds. La URL sólo se configura al arrancar, no desde una solicitud.

## Collector OpenTelemetry real

Con Docker iniciado, desde esta carpeta:

```powershell
docker compose -f observability/compose.yaml up -d
dotnet run -c Release -- --Telemetry:OtlpEnabled=true
```

Después de consultar `/catalog`, en otra terminal:

```powershell
docker compose -f observability/compose.yaml logs -f collector
```

OTLP HTTP/protobuf envía trazas a `/v1/traces` y métricas a `/v1/metrics` del
collector en localhost:4318. El contenedor oficial está fijado por versión y
digest. Su exporter debug muestra spans y métricas, sin UI ni almacenamiento
persistente. Detener: `docker compose -f observability/compose.yaml down`.

`Telemetry:Console=true` muestra spans sin Docker. Escucha la fuente y el Meter
ResilienceLab y agrega instrumentación ASP.NET Core. La instrumentación propia
no captura URLs ni contenido; ASP.NET Core agrega metadatos HTTP entrantes.
Referencias oficiales: [trazas .NET](https://opentelemetry.io/docs/languages/dotnet/traces/getting-started-aspnetcore/)
y [exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/).

## Verificación reproducible

Desde la raíz, sin depender del servicio externo:

```powershell
pwsh -File examples/CatalogApi/verify.ps1
pwsh -File examples/CatalogApi/verify.ps1 -WithCollector
```

Inicia un proveedor de pruebas separado en localhost:5111 y la API en :5110.
Comprueba doce escenarios: caché fría, recuperación tras dos503, 404, JSON inválido,
timeouts, datos frescos, dos caídas sostenidas, circuito abierto sin envíos,
recuperación HalfOpen, cancelación del consumidor y caché vencida. Comprueba
handlers finalizados y spans; con Docker exige spans y métricas recibidos en OTLP.
Detiene sólo los procesos/contenedor que creó y rechaza puertos ocupados.

Logs en artifacts/catalog-api. CI ejecuta los escenarios sin Docker. Son pruebas
acotadas; no acreditan estabilidad prolongada ni rendimiento productivo.
