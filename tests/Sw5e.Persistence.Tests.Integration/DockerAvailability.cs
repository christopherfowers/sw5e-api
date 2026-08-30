namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// Whether a Docker daemon can be reached, and therefore whether the tests in
/// this project can run.
/// </summary>
/// <remarks>
/// <para>
/// These tests run against a real PostgreSQL container because the things they
/// check — that a migration applies, that a check constraint refuses a bad row,
/// that byte-order collation produces the ordering the file-backed store does —
/// are properties of PostgreSQL, not of C#. An in-memory or SQLite substitute
/// would answer every one of them differently and would pass while the real
/// database was broken.
/// </para>
/// <para>
/// Not every machine has a daemon; this one does not. Rather than making the
/// whole suite unrunnable there, the tests skip themselves when Docker is
/// unreachable. That is a real risk — a suite that can silently test nothing is
/// a suite that eventually does — so CI carries a step that fails the build if
/// anything was skipped on a runner that has a daemon. The skip is a
/// convenience for a developer's machine, not a way for the database to go
/// untested.
/// </para>
/// </remarks>
internal static class DockerAvailability
{
    /// <summary>Whether a Docker endpoint appears to exist.</summary>
    public static bool IsAvailable { get; } = Probe();

    /// <summary>Why the tests were skipped, or null when they were not.</summary>
    public static string? SkipReason { get; } = IsAvailable
        ? null
        : "No Docker daemon is reachable, so PostgreSQL cannot be started. " +
          "CI fails the build if this skip happens on a runner that has one.";

    private static bool Probe()
    {
        // An explicitly configured endpoint is taken at its word: it may be a
        // remote daemon, a rootless socket in an unusual place, or a TCP
        // address, none of which are findable by looking at the default paths.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            // Named pipes are not visible to File.Exists; they are enumerable as
            // entries of the pipe filesystem. Docker Desktop removes this pipe
            // when the engine stops, so its presence tracks the daemon rather
            // than the installation.
            try
            {
                return Directory.EnumerateFiles(@"\\.\pipe\")
                                .Any(pipe => pipe.EndsWith("docker_engine", StringComparison.Ordinal));
            }
            catch (IOException)
            {
                return false;
            }
        }

        return File.Exists("/var/run/docker.sock");
    }
}

/// <summary>A fact that skips itself when no Docker daemon is reachable.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute() => Skip = DockerAvailability.SkipReason;
}

/// <summary>A theory that skips itself when no Docker daemon is reachable.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute() => Skip = DockerAvailability.SkipReason;
}
