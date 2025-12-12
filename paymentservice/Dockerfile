FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["paymentservice.csproj", "./"]
RUN dotnet restore "paymentservice.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "paymentservice.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "paymentservice.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "paymentservice.dll"]