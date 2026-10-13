namespace EQLogParser;

[TestClass]
public sealed class NamesTableProjectionTest
{
  private sealed record DisplayRow(string Name, IdentityKind Kind, string Type, string? PlayerClass,
    string Owner, string Why) : IIdentityCensusRow;

  private static DisplayRow Project(ClassificationReport.Row row)
    => new(row.Name, row.Kind, IdentityVocabulary.TypeWord(row.Kind), row.Class,
      PetOwnership.DisplayOf(row.PetOwner), IdentityVocabulary.WhyWord(row.Reason, row.Kind));

  [TestMethod]
  public void OnlyMatchingRowsReachTheExpensiveProjection()
  {
    ClassificationReport.Row[] source =
    [
      new() { Name = "Ann", Kind = IdentityKind.Player },
      new() { Name = "Anne", Kind = IdentityKind.Npc },
      new() { Name = "Bob", Kind = IdentityKind.Player },
      new() { Name = "Joann", Kind = IdentityKind.Player }
    ];
    var question = NamesTableView.For("ann", [IdentityKind.Player], IdentityRowOrder.NameColumn, false);
    var calls = new List<string>();
    var result = NamesTableView.Project(source, question, row =>
    {
      calls.Add(row.Name);
      return Project(row);
    });
    CollectionAssert.AreEqual(new[] { "Ann", "Joann" }, calls);
    CollectionAssert.AreEqual(calls, result.Select(row => row.Name).ToList());

    calls.Clear();
    NamesTableView.Project(source, question with { Query = "missing" }, row =>
    {
      calls.Add(row.Name);
      return Project(row);
    });
    Assert.AreEqual(0, calls.Count, "an empty answer must construct no display rows");
  }

  [TestMethod]
  public void FilteringBeforeProjectionPreservesEveryGlobalSortAndTieBreak()
  {
    ClassificationReport.Row[] source =
    [
      new() { Name = "Cy", Kind = IdentityKind.Player, Class = "Wizard", Reason = "R3-chat" },
      new() { Name = "Ann", Kind = IdentityKind.Player, Class = "Bard", Reason = "R3-chat" },
      new() { Name = "Bob", Kind = IdentityKind.Npc, Reason = "R6-npcdb" },
      new() { Name = "Dan", Kind = IdentityKind.Player, Class = "Bard", Reason = "R3-chat" }
    ];
    foreach (var column in IdentityRowOrder.Columns)
    {
      foreach (var descending in new[] { false, true })
      {
        var question = NamesTableView.For("", [IdentityKind.Player], column, descending);
        var oldOrder = NamesTableView.Build(source.Select(Project).ToArray(), question);
        var newOrder = NamesTableView.Project(source, question, Project);
        CollectionAssert.AreEqual(oldOrder, newOrder, $"{column}, descending={descending}");
      }
    }
  }
}
