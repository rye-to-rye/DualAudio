using System.Windows.Threading;

namespace DualAudio.App.Services;

/// <summary>Posts work to the UI thread. The ViewModel is the only place that marshals service events.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
