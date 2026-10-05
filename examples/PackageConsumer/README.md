# API consumidora de los paquetes NuGet

Proyecto independiente de ResilienceLab.slnx: sólo tiene PackageReference a
ResilienceLab y ResilienceLab.Http 0.5.0. No referencia los proyectos de la biblioteca.
Su global.json permite copiarlo junto con los paquetes y usarlo fuera del repositorio.

Desde la raíz del repositorio:

```powershell
pwsh -NoProfile -File examples/PackageConsumer/verify.ps1
```

El script genera los .nupkg, restaura con un cache nuevo para cada ejecución, confirma que las
dependencias se resolvieron como paquetes, compila y ejecuta seis comprobaciones
HTTP contra su propia instancia en http://127.0.0.1:5101. Detiene esa instancia
al terminar. Requiere .NET 10, PowerShell 7 y el puerto 5101 libre.

DI registra un pipeline singleton por nombre y un ResilientHttpClient transitorio.
El retry del adaptador HTTP está desactivado: los tres intentos pertenecen al
pipeline. Respuestas y cuerpos se leen y liberan dentro del timeout de un segundo.
El circuito comparte estado entre solicitudes y abre después de dos errores HTTP
definitivos; timeout no se cuenta como error del circuito en esta muestra.
Fallback devuelve cached-value para errores seleccionados y circuito abierto.

La dependencia simulada está dentro de la misma API, pero las solicitudes atraviesan
HTTP/Kestrel. Los endpoints son:

| Endpoint | Comportamiento con circuito cerrado |
|---|---|
| /catalog/healthy | fresh-value, un intento. |
| /catalog/flaky | Dos 503 y recuperación en el tercer intento. |
| /catalog/slow | Dependencia de dos segundos, timeout de un segundo y cached-value. |
| /catalog/down | Tres 503 y cached-value; dos llamadas consecutivas abren el circuito. |

Tras abrirse, cualquier llamada devuelve caché con cero intentos durante 30 segundos.
Reiniciar el proceso limpia el estado. El script verifica precisamente esa secuencia.
ActivityListener imprime spans de pipeline, estrategias e intentos y su TraceId.
Los logs de la verificación están en artifacts/package-consumer.

Para dejar la API abierta, después de ejecutar la verificación:

```powershell
dotnet run --project examples/PackageConsumer -c Release --no-build
```

NuGet.Config obliga a obtener los dos paquetes ResilienceLab del feed local
artifacts/packages; las dependencias transitivas de Microsoft se obtienen de nuget.org.
La restauración puede necesitar acceso a nuget.org si no están en cache.
Para llevar el ejemplo a otra carpeta, copia los dos .nupkg a un directorio y cambia
el value del feed local en NuGet.Config a esa ubicación. Después ejecuta dotnet restore
con ese config y dotnet run. No necesitas copiar src ni la solución de la biblioteca.

CI incluye este script después del empaquetado y guarda sus logs como artifacts.
No publica paquetes ni instala desde NuGet público los paquetes ResilienceLab.
