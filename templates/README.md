# Clojure.MSBuild.Templates

`dotnet new` templates for ClojureCLR. Projects are built, tested and run with the dotnet CLI
through [Clojure.MSBuild](https://github.com/Dangercoder/Clojure.MSBuild); no other tools needed.

```bash
dotnet new install Clojure.MSBuild.Templates
```

| Template | Short name | Contents |
|----------|------------|----------|
| Console application | `clojure-clr-console` | `-main`, ready for `dotnet run` |
| Minimal API | `clojure-clr-minimal-api` | ASP.NET Core minimal API, SQLite, HoneySQL, clojure.spec domain model, OpenTelemetry, Dockerfile, docker-compose with the Aspire dashboard, end-to-end tests |

```bash
dotnet new clojure-clr-minimal-api -n orders --root-namespace constructly.se
cd orders
dotnet build
dotnet test
dotnet run
```

`--root-namespace` sets the root of the generated Clojure namespaces (`constructly.se.server`,
`constructly.se.routes`, ...). It defaults to the project name in lower case (`-n my-api` gives
`my-api.server` in `src/my_api/`).

Requires the .NET 11 SDK.
