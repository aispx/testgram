using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 10.3 — a minimal real MongoDB harness for the ingestion integration test.
///
/// <para>Launches a real <c>mongod</c> process (the actual MongoDB server, not a mock) against a throwaway
/// temp data directory on a free localhost port, waits until it accepts connections, and exposes an
/// <see cref="IMongoDatabase"/> for the production <c>MetricsStore</c> to run against. Disposing the server
/// terminates the process and removes the temp data directory.</para>
///
/// <para>There is no embedded-Mongo NuGet harness in this repository (no Mongo2Go / EphemeralMongo /
/// Testcontainers reference), so rather than adding one without network access this harness drives the
/// <c>mongod</c> binary already present on the machine. When no <c>mongod</c> binary is available the
/// integration test skips cleanly via <see cref="MongoAvailable"/> / <see cref="RequiresMongoDbFactAttribute"/>.</para>
/// </summary>
public sealed class EmbeddedMongoServer : IDisposable
{
    private readonly Process _process;
    private readonly string _dataDir;

    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }

    private EmbeddedMongoServer(Process process, string dataDir, int port)
    {
        _process = process;
        _dataDir = dataDir;
        Client = new MongoClient($"mongodb://127.0.0.1:{port}/?directConnection=true");
        Database = Client.GetDatabase($"stats_it_{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Locates the <c>mongod</c> binary on the machine, or <see langword="null"/> when none is available.
    /// Checks the <c>STATS_TEST_MONGOD</c> override first, then the system <c>PATH</c>.
    /// </summary>
    public static string? LocateMongod()
    {
        var overridePath = Environment.GetEnvironmentVariable("STATS_TEST_MONGOD");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exeNames = OperatingSystem.IsWindows() ? new[] { "mongod.exe" } : new[] { "mongod" };
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var exe in exeNames)
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>True when a real MongoDB server can be launched in this environment.</summary>
    public static bool MongoAvailable => LocateMongod() != null;

    /// <summary>
    /// Starts a real <c>mongod</c> instance and returns once it accepts connections. Throws
    /// <see cref="InvalidOperationException"/> when no <c>mongod</c> binary is available — callers gate on
    /// <see cref="MongoAvailable"/> first.
    /// </summary>
    public static EmbeddedMongoServer Start(TimeSpan? readyTimeout = null)
    {
        var mongod = LocateMongod()
            ?? throw new InvalidOperationException("No mongod binary is available on this machine.");

        var port = GetFreeTcpPort();
        var dataDir = Path.Combine(Path.GetTempPath(), $"stats_it_mongo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = mongod,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--dbpath");
        startInfo.ArgumentList.Add(dataDir);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--bind_ip");
        startInfo.ArgumentList.Add("127.0.0.1");
        // Keep the footprint small — this is a short-lived single-test instance.
        startInfo.ArgumentList.Add("--wiredTigerCacheSizeGB");
        startInfo.ArgumentList.Add("0.25");

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start mongod process.");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(dataDir);
            throw new InvalidOperationException($"Failed to launch mongod at '{mongod}': {ex.Message}", ex);
        }

        // Drain stdio so the process buffers never block.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var server = new EmbeddedMongoServer(process, dataDir, port);
        try
        {
            server.WaitUntilReady(readyTimeout ?? TimeSpan.FromSeconds(30));
        }
        catch
        {
            server.Dispose();
            throw;
        }

        return server;
    }

    private void WaitUntilReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"mongod exited early with code {_process.ExitCode} before becoming ready.");
            }

            try
            {
                var admin = Client.GetDatabase("admin");
                admin.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(250);
            }
        }

        throw new TimeoutException(
            $"mongod did not become ready within {timeout.TotalSeconds:0}s. Last error: {last?.Message}");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            _process.Dispose();
            TryDeleteDirectory(_dataDir);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup of the throwaway data directory
        }
    }
}
