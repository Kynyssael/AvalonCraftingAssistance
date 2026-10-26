using System;
using System.Threading.Tasks;

namespace AvalonPlugin;

/// <summary>
/// Runs a background load and exposes its result to the UI thread, which polls it every frame.
/// Starting a new load discards the result of any older one.
/// </summary>
public sealed class Loadable<T> where T : class
{
    private Task<T>? task;

    public T? Value { get; private set; }
    public string? Error { get; private set; }
    public bool IsLoading => task is { IsCompleted: false };

    public void Start(Func<Task<T>> load)
    {
        Error = null;
        var current = task = Task.Run(load);
        current.ContinueWith(t =>
        {
            if (t != task) return;
            if (t.IsCompletedSuccessfully)
            {
                Value = t.Result;
            }
            else
            {
                var error = t.Exception?.GetBaseException();
                Error = error?.Message ?? "Cancelled.";
                if (error is not Teamcraft.TeamcraftException)
                    Plugin.Log.Error(error, "Background load failed");
            }
        }, TaskScheduler.Default);
    }

    public void Clear()
    {
        task = null;
        Value = null;
        Error = null;
    }
}
