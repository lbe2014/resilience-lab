# Reglas para agentes

Proyecto educativo en C# .NET 10, implementación propia sin copiar Polly.
Lee README.md y ROADMAP.md antes de modificar código.
Haz cambios pequeños y explica las decisiones en español.
No leas ni imprimas secretos. No publiques artículos o paquetes sin instrucción.

## Contratos
- Conserva Retry.ExecuteAsync y MaxRetries como intentos adicionales.
- Nunca reintentes OperationCanceledException. Filtros explícitos en el núcleo.
- OnRetry se ejecuta antes de esperar; su excepción aborta y se propaga.
- Timeout es cooperativo: espera a que termine la operación; cancelación del consumidor prevalece.
- CircuitBreaker se reutiliza por dependencia, usa reloj monotónico y una sola prueba HalfOpen.
- No ejecutes delegados del consumidor bajo el bloqueo del circuito.
- Telemetry y OnStateChanged son observadores: sus excepciones se aíslan; OnRetry conserva su contrato de abortar.
- No registres mensajes/objetos de excepción ni datos de solicitudes en la telemetría automática.
- Mantén nombres de dependencia estables y etiquetas de cardinalidad acotada.
- HTTP: nueva solicitud/contenido por intento, libera descartes, respuesta final del consumidor.
- Mantén la biblioteca independiente de IA y proveedores externos.
- Hedging debe cancelar y esperar a todos sus intentos; libera los resultados descartados y nunca abandones tareas.
- Retry por resultado devuelve el último resultado al agotar el presupuesto; el consumidor lo administra.
- OnDiscardResult reemplaza la liberación automática; sin callback usa IAsyncDisposable antes de IDisposable.
- RateLimit comparte un limitador cuyo ciclo de vida pertenece al consumidor; siempre libera los leases.
- El cliente tipado de fábrica libera su HttpClient; el adaptador manual conserva propiedad externa.
- Pipeline: orden de registro de afuera hacia adentro; Build independiente y circuito persistente por pipeline.
- DI: pipeline singleton por nombre y tipo; no captures servicios scoped en su configuración o callbacks.
- Trazas opt-in por Telemetry y ActivitySource; conserva jerarquía, cancelación y restauración de Activity.Current.
- No registres mensajes, stack traces, URLs, cuerpos ni resultados en spans; aísla errores de listeners.

## Verificación
Ejecuta dotnet test -c Release y dotnet run --project samples/Demo -c Release.
Para cambios de distribución, empaqueta ambos proyectos en artifacts.
Usa FakeTimeProvider, aleatoriedad controlada y TaskCompletionSource en pruebas.
Evita depender de servicios externos, azar o tiempos exactos reales.
No alteres archivos de otros agentes mientras trabajen; reparte archivos y acuerda APIs primero.
