dotnet build AssetsManager.csproj


>null

# Debugs/Publish types

textSharp
dotnet build -c Release

AssetsManager
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=false

