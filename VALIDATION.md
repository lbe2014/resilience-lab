# Validación de ResilienceLab 0.3.0

Validado el 5 de octubre de 2026, Windows x64, SDK .NET 10.0.401.

## Resultados

| Componente | Pruebas aprobadas |
|---|---:|
| Núcleo, estrategias adicionales y observabilidad | 111 |
| Adaptador HTTP, observabilidad y fábrica | 55 |
| Total | 166 |

Cero fallos y cero pruebas omitidas. Ejecución conjunta en Release.
Los informes TRX finales se guardan en `artifacts/test-results/v0.3.0`.
Se ejecutó con `-warnaserror`: sin advertencias. Las 40 pruebas nuevas respecto a
0.2.0 cubren resultados genéricos, fallback, límites y cola, cancelación,
liberación de recursos, hedging, métricas y gestión del cliente de fábrica.
Un bloqueo detectado durante el desarrollo permitió corregir el incremento del
contador de reintentos sin callback; la suite final completa pasó después de la corrección.

La demo comprobó recuperación al tercer intento, cancelación antes de la espera,
timeout cooperativo, circuito Open → HalfOpen → Closed, HTTP simulado 503 → 200
y composición con recuperación tras dos intentos. No realizó llamadas de red.
También comprobó fallback, Retry por resultados, token bucket, hedging y
IHttpClientFactory: once escenarios en total, con métricas, eventos y logs.

Se generaron `ResilienceLab.0.3.0.nupkg` y `ResilienceLab.Http.0.3.0.nupkg`,
con README, guías de observabilidad/estrategias y DLL. Compilación, demo y
empaquetado terminaron sin advertencias.

## Reproducir

```powershell
dotnet test -c Release -warnaserror --logger trx --results-directory artifacts/test-results/v0.3.0
dotnet run --project samples/Demo -c Release
dotnet pack src/ResilienceLab -c Release -o artifacts
dotnet pack src/ResilienceLab.Http -c Release -o artifacts
```

## Límites comprobados y pendientes

Las pruebas controlan tiempo y azar; los escenarios HTTP usan handlers simulados.
Esto no constituye una prueba de carga, compatibilidad con servidores reales
o paridad con Polly. Timeout y hedging requieren cooperación con cancelación.
Rate limiting usa limitadores de .NET en el proceso, sin presupuesto distribuido.
La publicación pública y el agente del blog quedan pendientes según ROADMAP.md.
# Integración HTTP local adicional (2026-10-05)

`pwsh -NoProfile -File samples/FaultApi/verify.ps1` compiló sin warnings ni errores
y aprobó seis llamadas sobre Kestrel/localhost: retry 200 con tres intentos,
timeout 504 con un intento, fallback 200 con tres intentos y valor de caché,
dos fallos definitivos 502 del circuito y rechazo 503 con cero intentos.
La instancia iniciada por el script se detuvo al terminar. Esta muestra no valida
carga ni una API externa. La versión de las DLL permanece en 0.3.0.
# Pipelines 0.4.0 (2026-10-05)

- `dotnet test -c Release --nologo -warnaserror`: 180 pruebas aprobadas,
  125 del núcleo y 55 de HTTP, sin errores ni omisiones. Incluye 14 pruebas nuevas
  de pipelines, timeout con FakeTimeProvider, composición, estado, DI y concurrencia.
- `dotnet run --project samples/Demo -c Release`: doce escenarios completados,
  incluido pipeline por nombre registrado en DI con recuperación tras retry.
- `pwsh -NoProfile -File samples/FaultApi/verify.ps1`: seis comprobaciones HTTP
  locales aprobadas, compilación sin warnings ni errores.
- Ambos paquetes 0.4.0 generados localmente en artifacts, incluyendo PIPELINES.md.
  No se publicaron. Las estrategias y el cliente HTTP independientes se conservan.
# Trazas 0.5.0 (2026-10-05)

- `dotnet test -c Release --nologo -warnaserror`: 194 pruebas aprobadas,
  137 del núcleo y 57 HTTP; sin fallos ni omisiones. Las 14 nuevas verifican contexto,
  jerarquía, concurrencia, intentos, timeout, cancelación, fallback, circuito, resultados,
  rate limiting, hedging, sampling, aislamiento de listeners y privacidad HTTP.
- Doce demos completadas con ActivityListener: spans, TraceId, SpanId, parent,
  duración y eventos visibles en consola.
- `pwsh -NoProfile -File samples/FaultApi/verify.ps1 -Tracing`: seis llamadas
  HTTP locales aprobadas y emisión de spans HTTP/timeout confirmada en el log.
- Paquetes locales 0.5.0 del núcleo y HTTP generados con TRACING.md.
  No se publicó ni se envió a un collector externo. Exportación OpenTelemetry
  documentada; verificación ejecutada con ActivityListener local.
# Carga local (2026-10-05)

- Nuevo runner samples/LoadCheck y endpoints acotados /load de FaultApi.
- Dos perfiles aprobados: 256 llamadas por fase/concurrencia 32 y 512/concurrencia 64.
  Ocho fases por perfil, diez comprobaciones cada una. Compilación sin warnings ni errores.
- Perfil 512/64: retry recuperó 512 llamadas con exactamente 1536 intentos;
  caída sostenida agotó 1536; circuito abierto rechazó 512 con cero envíos;
  tras una prueba sana recuperó 512 llamadas; limitador sin cola aceptó cuatro,
  rechazó 508 y devolvió sus cuatro permisos; 512 timeouts asentados;
  hedging completó 512 con 1024 intentos y pico de 128 handlers activos.
- 5643 intentos cliente iniciados/terminados y 10253 spans iniciados/terminados.
  El total incluye seis intentos de calentamiento del circuito y una prueba de recuperación.
  Todos los snapshots finales registraron cero handlers activos.
- Informes del perfil 512/64 en artifacts/load-check/report.json y report.md;
  copia del perfil 256/32 en artifacts/load-check-256. Incluyen runtime, percentiles
  medidos, tipos de error, contadores y configuración. No son cifras de producción.
- Se conservaron 194 unit tests aprobados en Release y los doce escenarios de Demo.
  La versión de la DLL permanece en 0.5.0; esta etapa agrega muestras y validación.
- Límite: carga local acotada, sin prueba prolongada, API externa ni prueba de fugas
  de memoria. Los percentiles incluyen rechazos, y hedging aumenta la presión HTTP.
# Preparación de GitHub Actions (2026-10-05)

- Repositorio Git local inicializado en main y dos workflows preparados: CI y carga manual.
- Comandos del CI verificados localmente: 194 tests con TRX y detección de bloqueos,
  doce demos, seis checks HTTP con tracing y ambos paquetes en artifacts/packages.
- Actions fijadas a commits de checkout v7, setup-dotnet v6 y upload-artifact v7;
  refs comprobadas con git ls-remote. Permisos contents: read; sin publicación NuGet.
- Repositorio privado creado y subido: https://github.com/lbe2014/resilience-lab,
  rama principal main. Identidad local configurada con correo noreply de GitHub;
  no se cambió la identidad global de Git.
- CI hospedado aprobado: https://github.com/lbe2014/resilience-lab/actions/runs/37346834705.
  Tests, demo, HTTP/tracing y empaquetado completados, artifacts publicados.
- Carga manual hospedada aprobada: https://github.com/lbe2014/resilience-lab/actions/runs/37346922144,
  perfil 256 solicitudes/concurrencia 32, informe y logs disponibles como artifacts.
# Consumidor NuGet independiente (2026-10-05)

- examples/PackageConsumer está fuera de ResilienceLab.slnx y sólo utiliza
  PackageReference a ResilienceLab y ResilienceLab.Http 0.5.0.
- Verificación local aprobada: restauración desde feed local, dependencias de tipo
  package en assets, compilación Release sin warnings/errores, seis checks HTTP y
  spans de pipeline, retry, HTTP, timeout, fallback y circuito.
- Copia independiente en work/PackageConsumer-check restaurada y compilada con
  SDK 10.0.401; no contiene src ni referencias a proyectos de la biblioteca.
- El script de verificación crea un cache nuevo por ejecución para evitar reutilizar
  una versión anterior del mismo paquete en el cache global o en un run previo.
- CI aprobado en GitHub, incluido el consumidor y los 194 unit tests:
  https://github.com/lbe2014/resilience-lab/actions/runs/37348008986.
- La versión de la biblioteca permanece en 0.5.0; no se publicó en NuGet.
# Primera publicación NuGet (2026-10-05)

- Política resilience-lab creada en NuGet con autorización explícita del usuario,
  restringida a ResilienceLab y ResilienceLab.Http, workflow publish.yml.
- Workflow 37350047486 aprobado: build/tests, consumidor NuGet, identidad/licencia,
  autenticación OIDC y push de ambos paquetes 0.5.0. Los logs confirmaron dos
  mensajes Your package was pushed. No se guardó una API key permanente.
- Política comprobada en la UI como Active, con propietario/repo ligados a IDs
  permanentes. Captura local artifacts/nuget-policy-active.jpg.
- Tras el push, el índice flatcontainer y páginas públicas todavía devolvieron 404:
  validación/indexación de NuGet pendiente. No se verificó instalación pública todavía.
- Repo sigue privado; no se creó tag ni release. Versión experimental, licencia MIT.
