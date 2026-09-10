# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY . .
# Restores NuGet packages, AOT-compiles the Clojure namespaces (HoneySQL is fetched
# from git into ~/.gitlibs during the build) and publishes the standalone output.
RUN --mount=type=cache,target=/root/.nuget/packages \
    --mount=type=cache,target=/root/.gitlibs \
    dotnet publish ClojureApi.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data
# The aspnet image listens on port 8080 by default (HTTP_PORTS).
ENV ConnectionStrings__Default="Data Source=/data/todos.db" \
    OTEL_SERVICE_NAME=ClojureApi
EXPOSE 8080
ENTRYPOINT ["dotnet", "CLJASSEMBLYNAME.dll"]
