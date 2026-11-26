# Endpoint Studio

A visual, no-code studio for **.NET Web APIs**, from **.NET Framework 4.x (ASP.NET Web API 2)** through **.NET Core** to **.NET 11**. Open any .NET project to browse, add, edit and delete its endpoints, or turn a SQL Server or MySQL database into a ready-to-run API in a few clicks. Users only ever see forms, lists and buttons. They never see code.

## Features

| Area | What you can do |
| --- | --- |
| **Projects & solutions** | Browse your computer for projects and solutions (`.sln` / `.slnx`), scan a folder, reopen recent items, show them in Explorer or open them in VS Code |
| **Reads the whole solution** | Opening a project also reads the projects it references, so controllers in libraries, DbContexts in data projects and services (`IXxxService` → implementation) are found. The Solution page shows every project with its role (API, Services, Data access, Models, Tests), version, dependencies and endpoint count |
| **Endpoint explorer** | Endpoints grouped by controller or route group, colour-coded HTTP methods, search, method filters and dashboard stats |
| **Add endpoints** | A two-step wizard that follows how the project is already built: **copy an existing endpoint** (same checks, envelope and error handling, with a different, or brand-new, service operation), **call a service operation**, **run SQL or a stored procedure** with the project's own driver and connection strings (with a safe test runner), use an **Entity Framework table**, or return a simple response |
| **Endpoint details** | Every endpoint shows its access, inputs (where they come from, type, required), the service it calls, what it returns, possible results, an example request with a body built from the real request classes (also classes inside referenced DLLs), and its location in the code |
| **Edit endpoints** | Change the HTTP method, path, name, description and sign-in requirement |
| **Delete endpoints** | Removed cleanly after you confirm |
| **Safety** | Every edit is checked for syntax before it's saved. Each change is recorded in a **History**, and **Undo** is one click away |
| **Build check** | Compiles the project and lists any problems in plain language |
| **Run & try** | Starts the API on a free port, and a **Try it** panel sends real requests and explains the status codes |
| **Export** | Downloads a Postman collection (v2.1) of every endpoint, with sample request bodies |
| **New from database** | A four-step wizard: connect (**SQL Server, PostgreSQL, MySQL, Oracle or SQLite**) → pick a database or schema → pick tables and operations → settings. It generates entities, a `DbContext`, CRUD endpoints (Controllers or Minimal APIs) and optional interactive API docs, then runs a build check |
| **Health check** | Scores the project and lists problems: duplicate routes, open endpoints in a protected API, keys and certificates in the project folder, passwords in config files, error details sent to callers, data changes through GET, unsupported .NET versions, old packages, missing descriptions |
| **Upgrade assistant** | Moves .NET Core 2.x/3.x and .NET 5–9 projects (optionally with the projects they use) to a newer .NET: target framework (also in `Directory.Build.props`), version-matched packages (also in `Directory.Packages.props`), obsolete ASP.NET Core 2 packages, and package conflicts found by restore. One Undo reverts it all |
| **Every .NET version** | Detects each project's .NET version and writes code that fits it (see the table below) |
| **Settings** | View and edit `appsettings*.json` (and `Web.config`) as a friendly list: sections, secrets hidden until revealed, add/remove settings, add environments (Staging, Production…), undo |
| **Publish & deploy** | Folder or network share (IIS-safe with `app_offline.htm`), ZIP, Docker image (build, run locally, push to a registry), Docker on a remote server over SSH, Linux server over SSH, IIS with Web Deploy, Azure App Service, FTP/FTPS. Live log, cancel, saved options per project (passwords are never saved). Build a whole solution from the Solution page |
| **Recover code** | Turn a published app's `.dll` files back into C# projects (ICSharpCode.Decompiler, the ILSpy engine) linked in one solution, ready to open in the studio. Detects your own code, third-party libraries and obfuscated files. Only for code you own or may inspect |
| **Look & feel** | Light, dark and system themes, and a responsive layout |

## How it works

```
React UI (Vite, TypeScript)  ──/api──▶  Local helper service (ASP.NET Core, .NET 10)
                                          ├─ Roslyn: reads and edits C# source precisely
                                          ├─ SqlClient / MySqlConnector: reads database structure
                                          └─ dotnet CLI: builds and runs projects
```

* A browser can't read or change files on disk, so the UI talks to a small **local helper service** in [`server/`](server/). It only listens on `localhost`.
* Projects are analysed with **Roslyn**, the C# compiler platform. It understands both styles:
  * **Controllers**: `[ApiController]` classes, `[Route]` and `[Http*]` attributes, including `[controller]` and `[action]` tokens.
  * **Minimal APIs**: `MapGet/MapPost/…` calls, `MapGroup` prefixes followed through variables, and `WithName`/`WithSummary`/`RequireAuthorization` chains.
* Changes are small, targeted text edits based on the syntax tree, so the rest of the file keeps its formatting. When needed, the studio also wires up `Program.cs`, for example `AddControllers()`/`MapControllers()` or registering a new endpoint group.
* History backups are stored in `%LOCALAPPDATA%\EndpointStudio`, never inside your project.

## .NET version support

| Project type | Browse | Add / edit / delete | Build check | Run & try |
| --- | --- | --- | --- | --- |
| .NET Framework 4.x: ASP.NET Web API 2 (old-style or SDK project files, `packages.config`, EF6) | ✅ attribute routes (`[RoutePrefix]`/`[Route]`) and convention routes (`api/{controller}/{id}`) | ✅ controllers (`IHttpActionResult`, EF6 data endpoints); new files are added to the project file and attribute routing is switched on automatically | MSBuild (Visual Studio or Build Tools) | IIS Express |
| .NET Core 2.x – 3.1, .NET 5 (`Startup` class) | ✅ | ✅ controllers | `dotnet build` | `dotnet run` (needs that runtime) |
| .NET 6 | ✅ controllers and minimal APIs | ✅ minimal APIs without `MapGroup` (full paths per endpoint) | `dotnet build` | `dotnet run` |
| .NET 7 – 11 | ✅ | ✅ everything, including `MapGroup` and `WithSummary` | `dotnet build` | `dotnet run` |

Generated code follows each project's settings: nullable annotations only when `<Nullable>` is on, block or file-scoped namespaces, explicit `using`s when implicit usings are off, `JObject` instead of `JsonElement` before .NET Core 3, and `DateTime` instead of `DateOnly` on older versions. Out-of-support versions show a notice.

**New projects from a database** can target **.NET 6, 7, 8, 9, 10** (and 11 when its SDK is installed), each with matching packages:

| Target | EF Core (SQL Server) | Pomelo (MySQL) | API docs |
| --- | --- | --- | --- |
| .NET 6 / 7 | 7.0.20 | 7.0.0 | Swagger UI (Swashbuckle 6.9) |
| .NET 8 | 8.0.34 | 8.0.3 | Swagger UI (Swashbuckle 6.9) |
| .NET 9 | 9.0.23 | 9.0.0 | Built-in OpenAPI + Scalar 2.21 |
| .NET 10 / 11 | 10.0.15 / 11.0.0 | 9.0.0 | Built-in OpenAPI + Scalar 3.2 |

## Databases

| Database | Reads | Generated project uses | Notes |
| --- | --- | --- | --- |
| SQL Server | Databases, schemas, tables, views, identity, computed and rowversion columns | `Microsoft.EntityFrameworkCore.SqlServer` | Windows or SQL sign-in |
| PostgreSQL | Databases, schemas, tables, views, identity/serial, `uuid`, `jsonb`, `timestamptz` | `Npgsql.EntityFrameworkCore.PostgreSQL` | snake_case names become PascalCase; arrays and custom enum types are skipped |
| MySQL / MariaDB | Databases, tables, views, auto-increment | `Pomelo.EntityFrameworkCore.MySql` | |
| Oracle | Schemas (owners), tables, views, identity and virtual columns, `NUMBER(p,s)` precision | `Oracle.EntityFrameworkCore` | Connect with host, port and **service name**; UPPER_CASE names become PascalCase; Oracle 12c or newer |
| SQLite | Tables and views in a `.db` file | `Microsoft.EntityFrameworkCore.Sqlite` | Pick the file with the built-in browser |

## Requirements

* [.NET SDK 10](https://dotnet.microsoft.com/download) (runs the helper service and builds every .NET Core / .NET project)
* Node.js 20+
* Optional: Visual Studio or the Build Tools with IIS Express, to build and run .NET Framework projects
* Optional: the ASP.NET Core runtime of each version you want to *run* (editing and building don't need it)

## Getting started

```bash
npm install
npm run dev        # starts the helper service (port 5199) and the UI (http://localhost:5173)
```

Single-URL production mode, where the helper service also serves the built UI:

```bash
npm run build      # builds the UI into server/wwwroot and compiles the server
npm start          # open http://localhost:5199
```

## Project layout

```
src/                      React app
  pages/                  Home, Project, New-from-database wizard
  components/             Endpoint editor, details drawer & Try it, folder picker, dialogs, UI kit
server/                   .NET helper service
  Services/               Project analyzer (Roslyn), database reader, build/run, history, Postman export
  Codegen/                Endpoint writer (add/edit/delete) and project generator
scripts/dev.mjs           Runs the server and the UI together
```

## Notes & limitations

* "Only signed-in users" adds `[Authorize]` / `RequireAuthorization()`. The project must already have authentication configured for those endpoints to be callable.
* Editing changes how an endpoint is reached and described. To change what it does, delete it and add it again.
* Generated projects store the connection string in `appsettings.json`. Move secrets to user-secrets or environment variables before sharing.
* MySQL projects use Pomelo (EF Core 9); SQL Server projects use EF Core 10.

## Ideas for next steps

* Request/response DTOs and validation rules from the UI
* Authentication setup wizard (JWT / cookies)
* Relationships (navigation properties) in generated projects
* Git integration: show a change summary and commit from the studio
