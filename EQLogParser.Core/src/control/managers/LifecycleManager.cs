namespace EQLogParser
{
  internal interface ILifecycle
  {
    void Clear(bool serverChanged);
    void Shutdown();
  }

  internal static class LifecycleManager
  {
    private static readonly List<ILifecycle> _registrations = [];
    private static readonly object _lock = new();

    internal static void Register(ILifecycle instance)
    {
      lock (_lock)
      {
        if (!_registrations.Contains(instance))
        {
          _registrations.Add(instance);
        }
      }
    }

    internal static void Clear(bool serverChanged)
    {
      ILifecycle[] registrations;

      lock (_lock)
      {
        registrations = [.. _registrations];
      }

      foreach (var instance in registrations)
      {
        instance.Clear(serverChanged);
      }

      // The one raise for "the log closed / a new capture begins": the seven grids and charts blank on this. It lives
      // on the PATH, not on FightManager — deleting the legacy store must not silently stop the clear (a board that
      // keeps showing the previous night's raid is worse than an empty one), and firing after the fan-out means every
      // store is already reset when the views look at the world. Clear All raises it itself.
      CombatEvents.FireActiveDataCleared(serverChanged);
    }

    internal static void Shutdown()
    {
      ILifecycle[] registrations;

      lock (_lock)
      {
        registrations = [.. _registrations];
      }

      foreach (var instance in registrations)
      {
        instance.Shutdown();
      }
    }
  }
}
