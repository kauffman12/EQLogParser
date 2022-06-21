using Syncfusion.UI.Xaml.Grid;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;

namespace EQLogParser
{
  class MainActions
  {
    private const string PETS_LIST_TITLE = "Verified Pets ({0})";
    private const string PLAYER_LIST_TITLE = "Verified Players ({0})";
    private static readonly ObservableCollection<SortableName> VerifiedPlayersView = new ObservableCollection<SortableName>();
    private static readonly ObservableCollection<SortableName> VerifiedPetsView = new ObservableCollection<SortableName>();
    private static readonly ObservableCollection<PetMapping> PetPlayersView = new ObservableCollection<PetMapping>();
    private static readonly SortablePetMappingComparer TheSortablePetMappingComparer = new SortablePetMappingComparer();

    internal static void Clear(ContentControl petsWindow, ContentControl playersWindow)
    {
      PetPlayersView.Clear();
      VerifiedPetsView.Clear();
      VerifiedPlayersView.Clear();
      VerifiedPlayersView.Add(new SortableName { Name = Labels.UNASSIGNED });
      DockingManager.SetHeader(petsWindow, string.Format(PETS_LIST_TITLE, VerifiedPetsView.Count));
      DockingManager.SetHeader(playersWindow, string.Format(PLAYER_LIST_TITLE, VerifiedPlayersView.Count));
    }

    internal static void InitPetOwners(MainWindow main, SfDataGrid petMappingGrid, GridComboBoxColumn ownerList, ContentControl petMappingWindow)
    {
      // pet -> players
      petMappingGrid.ItemsSource = PetPlayersView;
      ownerList.ItemsSource = VerifiedPlayersView;
      PlayerManager.Instance.EventsNewPetMapping += (sender, mapping) =>
      {
        main.Dispatcher.InvokeAsync(() =>
        {
          var existing = PetPlayersView.FirstOrDefault(item => item.Pet.Equals(mapping.Pet, StringComparison.OrdinalIgnoreCase));
          if (existing != null)
          {
            if (existing.Owner != mapping.Owner)
            {
              PetPlayersView.Remove(existing);
              InsertPetMappingIntoSortedList(mapping, PetPlayersView);
            }
          }
          else
          {
            InsertPetMappingIntoSortedList(mapping, PetPlayersView);
          }

          DockingManager.SetHeader(petMappingWindow, "Pet Owners (" + PetPlayersView.Count + ")");
        });

        main.CheckComputeStats();
      };
    }

    internal static void InitVerifiedPlayers(MainWindow main, SfDataGrid playersGrid, ContentControl playersWindow, ContentControl petMappingWindow)
    {
      // verified player table
      playersGrid.ItemsSource = VerifiedPlayersView;
      PlayerManager.Instance.EventsNewVerifiedPlayer += (sender, name) =>
      {
        main.Dispatcher.InvokeAsync(() =>
        {
          Helpers.InsertNameIntoSortedList(name, VerifiedPlayersView);
          DockingManager.SetHeader(playersWindow, string.Format(PLAYER_LIST_TITLE, VerifiedPlayersView.Count));
        });
      };

      PlayerManager.Instance.EventsRemoveVerifiedPlayer += (sender, name) =>
      {
        main.Dispatcher.InvokeAsync(() =>
        {
          var found = VerifiedPlayersView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPlayersView.Remove(found);
            DockingManager.SetHeader(playersWindow, string.Format(PLAYER_LIST_TITLE, VerifiedPlayersView.Count));

            var existing = PetPlayersView.FirstOrDefault(item => item.Owner.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
              PetPlayersView.Remove(existing);
              DockingManager.SetHeader(petMappingWindow, "Pet Owners (" + PetPlayersView.Count + ")");
            }

            main.CheckComputeStats();
          }
        });
      };
    }

    internal static void InitVerifiedPets(MainWindow main, SfDataGrid petsGrid, ContentControl petsWindow, ContentControl petMappingWindow)
    {
      // verified pets table
      petsGrid.ItemsSource = VerifiedPetsView;
      PlayerManager.Instance.EventsNewVerifiedPet += (sender, name) => main.Dispatcher.InvokeAsync(() =>
      {
        Helpers.InsertNameIntoSortedList(name, VerifiedPetsView);
        DockingManager.SetHeader(petsWindow, string.Format(PETS_LIST_TITLE, VerifiedPetsView.Count));
      });

      PlayerManager.Instance.EventsRemoveVerifiedPet += (sender, name) =>
      {
        main.Dispatcher.InvokeAsync(() =>
        {
          var found = VerifiedPetsView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPetsView.Remove(found);
            DockingManager.SetHeader(petsWindow, string.Format(PETS_LIST_TITLE, VerifiedPetsView.Count));

            var existing = PetPlayersView.FirstOrDefault(item => item.Pet.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
              PetPlayersView.Remove(existing);
              DockingManager.SetHeader(petMappingWindow, "Pet Owners (" + PetPlayersView.Count + ")");
            }

            main.CheckComputeStats();
          }
        });
      };
    }

    private static void InsertPetMappingIntoSortedList(PetMapping mapping, ObservableCollection<PetMapping> collection)
    {
      int index = collection.ToList().BinarySearch(mapping, TheSortablePetMappingComparer);
      if (index < 0)
      {
        collection.Insert(~index, mapping);
      }
      else
      {
        collection.Insert(index, mapping);
      }
    }

    private class SortablePetMappingComparer : IComparer<PetMapping>
    {
      public int Compare(PetMapping x, PetMapping y) => string.CompareOrdinal(x?.Owner, y?.Owner);
    }
  }
}
