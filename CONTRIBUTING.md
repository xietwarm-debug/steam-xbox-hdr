# Contributing

Issues and pull requests are welcome. Please include Windows build and test results when changing native display detection, key forwarding, or JPEG XR validation.

Before opening a pull request, run:

```powershell
dotnet build src/SteamXboxHdr/SteamXboxHdr.csproj -c Release
dotnet run --project tests/SteamXboxHdr.Tests/SteamXboxHdr.Tests.csproj -c Release
```

Do not attach personal screenshots, diagnostic JSON, Steam account folders, or unredacted local paths. If reporting a Game Bar problem, describe the Windows version, display/HDR mode, game, capture extension, and relevant redacted log line.
