# Sistema de diseño de DataPeach

<!-- peach-hijo: DISENO v1.0 -->
> DataPeach comparte su sistema de diseño con las demás apps Peach (GitPeach, TextPeach, RestPeach): mismos
> colores, componentes, atajos y tono, para que pasar de una a otra no cueste nada. Este documento aplica esas
> reglas a DataPeach y agrega lo propio, marcado como **Propio de DataPeach**. Si cambias algo que también afecta
> a las otras apps (un color, un atajo común, un componente), dilo en el *pull request*: se cambia en todas.

> **Regla de oro:** DataPeach parece un IDE sobrio para **trabajar con datos**. Fondo gris carbón, texto gris
> claro, un solo azul de acento, y color solo cuando significa algo (motor, cambio pendiente, producción, error).
> Nada decorativo, y nada que toque la base de datos sin que lo veas antes.

Índice:

1. [Principios](#1-principios)
2. [Base técnica](#2-base-técnica)
3. [Color](#3-color)
4. [Tipografía](#4-tipografía)
5. [Espaciado, forma y bordes](#5-espaciado-forma-y-bordes)
6. [Anatomía de la ventana](#6-anatomía-de-la-ventana)
7. [Componentes](#7-componentes)
8. [Patrones de interacción](#8-patrones-de-interacción)
9. [Teclado](#9-teclado)
10. [Textos y tono](#10-textos-y-tono)
11. [Iconografía y marca](#11-iconografía-y-marca)
12. [Rendimiento](#12-rendimiento)
13. [Dónde está cada cosa en el código](#13-dónde-está-cada-cosa-en-el-código)

---

## 1. Principios

1. **Oscuro y denso, como un IDE.** Solo tema oscuro (`RequestedThemeVariant="Dark"`). Filas compactas, fuente
   base de 13 px, mucha información visible a la vez. No hay modo claro.
2. **El color es información.** El 90 % de la pantalla son grises. El azul de acento marca lo seleccionado y la
   acción principal. Verde, rojo, ámbar, morado… solo comunican estado o tipo, siempre con el mismo significado
   ([3.4](#34-colores-semánticos)), el mismo que en las otras apps.
3. **Una acción principal por zona.** En cada barra hay como máximo un botón `accent` (azul): **Ejecutar** en la
   consola, **Aplicar** en el staging, **Nueva conexión...** en la Home.
4. **Nada llega a la base sin verse antes (Propio de DataPeach).** Editar celdas, agregar o borrar filas no
   escribe nada: los cambios esperan en el **staging** como SQL exacto y se aplican juntos, en una transacción,
   con **Aplicar**. Una tabla sin clave primaria no se edita.
5. **Lo destructivo nunca es por defecto.** Descartar o quitar requieren un gesto explícito y, si no se puede
   deshacer, confirmación. "Quitar de la lista" dice que no toca la base de datos. Las conexiones de
   **producción** piden confirmación antes de cualquier sentencia que escriba; las de **solo lectura** no la
   ejecutan.
6. **Nada se mueve solo.** La ventana se abre donde la dejaste, invisible hasta estar colocada. Las listas se
   reemplazan de una vez. Los resultados llegan por tandas pero nunca mueven la selección ni el scroll.
7. **Todo se puede hacer con teclado.** Cada acción frecuente tiene atajo y todos se listan en F1
   ([9](#9-teclado)).
8. **Estado persistente.** Conexiones, grupos, pestañas, consolas abiertas, ventana e idioma se guardan solos.
   Las consolas son archivos `.sql` que se guardan mientras escribes.
9. **Inglés y español en la interfaz, inglés en el código.** Textos visibles en los dos idiomas
   ([10](#10-textos-y-tono)); clases, variables y comentarios en inglés.
10. **Sin nombres de terceros.** La interfaz nunca menciona ni lanza aplicaciones de terceros. Los paquetes de los
    drivers se nombran tal cual (Npgsql, MySqlConnector, Microsoft.Data.Sqlite) porque es lo que se descarga.
11. **Lo que solo se lee nunca parece editable.** Mensajes, SQL del staging y errores se muestran con
    `TextBlock`/`SelectableTextBlock`. `TextBox` es solo para escribir.
12. **Portable y autocontenido.** Un único `.exe` que funciona sin instalar y **sin drivers adentro**: cada driver
    se descarga cuando hace falta ([8.6](#86-drivers-propio-de-datapeach)). Con un `portable.txt` al lado, los
    datos (también los drivers) se guardan junto al exe. El instalador (`datapeach-windows-x64-setup.exe`, `install.sh`) es
    opcional: copia ese mismo exe a una carpeta del usuario, lo registra en el buscador del sistema y deja
    `installed.txt` al lado.
13. **Actualizar no interrumpe.** Una versión nueva solo se nota como un botón **↑ x.y.z** junto al **?**; nada
    se descarga sin confirmar en su popup (Actualizar y reiniciar / Ahora no / Omitir esta versión).

---

## 2. Base técnica

| Pieza | Elección | Notas |
|---|---|---|
| UI | **Avalonia 11.3** + `Avalonia.Themes.Fluent` | `RequestedThemeVariant="Dark"`, estilos propios encima (`Styles.axaml`). |
| Runtime | **.NET 10** (`net10.0`), C# `latest`, `Nullable` e `ImplicitUsings` | `InvariantGlobalization=false` (**diferencia con la familia**: el driver de SQL Server exige ICU; `Program.Main` fija la cultura invariante), `SatelliteResourceLanguages=en`. |
| MVVM | **CommunityToolkit.Mvvm 8.4** | `[ObservableProperty]` sobre campos `_camelCase`, `[RelayCommand]`. |
| Bindings | `AvaloniaUseCompiledBindingsByDefault=true` | Todo XAML declara `x:DataType`. |
| Render | Software (`Win32RenderingMode.Software`) | Se excluye `av_libglesv2.dll` al publicar (≈25 MB menos). |
| DPI | `app.manifest` con `PerMonitorV2` | |
| Editor SQL | **AvaloniaEdit 11.4** con un coloreado propio (`Controls/SqlEditor.cs`) | **Propio de DataPeach:** sin TextMate; el lexer de `DataPeach.Core/Sql` pinta con los colores de "Peach Dark" ([3.8](#38-resaltado-de-sql-peach-dark)). |
| Drivers | Paquetes ADO.NET oficiales descargados de nuget.org | Cargados aparte (`AssemblyLoadContext`); la app solo usa `DbProviderFactory`. |
| Publicación | `build.cmd [versión]` → single-file, self-contained, `win-x64`, ReadyToRun, **sin compresión** | Sin compresión arranca en ~240 ms y usa un tercio de memoria (el exe pesa ~120 MB). |
| Estado | `%APPDATA%\peachapps\DataPeach\` o `.\data\` con `portable.txt` | `state.json`, `secrets.json`, `drivers\`, `queries\`. |
| Tests | xUnit en `tests/DataPeach.Tests` | Lexer, sentencias, autocompletado, staging, exportadores, NuGet, sesión SQLite, idiomas; con `DATAPEACH_ENGINE_TESTS=1`, cada motor contra un servidor real (`tests/docker/compose.yml`). |

---

## 3. Color

Todos los valores son hexadecimales sRGB. En XAML se escriben en minúsculas (`#1e1f22`); en C# se usan desde
`Controls/Palette.cs`. Nunca inventes un gris nuevo: elige el más cercano de estas tablas.

### 3.1 Superficies (de más oscura a más clara)

| Token (`Palette.*`) | Hex | Uso en DataPeach |
|---|---|---|
| (título) | `#111214` | Barra de título propia, botón Conexiones. |
| `Chrome` | `#18191c` | Barra de pestañas de la ventana, barra de estado. |
| `Code` | `#1b1c1f` | Editor SQL, rejilla de resultados. |
| `Bg` | `#1e1f22` | Fondo de ventana; franja de pestañas de consolas y tablas; números de fila. |
| `Sidebar` | `#222327` | Árbol de estructura, panel de detalle. |
| `Panel` | `#26272b` | Toolbar, barra de la consola, filtro de la tabla, cabecera de la rejilla, staging, diálogos. |
| `Panel2` | `#2d2e33` | Botón normal, *hover* de filas, columna seleccionada en la cabecera. |
| `Border` | `#3a3c42` | **Todos** los bordes, separadores, `GridSplitter`, *hover* de botón. |

- Cada franja horizontal cambia de superficie y se separa con un borde de 1 px `#3a3c42`.
- Nunca blanco ni grises claros como fondo; el más claro permitido es `#3a3c42`.
- Línea actual del editor: `#232428`. Líneas de la rejilla: `#26272b` sobre `#1b1c1f`.

### 3.2 Texto

| Token | Hex | Uso |
|---|---|---|
| `Text` | `#d4d4d4` | Texto normal y valores de celdas. |
| `ContextFg` | `#c8c8c8` | Número de fila seleccionada. |
| `Dim` | `#8a8d93` | Rutas, tipos de columna, metadatos, *watermarks*, `NULL` (en cursiva). Clase `TextBlock.dim`. |
| `Gutter` | `#5d6066` | Números de línea y de fila. |
| Blanco | `#ffffff` | **Solo** sobre fondos de color: botón `accent`, pestaña o celda seleccionada. |

### 3.3 Acento y selección

| Uso | Hex |
|---|---|
| Acento (botón principal, cursor, celda actual) | `#4a90d9` |
| Acento *hover* | `#5aa0e9` |
| Seleccionado (filas, celdas, `ToggleButton:checked`, selección de texto) | `#33507a` |
| Seleccionado + *hover* | `#3a5a88` |
| Fila en *hover* | `#2d2e33` |

### 3.4 Colores semánticos

Significado fijo, igual en todas las apps Peach. Si un color ya significa algo, no lo uses para otra cosa.

| Nombre | Hex | Significado en DataPeach |
|---|---|---|
| Verde | `#6cc070` | Fila nueva en el staging (`+`), driver instalado, conexión probada, consola ejecutando. |
| Rojo | `#e0645a` | Error de una sentencia, fila a borrar (`−`), "archivo no encontrado". |
| Ámbar | `#e0a24a` | Atención: celda editada (`~`), cambios pendientes, **producción**, clave primaria (`⚿`), favorito ★. |
| Ámbar claro | `#f0c890` | Texto sobre `#5a4318` (banner de producción, celda editada, error de conexión). |
| Azul | `#4a90d9` | Columnas en el árbol (`c`), PostgreSQL. |
| Morado | `#9a7ae0` | Esquemas (`S`). |
| Texto | `#d4d4d4` | Bases de datos del servidor (`D`); carpetas del árbol (`▤`) en `dim`. |
| Teal | `#4ac7c0` | Tablas y vistas (`T`, `V`), claves foráneas, SQLite. |
| Rosa | `#d96a9b` | Código: palabras clave SQL, consolas (`›_`), procedimientos y funciones en el árbol (`P`, `F`). |
| Oliva | `#c8c84a` | Parámetros (`:id`, `$1`, `@p`). |

### 3.5 Banners

Todo el ancho, debajo de la toolbar de la conexión, `Padding="10,5"`, texto SemiBold, botones a la derecha.

| Tipo | Fondo | Cuándo |
|---|---|---|
| Atención | `#5a4318` (texto `#f0c890`, botones con borde `#8a6d1f`) | Conexión de **producción**; error al conectar, con **Reintentar**. |
| Información | `#25364d` | Conexión de **solo lectura**; falta el driver, con **Descargar driver** (`accent`). |

### 3.6 Staging y rejilla (Propio de DataPeach)

Los mismos colores que los diffs de la familia, porque un cambio pendiente es un diff contra la base.

| Estado de una celda o fila | Fondo | Texto |
|---|---|---|
| Celda editada | `#5a4318` | `#f0c890` |
| Fila nueva | `#1d3a2a` | `#a6e3b4` |
| Fila a borrar | `#431f22` | `#f4a6a6`, tachada |
| `NULL` | — | `Dim`, cursiva |
| Selección | `#33507a` | blanco |
| Celda actual | borde 1 px `#4a90d9` | — |

En el panel de staging cada sentencia lleva su letra: `~` ámbar (UPDATE), `+` verde (INSERT), `−` rojo (DELETE).

### 3.7 Color por motor

Cada motor es una categoría de la serie de la familia: **PostgreSQL** azul `#4a90d9`, **MySQL** ámbar
`#e0a24a`, **SQLite** teal `#4ac7c0`, **SQL Server** morado `#9a7ae0`, **Oracle** rojo `#e0645a`, **DuckDB** oliva
`#c8c84a`, **SAP ASE** verde `#6cc070`. MariaDB y CockroachDB se conectan como MySQL y PostgreSQL y llevan su color. Se muestra como un punto `●` de 9–10 px en la pestaña, la barra de estado,
la Home y el diálogo de drivers, y como texto en la Home y la toolbar (motor y versión del servidor).

### 3.8 Resaltado de SQL "Peach Dark"

Los colores del tema `peach-dark.json` de la familia, aplicados por `SqlColorizer`:

| Token | Hex | Estilo |
|---|---|---|
| Texto por defecto | `#d4d4d4` sobre `#1b1c1f` | |
| Comentario (`--`, `/* */`, `#` en MySQL) | `#6f737a` | cursiva |
| Palabra clave | `#d96a9b` | |
| Operador | `#a9acb2` | |
| Puntuación | `#8a8d93` | |
| Cadena (`'…'`, `$$…$$`) | `#8fcf94` | |
| Número | `#e0a24a` | |
| Identificador entre comillas | `#a8c8e8` | |
| Función (identificador seguido de `(`) | `#7db4ea` | |
| Parámetro | `#c8c84a` | |

Editor: selección `#33507a`, línea actual `#232428`, cursor `#4a90d9`, números de línea `#5d6066`.

---

## 4. Tipografía

**UI:** `Segoe UI`. **Monoespaciada:** `Cascadia Mono, Consolas, Courier New` (clase `TextBlock.mono`,
`SelectableTextBlock.mono`, `TextBox.mono`). Celdas, editor, SQL del staging, filtros WHERE/ORDER BY y tipos de
columna van en monoespaciada.

| Tamaño | Uso |
|---|---|
| 9–10 | Glifos `●`, ✕ de pestañas, etiquetas `PROD` / `SL`. |
| 11 | Tipos de columna, SQL del staging, letras del árbol, metadatos. |
| 12 | Árbol, celdas, cabecera de la rejilla, pestañas de consolas, resumen de resultados. |
| **13** | **Base** de la interfaz y del editor SQL. |
| 14 | Nombre de la conexión en la Home. |
| 16 | Título de un estado vacío. |
| 18 | Nombre de la app en "Acerca de". |

Pesos: `Normal`; **SemiBold** para títulos de sección, nombres principales y cabeceras de columna; **Bold** solo
para letras de tipo (`T`, `V`, `S`, `c`). Cursiva para vistas, `NULL`, notas y comentarios.

---

## 5. Espaciado, forma y bordes

Unidad base de 2 px; valores habituales 2, 4, 6, 8, 10, 12, 14, 18.

| Elemento | Medida |
|---|---|
| Toolbar | `Padding="8,6"` (conexión) o `"10"` (Home); botones con `Spacing="4"`. |
| Separador vertical en toolbar | `Border Width="1" Background="#3a3c42" Margin="6,2"`. |
| Barra de estado | `Padding="10,3"`. |
| Fila de la Home | `Margin="10,8"`, columnas `28,*,Auto`. |
| Fila del árbol | 22 px, `+14` px por nivel. |
| Rejilla | Filas de 22 px, cabecera de 26 px, celdas con 8 px de margen; columnas de 44 a 360 px al abrir. |
| Diálogo | `Margin="18"`, `Spacing="12"`–`"16"`; botones `MinWidth=84`, `Padding="12,5"`, a la derecha, el principal primero. |
| Botón | `CornerRadius=3`, `Padding="10,4"`, borde 1 px. `flat`: sin borde, `Padding="6,2"`. |
| Etiqueta de entorno (`Border.envtag`) | `CornerRadius=3`, `Padding="5,0"`, 10 px SemiBold. |
| `GridSplitter` | 4 px, `#3a3c42`. |

Esquinas: 3 px controles, 4 px tarjetas, 0 en segmentos y pestañas. Sin sombras ni degradados (salvo el logo).

---

## 6. Anatomía de la ventana

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 🫐 DataPeach  tienda · C:\datos\tienda.db                    ─   ☐   ✕      │ título     #111214 (34 px)
├──────────────────────────────────────────────────────────────────────────────┤
│▌Conexiones▐│ ● tienda ✕ │ ● ventas PROD ✕ │                               ? │ pestañas   #18191c
├──────────────────────────────────────────────────────────────────────────────┤
│ Nueva consola  Abrir consola... │ Abrir tabla  Refrescar      PostgreSQL 16.4 │ toolbar   #26272b
├──────────────────────────────────────────────────────────────────────────────┤
│ (banner: producción / solo lectura / falta el driver / error al conectar)    │
├──────────────┬┬───────────────────────────────────────────┬┬─────────────────┤
│ Estructura   ││ ›_ console ✕ │ T clientes ✕               ││ Detalle         │
│ [Filtrar…]   ││ ▶ Ejecutar  Ejecutar todo  Cancelar  2 de 2 ││ clientes  tabla │
│ ▾ S public   ││ editor SQL                                 ││ ⚿ id   integer  │
│   ▸ T pedidos││────────────────────────────────────────────││ Índices, FK     │
│   ▾ T clientes│ Resultado 1 │ Resultado 2   6 filas · 35 ms ││─────────────────│
│     ⚿ id     ││ rejilla de resultados                      ││ 3 cambios pend. │
│ #222327      ││ #1b1c1f                                    ││ ~ UPDATE …      │
│              ││                                            ││ [Aplicar] [Desc]│
├──────────────┴┴───────────────────────────────────────────┴┴─────────────────┤
│ ● PostgreSQL   Aplicado: 3 filas en 9 ms                   3 cambios pendientes│ estado #18191c
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Barra de título propia** (34 px, `#111214`): icono 18 px, "DataPeach" SemiBold y, en `dim` 12 px, la
  conexión y a dónde apunta. Botones de ventana de 46×34 dibujados con `Path` (cerrar en *hover* rojo).
- **Barra de pestañas** (`#18191c`, alto mínimo 36 px): botón fijo **Conexiones** (`Button.home`), una pestaña
  por conexión con el punto del motor y la etiqueta `PROD` / `SL` si corresponde, y `?` (F1) a la derecha.
- **Toolbar** (`#26272b`): Nueva consola, Abrir consola..., Abrir tabla, Refrescar; a la derecha el motor y la
  versión del servidor en el color del motor.
- **Cuerpo** en tres columnas con `GridSplitter` de 4 px: estructura 260 px (mín. 160), consolas y tablas
  (mín. 360), detalle y staging 300 px (mín. 200).
- **Barra de estado**: punto y nombre del motor, último aviso; a la derecha "n cambios pendientes" en ámbar
  (clic: lleva al panel de staging) y el progreso indeterminado.

**Colocación de la ventana** (`App.PlaceWindow`): la de la familia, 1200×760 DIP por defecto.

---

## 7. Componentes

Los estilos viven en `src/DataPeach/Styles.axaml`. Usa clases, no colores sueltos (salvo fondos de franjas).

### 7.1 Botones

| Clase | Aspecto | Cuándo |
|---|---|---|
| (ninguna) | `#2d2e33`, borde `#3a3c42`, *hover* `#3a3c42` | Acciones normales. |
| `accent` | `#4a90d9`, texto blanco | Nueva conexión, ▶ Ejecutar, Aplicar, Guardar, Descargar driver. Una por zona. |
| `flat` | transparente, sin borde | Secundarias: ✕, `⋯`, ↶, "Copiar SQL", "1.000 filas más". |
| `seg` (+ `active`, `error`) | subrayado de 2 px | Resultado 1 / Resultado 2…; en rojo si la sentencia falló. |
| `mode left/mid/right` (+ `active`) | botones pegados | Motor (SQLite \| PostgreSQL \| MySQL \| SQL Server \| Oracle \| DuckDB), cuidado (Normal \| Producción \| Solo lectura), idioma. |
| `ToggleButton` | marcado `#33507a` | ★ Favoritos. |

Cursor de mano en todo lo que se pulsa; flecha en filas, árbol, pestañas y rejilla. Etiquetas cortas; si abre
un diálogo, termina en `...`. Botones de un glifo (`✕`, `⋯`, `↶`) siempre con `ToolTip.Tip`.

### 7.2 Campos de texto

`TextBox` con fondo `#26272b` y borde `#3a3c42`; *watermark* con un ejemplo real
("id > 100 AND name LIKE 'a%'"). El filtro del árbol: `Esc` limpia, `↓` pasa a la lista, `Enter` abre la primera
tabla. WHERE y ORDER BY: `Enter` recarga.
Un campo que se puede dejar vacío lo dice en la etiqueta: "Puerto (opcional)", y el *watermark* explica qué pasa si queda vacío.

### 7.3 Árbol de estructura (Propio de DataPeach)

Una lista plana virtualizada (rápida con miles de tablas), como el explorador de SQL Server: base de datos →
esquema → carpetas (Tablas, Vistas, Procedimientos, Funciones) → objeto → columnas. Filas de 22 px,
12 px. Chevron `▸`/`▾` clicable, letra de tipo en su color (`D` texto, `▤` `dim`, `S` morado, `T`/`V` teal, `c`
azul, `⚿` ámbar para la clave primaria, `P`/`F` rosa), nombre (vistas en cursiva) y a la derecha, en `dim` 11 px y
hasta 120 px, el tipo de la columna, "sin clave", el número de objetos de una carpeta o la firma de la rutina
("(id int, INOUT n int)"; si es larga, "3 parámetros").

- **Bases de datos** (SQL Server, PostgreSQL, MySQL/MariaDB, SAP ASE): todas las del servidor en la raíz, y las
  suyas (master, tempdb, model, msdb, template1, mysql, information_schema, sybsystemprocs…) en una carpeta
  **Sistema** cerrada al final. La base de la conexión va en SemiBold y se abre sola (si es de sistema, solo cuando
  la conexión la nombra). Cada base se lee la primera vez que se abre ("Cargando…" debajo mientras tanto, o el
  error en cursiva). Oracle, SQLite y DuckDB no tienen este nivel.
- **Esquemas:** con uno solo, las carpetas van directo bajo la base; con varios, el esquema por defecto se abre
  solo. Un esquema sin objetos dice "Sin tablas".
- **Carpetas:** solo las que tienen algo; Tablas empieza abierta y las demás cerradas.

Menú de una base: Nueva consola en esta base · Refrescar · Copiar nombre. El filtro muestra en una lista plana
los objetos que coinciden en las bases ya leídas, con su nombre completo (`ventas.dbo.clientes`). Doble clic, `Enter` o F4
abren los datos; menú: Abrir datos · SELECT en la consola · COUNT en la consola · Copiar nombre · Copiar lista
de columnas. En una rutina, doble clic o `Enter` abre su código en una consola propia (un script que la recrea:
`CREATE OR REPLACE`, `CREATE OR ALTER`, `DELIMITER $$` o un `DROP` previo según el motor) y el menú ofrece
Ejecutar… (un `Form` con un campo por parámetro de entrada; vacío es NULL y `=` escribe una expresión; la llamada
va a la consola actual y corre ahí, con el cuidado de siempre) · Abrir código · DROP en la consola · Copiar nombre.
El panel de detalle muestra sus parámetros (los de salida en ámbar) y lo que devuelve.

### 7.4 Pestañas

- **De conexión** (ventana): ver [6](#6-anatomía-de-la-ventana). `Ctrl+1` selecciona Conexiones.
- **De consola y tabla** (`ListBox.doctabs tabstrip`): `›_` rosa para consolas, `T` teal para tablas; un `●`
  verde mientras la consola ejecuta. Menú: Renombrar… y Mostrar el archivo (consolas), Cerrar las demás, Cerrar.

### 7.5 Consola SQL (Propio de DataPeach)

- Barra (`#26272b`): **▶ Ejecutar** (`accent`, `Ctrl+Enter`), Ejecutar todo (`Ctrl+Shift+Enter`), Cancelar (`Esc`);
  en servidores con varias bases, el botón `D base ▾` con la base donde corre la consola (un menú con las bases;
  más de 15, el selector con filtro); a la derecha el resumen de la última ejecución. Un `USE otra` ejecutado en la
  consola la mueve a esa base. Abrir datos, SELECT, código o la llamada de una rutina van a una consola de su base.
- `Ctrl+Enter` ejecuta la selección o, sin selección, la sentencia donde está el cursor (o la anterior si el
  cursor quedó justo después del `;`). "Ejecutar todo" corre todas en orden y se detiene en el primer error.
- Debajo, separados por un `GridSplitter`, los resultados: un segmento por resultado (rojo si falló), el resumen
  ("6 filas · 35 ms", "12 filas cambiadas", "(limitado)") y `⋯` para copiar o guardar todo.
- Un error se muestra como texto rojo monoespaciado seleccionable, nunca como diálogo.
- Autocompletado: se abre solo tras `alias.` o la segunda letra de una palabra, y con `Ctrl+Space`. Letra de tipo
  en su color (`c` columna, `T` tabla, `S` esquema, `ƒ` función, `k` palabra clave) y el tipo o la tabla en
  `dim`. Primero las columnas de las tablas que nombra la sentencia.

### 7.6 Rejilla de resultados (Propio de DataPeach)

Dibujada a mano (`Controls/ResultGrid.cs`): solo pinta lo visible, así que 50.000 filas cuestan lo mismo que 50.

- Cabecera fija de 26 px (`#26272b`, SemiBold, `⚿` ámbar en las columnas de la clave), números de fila a la
  izquierda en `#5d6066`, barras de desplazamiento de 4 px propias.
- Valores en monoespaciada de 12 px; números alineados a la derecha; `NULL` en `dim` cursiva; saltos de línea
  como `⏎`; binarios como `0x…`.
- Selección por rango (clic, arrastrar, `Shift`), filas enteras desde los números, columna entera desde la
  cabecera; borde azul en la celda actual. Columnas redimensionables arrastrando el borde de la cabecera.
- `Ctrl+C` copia como TSV (lo que pegan las hojas de cálculo); `Ctrl+Shift+C` con encabezados. Menú: Copiar ·
  Copiar con encabezados · Copiar selección como ▸ · Copiar todo como ▸ · Guardar todo como ▸ y, si se puede
  editar, Editar celda · Poner NULL · Agregar fila · Borrar filas.
- Formatos de exportación: CSV, TSV, JSON, tabla Markdown, sentencias INSERT, interfaz TypeScript.

### 7.7 Tabla de datos (Propio de DataPeach)

- Franja de filtro: `WHERE` y `ORDER BY` en `dim` monoespaciado delante de cada campo, **Aplicar**, y a la derecha
  `+ Fila`, Abrir en consola y `⋯`.
- Lee de a 1.000 filas ("1.000 filas más" al pie, con "(hay más)" en el resumen); nunca cuenta la tabla entera.
- Si no se puede editar, una línea `dim` en cursiva dice por qué (vista, sin clave primaria, conexión de solo
  lectura).
- Editar: doble clic, `F2`, `Enter` o simplemente escribir sobre la celda. `Enter` confirma y baja, `Tab` pasa a
  la siguiente, `Esc` cancela. Un valor que no corresponde al tipo de la columna se rechaza con un aviso.

### 7.8 Staging (Propio de DataPeach)

Panel al pie de la columna derecha (`#26272b`, borde superior), visible solo con cambios pendientes:
título "n cambios pendientes" en ámbar, **Copiar SQL** (`flat`), la lista de sentencias tal como se van a
ejecutar (monoespaciada 11 px, seleccionable, con su letra de color y `↶` para deshacer cada una) y al pie
**Aplicar** (`accent`, `Ctrl+Shift+K`) y Descartar. Varias ediciones de una fila son un solo `UPDATE`.

Aplicar ejecuta todo en una transacción. Si una sentencia falla (una clave foránea, una fila que ya no existe)
se deshace todo, los cambios siguen pendientes y un `Message` explica qué pasó. Si hay filas a borrar, Aplicar
pregunta siempre (en cualquier conexión) cuántas se borran por tabla y cuánto tiempo se guardan; cada `DELETE` lee
antes su fila en la misma transacción para el historial de borrados ([8.8](#88-historial-de-borrados-propio-de-datapeach)).

### 7.9 Etiquetas de entorno (Propio de DataPeach)

`Border.envtag`: **Producción** con fondo `#5a4318`, borde `#8a6d1f` y texto `#f0c890` (`PROD` en la pestaña);
**Solo lectura** con fondo `#2d2e33`, borde `#3a3c42` y texto `dim` (`SL` en la pestaña).

### 7.10 Diálogos

Construidos en código, modales, fondo `#26272b`, sin redimensionar, centrados.

| Diálogo | Contenido |
|---|---|
| Nueva / Editar conexión | Motor (`mode`), estado del driver en `dim`, Nombre, Host + Puerto, Base de datos, Usuario + Contraseña (`•`), o Archivo + Examinar... para SQLite y DuckDB; Cuidado (Normal \| Producción \| Solo lectura) con su explicación; Carpeta del proyecto para consolas; **▸ Avanzado** plegable (abierto si la conexión ya tiene opciones): casillas con las opciones comunes del motor (`ToolTip` con el `clave=valor` que agregan) y Otras opciones en texto libre; ambas se guardan juntas en la cadena de opciones. Los campos que el motor no exige llevan "(opcional)" en la etiqueta. **Probar conexión** a la izquierda (descarga el driver si falta; resultado en verde o rojo), Guardar (`accent`) y Cancelar a la derecha. |
| Drivers | Una fila por motor: punto de color, nombre, paquete, estado (no instalado / instalado versión · tamaño · fecha / actualización recomendada) y los paquetes descargados; Descargar o Actualizar, Versión... (lista de nuget.org) y Quitar. |
| `Confirm`, `Message`, `Prompt`, `Pick`, Acerca de | Los de la familia. |

### 7.11 Barras de desplazamiento

Una sola para todo, en `Styles.axaml`: línea de **4 px**, esquinas de 2 px, sin flechas; la rejilla usa la misma.

---

## 8. Patrones de interacción

### 8.1 Home de conexiones

- Toolbar: **Nueva conexión...** (`accent`), **Abrir archivo de base de datos...**, **Nuevo grupo...**, **Drivers...**,
  filtro y, a la derecha, `▾ Expandir` / `▸ Contraer` y `★ Favoritos`.
- Fila: ★ · nombre 14 px SemiBold · motor en su color · etiqueta de entorno · "(archivo no encontrado)" en rojo ·
  destino `dim` (`usuario@host:puerto/base` o la ruta) · fecha de uso a la derecha.
- Arrastrar archivos `.db`, `.sqlite`, `.sqlite3`… a la ventana crea (o reutiliza) su conexión y la abre.
- Menú: Abrir · Editar… · Duplicar… · Favorito · Mover a grupo ▸ · Mostrar la carpeta del archivo · Quitar de la
  lista… (con confirmación; borra también la contraseña guardada, nunca la base).
- Grupos como en la familia (`ConnectionGrouping`).

### 8.2 Pestañas persistentes

Las conexiones abiertas y la activa se restauran al iniciar, y dentro de cada una sus consolas y la activa. La
conexión se abre al mostrarse la pestaña, no al arrancar la app. Cerrar con cambios pendientes pide confirmación.

### 8.3 Ocupado y avisos

Progreso indeterminado en la barra de estado mientras se conecta o se lee la estructura; el resultado de una
acción va al aviso ("Aplicado: 3 filas en 9 ms", "Copiado como JSON"). Los errores con detalle abren además un
`Message`, salvo los de una consulta, que se muestran en el área de resultados.

### 8.4 Consolas como archivos

Cada consola es un `.sql` que se guarda solo (800 ms después de dejar de escribir y al ejecutar o cerrar). Sin
carpeta de proyecto viven en `%APPDATA%\peachapps\DataPeach\queries\<conexión>\`; con ella, en
`<carpeta>\.peach\queries\`, listas para versionar con git. Una consola vacía que se cierra no deja archivo.

### 8.5 Cuidado por conexión

| Cuidado | Qué cambia |
|---|---|
| Normal | Nada extra; las ediciones de tablas pasan por el staging. |
| Producción | Banner ámbar, etiqueta `PROD`; confirmación con las sentencias que escriben antes de ejecutarlas y antes de aplicar el staging. |
| Solo lectura | Banner azul, etiqueta `SL`; la sesión se abre en solo lectura (PostgreSQL, MySQL, SQLite con `Mode=ReadOnly`, DuckDB con `ACCESS_MODE=READ_ONLY`; SQL Server, Oracle y SAP ASE no tienen sesión de solo lectura y solo frena la app); las sentencias que escriben no se ejecutan; las tablas no se editan. |

Qué "escribe" lo decide `SqlStatements.IsWrite`, que es conservador: lo que no reconoce como lectura cuenta como
escritura.

### 8.6 Drivers (Propio de DataPeach)

DataPeach no trae drivers. La primera vez que se abre una conexión de un motor, un banner azul ofrece descargar el
suyo (paquete oficial de nuget.org, 1–2 MB); el diálogo de conexión lo descarga al probar. Desde **Drivers...** se
actualiza, se elige otra versión o se quita, sin actualizar DataPeach. Un driver en uso no se reemplaza hasta
reiniciar, y la app lo dice.

### 8.7 Carpeta compartida `peachapps`

```
%APPDATA%\peachapps\DataPeach\   state.json · secrets.json · location.json · drivers\ · queries\
```

`secrets.json` guarda las contraseñas cifradas con DPAPI para el usuario (en Linux, con permisos 600); nunca en
`state.json` ni en la carpeta del proyecto. `location.json` se escribe al abrir una compilación Release.
`state.json` también guarda la búsqueda de versiones (`CheckUpdates`, `LastUpdateCheck`, `LatestVersion`,
`SkippedVersion`): como mucho una consulta a GitHub al día.

---

### 8.8 Historial de borrados (Propio de DataPeach)

Borrar tiene vuelta atrás. Todo `DELETE` o `TRUNCATE` de una consola pide confirmación en cualquier conexión
(doble validación: el diálogo dice cuántas filas quita de cada tabla y si se guardan; en producción lo dice
también), y el staging pregunta lo mismo cuando tiene filas a borrar. Antes de borrar se leen las mismas filas
(el mismo `WHERE`, en la misma transacción; `TRUNCATE` justo antes) y se guardan en `history/<conexión>/` de la
carpeta de configuración, hasta 100.000 por borrado. Un `DELETE` que no se puede respaldar (`USING`, `JOIN`,
`RETURNING`, CTE, `TOP`) se ejecuta igual si se confirma, y la confirmación dice "NO se guardan" y por qué.

El botón **Borrados (n)** de la barra de la conexión abre el historial: a la izquierda cada borrado (tabla, fecha,
origen consola/staging, filas; ámbar "sin respaldo", verde "restaurado"); a la derecha lo que se ejecutó y el script
que devuelve las filas (`INSERT` fila por fila, con `IDENTITY_INSERT` en SQL Server y SAP ASE), y **Restaurar…**
(`accent`; desactivado si ese borrado ya se restauró; confirma siempre, ejecuta en una transacción: o vuelven todas o ninguna), Abrir en la consola, Copiar
script y Quitar del historial. Arriba, "Guardar los borrados" con el `mode` 7 días · 30 días · 90 días · Siempre
(30 por defecto); lo viejo se borra al abrir la app.

## 9. Teclado

Atajos comunes a todas las apps Peach (no se reutilizan para otra cosa) y los propios de DataPeach. Se listan en
`ViewModels/Shortcuts.cs`, que alimenta F1.

| Atajo | Acción |
|---|---|
| `Ctrl+P` | Ir a tabla (en una conexión) / abrir conexión |
| `Ctrl+Shift+O` | Abrir conexión (búsqueda rápida) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Pestaña siguiente / anterior |
| `Ctrl+1 … Ctrl+9` | Ir a la pestaña N |
| `Ctrl+W` / `Ctrl+F4` | Cerrar consola o tabla (o la conexión si no hay ninguna) |
| `F1` | Acerca de, atajos e idioma |
| `F5` | Volver a leer la estructura y las filas de la tabla abierta (con su filtro y orden; los cambios pendientes se mantienen) |
| `Ctrl+Shift+E` | Filtrar las tablas |
| `Ctrl+Enter` | Ejecutar la sentencia del cursor o la selección |
| `Ctrl+Shift+Enter` | Ejecutar toda la consola |
| `Esc` | Cancelar la consulta en curso / cerrar diálogo / limpiar filtro |
| `Ctrl+Space` | Autocompletar |
| `Ctrl+N` | Nueva consola |
| `F4` | Abrir los datos de la tabla seleccionada |
| `Ctrl+Shift+K` | Aplicar los cambios pendientes |
| `F2` / `Enter` | Editar la celda |
| `Ctrl+C` / `Ctrl+Shift+C` | Copiar celdas / con encabezados |
| `Alt+Insert` · `Ctrl+Delete` · `Ctrl+Shift+N` | Agregar fila · borrar filas · poner NULL |

---

## 10. Textos y tono

- **Inglés y español.** Ningún texto visible va escrito en XAML ni en C#: todos están en
  `src/DataPeach.Core/Strings.cs` como par (inglés, español). En XAML `{l:L clave}`, en C# `Loc.T`, `Loc.F` y
  `Loc.Plural`. Un test verifica que cada clave usada exista en los dos idiomas con los mismos marcadores.
- No se traducen: nombres de apps, palabras de SQL (`WHERE`, `NULL`, `UPDATE`), nombres de motores y paquetes.
- Inglés claro y corto, *sentence case*. Español neutro con tuteo, sin exclamaciones ni emojis.
- Avisos descriptivos: "Aplicado: 3 filas en 9 ms", "Quitada de la lista: tienda (la base de datos no se tocó)".
- Errores: qué pasó + el dato concreto. Los del motor se muestran tal cual los da el driver.
- Números con separador de miles, tiempos en ms (o s con dos decimales desde 1 s), fechas `yyyy-MM-dd HH:mm`.

---

## 11. Iconografía y marca

- **Glifos Unicode** en vez de una fuente de iconos: `★` favorito, `✕` cerrar, `●` motor, `⋯` más, `↶` deshacer
  un cambio, `▶` ejecutar, `⚿` clave primaria, `⏎` salto de línea en una celda, `›_` consola. Letras: `S` esquema,
  `T` tabla, `V` vista, `c` columna, `ƒ` función, `k` palabra clave, `D` base de datos, `▤` carpeta del árbol;
  `~ + −` en el staging.
- **Logo:** un durazno azul (índigo a la izquierda, celeste a la derecha) con hoja verde y un cilindro de base de
  datos blanco, con el mismo acabado que los logos de GitPeach y TextPeach. Va en `app.ico`, `icon64.png` y
  `logo.png` (fondo transparente, recortado al borde del dibujo); se muestra en la barra de título, "Acerca de" y
  el estado vacío de la Home, **nunca** en la barra de pestañas.
- El nombre es **DataPeach**, una palabra, con D y P mayúsculas.

---

## 12. Rendimiento

La velocidad es el requisito principal de DataPeach. Medido en Windows 11, Release:

| Qué | Valor |
|---|---|
| Arranque hasta la ventana (en caliente) | ~240 ms |
| Memoria privada en reposo | ~30 MB |
| Leer la estructura de una base SQLite | ~10–30 ms |
| 50.000 filas en la rejilla (consulta SQLite) | ~140 ms |

Cómo se mantiene:

- **Rejilla dibujada a mano** y **árbol como lista virtualizada**: el costo depende de lo visible, no del tamaño.
- **Resultados en streaming**: las primeras filas se muestran a los ~30 ms y el resto llega por tandas cada
  100 ms; tope de 50.000 filas por resultado (comunicado como "(limitado)"); `Esc` cancela en el servidor.
- **Estructura en una sola consulta** por motor (`pg_catalog`, `information_schema`, `pragma_table_info`), en una
  conexión aparte para no esperar a una consulta larga; detalle (índices, FK) solo al seleccionar una tabla.
- **Sin TextMate** (coloreado SQL propio) y **sin drivers** en el exe; publicación **sin compresión**.
- Disco y red siempre fuera del hilo de la interfaz; *debounce* al guardar consolas (800 ms).

---

## 13. Dónde está cada cosa en el código

| Qué | Dónde |
|---|---|
| Estilos y clases (`accent`, `seg`, `mode`, `envtag`, barras de 4 px…) | `src/DataPeach/Styles.axaml` |
| Colores en C# | `src/DataPeach/Controls/Palette.cs` |
| Rejilla de resultados | `src/DataPeach/Controls/ResultGrid.cs` |
| Coloreado SQL y autocompletado (vista) | `src/DataPeach/Controls/SqlEditor.cs` |
| Lexer, sentencias, autocompletado (lógica) | `src/DataPeach.Core/Sql/` |
| Staging | `src/DataPeach.Core/Staging/StagingArea.cs` |
| Historial de borrados | `src/DataPeach.Core/History/`, `src/DataPeach/Views/DeleteHistoryDialog.cs` |
| Procedimientos y funciones por motor | `src/DataPeach.Core/Db/DialectRoutines.cs`, `SybaseDialect.cs` |
| Diálogos | `src/DataPeach/Views/Dialogs.cs`, `ConnectionDialog.cs`, `DriversDialog.cs`, `DeleteHistoryDialog.cs` |
| Textos | `src/DataPeach.Core/Strings.cs` |
| Atajos de F1 | `src/DataPeach/ViewModels/Shortcuts.cs` |

Si agregas un patrón de interfaz nuevo, documéntalo aquí.
