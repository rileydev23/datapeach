# Contributing to DataPeach

[English](#english) · [Español](#español)

---

## English

Thanks for helping! DataPeach is a fast, free database client for Windows and Linux (Avalonia + .NET 10). Small, focused
contributions are the easiest to review and the most likely to be merged.

### Before you start

- **Bugs:** open an [issue](../../issues/new/choose) with steps to reproduce, what you expected and what
  happened. Screenshots help a lot for anything visual.
- **New features:** open an issue first and wait for a reply before writing code. DataPeach deliberately
  stays fast and small; some good ideas will not fit, and it is better to know before you spend time on
  them.
- Small fixes (typos, an obvious one-line bug) can go straight to a pull request.

### Workflow

1. **Fork** the repository and clone your fork.
2. Create a branch from `main` named after the kind of change:

   | Prefix | For | Example |
   |---|---|---|
   | `feature/` | something new | `feature/minimap` |
   | `fix/` | a bug | `fix/diff-scroll-sync` |
   | `docs/` | README, DISENO.md, comments only | `docs/shortcuts-table` |
   | `refactor/` | internal change, same behavior | `refactor/split-project-vm` |
   | `chore/` | build, CI, dependencies | `chore/update-avalonia` |

   Lowercase, words separated by `-`. A check on every pull request rejects other names.
3. Make your change. Keep the pull request about **one thing**; unrelated cleanups go in their own PR.
4. Check that it builds and the tests pass:

   ```
   dotnet build
   dotnet test
   ```

   `dev.cmd` builds in Debug and runs the app.
5. Open a pull request against `main` and fill in the template. Nobody pushes to `main` directly, including
   the maintainer: every change goes through a reviewed pull request.

### Commit messages

A short summary of what the change does, in English or Spanish, like the existing history:
`Diff side by side scrolls both panes together`. No prefix needed (the branch name already says the kind of
change). Add a blank line and more detail when the *why* is not obvious.

### Code conventions

- **Every visible text lives in `src/DataPeach.Core/Strings.cs`**, in English and Spanish (neutral
  Spanish, *tuteo*, capital letter only at the start). XAML uses `{l:L key}`, C# uses `Loc.T("key")`.
  Code, names and comments are in English.
- Follow [docs/DISENO.md](docs/DISENO.md): only palette colors, one primary button per area, read-only
  information is never shown in an editable field, the interface does not mention third-party apps. If you
  add a new UI pattern, document it there.
- Disk, network and database work runs off the UI thread; the grid and the tree only draw what is visible.
- **Nothing reaches the database unseen:** grid edits go through the staging area, statements that write
  respect the connection's care (production asks, read-only refuses), and drivers are never bundled.
- Pure logic (lexer, statements, completion, staging, exporters, NuGet…) lives in `DataPeach.Core` and comes with tests in `tests/DataPeach.Tests`.
- Match the style of the surrounding code; no new dependencies without discussing them in an issue first.

### Releases

Versions follow [SemVer](https://semver.org) and nobody edits a version number by hand:

- Each pull request gets a label from its branch name (`feature/` → `enhancement`, `fix/` → `bug`, …), and
  every merge updates a **draft release** with the notes grouped by kind.
- The next version comes from those labels: any `enhancement` raises the minor version (0.3.0 → 0.4.0),
  otherwise the patch (0.3.0 → 0.3.1).
- DataPeach is still at **0.x**, so breaking changes raise the minor version too. The maintainer sets
  **1.0.0** by hand on the draft when the time comes; after that, the `breaking` label raises the major one.
- A merge is not a release: the draft collects several pull requests and is published when it is worth it.
- When the maintainer publishes the draft, the tag `vX.Y.Z` is created and a workflow builds `datapeach-windows-x64-portable.exe`, `datapeach-windows-x64-setup.exe` and
  `datapeach-linux-x64.tar.gz` with that version and attaches them to the release.

### License of contributions

DataPeach is licensed under the [GNU GPL v3.0 or later](LICENSE). By sending a pull request:

1. Your contribution is licensed under the GPL-3.0-or-later, like the rest of the project.
2. You also grant rileydev23 a perpetual, worldwide, non-exclusive, royalty-free license to use, modify and
   relicense your contribution under other terms. This is what lets the author offer commercial licenses to
   companies that cannot use the GPL; the GPL version stays available to everyone.
3. You keep the copyright of your work, and you confirm that you wrote it yourself or have the right to
   contribute it.

---

## Español

¡Gracias por ayudar! DataPeach es un cliente de bases de datos rápido y gratuito para Windows y Linux (Avalonia + .NET 10).
Las contribuciones pequeñas y enfocadas son las más fáciles de revisar y las que más probablemente se
integren.

### Antes de empezar

- **Errores:** abre un [issue](../../issues/new/choose) con los pasos para reproducirlo, qué esperabas y qué
  pasó. Las capturas ayudan mucho en todo lo visual.
- **Funciones nuevas:** abre primero un issue y espera respuesta antes de escribir código. DataPeach se
  mantiene rápido y pequeño a propósito; algunas buenas ideas no van a encajar, y es mejor saberlo
  antes de invertir tiempo.
- Los arreglos chicos (una errata, un error obvio de una línea) pueden ir directo a un *pull request*.

### Flujo de trabajo

1. Haz un **fork** del repositorio y clona tu fork.
2. Crea una rama desde `main` con el prefijo del tipo de cambio:

   | Prefijo | Para | Ejemplo |
   |---|---|---|
   | `feature/` | algo nuevo | `feature/minimap` |
   | `fix/` | un error | `fix/diff-scroll-sync` |
   | `docs/` | solo README, DISENO.md o comentarios | `docs/shortcuts-table` |
   | `refactor/` | cambio interno, mismo comportamiento | `refactor/split-project-vm` |
   | `chore/` | compilación, CI, dependencias | `chore/update-avalonia` |

   En minúsculas y con las palabras separadas por `-`. Una revisión automática en cada *pull request*
   rechaza otros nombres.
3. Haz tu cambio. Que el *pull request* trate de **una sola cosa**; las limpiezas que no tienen que ver van en
   otro.
4. Comprueba que compila y que los tests pasan:

   ```
   dotnet build
   dotnet test
   ```

   `dev.cmd` compila en Debug y abre la app.
5. Abre un *pull request* contra `main` y completa la plantilla. Nadie sube cambios directo a `main`, ni
   siquiera el mantenedor: todo pasa por un *pull request* revisado.

### Mensajes de commit

Un resumen corto de lo que hace el cambio, en español o inglés, como el historial actual:
`El diff lado a lado desplaza los dos paneles juntos`. No hace falta prefijo (el nombre de la rama ya dice el
tipo de cambio). Agrega una línea en blanco y más detalle cuando el *porqué* no sea obvio.

### Convenciones de código

- **Todo texto visible va en `src/DataPeach.Core/Strings.cs`**, en inglés y en español (español neutro,
  tuteo, mayúscula solo al inicio). En XAML se usa `{l:L clave}` y en C# `Loc.T("clave")`. Código, nombres y
  comentarios en inglés.
- Respeta [docs/DISENO.md](docs/DISENO.md): solo colores de la paleta, un botón principal por zona, lo que
  solo se lee nunca va en un campo editable, la interfaz no menciona aplicaciones de terceros. Si agregas un
  patrón de interfaz nuevo, documéntalo ahí.
- Todo lo que toque disco, red o la base va fuera del hilo de la interfaz; la rejilla y el árbol solo dibujan lo visible.
- **Nada llega a la base sin verse antes:** las ediciones de la rejilla pasan por el staging, las sentencias
  que escriben respetan el cuidado de la conexión (producción pregunta, solo lectura no ejecuta) y los drivers
  nunca van dentro del exe.
- La lógica pura (lexer, sentencias, autocompletado, staging, exportadores, NuGet…) vive en `DataPeach.Core` y va con tests en `tests/DataPeach.Tests`.
- Sigue el estilo del código que rodea tu cambio; nada de dependencias nuevas sin hablarlo antes en un issue.

### Versiones y releases

Las versiones siguen [SemVer](https://semver.org) y nadie cambia el número de versión a mano:

- Cada *pull request* recibe una etiqueta según el nombre de su rama (`feature/` → `enhancement`, `fix/` →
  `bug`, …), y cada merge actualiza un **borrador del próximo release** con las notas agrupadas por tipo.
- La versión siguiente sale de esas etiquetas: si hay algún `enhancement` sube la versión menor
  (0.3.0 → 0.4.0); si no, el parche (0.3.0 → 0.3.1).
- DataPeach todavía está en **0.x**, así que los cambios incompatibles también suben la versión menor. El
  mantenedor pone la **1.0.0** a mano en el borrador cuando llegue el momento; desde ahí, la etiqueta
  `breaking` sube la versión mayor.
- Un merge no es un release: el borrador junta varios *pull requests* y se publica cuando vale la pena.
- Cuando el mantenedor publica el borrador, se crea el tag `vX.Y.Z` y un workflow compila `datapeach-windows-x64-portable.exe`, `datapeach-windows-x64-setup.exe` y
  `datapeach-linux-x64.tar.gz` con esa versión y los adjunta al release.

### Licencia de las contribuciones

DataPeach se distribuye bajo la [GNU GPL v3.0 o posterior](LICENSE). Al enviar un *pull request*:

1. Tu contribución queda bajo la GPL-3.0-or-later, como el resto del proyecto.
2. Además, le das a rileydev23 una licencia perpetua, mundial, no exclusiva y gratuita para usar, modificar y
   relicenciar tu contribución bajo otros términos. Esto permite que el autor ofrezca licencias comerciales a
   empresas que no pueden usar la GPL; la versión GPL sigue disponible para todos.
3. Conservas los derechos de autor de tu trabajo y confirmas que lo escribiste tú o que tienes derecho a
   aportarlo.

Si hay diferencias entre las dos versiones de esta sección, vale la versión en inglés.
