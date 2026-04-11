using Monitor.Server.Models;

namespace Monitor.Server.Services;

public sealed class DashboardStateStore
{
    private DashboardSnapshot _current = DashboardSnapshot.Empty;

    public DashboardSnapshot Current => Volatile.Read(ref _current);

    public void Update(DashboardSnapshot snapshot)
    {
        Interlocked.Exchange(ref _current, snapshot);
    }
}
