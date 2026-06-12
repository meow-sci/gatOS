using Tomlyn;
using Tomlyn.Model;

namespace gatOS.Vm;

/// <summary>
///     The parsed <c>manifest.toml</c> produced by the guest image pipeline (OS_PLAN.md T2.3) —
///     the host↔guest boot contract: which artifacts to boot, the kernel command line, how to
///     SSH in, and the pinned dropbear host key.
/// </summary>
/// <param name="GuestVersion">The guest release number (matches <c>guest/GUEST_VERSION</c>).</param>
/// <param name="AlpineVersion">The Alpine release the image was built from (informational).</param>
/// <param name="Kernel">Kernel artifact filename, relative to the manifest.</param>
/// <param name="Initrd">Initramfs artifact filename, relative to the manifest.</param>
/// <param name="BaseImage">Base qcow2 artifact filename, relative to the manifest.</param>
/// <param name="KernelCmdline">The base kernel command line (the host appends <c>gatos.simport=…</c>).</param>
/// <param name="SshUser">Guest user to SSH in as.</param>
/// <param name="SshKey">Private key artifact filename, relative to the manifest.</param>
/// <param name="HostKeySha256">
///     Pin for the guest's dropbear host key: lowercase sha256 hex of the raw ssh-ed25519 public
///     key blob (exactly what SSH.NET's <c>HostKeyReceived.HostKey</c> hashes to).
/// </param>
public sealed record GuestManifest(
    int GuestVersion,
    string AlpineVersion,
    string Kernel,
    string Initrd,
    string BaseImage,
    string KernelCmdline,
    string SshUser,
    string SshKey,
    string HostKeySha256)
{
    /// <summary>The manifest schema version this code understands.</summary>
    public const int SupportedSchema = 1;

    /// <summary>Loads and validates a manifest file.</summary>
    /// <exception cref="InvalidDataException">The file is not valid TOML, has an unsupported schema, or misses required keys.</exception>
    public static GuestManifest Load(string path)
    {
        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))
                    ?? throw new InvalidDataException($"Guest manifest '{path}' is empty.");
        }
        catch (TomlException ex)
        {
            throw new InvalidDataException($"Guest manifest '{path}' is not valid TOML: {ex.Message}", ex);
        }

        var schema = GetInt(table, "schema", path);
        if (schema != SupportedSchema)
            throw new InvalidDataException(
                $"Guest manifest '{path}' has schema {schema}; this build understands schema {SupportedSchema}. "
                + "Update the mod (or the guest image) so they match.");

        return new GuestManifest(
            GuestVersion: GetInt(table, "guest_version", path),
            AlpineVersion: GetString(table, "alpine_version", path),
            Kernel: GetString(table, "kernel", path),
            Initrd: GetString(table, "initrd", path),
            BaseImage: GetString(table, "base_image", path),
            KernelCmdline: GetString(table, "kernel_cmdline", path),
            SshUser: GetString(table, "ssh_user", path),
            SshKey: GetString(table, "ssh_key", path),
            HostKeySha256: GetString(table, "host_key_sha256", path).ToLowerInvariant());
    }

    private static string GetString(TomlTable table, string key, string path)
        => table.TryGetValue(key, out var value) && value is string s && s.Length > 0
            ? s
            : throw new InvalidDataException($"Guest manifest '{path}' is missing required key '{key}'.");

    private static int GetInt(TomlTable table, string key, string path)
        => table.TryGetValue(key, out var value) && value is long l
            ? checked((int)l)
            : throw new InvalidDataException($"Guest manifest '{path}' is missing required integer key '{key}'.");
}
