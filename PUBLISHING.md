# Publicación NuGet mediante OIDC

Primera versión propuesta: 0.5.0 experimental, paquetes ResilienceLab y
ResilienceLab.Http, licencia MIT. Los nombres devolvieron 404 en el índice NuGet
el 2026-10-05; eso no reserva IDs ni garantiza que no exista un prefijo reservado.

La política de Trusted Publishing en NuGet.org debe coincidir con:

| Campo | Valor |
|---|---|
| Policy Name | resilience-lab |
| Package Owner | Lbe2014 |
| Provider | GitHub Actions |
| Repository Owner | lbe2014 |
| Repository | resilience-lab |
| Workflow File | publish.yml |
| Environment | Vacío |
| Permiso | Push new packages and package versions |
| Paquetes | ResilienceLab y ResilienceLab.Http, en líneas independientes |

No habilites unlist/relist ni un patrón comodín. La política autoriza a ese workflow
a publicar esos dos paquetes bajo tu cuenta, mediante credenciales temporales.
Para guardar la política hay que confirmar ese permiso. No requiere guardar una API key.

En Actions, selecciona Prepare or publish NuGet y ejecútalo en main. `publish=false`
es el valor inicial: compila, ejecuta tests, comprueba un consumidor de los paquetes
y valida ID/versión/licencia, guardando artifacts sin solicitar credenciales OIDC.
`publish=true` ejecuta además el job de publicación: pide un token temporal a NuGet
y publica primero el núcleo y después HTTP. No se ejecuta al hacer push ni crear un PR.
La publicación no es atómica: si sólo se publica el núcleo, vuelve a ejecutar;
skip-duplicate permite continuar con HTTP. Los errores restantes detienen el job.

El repo puede permanecer privado. Una política nueva de repo privado puede mostrar
activación temporal por siete días hasta la primera publicación, según
[la documentación de NuGet](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
La action [NuGet/login](https://github.com/NuGet/login) maneja el intercambio OIDC.
Ambos paquetes 0.5.0 fueron aceptados el 2026-10-05 mediante
[este workflow](https://github.com/lbe2014/resilience-lab/actions/runs/37350047486).
La política aparece Active y ligada a los IDs permanentes del repositorio y propietario.
Ambos paquetes ya aparecen en el índice público. Se verificó restauración desde
nuget.org como única fuente, compilación del consumidor y seis checks HTTP.
No se creó un tag/release; el repositorio continúa privado.
