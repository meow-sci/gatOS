using MeowSci.ModRuntime;

namespace RuntimeFixture;

public static class Entry
{
    public static bool Start(string name) => RuntimeBootstrap.Report(name, "NetworkConsumer");
}
