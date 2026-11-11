using Microsoft.Extensions.Logging;

namespace DualAudio.Platform.Windows.Internal;

/// <summary>
/// Raises events on the calling thread (no SynchronizationContext is captured; Core decides threading),
/// one subscriber at a time, so a throwing subscriber never reaches WinRT or skips the others.
/// </summary>
internal static class SafeEvent
{
    public static void Raise<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args, ILogger logger, string eventName)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<TArgs>)subscriber)(sender, args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Event} subscriber threw", eventName);
            }
        }
    }

    public static void Raise(EventHandler? handler, object sender, ILogger logger, string eventName)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler)subscriber)(sender, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Event} subscriber threw", eventName);
            }
        }
    }

    /// <summary>"0x8007048F" — for Detail strings and logs. Never includes exception messages.</summary>
    public static string HResult(int hresult) => $"0x{hresult:X8}";
}
