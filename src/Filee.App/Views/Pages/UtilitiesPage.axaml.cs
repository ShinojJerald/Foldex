using Avalonia.Controls;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;

namespace Filee.App.Views.Pages;

public partial class UtilitiesPage : UserControl
{
    public UtilitiesPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not UtilitiesPageViewModel vm)
                return;
            vm.PickFolderAsync = () => FileDialogs.OpenFolderAsync(this);
            vm.PickFilesAsync = multiple => FileDialogs.OpenFilesAsync(this, multiple);
            vm.ConfirmAsync = async (title, message) => TopLevel.GetTopLevel(this) is Window owner
                && await PromptWindow.ConfirmAsync(owner, title, message, Services.AppHost.Get<ILocalizer>()["utils.rename_button"]);
        };
        SizeChanged += (_, e) => Cards.Columns = e.NewSize.Width < 900 ? 1 : 2;
    }
}
