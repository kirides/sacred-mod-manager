using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SacredModManager.Core.Links;

/// <summary>What this process is.</summary>
public enum InstanceRole
{
    /// <summary>The first one: it runs and listens.</summary>
    Primary,

    /// <summary>Another one runs and received the message: this process should exit.</summary>
    Forwarded,

    /// <summary>Another one runs but did not answer: start normally, without listening.</summary>
    Standalone,
}

/// <summary>
/// One running manager per data folder. The first process takes an exclusive lock file and listens on a named pipe (current user only);
/// a process started later (for example by the operating system with a <c>gear-mod-manager://</c> link) hands its message to the first
/// one and exits. Messages are short UTF-8 texts: a link, or "activate".
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The listener reads at most this many bytes of a message; the sender cuts a longer text to a quarter of it in characters.</summary>
    public const int MaxMessageBytes = 8 * 1024;

    private readonly FileStream? _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private SingleInstance(FileStream? lockFile, string pipeName, InstanceRole role)
    {
        _lock = lockFile;
        _pipeName = pipeName;
        Role = role;
    }

    public InstanceRole Role { get; }

    /// <summary>
    /// Takes the lock and returns the primary instance, or hands <paramref name="message"/> to the running one and returns an instance with
    /// <see cref="InstanceRole.Forwarded"/>. If the running one cannot be reached the role is <see cref="InstanceRole.Standalone"/> (a second window is better than none).
    /// </summary>
    public static SingleInstance Acquire(string dataDirectory, string message, int retries = 12, int retryDelayMs = 150)
    {
        Directory.CreateDirectory(dataDirectory);
        string pipe = PipeNameFor(dataDirectory);
        string lockPath = Path.Combine(dataDirectory, "instance.lock");
        FileStream? file = TryLock(lockPath);
        if (file is not null)
        {
            return new SingleInstance(file, pipe, InstanceRole.Primary);
        }

        for (int i = 0; i < retries; i++)
        {
            if (TrySend(pipe, message, 250))
            {
                return new SingleInstance(null, pipe, InstanceRole.Forwarded);
            }

            Thread.Sleep(retryDelayMs); // the first instance may still be starting up
            file = TryLock(lockPath);
            if (file is not null)
            {
                return new SingleInstance(file, pipe, InstanceRole.Primary);
            }
        }

        return new SingleInstance(null, pipe, InstanceRole.Standalone);
    }

    public static string PipeNameFor(string dataDirectory)
        => "sacred-mod-manager-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..16];

    /// <summary>Starts listening; <paramref name="onMessage"/> is called on a pool thread for every message.</summary>
    public void Listen(Action<string> onMessage)
    {
        if (Role != InstanceRole.Primary)
        {
            return;
        }

        _loop = Task.Run(async () =>
        {
            NamedPipeServerStream? waiting = null;
            try
            {
                waiting = CreateServer();
                while (!_stop.IsCancellationRequested)
                {
                    NamedPipeServerStream current = waiting;
                    try
                    {
                        await current.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        await current.DisposeAsync().ConfigureAwait(false);
                        await Task.Delay(200, _stop.Token).ConfigureAwait(false); // a client that went away; listen again
                        waiting = CreateServer();
                        continue;
                    }

                    // the next instance listens before this connection is served: a pipe without any instance disappears on Linux, and a
                    // sender that connected in that moment would lose its message
                    waiting = CreateServer();
                    try
                    {
                        string text = await ReadAsync(current, _stop.Token).ConfigureAwait(false);
                        if (text.Length > 0)
                        {
                            try
                            {
                                onMessage(text);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // a failing handler must not stop the listener
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // the sender went away in the middle of the message
                    }
                    finally
                    {
                        await current.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            finally
            {
                if (waiting is not null)
                {
                    await waiting.DisposeAsync().ConfigureAwait(false);
                }
            }
        }, _stop.Token);
    }

    private NamedPipeServerStream CreateServer()
        => new(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>Sends one message to a listening instance; false when nobody answers within <paramref name="timeoutMs"/>.</summary>
    public static bool TrySend(string pipeName, string message, int timeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            byte[] bytes = Encoding.UTF8.GetBytes(message.Length > MaxMessageBytes / 4 ? message[..(MaxMessageBytes / 4)] : message);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(1000);
        }
        catch (AggregateException)
        {
            // cancelled
        }

        _stop.Dispose();
        _lock?.Dispose();
    }

    private static FileStream? TryLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string> ReadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes];
        int total = 0;
        int n;
        while (total < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, total).Trim();
    }
}
