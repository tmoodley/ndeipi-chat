using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace NdeipiChat.SubApps.DevHost;

/// <summary>An error boundary that also tells the dev host what went wrong.</summary>
public sealed class DevErrorBoundary : ErrorBoundary
{
    [Parameter] public Func<Exception, Task>? OnError { get; set; }

    protected override async Task OnErrorAsync(Exception exception)
    {
        await base.OnErrorAsync(exception);
        if (OnError is not null)
            await OnError(exception);
    }
}
