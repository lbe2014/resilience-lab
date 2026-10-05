# ResilienceLab v0.5.0

Primera publicación de ResilienceLab y ResilienceLab.Http en NuGet.org: una
biblioteca experimental para .NET 10, con licencia MIT e implementación propia.

Incluye reintentos por excepciones y resultados, fallback, timeout cooperativo,
circuit breaker, rate limiting y hedging. Los pipelines permiten configurar el
orden de las estrategias y registrarlos por nombre en DI. El adaptador HTTP
incluye Retry-After, solicitudes nuevas por intento e integración con
IHttpClientFactory.

La observabilidad es opcional: ILogger, diez instrumentos de métricas y
ActivitySource para estrategias, intentos y eventos. No registra automáticamente
URLs, cuerpos ni mensajes de excepción.

## Instalación

```powershell
dotnet add package ResilienceLab --version 0.5.0
dotnet add package ResilienceLab.Http --version 0.5.0
```

## Validación

- 194 pruebas aprobadas en Release: 137 del núcleo y 57 de HTTP.
- Doce escenarios de demo y seis comprobaciones HTTP del consumidor independiente.
- Dos perfiles de carga local, con ocho fases y diez comprobaciones por perfil.
- Restauración pública desde NuGet.org, compilación y seis comprobaciones del consumidor.
- Publicación mediante OIDC en GitHub Actions, sin API key permanente en el repositorio.

## Límites de esta entrega

API propia, sin compatibilidad con Polly ni equivalencia de madurez demostrada.
El timeout y hedging dependen de que la operación respete cancelación; esperan a
que terminen las tareas. El rate limiting es local al proceso. El circuito cuenta
excepciones seleccionadas. Los reintentos HTTP cubren envío y cabeceras; combinar
presupuestos de retry puede multiplicar intentos. La carga local no demuestra
rendimiento de producción; no se validó un collector OpenTelemetry externo.

El tag apunta a `d1fdde7c786ad98a666a08a51eeb7055f1b32b69`, el commit utilizado en
[la publicación NuGet](https://github.com/lbe2014/resilience-lab/actions/runs/37350047486).
Los archivos adjuntos se descargaron de NuGet.org: son los paquetes publicados,
sin reempaquetar documentación posterior bajo la misma versión.

Paquetes: [ResilienceLab](https://www.nuget.org/packages/ResilienceLab/0.5.0) y
[ResilienceLab.Http](https://www.nuget.org/packages/ResilienceLab.Http/0.5.0).
