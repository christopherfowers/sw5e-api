namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// A writable copy of the content fixture, for the tests that need the corpus
/// to change between two imports.
/// </summary>
/// <remarks>
/// A copy rather than edits in place: the committed fixture is read by every
/// other test in this project and by the parity comparison, and a test that
/// edited it would make the rest of the suite depend on execution order.
/// </remarks>
public sealed class TempCorpus : IDisposable
{
    private TempCorpus(string root) => Root = root;

    /// <summary>Absolute path to the copy.</summary>
    public string Root { get; }

    /// <summary>Copies the committed fixture into a fresh temporary directory.</summary>
    public static TempCorpus FromFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "sw5e-corpus-" + Guid.NewGuid().ToString("n"));

        foreach (var source in Directory.EnumerateFiles(ContentFixture.Path, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(ContentFixture.Path, source);
            var destination = Path.Combine(root, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }

        return new TempCorpus(root);
    }

    /// <summary>An empty directory, which is what an unmounted content volume looks like.</summary>
    public static TempCorpus Empty()
    {
        var root = Path.Combine(Path.GetTempPath(), "sw5e-corpus-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        return new TempCorpus(root);
    }

    /// <summary>Path to one document within the copy.</summary>
    public string PathTo(string type, string key) =>
        Path.Combine(Root, type, key + ".json");

    /// <summary>Rewrites one document, replacing a substring of its text.</summary>
    public void Edit(string type, string key, string find, string replace)
    {
        var path = PathTo(type, key);
        var text = File.ReadAllText(path);

        if (!text.Contains(find, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{find}' does not appear in {type}/{key}.json, so this edit would be a no-op " +
                "and the test that depends on it would pass for the wrong reason.");
        }

        File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));
    }

    /// <summary>Removes one document from the copy.</summary>
    public void Remove(string type, string key) => File.Delete(PathTo(type, key));

    /// <summary>Removes a whole type directory, as a half-copied volume would.</summary>
    public void RemoveType(string type) => Directory.Delete(Path.Combine(Root, type), recursive: true);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}
