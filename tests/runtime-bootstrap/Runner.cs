using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string[] fixtures = ["GatOSFixture", "UnscienceFixture", "Purr2D2Fixture"];
if (args.Length > 0 && args[0] == "--child") return Child(args[1], args.Skip(2).ToArray());
for (int i = 0; i < args.Length; i += 2)
{
    Require(args[i] == "--verify-entry" && i + 1 < args.Length, "Unexpected runner arguments.");
    VerifyEntry(args[i + 1]);
}
var cases = new List<string[]>();
foreach (string a in fixtures)
{
    cases.Add(["healthy", a]);
    foreach (string b in fixtures.Where(b => b != a))
    {
        cases.Add(["healthy", a, b]);
        foreach (string c in fixtures.Where(c => c != a && c != b)) cases.Add(["healthy", a, b, c]);
    }
}
cases.Add(["split"]);
cases.Add(["late", .. fixtures]);
cases.Add(["game-first", .. fixtures]);
cases.Add(["concurrent", .. fixtures]);
foreach (string[] scenario in cases)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("--child");
    foreach (string argument in scenario) start.ArgumentList.Add(argument);
    using Process process = Process.Start(start)!;
    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    if (process.ExitCode != 0) throw new Exception(string.Join(" ", scenario) + "\n" + await stdout + await stderr);
}
Console.WriteLine($"PASS: runtime bootstrap — {cases.Count} isolated cases; all standalone/pair/triple orders, concurrent startup, exact missing-method reproduction, late binding and pre-existing game copies.");
return 0;

static int Child(string mode, string[] order)
{
    var output = new StringWriter();
    Console.SetOut(TextWriter.Synchronized(output));
    try
    {
        string directory = AppContext.BaseDirectory;
        var game = new GameContext(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var poisoned = new ModContext(game, directory);
        Assembly? consumer = null;
        if (mode is "split" or "late" or "game-first")
        {
            game.LoadFromAssemblyName(new("System.Net.Primitives"));
            if (mode != "game-first")
            {
                consumer = poisoned.LoadFromAssemblyPath(Path.Combine(directory, "NetworkConsumer.dll"));
                BindPrimitives(consumer);
                var sockets = AssemblyLoadContext.Default.LoadFromAssemblyName(new("System.Net.Sockets"));
                // Resolve the default socket signatures against their own default primitives.
                _ = sockets.GetType("System.Net.Sockets.TcpListener", true)!.GetConstructors()
                    .SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToArray();
            }
        }
        if (mode == "split") RequireMissingMethods(consumer!);
        else
        {
            var entries = order.Select(name =>
            {
                var context = mode == "late" ? poisoned : new ModContext(game, directory);
                var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, name + ".dll"));
                // StarMap discovers all entry types and then constructs/invokes the mod.
                _ = assembly.GetTypes();
                return (Name: name, Context: context, Start: assembly.GetType("RuntimeFixture.Entry", true)!.GetMethod("Start")!);
            }).ToArray();
            bool expected = mode is not ("late" or "game-first");
            bool Start(MethodInfo method, string name) => (bool)method.Invoke(null, [name])!;
            if (mode == "concurrent")
                Task.WhenAll(entries.Select(e => Task.Run(() => Require(Start(e.Start, e.Name), "Concurrent startup was incompatible.")))).GetAwaiter().GetResult();
            else foreach (var entry in entries)
                Require(Start(entry.Start, entry.Name) == expected, entry.Name + ": incorrect compatibility result.");
            foreach (var entry in entries)
            {
                int before = output.ToString().Length;
                Require(Start(entry.Start, entry.Name) == expected, "Repeated report changed its result.");
                Require(output.ToString().Length == before, "Repeated report emitted duplicate diagnostics.");
                if (expected) CompileCalls(entry.Context.LoadFromAssemblyName(new("NetworkConsumer")));
            }
            string log = output.ToString();
            Require(log.Contains("context=Default") && log.Contains("path="), "Assembly ownership evidence missing.");
            if (!expected)
            {
                Require(log.Contains("INCOMPATIBLE") && log.Contains("Restart the game") && log.Contains("context=Game#"), "Actionable mismatch evidence missing.");
                if (mode == "late")
                {
                    Require(log.Contains("NetworkConsumer binds System.Net.IPAddress") && log.Contains("LocalEndpoint"), "Actual retained consumer bindings were not diagnosed.");
                    RequireMissingMethods(consumer!);
                }
            }
        }
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(output.ToString());
        Console.Error.WriteLine(error);
        return 1;
    }
}

static void BindPrimitives(Assembly assembly)
{
    using var stream = File.OpenRead(assembly.Location);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    foreach (var handle in metadata.TypeReferences)
    {
        var reference = metadata.GetTypeReference(handle);
        if (metadata.GetString(reference.Namespace) == "System.Net")
            assembly.ManifestModule.ResolveType(MetadataTokens.GetToken(handle));
    }
}

static void CompileCalls(Assembly assembly)
{
    foreach (string name in new[] { "Create", "Endpoint" })
        RuntimeHelpers.PrepareMethod(assembly.GetType("NetworkConsumer.Calls", true)!.GetMethod(name)!.MethodHandle);
}

static void RequireMissingMethods(Assembly assembly)
{
    foreach (var item in new[] { ("Create", "TcpListener..ctor"), ("Endpoint", "get_LocalEndpoint") })
    {
        try
        {
            RuntimeHelpers.PrepareMethod(assembly.GetType("NetworkConsumer.Calls", true)!.GetMethod(item.Item1)!.MethodHandle);
            throw new Exception("The negative control did not reproduce " + item.Item2);
        }
        catch (MissingMethodException error) { Require(error.Message.Contains(item.Item2), "Unexpected missing method: " + error.Message); }
    }
}

static void VerifyEntry(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var helper = metadata.TypeDefinitions.Single(h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "RuntimeBootstrap"
        && metadata.GetString(metadata.GetTypeDefinition(h).Namespace) == "MeowSci.ModRuntime");
    var initialize = metadata.GetTypeDefinition(helper).GetMethods().Single(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "Initialize");
    var module = metadata.TypeDefinitions.Single(h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "<Module>");
    var cctor = metadata.GetTypeDefinition(module).GetMethods().Single(h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == ".cctor");
    byte[] il = pe.GetMethodBody(metadata.GetMethodDefinition(cctor).RelativeVirtualAddress).GetILBytes()!;
    int token = MetadataTokens.GetToken(initialize);
    Require(Enumerable.Range(0, Math.Max(0, il.Length - 4)).Any(i => il[i] == 0x28 && BitConverter.ToInt32(il, i + 1) == token), "Production module does not call RuntimeBootstrap.Initialize.");
    Console.WriteLine("PASS: production entry module initializes the shared runtime bootstrap.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class GameContext(string runtime) : AssemblyLoadContext("Game")
{
    protected override Assembly? Load(AssemblyName name)
    {
        var existing = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        if (existing != null) return existing;
        string path = Path.Combine(runtime, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}

sealed class ModContext(GameContext game, string directory) : AssemblyLoadContext("Mod")
{
    protected override Assembly? Load(AssemblyName name)
    {
        var existing = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name)
            ?? game.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        if (existing != null) return existing;
        try { return game.LoadFromAssemblyName(name); }
        catch (FileNotFoundException)
        {
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
