using System.Security.Cryptography;

/// <summary>Assertion helpers and fixtures shared by the cross-platform CD regression program.
/// The program has no test framework, matching tests/DiscImageStudio.ArchitectureTests.</summary>
internal static class Check
{
    internal static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false.");
        }
    }

    internal static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}.");
        }
    }

    internal static void Throws<TException>(Action action, string name)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}.");
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal static class Fixtures
{
    private static readonly Lazy<string> RepositoryRootValue = new(() =>
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiscImageStudio.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate DiscImageStudio.slnx above the test binary.");
    });

    internal static string RepositoryRoot => RepositoryRootValue.Value;

    /// <summary>The same 256 px application icon the CI smoke test feeds to cd-generate.</summary>
    internal static string IconImage => Path.Combine(RepositoryRoot, "src", "DiscImageStudio.App", "Assets", "DiscImageStudio-256.png");

    internal static string CreateWorkDirectory(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "dis-cd-tests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
