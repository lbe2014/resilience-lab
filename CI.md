# GitHub Actions

El repositorio usa main y dos workflows para Windows:

- CI: push a main, pull request o ejecución manual; restaura, compila Release con
  warnings como errores, ejecuta tests, doce demos, seis checks HTTP con tracing y
  genera ambos paquetes NuGet. Guarda TRX/logs y paquetes como artifacts por 14 días.
  También compila y verifica examples/PackageConsumer desde los .nupkg generados,
  usando un cache dedicado y guardando los logs del consumidor.
- Local load check: sólo manual desde Actions → Run workflow; recibe requests y
  concurrency, ejecuta ocho fases contra su API localhost y guarda JSON/Markdown/logs.
  El informe también aparece en el resumen de la ejecución.

Los jobs tienen 15 minutos de límite y usan el SDK de global.json. Los scripts
de integración administran su propio proceso de API; no necesitan servicios externos.
Las actions se fijan a commits verificados de sus versiones actuales. Actualízalas
deliberadamente; el workflow no se actualiza automáticamente con un tag mutable.

Sólo se solicita contents: read y checkout no conserva credenciales. No hay pasos
de publicación NuGet, releases o despliegue; no requiere secretos configurados.
Los comandos se probaron localmente y ambos workflows aprobaron en GitHub el 2026-10-05:
[CI](https://github.com/lbe2014/resilience-lab/actions/runs/37346834705) y
[carga manual](https://github.com/lbe2014/resilience-lab/actions/runs/37346922144).
Las latencias del workload dependen de ese runner.
La ampliación con consumidor NuGet independiente también aprobó en
[CI](https://github.com/lbe2014/resilience-lab/actions/runs/37348008986).

Referencias oficiales:
[checkout](https://github.com/actions/checkout),
[setup-dotnet](https://github.com/actions/setup-dotnet),
[upload-artifact](https://github.com/actions/upload-artifact).
