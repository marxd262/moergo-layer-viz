using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// One tab in the layer-selector bar. The parent <see cref="MainWindowViewModel"/>
/// passes a <c>selectAction</c> so the VM doesn't need to know its owner.
/// </summary>
public partial class LayerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public int Index { get; }
    public string DisplayName { get; }
    public string TabColor { get; }
    public IRelayCommand SelectCommand { get; }

    public LayerViewModel(int index, string displayName, string tabColor, Action<int> selectAction)
    {
        Index = index;
        DisplayName = displayName;
        TabColor = tabColor;
        SelectCommand = new RelayCommand(() => selectAction(index));
    }
}
