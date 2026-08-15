using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OplusEdlTool
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
        }

        private void OK_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
