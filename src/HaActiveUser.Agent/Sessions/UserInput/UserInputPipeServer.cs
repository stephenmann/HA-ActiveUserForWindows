using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HaActiveUser.Agent.Sessions.UserInput;

/// <summary>
/// Receives idle reports from the per-session helpers. Reports are attributed to the SID of the
/// connecting process rather than anything in the payload, so a user can only report their own state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UserInputPipeServer(
    IUserInputTracker tracker,
    ILogger<UserInputPipeServer> logger,
    string pipeName = UserInputProtocol.PipeName) : BackgroundService
{
    /// <summary>A report is a decimal millisecond count, so anything longer is not one.</summary>
    private const int MaxReportLength = 32;

    private const long MaxReportedIdleMilliseconds = 7L * 24 * 60 * 60 * 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer(pipeName);
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                var accepted = server;
                server = null;
                _ = HandleClientAsync(accepted, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                logger.LogError(ex, "Idle report listener failed; retrying");
                await Task.Delay(UserInputProtocol.ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        string? sid = null;
        try
        {
            using (server)
            {
                var buffer = new byte[MaxReportLength];
                while (await ReadReportAsync(server, buffer, cancellationToken).ConfigureAwait(false) is { } line)
                {
                    sid ??= ResolveClientSid(server);
                    if (sid is null)
                    {
                        return;
                    }

                    // NumberStyles.None rejects signs and whitespace; the bound keeps a hostile
                    // value from overflowing TimeSpan. Anything past the stale window is useless.
                    if (long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var idleMilliseconds)
                        && idleMilliseconds <= MaxReportedIdleMilliseconds)
                    {
                        tracker.Report(sid, TimeSpan.FromMilliseconds(idleMilliseconds));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Idle reporter for {Sid} disconnected", sid ?? "unknown");
        }
    }

    /// <summary>
    /// Reads one newline-terminated report. Any authenticated user can write to this pipe, so the
    /// line is length-capped rather than buffered until a newline arrives.
    /// </summary>
    private static async Task<string?> ReadReportAsync(
        PipeStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            if (await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }

            if (buffer[length] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\r');
            }

            length++;
        }

        return null;
    }

    private string? ResolveClientSid(NamedPipeServerStream server)
    {
        try
        {
            string? sid = null;
            server.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
            return sid;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not identify the caller behind an idle report");
            return null;
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.Write | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }
}
