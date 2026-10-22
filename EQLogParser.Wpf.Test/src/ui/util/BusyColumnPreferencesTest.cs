using System.Windows.Controls;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.TreeGrid;

namespace EQLogParser;

// These exercise our layout migration and saved settings, not the vendor's rendering or drag implementation.
[TestClass]
public class BusyColumnPreferencesTest
{
  private const string SettingsKey = "BusyColumnPreferencesTestColumns";
  private const string OrderKey = SettingsKey + "DisplayIndex";
  private const string DefaultOrder = "Name,AssignedGroup,TotalSeconds,BusySeconds,Total";

  [TestInitialize]
  public void Setup() => ClearSettings();

  [TestCleanup]
  public void Cleanup() => ClearSettings();

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AFreshLayoutRemembersBusyBeforeItsFirstHide(bool tree)
  {
    var firstOrder = "";
    string[] visible = [];
    Sta.Run(() =>
    {
      var (picker, grid) = Load(tree);
      firstOrder = ConfigUtil.GetSetting(OrderKey);
      picker.Items.Cast<ComboBoxItemDetails>().Single(item => item.Value == "BusySeconds").IsChecked = false;
      DataGridUtil.SetHiddenColumns(picker, grid);

      var (reopened, _) = Load(tree);
      visible = Visible(reopened);
    });

    Assert.AreEqual(DefaultOrder, firstOrder, "initializing the layout must record Busy before any picker gesture");
    CollectionAssert.AreEqual(new[] { "Name", "AssignedGroup", "TotalSeconds", "Total" }, visible);
    Assert.AreEqual(DefaultOrder, ConfigUtil.GetSetting(OrderKey));
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AnOrderOnlyLayoutGainsBusyBesideSecWithoutHidingAnything(bool tree)
  {
    ConfigUtil.SetSetting(OrderKey, "Total,TotalSeconds,Name,AssignedGroup");
    string[] visible = [];
    Sta.Run(() => visible = Visible(Load(tree).Picker));

    const string expected = "Total,TotalSeconds,BusySeconds,Name,AssignedGroup";
    Assert.AreEqual(expected, ConfigUtil.GetSetting(OrderKey));
    CollectionAssert.AreEqual(expected.Split(','), visible);
    Assert.IsNull(ConfigUtil.GetSetting(SettingsKey), "no visibility selection still means every column is visible");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AVisibilityOnlyLayoutKeepsHiddenColumnsWhenBusyArrives(bool tree)
  {
    ConfigUtil.SetSetting(SettingsKey, "Name,Total");
    string[] visible = [];
    Sta.Run(() => visible = Visible(Load(tree).Picker));

    Assert.AreEqual(DefaultOrder, ConfigUtil.GetSetting(OrderKey));
    CollectionAssert.AreEqual(new[] { "Name", "AssignedGroup", "BusySeconds", "Total" }, visible,
      "AssignedGroup keeps its existing new-column behavior, but Sec must remain hidden");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AKnownBusyColumnKeepsItsHiddenStateAndCustomOrder(bool tree)
  {
    const string order = "BusySeconds,Total,Name,TotalSeconds,AssignedGroup";
    ConfigUtil.SetSetting(OrderKey, order);
    ConfigUtil.SetSetting(SettingsKey, "Total,Name");
    string[] visible = [];
    Sta.Run(() => visible = Visible(Load(tree).Picker));

    Assert.AreEqual(order, ConfigUtil.GetSetting(OrderKey));
    CollectionAssert.AreEqual(new[] { "Total", "Name" }, visible);
    Assert.AreEqual("Total,Name", ConfigUtil.GetSetting(SettingsKey));
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  public void AHeaderNameLayoutPreservesItsOrderAndVisibilityDuringMigration(bool tree)
  {
    ConfigUtil.SetSetting(OrderKey, "Damage,Sec,Name,Group");
    ConfigUtil.SetSetting(SettingsKey, "Damage,Name,Group");
    string[] visible = [];
    Sta.Run(() => visible = Visible(Load(tree).Picker));

    Assert.AreEqual("Total,TotalSeconds,BusySeconds,Name,AssignedGroup", ConfigUtil.GetSetting(OrderKey));
    CollectionAssert.AreEqual(new[] { "Total", "BusySeconds", "Name", "AssignedGroup" }, visible);
  }

  private static void ClearSettings()
  {
    ConfigUtil.RemoveSetting(SettingsKey);
    ConfigUtil.RemoveSetting(OrderKey);
  }

  private static (ComboBox Picker, SfGridBase Grid) Load(bool tree)
  {
    var picker = new ComboBox { Tag = SettingsKey };
    var columns = new[]
    {
      ("Name", "Name"), ("AssignedGroup", "Group"), ("TotalSeconds", "Sec"),
      ("BusySeconds", "Busy"), ("Total", "Damage")
    };
    SfGridBase grid;
    if (tree)
    {
      var treeGrid = new SfTreeGrid();
      foreach (var (mapping, header) in columns)
      {
        treeGrid.Columns.Add(new TreeGridTextColumn { MappingName = mapping, HeaderText = header });
      }

      grid = treeGrid;
    }
    else
    {
      var dataGrid = new SfDataGrid();
      foreach (var (mapping, header) in columns)
      {
        dataGrid.Columns.Add(new GridTextColumn { MappingName = mapping, HeaderText = header });
      }

      grid = dataGrid;
    }

    DataGridUtil.LoadColumns(picker, grid);
    return (picker, grid);
  }

  private static string[] Visible(ComboBox picker) =>
    [.. picker.Items.Cast<ComboBoxItemDetails>().Where(item => item.IsChecked).Select(item => item.Value)];
}
