using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Guyabano.Messaging;

public sealed class InMemoryWorkflowProgressHub :
    IWorkflowProgressPublisher,
    IWorkflowProgressSubscriber
{
    private readonly ConcurrentDictionary<string, ProgressStream> streams =
        new(StringComparer.Ordinal);

    public Task<WorkflowProgressEntry> PublishAsync(
        string workflowId,
        WorkflowProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();

        var stream = streams.GetOrAdd(workflowId, _ => new ProgressStream());
        WorkflowProgressEntry entry;
        Channel<WorkflowProgressEntry>[] subscribers;
        lock (stream.SyncRoot)
        {
            var sequence = ++stream.Sequence;
            entry = new WorkflowProgressEntry(
                sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                workflowId,
                progress);
            stream.Entries.Add(entry);
            subscribers = stream.Subscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
            subscriber.Writer.TryWrite(entry);

        return Task.FromResult(entry);
    }

    public async IAsyncEnumerable<WorkflowProgressEntry> SubscribeAsync(
        string workflowId,
        string? afterEntryId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        var afterSequence = ParseSequence(afterEntryId);
        var stream = streams.GetOrAdd(workflowId, _ => new ProgressStream());
        var channel = Channel.CreateUnbounded<WorkflowProgressEntry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        WorkflowProgressEntry[] replay;
        lock (stream.SyncRoot)
        {
            stream.Subscribers.Add(channel);
            replay = stream.Entries
                .Where(entry => ParseSequence(entry.EntryId) > afterSequence)
                .ToArray();
        }

        try
        {
            foreach (var entry in replay)
                yield return entry;

            await foreach (var entry in channel.Reader.ReadAllAsync(
                               cancellationToken))
            {
                yield return entry;
            }
        }
        finally
        {
            lock (stream.SyncRoot)
                stream.Subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    private static long ParseSequence(string? value) =>
        long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var sequence)
            ? sequence
            : 0;

    private sealed class ProgressStream
    {
        public object SyncRoot { get; } = new();

        public List<WorkflowProgressEntry> Entries { get; } = [];

        public List<Channel<WorkflowProgressEntry>> Subscribers { get; } = [];

        public long Sequence { get; set; }
    }
}
