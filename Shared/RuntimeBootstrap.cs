using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace MeowSci.ModRuntime;

/// <summary>Source-shared startup policy; compile into the entry module, never a separate dependency.</summary>
internal static class RuntimeBootstrap
{
    private static readonly object Gate = new();
    private static string? _preloadFailure;
    private static bool _reported, _compatible;
    internal static string? Failure { get; private set; }

    // Strings only: compiling this body must not bind IPAddress/TcpListener in the mod context.
    // Metadata inspection and consumer loading belong to the later, guarded startup report.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "Application startup code in StarMap entry modules; preloading must precede mod method execution.")]
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("System.Net.Primitives"));
            AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("System.Net.Sockets"));
        }
        catch (Exception error) { _preloadFailure = error.ToString(); }
    }

    /// <summary>Log once per entry module, including actual consumer bindings, without opening sockets.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Report(string modId, params string[] consumers)
    {
        lock (Gate)
        {
            if (_reported) return _compatible;
            _reported = true;
            var errors = new List<string>();
            var details = new List<string>();
            try
            {
                if (_preloadFailure != null) errors.Add("Default networking preload failed: " + _preloadFailure);
                Inspect(consumers, errors, details);
            }
            catch (Exception error) { errors.Add("Networking inspection failed: " + error); }
            _compatible = errors.Count == 0;
            Failure = _compatible ? null : "Incompatible .NET networking assemblies. Restart the game with a coherent "
                + "StarMap/runtime installation; already-bound references cannot be repaired by late preloading. "
                + string.Join(" | ", errors);
            Write($"[{modId}] Runtime networking: {(_compatible ? "ready" : "INCOMPATIBLE")}.");
            foreach (string detail in details) Write($"[{modId}] Runtime networking: {detail}");
            if (Failure != null) Write($"[{modId}] {Failure}");
            return _compatible;
        }
    }

    private static void Inspect(string[] consumers, List<string> errors, List<string> details)
    {
        Assembly entry = typeof(RuntimeBootstrap).Assembly;
        var context = AssemblyLoadContext.GetLoadContext(entry) ?? AssemblyLoadContext.Default;
        Assembly primitives = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("System.Net.Primitives"));
        Assembly sockets = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("System.Net.Sockets"));
        Type address = primitives.GetType("System.Net.IPAddress", true)!;
        Type endpoint = primitives.GetType("System.Net.EndPoint", true)!;
        Type listener = sockets.GetType("System.Net.Sockets.TcpListener", true)!;
        CheckContract("default runtime", address, endpoint, listener, errors);

        // Inspect TypeRefs in the actual consuming module, not just context.LoadFromAssemblyName:
        // a module can retain an earlier binding even when the context now returns a default copy.
        foreach (Assembly consumer in new[] { entry }.Concat(consumers.Select(name =>
                     context.LoadFromAssemblyName(new AssemblyName(name)))).Distinct())
            InspectConsumer(consumer, primitives, sockets, errors);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => a.GetName().Name is "System.Net.Primitives" or "System.Net.Sockets")
                     .OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            details.Add(Describe(assembly));
            if (AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default)
                errors.Add("Non-default networking copy: " + Describe(assembly));
        }
    }

    private static void InspectConsumer(Assembly consumer, Assembly primitives, Assembly sockets, List<string> errors)
    {
        if (string.IsNullOrEmpty(consumer.Location))
            throw new InvalidOperationException("Cannot inspect a fileless consumer: " + consumer.FullName);
        using var stream = File.OpenRead(consumer.Location);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            TypeReference reference = metadata.GetTypeReference(handle);
            string ns = metadata.GetString(reference.Namespace), name = metadata.GetString(reference.Name);
            Assembly? expected = ns == "System.Net" && name is "IPAddress" or "EndPoint" or "IPEndPoint"
                ? primitives : ns == "System.Net.Sockets" && name is "TcpListener" or "TcpClient" or "Socket" ? sockets : null;
            if (expected == null) continue;
            Type bound = consumer.ManifestModule.ResolveType(MetadataTokens.GetToken(handle));
            types[ns + "." + name] = bound;
            if (!ReferenceEquals(bound.Assembly, expected))
                errors.Add($"{consumer.GetName().Name} binds {bound.FullName} to {Describe(bound.Assembly)}; expected {Describe(expected)}");
        }
        if (types.TryGetValue("System.Net.IPAddress", out Type? address)
            && types.TryGetValue("System.Net.EndPoint", out Type? endpoint)
            && types.TryGetValue("System.Net.Sockets.TcpListener", out Type? listener))
            CheckContract(consumer.GetName().Name!, address, endpoint, listener, errors);
    }

    private static void CheckContract(string consumer, Type address, Type endpoint, Type listener, List<string> errors)
    {
        if (listener.GetConstructor(new[] { address, typeof(int) }) == null)
            errors.Add(consumer + ": TcpListener(IPAddress, Int32) does not match the bound IPAddress type.");
        if (listener.GetProperty("LocalEndpoint")?.PropertyType != endpoint)
            errors.Add(consumer + ": TcpListener.LocalEndpoint does not match the bound EndPoint type.");
    }

    private static string Describe(Assembly assembly)
    {
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        string owner = context == AssemblyLoadContext.Default ? "Default"
            : (context?.Name ?? "unnamed") + "#" + (context == null ? 0 : RuntimeHelpers.GetHashCode(context));
        return $"{assembly.FullName}; context={owner}; path={assembly.Location}";
    }

    // A diagnostic sink failure must never escape a module initializer or abort unrelated game startup.
    private static void Write(string line)
    {
        try { Console.WriteLine(line); }
        catch (Exception) { }
    }
}
