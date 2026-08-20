using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EQLogParser
{
  internal interface IDocumentContent
  {
    public void HideContent();
  }



  internal class ComboBoxItemDetails : INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null) =>
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ComboBoxItemDetails()
    {
    }

    public ComboBoxItemDetails(bool isChecked, string text)
    {
      IsChecked = isChecked;
      Text = text;
    }

    public string Text { get; set; }
    public string SelectedText { get; set; }

    private bool _isChecked;
    public bool IsChecked
    {
      get => _isChecked;
      set
      {
        if (_isChecked == value) return;
        _isChecked = value;
        OnPropertyChanged();
      }
    }

    public string Value { get; set; }
  }


  internal class ParseData
  {
    public CombinedStats CombinedStats { get; set; }
    public List<PlayerStats> Selected { get; } = [];
  }


  internal class PlayerStatsSelectionChangedEventArgs : EventArgs
  {
    public List<PlayerStats> Selected { get; } = [];
    public List<GroupEntry> SelectedGroups { get; } = [];
    public CombinedStats CurrentStats { get; set; }
  }


  internal class DataPointEvent
  {
    public string Action { get; set; }
    public RecordGroupCollection Iterator { get; set; }
    public List<PlayerStats> Selected { get; } = [];
    public List<GroupEntry> SelectedGroups { get; } = [];
  }


}
