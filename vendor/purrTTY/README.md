# Vendored purrTTY contract DLLs

These two assemblies are the **pinned inter-mod ABI** between gatOS and purrTTY. gatOS takes a
compile-time reference to them (`gatOS.Ssh`, `gatOS.GameMod`) and at runtime shares purrTTY's
loaded copies over the StarMap ALC (`ImportedAssemblies`, OS_PLAN.md D6 / §T6.1). They are
**committed** (small, and they pin the API contract).

| File | Purpose |
|---|---|
| `purrTTY.CustomShellContract.dll` | `ICustomShell`, `CustomShellRegistry`, `CustomShellMetadata`, `CustomShellStartOptions`, the shell event args + exceptions (namespace `purrTTY.Core.Terminal`). |
| `purrTTY.Logging.dll` | Referenced transitively by the contract assembly; an importer needs it on the reference path. |

## Provenance (pin)

- **Source repo:** `meow-sci/purrtty` (sibling checkout `../purrtty`)
- **Commit:** `dd8e4e5` (`dd8e4e5bfdecec2fbd77a3b740bca5fbfa815130`)
- **purrTTY version:** `0.1.0`
- **Built with:** `dotnet build purrTTY.CustomShellContract -c Release` (.NET 10)

## Refresh command

Run from the purrtty checkout, then commit the updated DLLs **deliberately** (this changes the
inter-mod API surface — review the contract diff first):

```bash
cd ../purrtty
dotnet build purrTTY.CustomShellContract -c Release
cp purrTTY.CustomShellContract/bin/Release/net10.0/purrTTY.CustomShellContract.dll \
   purrTTY.CustomShellContract/bin/Release/net10.0/purrTTY.Logging.dll \
   ../gatOS/vendor/purrTTY/
# then update the Commit / version pins above
```

**Rule: refresh only deliberately. These DLLs pin the inter-mod API** — bumping them is an ABI
decision, not a routine sync.
