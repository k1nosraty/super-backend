FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /source
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/SuperBackend.Api --locked-mode && \
    dotnet publish src/SuperBackend.Api -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=build /publish .
RUN mkdir /keys && chown "$APP_UID" /keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SuperBackend.Api.dll"]
