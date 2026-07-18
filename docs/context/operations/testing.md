# Testing Operations

## Fast Checks

```powershell
dotnet build TinyBoss.csproj --no-restore
dotnet test TinyBoss.Tests\TinyBoss.Tests.csproj --no-restore
```

Use targeted tests for handler, config, tiling, alias memory, voice policy, terminal buffer, and installer-helper changes.

## Live Checks

```powershell
dotnet run --project TinyBoss.csproj
Invoke-RestMethod http://127.0.0.1:8033/health
```

PitBoss integration should also verify:

```powershell
Invoke-RestMethod http://127.0.0.1:5199/v1/cli
```

## Manual / Device-Dependent

- Real dictation tests need microphone input and a live target app/window.
- Window tiling and overlay tests may depend on monitor layout and elevation.
