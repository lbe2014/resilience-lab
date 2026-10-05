# Roadmap

## Primera publicación NuGet
- Ambos paquetes 0.5.0 enviados mediante OIDC; política permanentemente activa.
- Pendiente disponibilidad tras indexación, instalación pública y tag/release.

## Consumidor NuGet independiente
- API separada de la solución con PackageReference a ambos paquetes 0.5.0.
- Restauración desde feed local con cache dedicado y seis comprobaciones HTTP de DI/pipeline/trazas.
- Verificación incluida en CI después del empaquetado; logs como artifacts.

## Integración continua
- Workflows de GitHub Actions: CI para main/PR y carga manual con parámetros.
- Resultados de pruebas, logs e informes y paquetes como artifacts; sin publicación NuGet.

## Validación de carga local
- Runner contra Kestrel con ocho fases: salud, retry, caída sostenida, circuito abierto,
  recuperación, rate limiting, timeout y hedging.
- Informes JSON/Markdown con percentiles, errores, intentos, concurrencia y tareas/spans terminados.
- Validación acotada local; pendiente servicio externo y estabilidad prolongada.

## Implementado en 0.5.0
- ActivitySource opt-in: spans de pipeline, estrategias e intentos, con eventos de resiliencia.
- WithTelemetry en el builder, herencia de contexto y configuración capturada en Build.
- Estados de error/cancelación y tags sin datos de solicitudes ni mensajes de excepción.
- Demo con listener, trazas opcionales en FaultApi y guía TRACING.md para OpenTelemetry.

## Implementado en 0.4.0
- Builder genérico con fallback, circuito, retries, timeout, rate limiting y hedging.
- Pipeline reutilizable; orden de afuera hacia adentro, circuito persistente y presupuestos por llamada.
- Registro singleton por nombre y tipo de resultado en DI, con rechazo de duplicados.
- Guía PIPELINES.md, demo y pruebas deterministas de orden, aislamiento, concurrencia y cancelación.

## Implementado en 0.1.0
- Retry con backoff limitado, jitter, cancelación y evento OnRetry.
- Pruebas de retrasos deterministas y errores de callback.
- Timeout cooperativo con precedencia de cancelación del consumidor.
- Circuit breaker con estados, contador consecutivo, recuperación y concurrencia.
- Adaptador HTTP con Retry-After, solicitudes nuevas y liberación de recursos.
- Demo local de seis escenarios y prueba de composición.
- Paquetes NuGet locales con README y metadatos.

## Próxima etapa: agente del blog
Construir una CLI que lea archivos seleccionados y resultados de pruebas,
genere un borrador Markdown con referencias al código y permita elegir el proveedor
de IA. El usuario revisará el texto antes de publicarlo.
La elección del proveedor y las credenciales se resolverán al iniciar esa etapa.

## Otras mejoras posibles
- Nombre elegido: ResilienceLab; licencia MIT. Comprobar disponibilidad de los IDs en NuGet antes de publicar.
- Integración y pruebas de carga con servicios reales.

## Muestra de integración local
- API ASP.NET Core con dependencia simulada y cliente tipado IHttpClientFactory.
- Recuperación tras 503, timeout, fallback y circuito compartido entre solicitudes.
- Seis verificaciones HTTP locales aprobadas; pendiente carga y servicios externos.

## Implementado en 0.2.0
- Telemetry opcional compartida por dependencia, ILogger y cinco instrumentos de métricas.
- Eventos de transición del circuito con secuencia y fecha UTC, fuera del bloqueo.
- Aislamiento de errores del logger, listeners y observadores del circuito.
- Instrumentación de HTTP y timeout, demo con métricas y guía OBSERVABILITY.md.
- 126 pruebas aprobadas en Release, incluidas 14 nuevas de observabilidad.

Prompt para continuar con Codex u OpenCode:

"Lee AGENTS.md, README.md y ROADMAP.md. Propón e implementa un cambio acotado del
roadmap con pruebas. Ejecuta dotnet test y la demo. Actualiza la documentación
con resultados comprobados. No publiques nada ni accedas a secretos."

## Implementado en 0.3.0
- Reintentos por resultados genéricos y excepciones opcionales, con descarte de recursos.
- Fallback por resultados y excepciones seleccionadas.
- RateLimit con limitadores estándar de .NET, cola, cancelación y RetryAfter.
- Hedging escalonado con selección de ganador, cancelación y limpieza de perdedores.
- Registro de cliente tipado con IHttpClientFactory y gestión de propiedad del HttpClient.
- Métricas y logs de las nuevas estrategias, guía STRATEGIES.md y once demos.
- 166 pruebas aprobadas en Release, 40 nuevas respecto a 0.2.0.
