# API local con fallos simulados

Desde la raíz de ResilienceLab:

```powershell
dotnet run --project samples/FaultApi -c Release
```

Abre http://127.0.0.1:5099/. Usa otra terminal para llamar:

```powershell
Invoke-RestMethod http://127.0.0.1:5099/demo/retry
Invoke-RestMethod http://127.0.0.1:5099/demo/timeout
Invoke-RestMethod http://127.0.0.1:5099/demo/fallback
Invoke-WebRequest http://127.0.0.1:5099/demo/circuit -SkipHttpErrorCheck
```

La muestra requiere .NET 10; los comandos de verificación requieren PowerShell 7.
Escucha únicamente en loopback, puerto fijo 5099. El consumidor usa IHttpClientFactory
y llama por HTTP a la dependencia simulada dentro del mismo servidor Kestrel.

| Escenario | Resultado esperado |
|---|---|
| retry | Dos 503 y luego 200; tres intentos. |
| timeout | La dependencia espera dos segundos; timeout cooperativo a 100 ms, HTTP 504. |
| fallback | Tres 503; devuelve `cached-value` con HTTP 200. |
| circuit | Las primeras dos llamadas agotan sus tres intentos y devuelven 502; la tercera devuelve 503 sin llamar a la dependencia. |

El circuito se comparte entre solicitudes. Tras tres segundos admite una prueba;
la dependencia `down` sigue fallando y lo vuelve a abrir. Reiniciar la API restablece
el circuito. Cada solicitud demo tiene un identificador y contador independiente,
eliminado al terminar. Los endpoints `/dependency/{mode}/{id}` sirven para explorar
los modos `flaky`, `down` y `slow`; las llamadas directas conservan contadores hasta
reiniciar. El límite aproximado de 1024 entradas evita crecimiento libre en uso manual.

Los logs de Resilience muestran los reintentos, timeout, fallback y transiciones.
Para mostrar spans en consola inicia con `-- --Tracing:Console=true`; para verificar
también su emisión usa `pwsh -File samples/FaultApi/verify.ps1 -Tracing`.
Las métricas se emiten por el Meter de la DLL; para exportarlas sigue OBSERVABILITY.md.
El circuito cuenta cada fallo definitivo, después de los reintentos del cliente HTTP.
La respuesta se lee y libera dentro del timeout. RequestAborted cancela la cadena
y la espera de la dependencia simulada.

Para verificar automáticamente, detén primero cualquier instancia de la API:

```powershell
pwsh -File samples/FaultApi/verify.ps1
```

El script compila, arranca su propia instancia, comprueba seis llamadas HTTP y
detiene el proceso al terminar. Guarda logs en `artifacts/fault-api`.
Estos checks comprueban integración local; no son una prueba de carga ni una
validación de una dependencia externa. Para el blog, muestra primero los 503,
después la recuperación y finalmente cómo el circuito evita nuevos intentos.
