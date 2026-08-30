using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sw5e.Email.Tests.Support;

/// <summary>
/// How the test relay should answer each stage of the conversation.
/// </summary>
/// <remarks>
/// Every reply is settable so a test can make one specific step fail with one
/// specific code, which is the only way to prove the adapter's transient and
/// permanent classification is really reading the reply code.
/// </remarks>
internal sealed class TestSmtpServerBehaviour
{
    public bool AdvertiseAuthLogin { get; set; }

    public string AuthReply { get; set; } = "235 2.7.0 Authentication successful";

    public string MailFromReply { get; set; } = "250 2.1.0 Ok";

    public string RcptToReply { get; set; } = "250 2.1.5 Ok";

    public string DataReply { get; set; } = "250 2.0.0 Ok: queued as TESTQUEUEID";

    /// <summary>
    /// How long to sit on an accepted connection before greeting it, so a test
    /// can produce a relay that answers the socket and then goes quiet — which
    /// is what a wedged relay looks like and is not the same as one that
    /// refuses the connection outright.
    /// </summary>
    public TimeSpan GreetingDelay { get; set; } = TimeSpan.Zero;
}

/// <summary>
/// A real SMTP listener on a loopback ephemeral port.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the alternative tests nothing. Replacing
/// <see cref="System.Net.Mail.SmtpClient"/> with a fake would leave the
/// adapter's entire job — building a MIME message, negotiating the session,
/// authenticating, reading reply codes — unexercised, and a test asserting
/// that a fake was called is a test that passes whether or not any of that
/// works.
/// </para>
/// <para>
/// With a socket on the other end, the production adapter runs unmodified and
/// the test asserts on the bytes that came out of it. It speaks just enough of
/// RFC 5321 for a submission: greeting, EHLO, optional AUTH LOGIN, MAIL FROM,
/// RCPT TO, DATA, QUIT. It is not a mail server and does not try to be.
/// </para>
/// </remarks>
internal sealed class TestSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentQueue<string> _commands = new();

    public TestSmtpServer(TestSmtpServerBehaviour? behaviour = null)
    {
        Behaviour = behaviour ?? new TestSmtpServerBehaviour();

        // Port 0 means the operating system picks a free one, so tests running
        // in parallel — and CI agents running several jobs — never collide.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public TestSmtpServerBehaviour Behaviour { get; }

    public string Host => "127.0.0.1";

    public int Port { get; }

    /// <summary>The raw DATA payload of every message accepted, dot-unstuffed.</summary>
    public IReadOnlyList<string> Messages => [.. _messages];

    /// <summary>Every command line received, in order.</summary>
    public IReadOnlyList<string> Commands => [.. _commands];

    /// <summary>The decoded AUTH LOGIN username, if the client authenticated.</summary>
    public string? AuthenticatedUserName { get; private set; }

    /// <summary>The decoded AUTH LOGIN password, if the client authenticated.</summary>
    public string? AuthenticatedPassword { get; private set; }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);

                // Not awaited: a test may open more than one connection, and a
                // conversation that stalls must not block the next accept.
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal. Expected.
        }
        catch (SocketException)
        {
            // The listener was closed under us. Also disposal.
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            // Latin1 rather than UTF-8 so every byte on the wire round-trips
            // as a character. The message body is quoted-printable and
            // therefore ASCII, but reading it through a decoder that could
            // substitute replacement characters would silently corrupt exactly
            // the thing under test.
            using var reader = new StreamReader(stream, Encoding.Latin1, false, 1024, true);
            await using var writer = new StreamWriter(stream, Encoding.Latin1, 1024, true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            if (Behaviour.GreetingDelay > TimeSpan.Zero)
            {
                await Task.Delay(Behaviour.GreetingDelay, _shutdown.Token);
            }

            await writer.WriteLineAsync("220 test.invalid ESMTP ready");

            while (await reader.ReadLineAsync() is { } line)
            {
                _commands.Enqueue(line);

                var verb = line.Split(' ', 2)[0].ToUpperInvariant();

                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-test.invalid");
                        if (Behaviour.AdvertiseAuthLogin)
                        {
                            await writer.WriteLineAsync("250-AUTH LOGIN");
                        }

                        await writer.WriteLineAsync("250 HELP");
                        break;

                    case "HELO":
                        await writer.WriteLineAsync("250 test.invalid");
                        break;

                    case "AUTH":
                        await HandleAuthAsync(line, reader, writer);
                        break;

                    case "MAIL":
                        await writer.WriteLineAsync(Behaviour.MailFromReply);
                        break;

                    case "RCPT":
                        await writer.WriteLineAsync(Behaviour.RcptToReply);
                        break;

                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        _messages.Enqueue(await ReadDataAsync(reader));
                        await writer.WriteLineAsync(Behaviour.DataReply);
                        break;

                    case "RSET":
                        await writer.WriteLineAsync("250 2.0.0 Ok");
                        break;

                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;

                    default:
                        await writer.WriteLineAsync("500 5.5.2 Unrecognised command");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Runs the AUTH LOGIN exchange and records what the client sent.
    /// </summary>
    /// <remarks>
    /// Both forms are handled. RFC 4954 allows the initial response to be sent
    /// with the command — <c>AUTH LOGIN &lt;base64 username&gt;</c> — and that
    /// is what the framework's client does, but the two-challenge form is what
    /// most documentation shows, so accepting only one would make this a test
    /// of the fixture rather than of the adapter.
    /// </remarks>
    private async Task HandleAuthAsync(string line, StreamReader reader, StreamWriter writer)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 3)
        {
            AuthenticatedUserName = FromBase64(parts[2]);
        }
        else
        {
            // base64("Username:")
            await writer.WriteLineAsync("334 VXNlcm5hbWU6");
            AuthenticatedUserName = FromBase64(await reader.ReadLineAsync() ?? string.Empty);
        }

        // base64("Password:")
        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
        AuthenticatedPassword = FromBase64(await reader.ReadLineAsync() ?? string.Empty);

        await writer.WriteLineAsync(Behaviour.AuthReply);
    }

    /// <summary>
    /// Reads the DATA payload up to the lone-dot terminator, undoing the
    /// transparency stuffing RFC 5321 requires.
    /// </summary>
    private static async Task<string> ReadDataAsync(StreamReader reader)
    {
        var builder = new StringBuilder();

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line == ".")
            {
                break;
            }

            // A body line that genuinely begins with a dot is sent doubled so
            // it cannot be mistaken for the terminator. Undo that, or a
            // message whose text happens to start a line with a dot compares
            // unequal for reasons that have nothing to do with the adapter.
            builder.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    private static string FromBase64(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
        }
        catch (FormatException)
        {
            return $"(not base64: {value})";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _shutdown.Dispose();
    }
}
