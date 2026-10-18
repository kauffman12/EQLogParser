using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EQLogParser
{
  // Temporary diagnostic: WPF exposes clock state publicly, but not property-to-clock or timing-root lookup.
  // Read internals only; report unsupported runtime layouts instead of creating, ticking or controlling clocks.
  internal sealed class UiAnimationClocks
  {
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo GetStorage = typeof(AnimationClock).Assembly
      .GetType("System.Windows.Media.Animation.AnimationStorage")
      ?.GetMethod("GetStorage", BindingFlags.Static | BindingFlags.NonPublic,
        [typeof(DependencyObject), typeof(DependencyProperty)]);
    private readonly HashSet<Clock> _propertyClocks = new(ReferenceEqualityComparer.Instance);
    private int _errors;

    internal void AppendProperty(StringBuilder report, DependencyObject target, DependencyProperty property)
    {
      try
      {
        if (GetStorage is null) throw new MissingMethodException("WPF AnimationStorage.GetStorage");
        var storage = GetStorage.Invoke(null, [target, property]);
        if (storage is null)
        {
          report.AppendLine("  property-clocks: no AnimationStorage found");
          return;
        }

        var count = 0;
        AppendList(ReadField(storage, "_animationClocks"), "local");
        var triggerLayers = ReadField(storage, "_propertyTriggerLayers");
        if (triggerLayers is not null && triggerLayers is not IDictionary)
        {
          throw new InvalidOperationException("Unexpected WPF animation-trigger layer collection");
        }
        if (triggerLayers is IDictionary layers)
        {
          foreach (DictionaryEntry layer in layers)
          {
            AppendList(ReadField(layer.Value, "_animationClocks"), $"trigger:{layer.Key}");
          }
        }
        if (count == 0)
        {
          report.AppendLine("  property-clocks: none (storage can retain a snapshot value without clocks)");
        }

        void AppendList(object list, string layer)
        {
          foreach (var clock in ReadClockList(list))
          {
            _propertyClocks.Add(clock);
            count++;
            AppendClock(report, $"  property-clock: layer={layer}", clock);
          }
        }
      }
      catch (Exception ex)
      {
        Unavailable(report, "property clocks", ex);
      }
    }

    internal void AppendTimingTree(StringBuilder report)
    {
      Dispatcher.CurrentDispatcher.VerifyAccess();
      try
      {
        // Reserved0 holds the existing MediaContext. Do not call MediaContext.From, which can create one.
        var media = ReadProperty(Dispatcher.CurrentDispatcher, "Reserved0");
        if (media is null)
        {
          report.AppendLine($"timing-tree: no existing MediaContext | clockInspectionErrors={_errors}");
          return;
        }
        if (media.GetType().FullName != "System.Windows.Media.MediaContext")
        {
          throw new InvalidOperationException("Dispatcher.Reserved0 is not a MediaContext");
        }

        var manager = ReadField(media, "_timeManager");
        var root = ReadField(manager, "_timeManagerClock");
        var roots = (IEnumerable)ReadField(root, "_rootChildren");
        var pending = new Stack<Clock>();
        foreach (var entry in roots)
        {
          if (entry is WeakReference reference && reference.Target is Clock clock) pending.Push(clock);
        }

        var seen = new HashSet<Clock>(ReferenceEqualityComparer.Instance);
        var states = new int[3];
        var activeAnimations = 0;
        var unmatchedActiveAnimations = 0;
        var details = new StringBuilder();
        while (pending.TryPop(out var clock))
        {
          if (!seen.Add(clock)) continue;
          states[(int)clock.CurrentState]++;
          var matched = _propertyClocks.Contains(clock);
          if (clock is AnimationClock && clock.CurrentState == ClockState.Active)
          {
            activeAnimations++;
            if (!matched) unmatchedActiveAnimations++;
          }
          AppendClock(details, $"timing-clock: reachableProperty={matched}", clock);
          if (clock is ClockGroup group)
          {
            foreach (var child in group.Children) pending.Push(child);
          }
        }

        report.AppendLine($"timing-tree: managerTime={Text(ReadProperty(manager, "CurrentTime"))};"
          + $" clocks={seen.Count}; active={states[(int)ClockState.Active]};"
          + $" filling={states[(int)ClockState.Filling]}; stopped={states[(int)ClockState.Stopped]};"
          + $" activeAnimations={activeAnimations}; unmatchedActiveAnimations={unmatchedActiveAnimations};"
          + $" clockInspectionErrors={_errors}");
        report.AppendLine("timing-tree note: reachableProperty=False means no scanned property matched,"
          + " not proof of a detached target");
        report.Append(details);
      }
      catch (Exception ex)
      {
        Unavailable(report, "timing tree", ex);
        report.AppendLine($"clockInspectionErrors={_errors}");
      }
    }

    private static IEnumerable<Clock> ReadClockList(object list)
    {
      if (list is null) yield break;
      if (list is IEnumerable enumerable)
      {
        foreach (var item in enumerable)
        {
          if (item is not Clock clock) throw new InvalidOperationException("Unexpected WPF animation-clock list entry");
          yield return clock;
        }
        yield break;
      }

      // AnimationStorage uses FrugalObjectList, which exposes Count/Item but is not IEnumerable.
      var count = (int)ReadProperty(list, "Count");
      var indexer = list.GetType().GetProperty("Item", InstanceMembers)
        ?? throw new MissingMemberException(list.GetType().FullName, "Item");
      for (var i = 0; i < count; i++)
      {
        if (indexer.GetValue(list, [i]) is not Clock clock)
        {
          throw new InvalidOperationException("Unexpected WPF animation-clock entry");
        }
        yield return clock;
      }
    }

    private static string Describe(Clock clock)
    {
      var timeline = clock.Timeline;
      var parent = clock.Parent;
      var property = Storyboard.GetTargetProperty(timeline);
      return $"id={Id(clock)} | parent={(parent is null ? "root" : Id(parent))} | type={timeline.GetType().Name}"
        + $" | name={Text(timeline.Name)} | state={clock.CurrentState} | paused={clock.IsPaused}"
        + $" | time={Text(clock.CurrentTime)} | progress={Text(clock.CurrentProgress)}"
        + $" | iteration={Text(clock.CurrentIteration)} | speed={Text(clock.CurrentGlobalSpeed)}"
        + $" | duration={Text(timeline.Duration)} | naturalDuration={Text(clock.NaturalDuration)}"
        + $" | repeat={Text(timeline.RepeatBehavior)} | autoReverse={timeline.AutoReverse}"
        + $" | begin={Text(timeline.BeginTime)} | fill={timeline.FillBehavior}"
        + $" | nextTick={Text(ReadProperty(clock, "InternalNextTickNeededTime"))}"
        + $" | targetName={Text(Storyboard.GetTargetName(timeline))} | targetProperty={Text(property?.Path)}";
    }

    private static string Id(Clock clock) =>
      RuntimeHelpers.GetHashCode(clock).ToString("x8", CultureInfo.InvariantCulture);

    private static object ReadField(object instance, string name)
    {
      if (instance is null) throw new InvalidOperationException($"Cannot read {name} on a null WPF object");
      for (var type = instance.GetType(); type is not null; type = type.BaseType)
      {
        var field = type.GetField(name, InstanceMembers | BindingFlags.DeclaredOnly);
        if (field is not null) return field.GetValue(instance);
      }
      throw new MissingFieldException(instance.GetType().FullName, name);
    }

    private static object ReadProperty(object instance, string name)
    {
      var property = instance.GetType().GetProperty(name, InstanceMembers)
        ?? throw new MissingMemberException(instance.GetType().FullName, name);
      return property.GetValue(instance);
    }

    private static string Text(object value)
    {
      var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace('\r', ' ').Replace('\n', ' ')
        ?? "<none>";
      return text.Length > 160 ? text[..160] + "..." : text;
    }

    private void AppendClock(StringBuilder report, string prefix, Clock clock)
    {
      try
      {
        report.AppendLine($"{prefix} | {Describe(clock)}");
      }
      catch (Exception ex)
      {
        Unavailable(report, $"{prefix}; id={Id(clock)}", ex);
      }
    }

    private void Unavailable(StringBuilder report, string scope, Exception ex)
    {
      _errors++;
      var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
      report.AppendLine($"clock inspection unavailable ({scope}): {cause.GetType().Name}: {Text(cause.Message)}");
    }
  }
}
