using System.Collections.Generic;

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
