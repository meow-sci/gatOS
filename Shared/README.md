# Shared runtime networking policy (v1)

`RuntimeBootstrap.cs` is the same source in purr2d2, gatOS and unscience. Each entry
project links its own copy into its entry module; there is no new DLL, package,
mod dependency, global resolver, Harmony patch or requirement that another mod be installed.
Update the copies together. `tools/check_runtime_bootstrap.py --peer-repo <checkout>`
checks source and regression-harness parity when other checkouts are available.

The module initializer explicitly loads `System.Net.Primitives` and `System.Net.Sockets`
through `AssemblyLoadContext.Default.LoadFromAssemblyName` before mod method execution.
It uses string assembly names and no networking types in its body. Preload failures are
retained for the guarded startup report, never thrown out of the module initializer.
The narrow CA2255 annotation documents application startup code in a StarMap entry
module; it does not disable other analyzer checks or change the warning policy.

The once-per-entry startup report checks the default constructor/property contract,
reads the real consuming modules' networking TypeRefs using PE metadata and
`Module.ResolveType`, and reports assembly identity, path and load context. Looking
only at `context.LoadFromAssemblyName` is insufficient: a module can retain an earlier
binding even after default copies have appeared. Any non-default networking copy is
reported as incompatible. The diagnostic opens no sockets and executes no game code.
A logging/inspection failure does not escape the startup report.

Do not ship .NET runtime, KSA/Brutal/Planet/Bepu host, Harmony or StarMap API DLLs in the
mod payload. Keep game assemblies in the game's context, and use explicit StarMap
imports for inter-mod contracts. Private third-party dependencies stay private.

This is an early mitigation, not a replacement for consistent framework resolution
in StarMap. The game and type discovery can run before a mod initializer; references
already bound to a second framework copy cannot be repaired by preloading. Diagnostics
therefore request a restart with a coherent loader/runtime installation.

# Regression checks

Run `python3 tools/check_runtime_bootstrap.py --entry-assembly <built-entry.dll>`.
`--entry-assembly` and `--peer-repo` may be repeated. The script builds temporary,
game-free fixtures from the production bootstrap source and runs each case in a fresh
.NET process. It verifies the actual built entry module calls the initializer.

The 19 cases cover the 15 standalone/pair/triple startup orders, concurrent startup,
an intentionally split framework as the negative control, bootstrap arriving after
consumer bindings, and a framework copy already loaded into the game context. The
negative control must reproduce both the missing TcpListener constructor and missing
LocalEndpoint getter. Healthy cases compile those exact call signatures without
opening a socket. Reports are idempotent and late/pre-existing failures include
paths, contexts and restart guidance. This establishes managed behavior, not a native
KSA/QEMU acceptance pass. The loader model follows StarMap's default-first,
game-context-next, mod-local-last resolution; game-directory runtime copies are modeled
using the current process runtime's assemblies.

All bootstrap, report and fixture state is process startup state. It contains no scene
objects, user configuration or durable choices; nothing is added to native saves or sidecars.
