using System.Windows;
using System.Windows.Input;
using BackgroundCut.Desktop.Ui;

namespace BackgroundCut.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); }
    private void OnDragOver(object sender, System.Windows.DragEventArgs e) => e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
    private void OnDrop(object sender, System.Windows.DragEventArgs e) { if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0 && DataContext is MainViewModel vm) vm.DropPath(files[0]); }
}
