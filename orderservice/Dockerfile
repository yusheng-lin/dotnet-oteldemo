FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["orderservice.csproj", "./"]
RUN dotnet restore "orderservice.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "orderservice.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "orderservice.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "orderservice.dll"]