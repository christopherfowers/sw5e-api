namespace Sw5e.Migrator;

/// <summary>
/// The command line, split into the command, the export's options, and the
/// settings the configuration builder is allowed to see.
/// </summary>
/// <remarks>
/// <para>
/// Parsed here rather than handed to the configuration provider, which is what
/// the migrator used to do with every <c>--</c> argument. That worked while the
/// only arguments were settings, and stops working the moment a command takes
/// one of its own: the provider rejects a bare flag such as <c>--check</c> with
/// a format error, and <c>--output /srv/content</c> leaves a loose
/// <c>/srv/content</c> that the command lookup would happily mistake for the
/// command. Both failures land at start-up, on a deploy, with a message about
/// argument format rather than about what was actually wrong.
/// </para>
/// <para>
/// So the export's own switches are consumed here and everything else is passed
/// through untouched, which keeps <c>--ConnectionStrings:Sw5e=...</c> working
/// exactly as it did.
/// </para>
/// </remarks>
/// <param name="Command">The command word, defaulting to <c>all</c>.</param>
/// <param name="Settings">Arguments to hand to the configuration builder.</param>
/// <param name="Export">What the <c>export</c> command was asked for.</param>
/// <param name="Error">
/// Why the command line could not be understood, or null when it could.
/// </param>
/// <remarks>
/// Public rather than internal only so the tests can reach it. Making it
/// visible through <c>InternalsVisibleTo</c> instead would also expose this
/// assembly's generated <c>Program</c>, which then collides with the API's in
/// any test project that references both.
/// </remarks>
public sealed record MigratorArguments(
    string Command,
    string[] Settings,
    ExportOptions Export,
    string? Error = null)
{
    /// <summary>Switches the export owns. Everything else is a setting.</summary>
    private static readonly string[] ValueSwitches = ["--output", "--type", "--key"];

    private static readonly string[] Flags = ["--check", "--no-prune"];

    public static MigratorArguments Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? command = null;
        string? output = null;
        string? contentType = null;
        string? key = null;
        var check = false;
        var prune = true;
        var settings = new List<string>();

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];

            // --name=value is split first so both spellings reach the same
            // place; the configuration provider accepts both and a command
            // line that works one way and not the other is a trap.
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator > 0 ? argument[..separator] : argument;
            var inline = separator > 0 ? argument[(separator + 1)..] : null;

            if (Flags.Contains(name, StringComparer.Ordinal))
            {
                if (inline is not null)
                {
                    return Failed($"'{name}' is a flag and takes no value.");
                }

                switch (name)
                {
                    case "--check":
                        check = true;
                        break;

                    default:
                        prune = false;
                        break;
                }

                continue;
            }

            if (ValueSwitches.Contains(name, StringComparer.Ordinal))
            {
                var value = inline;

                if (value is null)
                {
                    if (index + 1 >= arguments.Length)
                    {
                        return Failed($"'{name}' needs a value.");
                    }

                    value = arguments[++index];
                }

                switch (name)
                {
                    case "--output":
                        output = value;
                        break;

                    case "--type":
                        contentType = value;
                        break;

                    default:
                        key = value;
                        break;
                }

                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                settings.Add(argument);

                // A setting written as two tokens takes the next one with it,
                // so a value that happens to look like a command word is not
                // mistaken for one.
                if (inline is null && index + 1 < arguments.Length &&
                    !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    settings.Add(arguments[++index]);
                }

                continue;
            }

            if (argument.StartsWith('-'))
            {
                return Failed($"Unrecognised option '{argument}'.");
            }

            if (command is not null)
            {
                return Failed($"Only one command is accepted; got '{command}' and '{argument}'.");
            }

            command = argument;
        }

        return new MigratorArguments(
            command ?? "all",
            [.. settings],
            new ExportOptions(output, contentType, key, prune, check));

        static MigratorArguments Failed(string error) =>
            new("help", [], ExportOptions.None, error);
    }
}
