using System.Globalization;
using System.Text.Json;
using gatOS.Http;
using gatOS.Mqtt;
using gatOS.SimFs.Snapshots;
using gatOS.Ssh;
using gatOS.Vm;
using Renci.SshNet;

namespace gatOS.SimFs.Tests.Integration;

/// <summary>
///     The guest-v3 exit criterion for the extra transports (G5 HTTP, MQTT): boot the real
///     guest with the host's HTTP server and MQTT broker wired through
///     <see cref="VmHostOptions.HttpPortProvider"/> / <see cref="VmHostOptions.MqttPortProvider"/>,
///     and prove the guest-side activation that only v3 ships — the
///     <c>/etc/profile.d/gatos.sh</c> env (<c>$GATOS_HTTP</c>/<c>$GATOS_MQTT</c>), the
///     <c>/run/gatos/*-port</c> files, the <c>sim</c> host alias, and a real telemetry read
///     over slirp from inside the guest. Gated by <c>GATOS_IT=1</c>.
/// </summary>
/// <remarks>
///     The slirp path is identical for both transports (guest dials <c>10.0.2.2</c> = host
///     loopback, aliased to <c>sim</c> in <c>/etc/hosts</c>), so a successful HTTP GET proves
///     outbound reachability for MQTT too; the MQTT leg additionally confirms the broker accepts
///     the guest's TCP connection. Host-side protocol conformance is covered by
///     <c>gatOS.Http.Tests</c> and <c>gatOS.Mqtt.Tests</c>; this fixture is the in-guest wiring.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class TransportEnvIntegrationTests
{
    private string? _tempRoot;
    private CancellationTokenSource? _publisher;

    [SetUp]
    public void SetUp()
    {
        TestEnv.RequireIntegration();
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-it-transport-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
        _publisher = new CancellationTokenSource();
    }

    [TearDown]
    public void TearDown()
    {
        _publisher?.Cancel();
        _publisher?.Dispose();
        GatOsPaths.OverrideDataDirForTests(null);
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public async Task GuestActivatesHttpAndMqttEnv_AndReadsTelemetryOverSlirp()
    {
        // Host stack: one store feeding both extra transports, with a scripted flight so the
        // HTTP read returns live, advancing telemetry.
        var store = new SnapshotStore();
        await using var http = new SimHttpServer(store);
        await http.StartAsync();
        await using var mqtt = new SimMqttBroker(store);
        await mqtt.StartAsync(preferredPort: 0);
        _ = Task.Run(() => PublishFlightAsync(store, _publisher!.Token));

        var host = new VmHost(new VmHostOptions
        {
            Profile = "it-transport",
            GuestAssetsDir = TestEnv.RequireGuestAssetsDir(),
            HttpPortProvider = () => http.Port,
            MqttPortProvider = () => mqtt.Port,
        });
        await using var broker = new VmConnectionBroker(host);
        using var ssh = await broker.ConnectAsync(CancellationToken.None);

        // 1) The guest env file exists and exports the documented values. RunCommand is not a
        //    login shell, so source the profile.d drop-in explicitly (a login PTY sources it
        //    automatically; this checks the file the init script wrote at boot).
        var httpEnv = Run(ssh, ". /etc/profile.d/gatos.sh; echo \"$GATOS_HTTP\"").Trim();
        Assert.That(httpEnv, Is.EqualTo($"http://sim:{http.Port}/v1"), "$GATOS_HTTP points at the host server via the 'sim' alias");
        var mqttEnv = Run(ssh, ". /etc/profile.d/gatos.sh; echo \"$GATOS_MQTT\"").Trim();
        Assert.That(mqttEnv, Is.EqualTo($"sim:{mqtt.Port}"), "$GATOS_MQTT carries host:port");

        // 2) The bare-port files for non-shell consumers.
        Assert.That(Run(ssh, "cat /run/gatos/http-port").Trim(), Is.EqualTo(http.Port.ToString(CultureInfo.InvariantCulture)));
        Assert.That(Run(ssh, "cat /run/gatos/mqtt-port").Trim(), Is.EqualTo(mqtt.Port.ToString(CultureInfo.InvariantCulture)));

        // 3) The 'sim' host alias resolves to the slirp gateway (host loopback).
        Assert.That(Run(ssh, "getent hosts sim || grep -w sim /etc/hosts").Trim(), Does.Contain("10.0.2.2"));

        // 4) The real test: an HTTP GET from inside the guest, over slirp, to the host's server.
        //    busybox wget; /v1/time returns JSON with an advancing 'ut'.
        var body = Run(ssh, ". /etc/profile.d/gatos.sh; wget -q -T 10 -O - \"$GATOS_HTTP/time\"").Trim();
        Assert.That(body, Is.Not.Empty, "wget should return the /v1/time document over slirp");
        using (var json = JsonDocument.Parse(body))
        {
            Assert.That(json.RootElement.GetProperty("ut").GetDouble(), Is.GreaterThan(0),
                "the guest read live telemetry from the host HTTP server");
        }

        // 5) The MQTT broker accepts the guest's TCP connection over the same slirp path
        //    (busybox nc; the broker holds the socket so a successful connect returns 0).
        var reach = Run(ssh, $"nc -w 4 sim {mqtt.Port} </dev/null >/dev/null 2>&1; echo $?").Trim();
        Assert.That(reach, Is.EqualTo("0"), "the guest can open a TCP connection to the MQTT broker over slirp");

        await broker.VmHost.StopAsync(TimeSpan.FromSeconds(30));
        Assert.That(broker.VmHost.Status.State, Is.EqualTo(VmState.Stopped));
    }

    private static string Run(SshClient ssh, string command)
    {
        using var cmd = ssh.RunCommand(command);
        return cmd.Result;
    }

    private static async Task PublishFlightAsync(SnapshotStore store, CancellationToken ct)
    {
        var sequence = 0L;
        while (!ct.IsCancellationRequested)
        {
            sequence++;
            store.Publish(TestData.Snapshot(sequence, TestData.Vessel(radarAltitude: sequence)));
            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
