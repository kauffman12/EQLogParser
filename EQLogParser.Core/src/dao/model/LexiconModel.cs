using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EQLogParser
{
  internal class LexiconItem : INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _replace;
    public string Replace
    {
      get => _replace;
      set
      {
        if (_replace == value) return;
        _replace = value;
        OnPropertyChanged();
      }
    }

    private string _with;
    public string With
    {
      get => _with;
      set
      {
        if (_with == value) return;
        _with = value;
        OnPropertyChanged();
      }
    }
  }

  internal class TrustedPlayer : INotifyPropertyChanged
  {
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _name;
    public string Name
    {
      get => _name;
      set
      {
        if (_name == value) return;
        _name = value;
        OnPropertyChanged();
      }
    }
  }
}
