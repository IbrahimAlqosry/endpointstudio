# Endpoint Studio

A visual, no-code studio for **ASP.NET Core Web APIs**. Open any .NET project to browse, add, edit and delete its endpoints, or turn a SQL Server or MySQL database into a ready-to-run API in a few clicks. Users only ever see forms, lists and buttons. They never see code.

## Features

| Area | What you can do |
| --- | --- |
| **Projects** | Browse your computer for `.csproj` files, scan a folder for projects, reopen recent projects, show the project in Explorer or open it in VS Code |
| **Endpoint explorer** | Endpoints grouped by controller or route group, colour-coded HTTP methods, search, method filters and dashboard stats |
| **Add endpoints** | A two-step wizard. Choose what the endpoint does, then fill in a form. Data actions (list, get, search, add, update and delete records) work against the project's EF Core tables. Simple actions (message, echo, placeholder, no content) take custom inputs from the path, query, body or headers |
| **Edit endpoints** | Change the HTTP method, path, name, description and sign-in requirement |
| **Delete endpoints** | Removed cleanly after you confirm |
| **Safety** | Every edit is checked for syntax before it's saved. Each change is recorded in a **History**, and **Undo** is one click away |
| **Build check** | Compiles the project and lists any problems in plain language |
| **Run & try** | Starts the API on a free port, and a **Try it** panel sends real requests and explains the status codes |
| **Export** | Downloads a Postman collection (v2.1) of every endpoint, with sample request bodies |
| **New from database** | A four-step wizard: connect (SQL Server or MySQL) → pick a database → pick tables and operations → settings. It generates entities, a `DbContext`, CRUD endpoints (Controllers or Minimal APIs) and optional interactive API docs (Scalar), then runs a build check |
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

## Requirements

* [.NET SDK 10](https://dotnet.microsoft.com/download)
* Node.js 20+

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
