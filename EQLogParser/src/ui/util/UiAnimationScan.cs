using log4net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace EQLogParser
{
  // Temporary, operator-requested diagnostic. No polling or retained UI objects between snapshots.
  internal static class UiAnimationScan
  {
    private static readonly ILog Log = LogManager.GetLogger(typeof(UiAnimationScan));
    private static int _nextId;

    internal static async Task RunAsync()
    {
      Application.Current.Dispatcher.VerifyAccess();
      var id = ++_nextId;
      Log.Info($"animation scan #{id}: requested; two snapshots one second apart (animated does not imply ticking)");
      // Let the Tools menu close before inspecting its window tree.
      await Task.Delay(250);
      Log.Info($"animation scan #{id}, sample 1:\n{CaptureOpenWindows()}");
      await Task.Delay(1000);
      Log.Info($"animation scan #{id}, sample 2:\n{CaptureOpenWindows()}");
      Log.Info($"animation scan #{id}: complete; snapshots cover reachable elements, not detached animation clocks");
    }

    internal static string Capture(IEnumerable<DependencyObject> roots)
    {
      var elapsed = Stopwatch.StartNew();
      var report = new StringBuilder();
      var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
      var propertiesByType = new Dictionary<Type, DependencyProperty[]>();
      var pending = new Stack<(DependencyObject Node, string Path)>();
      var animatedProperties = 0;
      var errors = 0;
      foreach (var root in roots) pending.Push((root, Describe(root)));

      while (pending.TryPop(out var next))
      {
        var (node, path) = next;
        if (!seen.Add(node)) continue;
        node.VerifyAccess();
        try
        {
          if (node is ProgressBar bar)
          {
            report.AppendLine($"indicator: {path} | IsIndeterminate={bar.IsIndeterminate} | {VisibilityOf(bar)}");
          }

          if (HasAnimations(node))
          {
            var type = node.GetType();
            if (!propertiesByType.TryGetValue(type, out var properties))
            {
              properties = TypeDescriptor.GetProperties(node).Cast<PropertyDescriptor>()
                .Select(DependencyPropertyDescriptor.FromProperty).Where(p => p is not null)
                .Select(p => p.DependencyProperty).Distinct().ToArray();
              propertiesByType[type] = properties;
            }

            var candidates = new HashSet<DependencyProperty>(properties);
            var local = node.GetLocalValueEnumerator();
            while (local.MoveNext()) candidates.Add(local.Current.Property);
            foreach (var property in candidates.OrderBy(p => p.Name))
            {
              var source = DependencyPropertyHelper.GetValueSource(node, property);
              if (!source.IsAnimated) continue;
              animatedProperties++;
              report.AppendLine($"animated: {path} | property={property.OwnerType.Name}.{property.Name}"
                + $" | value={Format(node.GetValue(property))} | source={source.BaseValueSource} | {VisibilityOf(node)}");
            }
          }

          // Logical children include hidden content; visual children include instantiated control templates.
          foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Push(child, string.Empty);
          if (node is Visual or Visual3D)
          {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
              Push(VisualTreeHelper.GetChild(node, i), string.Empty);
            }
          }
          if (node is Popup popup && popup.Child is { } popupChild) Push(popupChild, ".Child");

          // Also inspect common non-visual animation targets, including brushes supplied by styles/resources.
          if (node is UIElement element) Push(element.RenderTransform, ".RenderTransform");
          if (node is FrameworkElement framework) Push(framework.LayoutTransform, ".LayoutTransform");
          if (node is Control control)
          {
            Push(control.Background, ".Background");
            Push(control.Foreground, ".Foreground");
            Push(control.BorderBrush, ".BorderBrush");
          }
          if (node is Border border)
          {
            Push(border.Background, ".Background");
            Push(border.BorderBrush, ".BorderBrush");
          }
          if (node is Panel panel) Push(panel.Background, ".Background");
          if (node is TextBlock text) Push(text.Foreground, ".Foreground");
          if (node is Shape shape)
          {
            Push(shape.Fill, ".Fill");
            Push(shape.Stroke, ".Stroke");
          }
          if (node is TransformGroup transforms)
          {
            foreach (var child in transforms.Children) Push(child, ".Children");
          }
          if (node is Brush brush)
          {
            Push(brush.Transform, ".Transform");
            Push(brush.RelativeTransform, ".RelativeTransform");
          }
          if (node is GradientBrush gradient)
          {
            foreach (var stop in gradient.GradientStops) Push(stop, ".GradientStops");
          }
        }
        catch (Exception ex)
        {
          errors++;
          report.AppendLine($"scan error: {path} | {ex.GetType().Name}: {Format(ex.Message)}");
        }

        void Push(DependencyObject child, string relation)
        {
          if (child is not null && !seen.Contains(child)) pending.Push((child, $"{path}{relation}/{Describe(child)}"));
        }
      }

      return $"visited={seen.Count}; animatedProperties={animatedProperties}; errors={errors}; scanMs={elapsed.ElapsedMilliseconds}\n"
        + report;
    }

    private static string CaptureOpenWindows() => Capture(Application.Current.Windows.Cast<DependencyObject>().ToArray());

    private static bool HasAnimations(DependencyObject node) => node switch
    {
      UIElement element => element.HasAnimatedProperties,
      ContentElement content => content.HasAnimatedProperties,
      Animatable animatable => animatable.HasAnimatedProperties,
      _ => false
    };

    private static string Describe(DependencyObject node)
    {
      var name = node switch
      {
        FrameworkElement element => element.Name,
        FrameworkContentElement content => content.Name,
        _ => string.Empty
      };
      var label = node.GetType().Name + (string.IsNullOrEmpty(name) ? string.Empty : $"[{name}]");
      return node is Window window ? $"{label}(title={Format(window.Title)})" : label;
    }

    private static string VisibilityOf(DependencyObject node) => node is UIElement element
      ? $"visibility={element.Visibility}; isVisible={element.IsVisible}"
      : "visibility=non-visual target (see owner path)";

    private static string Format(object value)
    {
      var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace('\r', ' ').Replace('\n', ' ') ?? "<null>";
      return text.Length > 160 ? text[..160] + "..." : text;
    }
  }
}
