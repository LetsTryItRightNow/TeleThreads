namespace TeleThreads.TelegramScanner;

// Telegram delivers an album as separate messages sharing media_group_id; only one of them carries the caption.
public sealed class MediaGroupAggregator(TimeSpan quietPeriod)
{
    private readonly Dictionary<string, PendingGroup> _groups = [];

    public bool HasPending => _groups.Count > 0;

    // Standalone messages come back as a post right away; album parts are held until TakeCompleted.
    public IncomingPost? Add(IncomingMessage message, DateTime now)
    {
        if (message.MediaGroupId is null)
            return ToPost([message]);

        if (!_groups.TryGetValue(message.MediaGroupId, out var group))
            _groups[message.MediaGroupId] = group = new PendingGroup();
        group.Messages.Add(message);
        group.LastSeen = now;
        return null;
    }

    public IReadOnlyList<IncomingPost> TakeCompleted(DateTime now)
    {
        var completed = _groups.Where(g => now - g.Value.LastSeen >= quietPeriod).ToList();
        foreach (var (id, _) in completed)
            _groups.Remove(id);
        return completed.Select(g => ToPost(g.Value.Messages)).OrderBy(p => p.FirstMessageId).ToList();
    }

    private static IncomingPost ToPost(List<IncomingMessage> messages)
    {
        var ordered = messages.OrderBy(m => m.MessageId).ToList();
        var first = ordered[0];
        return new IncomingPost(
            first.ChatId,
            ordered.Select(m => m.MessageId).ToList(),
            first.MediaGroupId,
            first.Date,
            ordered.Select(m => m.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            ordered.Select(m => m.PhotoFileId).OfType<string>().ToList());
    }

    private sealed class PendingGroup
    {
        public List<IncomingMessage> Messages { get; } = [];
        public DateTime LastSeen { get; set; }
    }
}
