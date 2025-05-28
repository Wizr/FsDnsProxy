FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
RUN apk add build-base

WORKDIR /build
COPY ./  /build
RUN dotnet tool restore
RUN dotnet paket restore
RUN dotnet publish ./FsDnsProxy/FsDnsProxy.fsproj -c Release /p:PublishAot=true -p:InvariantGlobalization=true -o /app/publish


FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-alpine
COPY --from=build /app/publish/* /FsDnsProxy/
WORKDIR /FsDnsProxy

# docker build . -t styx1000/fsdnsproxy --platform linux/amd64,linux/arm64 --push
