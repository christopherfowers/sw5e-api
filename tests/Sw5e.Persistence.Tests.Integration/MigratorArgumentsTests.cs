using Shouldly;
using Sw5e.Migrator;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The migrator's command line, which a deploy depends on being read correctly.
/// </summary>
/// <remarks>
/// Every one of these used to be handled by handing anything beginning with
/// <c>--</c> to the configuration builder and taking the first bare word as the
/// command. That is fine while the only arguments are settings and stops being
/// fine the moment a command takes one of its own: a bare <c>--check</c> makes
/// the configuration provider throw a format error before the process is up,
/// and the <c>/srv/content</c> in <c>--output /srv/content</c> is a bare word
/// that the command lookup would happily have run.
/// </remarks>
public sealed class MigratorArgumentsTests
{
    [Theory]
    [InlineData(new string[0], "all")]
    [InlineData(new[] { "migrate" }, "migrate")]
    [InlineData(new[] { "import" }, "import")]
    [InlineData(new[] { "export" }, "export")]
    public void TheCommandIsTheBareWord(string[] arguments, string expected)
    {
        var parsed = MigratorArguments.Parse(arguments);

        parsed.Error.ShouldBeNull();
        parsed.Command.ShouldBe(expected);
    }

    [Fact]
    public void SettingsStillReachTheConfigurationBuilderUntouched()
    {
        var parsed = MigratorArguments.Parse(
            ["migrate", "--ConnectionStrings:Sw5e=Host=db", "--Sw5e:Database:MaxRetryCount", "3"]);

        parsed.Error.ShouldBeNull();
        parsed.Command.ShouldBe("migrate");
        parsed.Settings.ShouldBe(
            ["--ConnectionStrings:Sw5e=Host=db", "--Sw5e:Database:MaxRetryCount", "3"]);
    }

    /// <summary>
    /// A setting's value is not mistaken for the command.
    /// </summary>
    /// <remarks>
    /// This is the failure that would be found on a deploy rather than here:
    /// the migrator would report "Unknown command '/srv/content'" and exit
    /// without applying a migration, and the connection string in the same
    /// command line would make it look like a configuration problem.
    /// </remarks>
    [Fact]
    public void AValueIsNotMistakenForTheCommand()
    {
        var parsed = MigratorArguments.Parse(
            ["--Content:RootPath", "/srv/content", "import"]);

        parsed.Error.ShouldBeNull();
        parsed.Command.ShouldBe("import");
        parsed.Settings.ShouldBe(["--Content:RootPath", "/srv/content"]);
    }

    [Fact]
    public void TheExportSwitchesAreTakenOutOfTheSettings()
    {
        var parsed = MigratorArguments.Parse(
            ["export", "--output", "/repo/content", "--type", "monster", "--key", "rancor-adult",
             "--check", "--no-prune", "--ConnectionStrings:Sw5e=Host=db"]);

        parsed.Error.ShouldBeNull();
        parsed.Command.ShouldBe("export");
        parsed.Settings.ShouldBe(["--ConnectionStrings:Sw5e=Host=db"]);
        parsed.Export.ShouldBe(
            new ExportOptions("/repo/content", "monster", "rancor-adult", Prune: false, Check: true));
    }

    [Fact]
    public void TheEqualsSpellingMeansTheSameThing()
    {
        MigratorArguments.Parse(["export", "--output=/repo/content", "--type=monster"]).Export
            .ShouldBe(MigratorArguments.Parse(
                ["export", "--output", "/repo/content", "--type", "monster"]).Export);
    }

    [Fact]
    public void ExportDefaultsToTheWholeCatalogueAndToPruning()
    {
        var parsed = MigratorArguments.Parse(["export", "--output", "/repo/content"]);

        parsed.Export.ShouldBe(new ExportOptions("/repo/content"));
        parsed.Export.Prune.ShouldBeTrue();
        parsed.Export.Check.ShouldBeFalse();
    }

    [Theory]
    [InlineData(new[] { "export", "--output" }, "needs a value")]
    [InlineData(new[] { "export", "--check=true" }, "takes no value")]
    [InlineData(new[] { "migrate", "import" }, "Only one command")]
    [InlineData(new[] { "-x" }, "Unrecognised option")]
    public void AnUnusableCommandLineIsRefusedWithAReason(string[] arguments, string expected)
    {
        var parsed = MigratorArguments.Parse(arguments);

        parsed.Error.ShouldNotBeNull();
        parsed.Error.ShouldContain(expected);
    }
}
