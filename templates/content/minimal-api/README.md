# ClojureApi

A ClojureCLR minimal API: ASP.NET Core, SQLite, HoneySQL, clojure.spec and OpenTelemetry, built and run
with the dotnet CLI through [Clojure.MSBuild](https://github.com/Dangercoder/Clojure.MSBuild).

```bash
dotnet build     # AOT-compiles the Clojure namespaces into a standalone assembly
dotnet run       # http://localhost:5080
dotnet test      # end-to-end tests against the in-process test server
```

## Endpoints

| Method | Path          | Description                          |
|--------|---------------|--------------------------------------|
| GET    | /health       | Liveness check                       |
| GET    | /todos        | List todos                           |
| GET    | /todos/{id}   | Get one todo                         |
| POST   | /todos        | Create: `{"title": "..."}`           |
| PUT    | /todos/{id}   | Update: `{"title": "...", "done": true}` |
| DELETE | /todos/{id}   | Delete                               |

```bash
curl -X POST localhost:5080/todos -H 'content-type: application/json' -d '{"title":"Hello"}'
curl localhost:5080/todos
```

## Layout

- `src/.../server.cljr` builds the application (configuration, telemetry, schema, routes) and holds `-main`.
- `src/.../todo/model.cljr` the domain as clojure.spec specs (`::title`, `::done`, derived `::display-name`,
  `::todo`, `::new-todo`, `::todo-update`). Request bodies are validated against them and the
  property-based tests generate their input with `s/gen` from them.
- `src/.../routes.cljr` HTTP handlers: plain functions of the `HttpContext` returning response maps.
- `src/.../todos.cljr` HoneySQL queries.
- `src/.../db.cljr` async ADO.NET helper: `(t/await (db/execute! db {:select ...}))` returns rows as maps.
- `src/.../web.cljr` the thin wrapper over minimal APIs (routing, JSON, async handlers).
- `src/.../telemetry.cljr` OpenTelemetry setup.
- `test/.../todos_test.cljr` end-to-end tests through HTTP, plus property-based tests driven by the specs.

The database connection string is `ConnectionStrings:Default` in `appsettings.json`
(environment: `ConnectionStrings__Default`).

## Telemetry

Traces, metrics and logs are collected with OpenTelemetry and exported over OTLP when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set. To see them locally, run the Aspire dashboard and
point the app at it:

```bash
docker compose up dashboard -d
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:18889 dotnet run
open http://localhost:18888
```

## Docker

```bash
docker compose up --build     # API on :8080, dashboard on :18888
docker build -t cljimagename . && docker run -p 8080:8080 cljimagename
```

## REPL

```bash
dotnet msbuild /t:clj-repl
```
