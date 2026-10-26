# CalcApp - builds from source inside Docker (needs the .NET SDK image, ~800 MB, only on the build machine)
#
#   docker build -t calcapp .
#   docker run -d --name calcapp -p 8080:8080 calcapp
#
# Run on a newer runtime (the app is built for .NET 8 and rolls forward to 9 / 10):
#   docker build --build-arg RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0 -t calcapp .

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:8.0

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY src/CalcApp/CalcApp.csproj CalcApp/
# Restore from nuget.org; if nuget.org is unreachable, restore with no package sources
# (this app uses no NuGet packages, so that is enough).
RUN dotnet restore CalcApp/CalcApp.csproj \
 || (printf '<configuration><packageSources><clear /></packageSources></configuration>' > /tmp/offline.nuget.config \
     && dotnet restore CalcApp/CalcApp.csproj --configfile /tmp/offline.nuget.config)
COPY src/CalcApp/ CalcApp/
RUN dotnet publish CalcApp/CalcApp.csproj -c Release -o /app --no-restore

FROM ${RUNTIME_IMAGE}
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080
# non-root user that ships with the official .NET 8+ images (UID 1654)
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "CalcApp.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "CalcApp.dll"]
