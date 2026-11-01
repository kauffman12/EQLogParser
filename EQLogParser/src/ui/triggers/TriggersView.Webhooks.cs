using Syncfusion.Windows.PropertyGrid;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class TriggersView
  {
    private void EventsUpdatingTriggers(bool updating)
    {
      Dispatcher.InvokeAsync(() =>
      {
        triggerStatus.Visibility = updating ? Visibility.Visible : Visibility.Collapsed;
        RefreshWebhookStatus();
      });
    }

    private void WebhookQueueChanged() => Dispatcher.InvokeAsync(RefreshWebhookStatus);

    private void RefreshWebhookStatus()
    {
      var status = MainActions.DiscordWebhooks.Status;
      var errors = status.Errors.Length;
      // Both messages own the same title slot; cache work temporarily takes priority.
      webhookStatus.Visibility = triggerStatus.Visibility != Visibility.Visible && (status.Pending > 0 || errors > 0)
        ? Visibility.Visible : Visibility.Collapsed;
      webhookStatusText.Content = $"Webhooks Pending: {status.Pending}"
        + (errors > 0 ? $" • Errors: {errors}" : "");
      webhookStatusText.SetResourceReference(Control.ForegroundProperty,
        errors > 0 ? "EQStopForegroundBrush" : "ContentForeground");
    }

    private void WebhookInfoClick(object sender, RoutedEventArgs e)
    {
      if (!_ready) return;
      var status = MainActions.DiscordWebhooks.Status;
      var menu = new ContextMenu { PlacementTarget = webhookInfo, Placement = PlacementMode.Bottom };
      menu.Items.Add(new MenuItem { Header = $"Pending: {status.Pending}", IsEnabled = false });
      menu.Items.Add(new MenuItem { Header = $"Collecting: {status.Aggregating}", IsEnabled = false });
      menu.Items.Add(new MenuItem { Header = $"Retrying: {status.Retrying}", IsEnabled = false });
      menu.Items.Add(new MenuItem { Header = $"Errors: {status.Errors.Length}", IsEnabled = false });
      menu.Items.Add(new Separator());
      if (status.Errors.Length > 0)
      {
        menu.Items.Add(new MenuItem { Header = "Select an error to open its trigger", IsEnabled = false });
        foreach (var error in status.Errors)
        {
          var header = new StackPanel();
          header.Children.Add(new TextBlock { Text = error.Source.Name, FontWeight = FontWeights.SemiBold });
          header.Children.Add(new TextBlock
          {
            Text = error.Message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
          });
          var item = new MenuItem { Header = header, Tag = error };
          item.Click += WebhookErrorClick;
          menu.Items.Add(item);
        }
      }
      else
      {
        menu.Items.Add(new MenuItem { Header = "Temporary failures retry automatically", IsEnabled = false });
      }

      menu.Items.Add(new MenuItem
      {
        Header = "Stop or disable the trigger to discard pending messages",
        IsEnabled = false
      });
      webhookInfo.ContextMenu = menu;
      menu.IsOpen = true;
      e.Handled = true;
    }

    private async void WebhookErrorClick(object sender, RoutedEventArgs e)
    {
      if (!_ready || sender is not MenuItem { Tag: WebhookError error }) return;
      try
      {
        if (characterView.Visibility == Visibility.Visible) characterView.SelectCharacter(error.Source.CharacterId);
        await _characterSelectionTask;
        if (!_ready) return;
        await theTreeView.SelectNode(error.Source.TriggerId);
        if (!_ready || generalPropertyGrid.SelectedObject is not TriggerPropertyModel model
          || model.Node.Id != error.Source.TriggerId) return;
        propertyTabControl.SelectedItem = generalPropertyGridTab;
        generalPropertyGrid.ExpandCategory("Data Sharing");
        generalPropertyGrid.SelectedPropertyItem = generalPropertyGrid.Properties
          .OfType<PropertyItem>().FirstOrDefault(item => item.Name == "ChatWebhook");
        await Dispatcher.InvokeAsync(_webhookEditor.FocusWebhook, DispatcherPriority.ContextIdle);
      }
      catch (Exception ex)
      {
        Log.Error("Problem selecting webhook trigger", ex);
      }
    }
  }
}
