using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF.Views;

public partial class BookBrowseView : UserControl
{
    public BookBrowseView()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            DataContext is BookBrowseViewModel viewModel &&
            viewModel.SearchBooksCommand.CanExecute(null))
        {
            viewModel.SearchBooksCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void ImportBook_Click(
        object sender,
        System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not BookBrowseViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import a local book",
            Filter = "Supported books (*.epub;*.pdf)|*.epub;*.pdf|EPUB books (*.epub)|*.epub|PDF books (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true)
        {
            await viewModel.ImportLocalFileAsync(dialog.FileName);
        }
    }
}
