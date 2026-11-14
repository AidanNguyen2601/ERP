# ===== Build stage =====
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ERP.Web/ERP.Web.csproj src/ERP.Web/
RUN dotnet restore src/ERP.Web/ERP.Web.csproj
COPY src/ src/
RUN dotnet publish src/ERP.Web/ERP.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ===== Runtime stage =====
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    TZ=Asia/Ho_Chi_Minh
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ERP.Web.dll"]
