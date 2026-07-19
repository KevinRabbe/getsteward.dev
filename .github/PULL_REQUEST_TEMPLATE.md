## What changed

Describe the behavior or architecture changed by this PR.

## Why this boundary

Explain why the change belongs in Core, Infrastructure, a game adapter, or the composition/UI layer.

## Validation

- [ ] `dotnet format SharedWorlds.sln --verify-no-changes --no-restore`
- [ ] `dotnet build SharedWorlds.sln --configuration Release --no-restore`
- [ ] `dotnet test SharedWorlds.sln --configuration Release --no-build`
- [ ] Relevant manual game test completed, when required

## Safety and compatibility

- [ ] Does not modify original imported saves
- [ ] Does not add game-specific logic to Core
- [ ] Persisted data compatibility considered
- [ ] Failure/recovery behavior considered

## Documentation

List documentation updated, or explain why none is required.
