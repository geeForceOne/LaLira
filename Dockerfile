# Build from the repository root:
#   docker build -t lalira .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/LaLira.Core/LaLira.Core.csproj src/LaLira.Core/
COPY src/LaLira.Web/LaLira.Web.csproj src/LaLira.Web/
RUN dotnet restore src/LaLira.Web/LaLira.Web.csproj

COPY src/ src/
# Built into the app for its release notes page.
COPY CHANGELOG.md ./
# The version shown in the app; the publish workflow passes the release tag's number.
ARG VERSION=dev
RUN dotnet publish src/LaLira.Web/LaLira.Web.csproj -c Release -o /app -p:InformationalVersion=$VERSION

# The regular (Ubuntu-based) runtime image ships ICU, which the app needs for German month names
# and region formats such as de-CH. Don't switch to a chiseled or invariant-globalization image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Everything persistent (database, Data Protection keys, backups) goes to /data, which
# docker-compose mounts as a volume. Nothing is written inside the image.
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__DataDirectory=/data
VOLUME /data
# Runs as root, like Recall, so a host folder or an existing volume mounted at /data works
# without first changing its owner.
EXPOSE 8080

ENTRYPOINT ["dotnet", "LaLira.Web.dll"]
