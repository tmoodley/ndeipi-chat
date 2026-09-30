using System.Text.Json;
using NdeipiChat.SubApps.Sdk;

namespace NdeipiChat.SubApps.DevHost;

/// <summary>
/// The shell's realtime topics, in memory. Anything published to a topic reaches the app's
/// subscriptions to it: from a mock (<see cref="DevRequest.Realtime"/>), from Program.cs, or from
/// the dev host's Realtime panel. Unlike the Ndeipi API, it lets the app follow any topic; the API
/// decides that with an ITopicPolicy.
/// </summary>
public sealed class DevRealtime : ISubAppRealtime
{
    readonly List<Subscription> _subscriptions = [];

    internal JsonSerializerOptions Json { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>A topic was followed (true) or dropped (false).</summary>
    internal event Action<string, bool>? SubscriptionChanged;

    /// <summary>Something was published: the topic and its payload as JSON.</summary>
    internal event Action<string, string>? Published;

    /// <summary>The app's handler for a topic threw.</summary>
    internal event Action<string, Exception>? HandlerFailed;

    /// <summary>The topics the app follows now.</summary>
    public IReadOnlyList<string> Topics
    {
        get
        {
            lock (_subscriptions)
                return _subscriptions.Select(s => s.Topic).Distinct().ToList();
        }
    }

    public Task<IAsyncDisposable> SubscribeAsync(string topic, Func<JsonElement, Task> onMessage)
    {
        var subscription = new Subscription(this, topic, onMessage);
        lock (_subscriptions)
            _subscriptions.Add(subscription);
        SubscriptionChanged?.Invoke(topic, true);
        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    /// <summary>Sends <paramref name="payload"/> to everything following <paramref name="topic"/>.</summary>
    public Task PublishAsync(string topic, object? payload) =>
        PublishJsonAsync(topic, JsonSerializer.Serialize(payload, payload?.GetType() ?? typeof(object), Json));

    internal async Task PublishJsonAsync(string topic, string json)
    {
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.Clone();
        Published?.Invoke(topic, json);
        List<Subscription> targets;
        lock (_subscriptions)
            targets = _subscriptions.Where(s => s.Topic == topic).ToList();
        foreach (var target in targets)
        {
            // The app's handler failing is the app's bug, not the publisher's: report it, carry on.
            try
            {
                await target.OnMessage(payload);
            }
            catch (Exception ex)
            {
                HandlerFailed?.Invoke(topic, ex);
            }
        }
    }

    void Remove(Subscription subscription)
    {
        bool last;
        lock (_subscriptions)
        {
            if (!_subscriptions.Remove(subscription))
                return;
            last = _subscriptions.All(s => s.Topic != subscription.Topic);
        }
        if (last)
            SubscriptionChanged?.Invoke(subscription.Topic, false);
    }

    sealed class Subscription(DevRealtime owner, string topic, Func<JsonElement, Task> onMessage) : IAsyncDisposable
    {
        public string Topic { get; } = topic;

        public Func<JsonElement, Task> OnMessage { get; } = onMessage;

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
