using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  internal class ViewOption(string name, Action<int> handler)
  {
    public string Name { get; } = name;
    public Action<int> Handler { get; } = handler;
  }

  internal class ViewOptionRegistry
  {
    private readonly List<ViewOption> _options = [];
    private ViewOption _selectedOption;
    private int _selectedIndex = -1;

    public void AddOption(string name, Action<int> handler)
    {
      _options.Add(new ViewOption(name, handler));
    }

    public List<string> GetDisplayNames()
    {
      return [.. _options.Select(o => o.Name)];
    }

    public void OnSelectionChanged(int selectedIndex)
    {
      if (selectedIndex < 0 || selectedIndex >= _options.Count)
        return;

      _selectedIndex = selectedIndex;
      _selectedOption = _options[selectedIndex];
      _selectedOption.Handler(selectedIndex);
    }

    public ViewOption GetSelectedOption()
    {
      return _selectedOption;
    }

    public string GetSelectedOptionName()
    {
      return _selectedOption?.Name;
    }

    public int GetSelectedIndex()
    {
      return _selectedIndex;
    }
  }
}
