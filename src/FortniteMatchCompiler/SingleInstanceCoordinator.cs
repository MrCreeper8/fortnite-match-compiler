using System.IO.Pipes;
using System.Text;

namespace FortniteMatchCompiler;

public static class SingleInstanceCoordinator
{
    private const string PipeName = "FortniteMatchCompiler-Commands-v1";

    public static bool NotifyExisting(string command)
    {
        return NotifyExisting(command, PipeName);
    }

    public static bool NotifyExisting(string command, string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(timeout: 1500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (Exception exception)
        {
            AppLogger.Write($"Could not notify the existing app instance: {exception.Message}");
            return false;
        }
    }

    public static async Task ListenAsync(
        Action<string> commandHandler,
        CancellationToken cancellationToken)
    {
        await ListenAsync(commandHandler, cancellationToken, PipeName);
    }

    public static async Task ListenAsync(
        Action<string> commandHandler,
        CancellationToken cancellationToken,
        string pipeName)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    commandHandler(command);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                AppLogger.Write($"Single-instance command listener error: {exception.Message}");
                await Task.Delay(250, cancellationToken);
            }
        }
    }
}
