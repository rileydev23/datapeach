# Arquitectura de DataPeach

<!-- peach-hijo: ARQUITECTURA v1.0 -->
Cliente de bases de datos (el diseño está en [DISENO.md](DISENO.md)).
**Patrón:** MVVM con CommunityToolkit.Mvvm, armado directo (como TextPeach y RestPeach). Todo lo que no es
pantalla vive en `DataPeach.Core`, sin Avalonia y con tests. Los servicios marcados como *comunes* (`PeachApps`,
`SingleInstance`, `Loc`…) son iguales en las demás apps Peach; si los cambias, avísalo en el *pull request*.

```
src/DataPeach.Core                     (sin Avalonia)
  Models ── ConnectionEntry · DbKind · ConnectionMode
  Store · ConnectionGrouping · Secrets · Loc + Strings
  Drivers/  DriverCatalog · DriverManager (descarga y carga) · NuGetPackage (nuspec, carpetas, versiones)
  Db/       Dialect (Postgres · MySql · Sqlite · SqlServer · Oracle · DuckDb · Sybase) · DialectRoutines ·
            DbSession · Schema
  History/  DeleteHistory · DeleteRecord · StoredValue · DeleteInverse · DeletePlan
  Sql/      SqlTokenizer · SqlStatements · SqlCompletion · SqlKeywords
  Staging/  StagingArea
  Data/     ValueText · Exporters

src/DataPeach                          (Avalonia)
  ViewModels ── MainWindowViewModel ─┬─ HomeViewModel (conexiones y grupos)
                                     └─ ConnectionViewModel (una pestaña por conexión)
                                          ├─ ConsoleViewModel (una por .sql) ── ResultViewModel ×N
                                          └─ TableDataViewModel (una por tabla) ── ResultViewModel
  Controls ── ResultGrid · SqlColorizer · Palette
  Views    ── MainWindow · HomeView · ConnectionView · ConsoleView · TableDataView · Dialogs ·
              ConnectionDialog · DriversDialog · GridMenus · MenuPresenter · TabStrip
  Services ── AppServices (Store + Secrets + DriverManager + DeleteHistory) · PeachApps · SingleInstance · LExtension
```

## Drivers

DataPeach no referencia ningún driver. `DriverCatalog` dice qué paquete de nuget.org usa cada motor
(Microsoft.Data.Sqlite, Npgsql, MySqlConnector, Microsoft.Data.SqlClient, Oracle.ManagedDataAccess.Core,
AdoNetCore.AseClient, DuckDB.NET.Data.Full), la versión recomendada y el tipo de su `DbProviderFactory`. MariaDB usa el de MySQL y
CockroachDB el de PostgreSQL.

**Instalar** (`DriverManager.InstallAsync`): descarga el `.nupkg` de
`api.nuget.org/v3-flatcontainer`, lee su `.nuspec` y recorre las dependencias (grupo del mejor *framework* para
.NET 10; si dos piden la misma, gana la versión más alta). Salta lo que ya trae el runtime (lo que el contexto
por defecto puede cargar). De cada paquete copia las DLL del mejor `lib/<tfm>/` (o `runtimes/<rid>/lib/`) y las
nativas de `runtimes/<rid>/native/` a `drivers/<motor>.tmp/`, escribe `driver.json` y, si todo llegó, lo mueve a
`drivers/<motor>/`. La carpeta temporal se borra siempre. `NuGetPackage` es puro y está testeado. El mejor
framework sale de `NuGetPackage.Rank`: `netX.0` > `netcoreapp3.1` > … > `netcoreapp2.0` > `netstandard2.1` >
`netstandard2.0` > `netcoreapp1.x`; AdoNetCore.AseClient trae `netcoreapp1.0` y `netcoreapp2.2`, y solo el segundo
tiene su `DbProviderFactory`.

**Cargar** (`DriverManager.Load`): un `AssemblyLoadContext` por driver (`DriverLoadContext`) que resuelve primero
desde la app (así `System.Data.Common` es el mismo tipo en los dos lados) y después desde la carpeta del driver;
las librerías nativas (`e_sqlite3`, `duckdb`, el SNI de SqlClient) también salen de ahí. Devuelve `Factory.Instance`. Un driver cargado no se
reemplaza hasta reiniciar.

Por esto la app se publica con JIT (ReadyToRun) y no con NativeAOT: AOT no puede cargar ensamblados descargados.

Tampoco usa `InvariantGlobalization` (a diferencia del resto de la familia): Microsoft.Data.SqlClient se niega a
abrir conexiones sin ICU. `Program.Main` fija la cultura invariante, así números y fechas se escriben igual que
antes; medido, cuesta ~15 ms de arranque y nada de memoria. En Linux la app necesita `libicu`.

## Sesión y dialectos

`DbSession` envuelve una conexión de un motor:

- **Conexión principal** para las consolas y el staging, con un `SemaphoreSlim` (una operación a la vez), así
  `SET`, tablas temporales y variables de sesión se comportan como en una terminal.
- **Conexiones cortas** para leer la estructura y el detalle, para no esperar a una consulta larga.
- `RunAsync(sql, IResultSink, maxRows, ct)`: ejecuta una sentencia y entrega columnas y filas por tandas (las
  primeras a los ~30 ms o 200 filas, después cada 100 ms); corta en `maxRows` y lo informa. Cancelar el token
  llama a `DbCommand.Cancel()`, y una sentencia cancelada siempre termina en error aunque el servidor la cierre
  sin uno (MySQL: `KILL QUERY` hace que `SLEEP()` devuelva 1). En DuckDB las escrituras sin `RETURNING` van por
  `ExecuteNonQuery`, porque su lector devuelve el conteo como una fila `Count`.
- `ApplyAsync(commands, ct, captures, captured)`: todo en una transacción; un `UPDATE`/`DELETE` que no toca filas
  aborta con `StaleRowException` y deshace todo. Un `DELETE` del staging trae su `SELECT` de captura (la misma clave)
  y su fila se lee justo antes, en la misma transacción.
- `DeleteWithBackupAsync(sql, backupSql, transactional, maxRows, sink, ct)`: un `DELETE` de la consola lee antes sus
  filas con `DeletePlan.SelectSql` en la misma transacción (si el usuario abrió una a mano, el driver no deja otra y
  va sin envoltorio); un `TRUNCATE` se ejecuta justo después de leer, porque en varios motores confirma solo.
- Solo lectura: `Mode=ReadOnly` en SQLite, `ACCESS_MODE=READ_ONLY` en DuckDB y `SET SESSION … READ ONLY` en
  PostgreSQL y MySQL al abrir. SQL Server, Oracle y SAP ASE no tienen sesión de solo lectura: ahí solo frena la app
  (`SqlStatements.IsWrite`).

**Varias bases por servidor.** En SQL Server, PostgreSQL, MySQL/MariaDB y SAP ASE (`Dialect.HasDatabases`) la
conexión no se limita a una base: `LoadDatabasesAsync` lista las del servidor (`sys.databases` con
`HAS_DBACCESS`, `pg_database` con `datallowconn`, `information_schema.SCHEMATA`, `master..sysdatabases`), marca las
de sistema y dice en cuál cayó la conexión. Cada base tiene su propia `DbSession` (`ForDatabase`: la misma entrada
con otro campo de base), creada al primer uso: así funciona igual en PostgreSQL, donde una conexión no puede leer
otra base, y el SQL generado no lleva el nombre de la base. `ConnectionViewModel` guarda un `SchemaSnapshot` por
base, leído al abrirla (`Stamp` marca tablas y rutinas con su base; `FullName` = `base.esquema.tabla`). Las
consolas recuerdan su base (`ConnectionSession.ConsoleDatabases`); un `USE` (SQL Server, MySQL, ASE) mueve la consola
y devuelve la conexión compartida a su base. El staging aplica una transacción por base, en orden, y quita del panel
lo ya aplicado si una falla. El historial de borrados guarda la base para restaurar en ella.

`Dialect` es lo único específico de cada motor: comillas, literales (para la vista previa), parámetros (`@pN`; en
PostgreSQL con `CAST(@pN AS <tipo de la columna>)` para no depender de cómo el driver adivina el tipo; `:pN` en
Oracle, `$pN` en DuckDB), el `SELECT` de una tabla (`LIMIT`, `TOP` en SQL Server, `FETCH FIRST` en Oracle), cadena
de conexión, la consulta de estructura (una sola: `pg_catalog`, `information_schema`, `pragma_table_info`,
`sys.*`, `all_*`, `duckdb_columns()`) y la del detalle (índices y claves foráneas). Un motor nuevo es un `Dialect`
nuevo, una fila en `DriverCatalog`, un `DbKind` y una clase en `tests/DataPeach.Tests/Engines/`.

Particularidades que ya resuelve el dialecto:

- **PostgreSQL / CockroachDB:** Cockroach devuelve `relispartition` nulo y agrega a las tablas sin clave una
  columna oculta `rowid` como clave; se detecta con `version()` y esas columnas se quitan (si no, la tabla
  parecería editable por una clave que la grilla nunca ve).
- **SQL Server:** sin usuario usa `Integrated Security`. `SqlConnectionStringBuilder.ContainsKey` es verdadero para
  toda clave que conoce, así que "¿lo puso el usuario?" se pregunta a sus opciones (`Dialect.HasOption`).
- **Oracle:** los nombres sin comillas se guardan en mayúsculas (`Dialect.Name` solo deja sin comillas los que ya lo
  están). `DATA_DEFAULT` es `LONG` y no se lee: los valores por defecto no se muestran. `DBMS_SESSION.SLEEP` no
  se puede cancelar; una consulta sí.
- **MySQL / MariaDB:** `Allow User Variables=true` (si el usuario no lo fija): sin eso MySqlConnector toma todo
  `@nombre` por un parámetro y fallan `SET @x = 1` y los OUT de los procedimientos.
- **SAP ASE (Sybase):** catálogo viejo: `sysobjects`, `syscolumns` (`status & 8` admite NULL, `& 128` identidad),
  `sysindexes` (`status & 2048` clave primaria, `& 2` único; las columnas con `index_col` del 1 al 16) y
  `sysreferences` (`fokeyN`/`refkeyN` con `col_name`). Los dueños son los esquemas; la vista de sistema
  `sysquerymetrics` se deja fuera. `object_id` no acepta corchetes, así que el detalle filtra por nombre y dueño.
  Corchetes y `TOP n` funcionan desde ASE 15. Cadena con `Charset=utf8` para no perder acentos.
- **DuckDB:** archivo como SQLite; `DbKinds.DetectFile` reconoce el motor por la cabecera (`SQLite format 3` o
  `DUCK` en el byte 8) abriendo el archivo compartido, porque DuckDB lo deja abierto. STRUCT y MAP llegan como
  diccionarios (`{clave: valor}` en la grilla).

`SqlStatements.Split` corta en `;` y además en una línea `GO` (SQL Server, SAP ASE) o `/` (Oracle). Un
procedimiento, función o trigger de SQL Server o SAP ASE y un bloque PL/SQL de Oracle (`BEGIN`, `DECLARE`,
`CREATE [OR REPLACE] PROCEDURE…`) conservan sus `;` hasta esa línea. En MySQL una línea `DELIMITER $$` cambia el
separador, como en su cliente; por eso `$$…$$` solo es una cadena en PostgreSQL y DuckDB.

## Procedimientos y funciones

`DialectRoutines.cs` (parcial de cada dialecto) da cuatro cosas por motor: `LoadRoutinesAsync` (lista con sus
parámetros y lo que devuelven, en la misma carga que la estructura; si falla, la estructura igual se muestra),
`LoadRoutineSourceAsync` (un script que la recrea), `CallSql` (la llamada, con los valores ya escritos como
literales por `ArgLiteral`) y `DropSql`.

| Motor | Lista | Código | Llamada |
|---|---|---|---|
| PostgreSQL | `pg_proc` (`prokind` f/p, sin las de extensiones), argumentos con `proallargtypes`/`proargmodes` | `pg_get_functiondef` (un `CREATE` de Cockroach pasa a `CREATE OR REPLACE`) | `CALL p(…, NULL por cada OUT)` · `SELECT * FROM f(…)` |
| MySQL | `information_schema.ROUTINES` y `PARAMETERS` | `SHOW CREATE` entre `DELIMITER $$` | OUT por variables de sesión: `SET @x…; CALL p(…, @x); SELECT @x` |
| SQL Server | `sys.objects` (P, FN, IF, TF…) y `sys.parameters` | `OBJECT_DEFINITION` como `CREATE OR ALTER` | `DECLARE`, `EXEC … OUTPUT`, `SELECT`; las escalares con su esquema |
| Oracle | `all_objects` y `all_arguments` (posición 0 = retorno) | `all_source` + `/` | `SELECT f(…) FROM dual` o un bloque `BEGIN … END;` |
| SAP ASE | `sysobjects` P y SF; `syscolumns` (`status2 & 2` salida; "Return Type" es el retorno) | `syscomments` tras un `DROP` condicional | como SQL Server; funciones siempre con dueño |
| DuckDB | `duckdb_functions()`: macros y macros de tabla | `CREATE OR REPLACE MACRO` | `SELECT m(…)` / `SELECT * FROM m(…)` |

Ejecutar desde el árbol escribe la llamada en la consola y la corre ahí: la seguridad (producción, solo lectura,
borrados) es la de cualquier sentencia.

## Historial de borrados

`DeletePlan.For` reconoce los `DELETE`/`TRUNCATE` de una consola y arma el `SELECT *` con la misma tabla, alias y
`WHERE` (y `ORDER BY`/`LIMIT` de MySQL) y el `COUNT` de la confirmación; lo demás (`USING`, `JOIN`, `RETURNING`,
`OUTPUT`, `TOP`, CTE, `MERGE`) queda sin respaldo con el motivo. Las palabras dentro de paréntesis (subconsultas)
no cuentan. `ConnectionViewModel.ConfirmDeletesAsync` pregunta siempre; la confirmación de producción de
`ConfirmWritesAsync` deja fuera los borrados para no preguntar dos veces.

`DeleteRecord.Create` guarda columnas, tipos declarados (PostgreSQL castea con ellos al restaurar), columnas de
identidad y las filas como texto con etiqueta de tipo (`StoredValue`: `i:12`, `d:2024-…`, `x:CAFE`…), así vuelven
como el mismo tipo .NET. `DeleteHistory` es un JSON por borrado en `history/<id de conexión>/`, purgado por
antigüedad al abrir la app y al cambiar la retención (`AppState.DeleteHistoryDays`, 30 por defecto, 0 = siempre).
`DeleteInverse` genera el script (literales, para leer o editar) y los comandos (parámetros) que restauran: un
`INSERT` por fila, con `SET IDENTITY_INSERT` alrededor en SQL Server y SAP ASE si hay columnas de identidad.
Restaurar usa `ApplyAsync`: todo o nada.

## Consola

`ConsoleViewModel` tiene el `TextDocument` (AvaloniaEdit) de un `.sql` que se guarda solo. `Ctrl+Enter` toma la
selección o `SqlStatements.At(texto, cursor)`; `SqlStatements.Split` separa por `;` fuera de cadenas y
comentarios (incluye `$$…$$`). Antes de ejecutar, `ConnectionViewModel.ConfirmWritesAsync` aplica el cuidado de
la conexión con `SqlStatements.IsWrite`. Cada sentencia corre en `Task.Run`; un `ResultCollector` convierte lo
que llega en `ResultViewModel`s en el hilo de la interfaz, en orden. Si la sentencia fue DDL se vuelve a leer la
estructura.

`ConsoleView` se recicla entre consolas: en `OnDataContextChanged` cambia el documento, el coloreado
(`SqlColorizer`, que retokeniza el documento entero en cada cambio y pinta por línea) y el acceso al cursor.
`SqlCompletion.Suggest` arma el autocompletado desde el `SchemaSnapshot` cacheado: palabra bajo el cursor,
`alias.`, palabra anterior (FROM/JOIN → tablas) y las tablas de la sentencia con sus alias.

## Tabla y staging

`TableDataViewModel` lee `SELECT * … WHERE … ORDER BY … LIMIT n+1` (la fila de más dice si hay más) y crea un
`ResultViewModel` con un `TableEditContext` (tabla, staging de la conexión e índices de la clave primaria en el
resultado) si la tabla se puede editar.

`ResultViewModel` es la fuente de `ResultGrid` (`IGridSource`): para cada celda devuelve el valor editado si lo
hay y su estado (editada, nueva, a borrar); las filas nuevas son las inserciones pendientes de esa tabla, al
final. `SetValue` convierte el texto al tipo de la columna (`ValueText.TryParse`) y lo pasa al staging.

`StagingArea` (puro) guarda los cambios en orden, junta las ediciones de una fila en un `UPDATE`, convierte
editar una fila nueva en cambiar su `INSERT`, descarta las ediciones de una fila que se borra, y genera para cada
cambio el SQL que se ejecuta (con parámetros) y el que se muestra (con literales). La clave de una fila es el
valor de su clave primaria leído de la base, nunca su posición.

## Rejilla

`ResultGrid` es un `Control` con `Render` propio: calcula las filas y columnas visibles desde el desplazamiento y
dibuja solo esas, con `FormattedText` cacheado por texto y color. Tiene sus propias `ScrollBar` (4 px), un
`TextBox` superpuesto para editar y el menú lo arma el dueño (`MenuRequested` → `GridMenus`). Las columnas se
dimensionan con las primeras 200 filas y no se achican mientras llegan más.

## Estado

| Archivo | Qué |
|---|---|
| `state.json` (`Store`) | Conexiones (sin contraseña), grupos, pestañas abiertas y consolas de cada una, ventana, idioma, tope de filas. |
| `secrets.json` (`Secrets`) | Contraseñas por id de conexión, cifradas con DPAPI (Windows) o con permisos 600. |
| `drivers/<motor>/` | DLLs del driver y `driver.json` (paquete, versión, paquetes descargados, tamaño). |
| `queries/<id>/*.sql` o `<proyecto>/.peach/queries/*.sql` | Consolas. |
| `history/<id>/*.json` (`DeleteHistory`) | Borrados con sus filas, en texto plano como las consolas. |

## Idiomas

`Loc` y `Strings` viven en Core (los mensajes de los drivers y del staging también se traducen); la extensión
`{l:L clave}` de XAML está en la app (`Services/LExtension.cs`). `Loc.Init` corre antes de crear cualquier vista.

## Desarrollo

```
dev.cmd                     compila Debug y ejecuta
dotnet test DataPeach.slnx  tests (con DATAPEACH_NET_TESTS=1 también descarga y carga todos los drivers;
                            con DATAPEACH_ENGINE_TESTS=1 y tests/docker/compose.yml arriba, prueba cada motor)
build.cmd [x.y.z]           dist\DataPeach.exe portable (single-file, self-contained, ReadyToRun, sin compresión)
```

En Debug, `DATAPEACH_DEBUG_ACTIONS` ejecuta acciones separadas por `|` sobre la pestaña activa al iniciar, para
capturas sin simular teclas: `driver`, `sql:<texto>` (con `\n`), `run`, `table:<nombre>`, `expand:<nombre>`,
`staging`, `apply`, `newconn`, `drivers`, `about`, `open:<conexión>`, `wait:<ms>`. Con un `portable.txt` junto al
exe de Debug los datos quedan en `bin\Debug\net10.0\data\` y no tocan los tuyos.
