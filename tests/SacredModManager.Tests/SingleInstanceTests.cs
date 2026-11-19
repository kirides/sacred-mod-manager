using SacredModManager.Core.Links;
using Xunit;

namespace SacredModManager.Tests;

public class SingleInstanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Collects messages from Listen; <see cref="WaitFor"/> awaits a number of them with a timeout.</summary>
    private sealed class Inbox
    {
        private readonly List<string> _messages = [];
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];

        public int Count
        {
            get
            {
                lock (_messages)
                {
                    return _messages.Count;
                }
            }
        }

        public void Add(string message)
        {
            lock (_messages)
            {
                _messages.Add(message);
                foreach ((int count, TaskCompletionSource done) in _waiters)
                {
                    if (_messages.Count >= count)
                    {
                        done.TrySetResult();
                    }
                }
            }
        }

        public async Task<IReadOnlyList<string>> WaitFor(int count)
        {
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_messages)
            {
                if (_messages.Count >= count)
                {
                    return [.. _messages];
                }

                _waiters.Add((count, done));
            }

            await done.Task.WaitAsync(Timeout);
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    [Fact]
    public void FirstAcquireIsPrimary()
    {
        using var dir = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "m");
        Assert.Equal(InstanceRole.Primary, first.Role);
    }

    [Fact]
    public void AcquireCreatesTheDataDirectory()
    {
        using var dir = new TempDir();
        string nested = dir.Combine("a", "b");
        using SingleInstance first = SingleInstance.Acquire(nested, "m");
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public async Task SecondAcquireForwardsTheMessage()
    {
        using var dir = new TempDir();
        var inbox = new Inbox();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "first");
        first.Listen(inbox.Add);

        using SingleInstance second = SingleInstance.Acquire(dir.Path, "gear-mod-manager://activate");
        Assert.Equal(InstanceRole.Forwarded, second.Role);
        Assert.Equal(["gear-mod-manager://activate"], await inbox.WaitFor(1));
        Assert.Equal(1, inbox.Count);
    }

    [Fact]
    public async Task SeveralMessagesArriveInOrder()
    {
        using var dir = new TempDir();
        var inbox = new Inbox();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "first");
        first.Listen(inbox.Add);

        string[] sent = [.. Enumerable.Range(0, 8).Select(i => "message " + i)];
        foreach (string m in sent)
        {
            using SingleInstance s = SingleInstance.Acquire(dir.Path, m);
            Assert.Equal(InstanceRole.Forwarded, s.Role);
        }

        Assert.Equal(sent, await inbox.WaitFor(sent.Length));
    }

    [Fact]
    public async Task NonAsciiTextSurvives()
    {
        using var dir = new TempDir();
        var inbox = new Inbox();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "first");
        first.Listen(inbox.Add);

        const string text = "gear-mod-manager://add-index?url=café 日本語 \U0001F600";
        using SingleInstance second = SingleInstance.Acquire(dir.Path, text);
        Assert.Equal([text], await inbox.WaitFor(1));
    }

    [Fact]
    public async Task OverLongMessageIsTruncatedNotFatal()
    {
        using var dir = new TempDir();
        var inbox = new Inbox();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "first");
        first.Listen(inbox.Add);

        using (SingleInstance s = SingleInstance.Acquire(dir.Path, new string('a', 50_000)))
        {
            Assert.Equal(InstanceRole.Forwarded, s.Role);
        }

        IReadOnlyList<string> got = await inbox.WaitFor(1);
        Assert.Equal(SingleInstance.MaxMessageBytes / 4, got[0].Length);
        Assert.All(got[0], c => Assert.Equal('a', c));

        // and the listener still works afterwards
        using SingleInstance again = SingleInstance.Acquire(dir.Path, "after");
        Assert.Equal("after", (await inbox.WaitFor(2))[1]);
    }

    [Fact]
    public async Task OverLongNonAsciiMessageStaysWithinTheByteLimit()
    {
        using var dir = new TempDir();
        var inbox = new Inbox();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "first");
        first.Listen(inbox.Add);

        using SingleInstance s = SingleInstance.Acquire(dir.Path, new string('日', 20_000));
        IReadOnlyList<string> got = await inbox.WaitFor(1);
        Assert.Equal(SingleInstance.MaxMessageBytes / 4, got[0].Length);
    }

    [Fact]
    public async Task WhitespaceOnlyMessagesAreNotDelivered()
    {
        using var dir = new TempDir();
        var inbox = new Inbox();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "first");
        first.Listen(inbox.Add);

        using (SingleInstance blank = SingleInstance.Acquire(dir.Path, "  \n "))
        {
            Assert.Equal(InstanceRole.Forwarded, blank.Role);
        }

        using SingleInstance real = SingleInstance.Acquire(dir.Path, "real");
        Assert.Equal(["real"], await inbox.WaitFor(1));
    }

    [Fact]
    public void AfterThePrimaryIsDisposedANewAcquireIsPrimary()
    {
        using var dir = new TempDir();
        SingleInstance first = SingleInstance.Acquire(dir.Path, "m");
        first.Listen(_ => { });
        Assert.Equal(InstanceRole.Primary, first.Role);
        first.Dispose();

        using SingleInstance next = SingleInstance.Acquire(dir.Path, "m", retries: 0);
        Assert.Equal(InstanceRole.Primary, next.Role);
    }

    [Fact]
    public async Task ANewPrimaryAfterDisposeReceivesMessages()
    {
        using var dir = new TempDir();
        SingleInstance first = SingleInstance.Acquire(dir.Path, "m");
        first.Listen(_ => { });
        first.Dispose();

        var inbox = new Inbox();
        using SingleInstance next = SingleInstance.Acquire(dir.Path, "m", retries: 0);
        next.Listen(inbox.Add);
        using SingleInstance sender = SingleInstance.Acquire(dir.Path, "hello");
        Assert.Equal(InstanceRole.Forwarded, sender.Role);
        Assert.Equal(["hello"], await inbox.WaitFor(1));
    }

    [Fact]
    public async Task DifferentDirectoriesDoNotInterfere()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var inboxA = new Inbox();
        var inboxB = new Inbox();
        using SingleInstance pa = SingleInstance.Acquire(a.Path, "pa");
        using SingleInstance pb = SingleInstance.Acquire(b.Path, "pb");
        Assert.Equal(InstanceRole.Primary, pa.Role);
        Assert.Equal(InstanceRole.Primary, pb.Role);
        pa.Listen(inboxA.Add);
        pb.Listen(inboxB.Add);

        using SingleInstance sa = SingleInstance.Acquire(a.Path, "to-a");
        using SingleInstance sb = SingleInstance.Acquire(b.Path, "to-b");
        Assert.Equal(["to-a"], await inboxA.WaitFor(1));
        Assert.Equal(["to-b"], await inboxB.WaitFor(1));
        Assert.Equal(1, inboxA.Count);
        Assert.Equal(1, inboxB.Count);
    }

    [Fact]
    public void PipeNameIsStablePerDirectoryAndDiffersBetweenDirectories()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        string name = SingleInstance.PipeNameFor(a.Path);
        Assert.Equal(name, SingleInstance.PipeNameFor(a.Path));
        Assert.Equal(name, SingleInstance.PipeNameFor(Path.Combine(a.Path, "..", Path.GetFileName(a.Path))));
        Assert.NotEqual(name, SingleInstance.PipeNameFor(b.Path));
        Assert.StartsWith("sacred-mod-manager-", name, StringComparison.Ordinal);
        Assert.Matches("^[a-z0-9-]+$", name);
        Assert.True(name.Length < 60);
    }

    [Fact]
    public void AcquireWhileThePrimaryDoesNotListenEndsAsStandalone()
    {
        using var dir = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "m");
        using SingleInstance second = SingleInstance.Acquire(dir.Path, "m", retries: 2, retryDelayMs: 10);
        Assert.Equal(InstanceRole.Primary, first.Role);
        Assert.Equal(InstanceRole.Standalone, second.Role);
    }

    [Fact]
    public void AcquireWithZeroRetriesIsStandaloneWhenLocked()
    {
        using var dir = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "m");
        using SingleInstance second = SingleInstance.Acquire(dir.Path, "m", retries: 0);
        Assert.Equal(InstanceRole.Standalone, second.Role);
    }

    [Fact]
    public void DisposingAStandaloneInstanceDoesNotReleaseThePrimaryLock()
    {
        using var dir = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "m");
        SingleInstance second = SingleInstance.Acquire(dir.Path, "m", retries: 1, retryDelayMs: 1);
        second.Dispose();
        using SingleInstance third = SingleInstance.Acquire(dir.Path, "m", retries: 1, retryDelayMs: 1);
        Assert.Equal(InstanceRole.Standalone, third.Role);
    }

    [Fact]
    public void ListenOnANonPrimaryInstanceDoesNothing()
    {
        using var dir = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "m");
        using SingleInstance standalone = SingleInstance.Acquire(dir.Path, "m", retries: 1, retryDelayMs: 1);
        Assert.Equal(InstanceRole.Standalone, standalone.Role);
        standalone.Listen(_ => Assert.Fail("a non-primary instance must not receive messages"));

        first.Listen(_ => { });
        using SingleInstance forwarded = SingleInstance.Acquire(dir.Path, "m");
        Assert.Equal(InstanceRole.Forwarded, forwarded.Role);
        forwarded.Listen(_ => Assert.Fail("a forwarded instance must not receive messages"));
    }

    [Fact]
    public void DisposeIsPromptWithAndWithoutListening()
    {
        using var dir = new TempDir();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SingleInstance silent = SingleInstance.Acquire(dir.Path, "m");
        silent.Dispose();
        SingleInstance listening = SingleInstance.Acquire(dir.Path, "m");
        listening.Listen(_ => { });
        listening.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
    }

    [Fact]
    public void TrySendWithoutAListenerReturnsFalse()
        => Assert.False(SingleInstance.TrySend("sacred-mod-manager-test-" + Guid.NewGuid().ToString("N"), "x", 20));

    [Fact]
    public async Task ACallbackThatThrowsDoesNotKillTheListener()
    {
        using var dir = new TempDir();
        var inbox = new Inbox();
        int calls = 0;
        using SingleInstance first = SingleInstance.Acquire(dir.Path, "m");
        first.Listen(m =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            inbox.Add(m);
        });

        using SingleInstance one = SingleInstance.Acquire(dir.Path, "one");
        using SingleInstance two = SingleInstance.Acquire(dir.Path, "two");
        Assert.Equal(["two"], await inbox.WaitFor(1));
    }
}
