namespace FirstmateTelegram.Tests.Support;

/// <summary>Output captured from FirstMate's real scripts (see Fixtures/firstmate-e9a6675/README.md) and quota-axi fixtures hand-derived from the spec.</summary>
public static class Fixtures
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "firstmate-e9a6675");

    public static string Read(string name) => File.ReadAllText(Path.Combine(Directory, name));

    public static string ReadQuota(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "quota-axi", name));
}
