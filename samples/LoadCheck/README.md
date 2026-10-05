# Carga local reproducible

Desde la raíz de ResilienceLab, detén cualquier instancia manual de FaultApi:

```powershell
pwsh -NoProfile -File samples/LoadCheck/run.ps1
```

El script requiere PowerShell 7 y .NET 10. Compila la solución, comprueba que el
puerto 5099 esté libre, inicia su propia API en loopback y ejecuta ocho fases con
256 llamadas cada una y hasta 32 operaciones concurrentes. Detiene únicamente
el proceso que inició, incluso cuando una comprobación falla.

```powershell
pwsh -NoProfile -File samples/LoadCheck/run.ps1 -Requests 512 -Concurrency 64
```

Acepta 16..100000 solicitudes por fase y concurrencia 8..256. El ejecutor tiene
un límite global de cinco minutos y falla si no termina. Un volumen mayor puede
necesitar cambiar ese límite deliberadamente. Cada ejecución reemplaza los informes
anteriores en artifacts/load-check; copia el directorio si quieres conservarlos.

## Fases y contratos comprobados

| Fase | Comprobación |
|---|---|
| healthy | Una llamada HTTP por operación, todas exitosas. |
| retry-recovery | Dos 503 antes del éxito; exactamente tres intentos por operación. |
| sustained-failure | 503 persistente; agota tres intentos y devuelve error. |
| open-circuit | Dos fallos definitivos abren previamente el circuito; la ráfaga posterior debe rechazar llamadas y reducir tráfico. |
| circuit-recovery | Tras cinco segundos admite una prueba sana, cierra y recupera la carga. |
| rate-limit | ConcurrencyLimiter con cuatro permisos, sin cola; rechaza exceso y libera todos los permisos. |
| timeout | Cancelación cooperativa a 50 ms frente a una dependencia de 500 ms; espera a que los handlers terminen. |
| hedging | Un intento lento y una alternativa lanzada tras 10 ms; como máximo dos intentos y limpieza completa. |

El control de fallos es determinista: el contador de intento vive en cada operación,
sin azar ni estado por identificador en el servidor. Los tiempos y percentiles sí
dependen del sistema operativo y su carga. La recuperación del circuito se espera
por estado, con un límite global de ejecución, no mediante una aserción de latencia.

El runner usa HttpClient sin retry automático, libera todas las respuestas y
compone las estrategias de la DLL; evita multiplicar el presupuesto con el adaptador
HTTP. Las estadísticas del servidor cubren /load/{mode}, sin contar health/stats/reset.
Reset sólo se admite sin handlers activos. Usa una instancia dedicada: llamadas
manuales concurrentes pueden contaminar las estadísticas.
Los totales globales de intentos incluyen seis envíos de calentamiento del circuito
y una prueba de recuperación fuera de las ocho fases tabuladas.

## Informes

- artifacts/load-check/report.json: configuración, runtime, resultados, errores por
  tipo, p50/p95/p99, llamadas/s, intentos, handlers iniciados/terminados/cancelados,
  tareas cliente y spans iniciados/terminados. `passed` identifica el éxito completo.
- artifacts/load-check/report.md: tabla para revisar los resultados de la carga local.
- server.log y server-errors.log: salida de la API.

ActivityListener captura todas las trazas de resiliencia, contando sus aperturas y
cierres sin retener spans en memoria. No es un exportador externo. Las fases esperan
hasta cinco segundos a que el servidor drene; el cliente también comprueba que cada
intento haya terminado y que cada span se haya cerrado.

Los percentiles son de **todas las operaciones**, incluidos rechazos inmediatos.
Un p95 pequeño con muchos rechazos no significa que las operaciones útiles sean rápidas.
La fase sin cola del limitador es una ráfaga: puede aceptar sólo las primeras cuatro
llamadas porque las restantes se rechazan rápidamente. No mide reparto justo ni tasa
sostenida. Hedging puede duplicar las solicitudes activas; si quieres acotarlas,
coloca AddRateLimit dentro de AddHedging.

Estas comprobaciones verifican presupuestos, estados y finalización de tareas bajo
carga local. No prueban ausencia de fugas de memoria, estabilidad durante horas,
capacidad de producción ni comportamiento de un servicio externo. Tampoco comparan
rendimiento con Polly. Los unit tests siguen usando relojes simulados; esta muestra
se ejecuta por separado para observar HTTP real y tiempos reales.
