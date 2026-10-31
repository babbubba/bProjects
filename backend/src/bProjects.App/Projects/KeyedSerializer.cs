namespace bProjects.App.Projects;

/// <summary>
/// Serializza per chiave (es. id di run o turno) la sequenza «leggi stato dal database + pubblica»: poiché ogni lettura
/// avviene dopo la precedente pubblicazione, lo stato consegnato per ultimo non è mai più vecchio di uno già consegnato.
/// Le voci si rimuovono quando nessuno le usa più.
/// </summary>
public sealed class KeyedSerializer<TKey> where TKey : notnull
{
    private sealed class Gate { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Users; }

    private readonly Dictionary<TKey, Gate> _gates = [];

    public async Task RunAsync(TKey key, Func<Task> action)
    {
        Gate gate;
        lock (_gates)
        {
            if (!_gates.TryGetValue(key, out gate!))
            {
                _gates[key] = gate = new Gate();
            }

            gate.Users++;
        }

        await gate.Semaphore.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            gate.Semaphore.Release();
            lock (_gates)
            {
                if (--gate.Users == 0)
                {
                    _gates.Remove(key);
                    gate.Semaphore.Dispose();
                }
            }
        }
    }
}
