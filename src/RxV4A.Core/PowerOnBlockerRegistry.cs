namespace RxV4A.Core;

public static class ApplicationScope
{
    public static bool IsOperationalInput(string inputId) =>
        !string.Equals(inputId, "alexa", StringComparison.OrdinalIgnoreCase);
}

public interface IPowerOnBlockerRegistry
{
    IReadOnlyList<string> Registered { get; }

    IReadOnlyList<string> Active { get; }

    bool IsBlocked { get; }

    event EventHandler? Changed;

    bool Activate(string contextId);

    bool Deactivate(string contextId);

    bool IsActive(string contextId);
}

public sealed class PowerOnBlockerRegistry : IPowerOnBlockerRegistry
{
    private readonly Lock _sync = new();
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _registered;

    public PowerOnBlockerRegistry(IEnumerable<string>? registeredContextIds = null)
    {
        _registered = new HashSet<string>(registeredContextIds ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Registered => _registered.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<string> Active
    {
        get
        {
            lock (_sync)
            {
                return _active.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public bool IsBlocked
    {
        get
        {
            lock (_sync)
            {
                return _active.Count > 0;
            }
        }
    }

    public event EventHandler? Changed;

    public bool Activate(string contextId)
    {
        Validate(contextId);
        bool changed;
        lock (_sync)
        {
            changed = _active.Add(contextId);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public bool Deactivate(string contextId)
    {
        Validate(contextId);
        bool changed;
        lock (_sync)
        {
            changed = _active.Remove(contextId);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public bool IsActive(string contextId)
    {
        Validate(contextId);
        lock (_sync)
        {
            return _active.Contains(contextId);
        }
    }

    private void Validate(string contextId)
    {
        if (!_registered.Contains(contextId))
        {
            throw new ArgumentOutOfRangeException(nameof(contextId), "Unknown power-on blocker context.");
        }
    }
}
