using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

namespace Thio_Universal_Agent;

public static class RuntimeHandlers
{
    public static int FindAvailablePort(int? defaultPortTry)
    {
        // Try the default port first if one was provided
        if (defaultPortTry.HasValue)
        {
            try
            {
                TcpListener defaultListener = new TcpListener(IPAddress.Loopback, defaultPortTry.Value);
                defaultListener.Start();
                defaultListener.Stop();
                return defaultPortTry.Value;
            }
            catch (SocketException)
            {
                // Default port is unavailable; fall through to let the OS assign one
            }
        }
        // --------------------------

        // Bind to port 0 to let the OS assign an available port
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        // Retrieve the port the OS actually assigned
        int assignedPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return assignedPort;
    }

    // Hands the current app's URL to a new instance if it tries to open
    public class SingleInstanceIpcService : BackgroundService
    {
        private const string PipeName = "ThioUniversalAgent_URL_Pipe";
        private readonly string _appUrl;

        // Pass the port in via Dependency Injection when we register it
        public SingleInstanceIpcService(int port)
        {
            _appUrl = $"http://localhost:{port}";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // This loop is fully managed by the .NET runtime. 
            // It yields the thread completely while waiting and cleanly exits on app shutdown.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using NamedPipeServerStream server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.Out,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    // The thread detaches here. The OS will trigger a callback to wake it up 
                    // only when a second instance tries to connect.
                    await server.WaitForConnectionAsync(stoppingToken);

                    using StreamWriter writer = new StreamWriter(server) { AutoFlush = true };
                    await writer.WriteLineAsync(_appUrl.AsMemory(), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // The app is shutting down (stoppingToken was cancelled). Exit cleanly.
                    break;
                }
                catch
                {
                    // If a client connects but immediately drops, catch it and wait for the next one.
                }
            }
        }
    }
}
