# The publish output is produced on the dev machine (dotnet publish -c Release
# -r linux-x64 --self-contained false) rather than in a build stage here: the
# Hetzner box has ~1.9 GB free and an SDK build there risks OOM-killing the
# bingewatch containers sharing the host.
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app
COPY publish/ ./

# SpecialFolder.LocalApplicationData resolves to $XDG_DATA_HOME on Linux, so
# pointing it at /data puts the ledger on the mounted volume with no code change.
ENV XDG_DATA_HOME=/data \
    ASPNETCORE_URLS=http://+:8080 \
    TZ=Europe/Istanbul

EXPOSE 8080
ENTRYPOINT ["dotnet", "ExpenseTracker.dll"]
