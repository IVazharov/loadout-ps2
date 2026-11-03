using System.Windows;
using System.Windows.Controls;
using Loadout.Library;

namespace Loadout.App;

public partial class InstalledGamesListPanel : UserControl
{
    public InstalledGamesListPanel()
    {
        InitializeComponent();
    }

    // SelectedItems cannot be bound, so Remove gets the selection from here.
    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is InstalledGamesViewModel viewModel)
        {
            viewModel.SetSelection(GamesList.SelectedItems.Cast<InstalledGameRow>().ToList());
        }
    }

    private void GamesList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView list)
        {
            GameColumnSort.Enable(list);
        }
    }
}
