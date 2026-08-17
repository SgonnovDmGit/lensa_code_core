using System.Windows;

namespace LenSa.Core.Views.Dialogs
{
    public partial class CommandConfirmDialog : Window
    {
        public bool AutoExecuteAll { get; private set; }

        public CommandConfirmDialog(string command, string description)
        {
            InitializeComponent();
            CommandText.Text = command;
            DescriptionText.Text = description;
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void AutoAll_Click(object sender, RoutedEventArgs e)
        {
            AutoExecuteAll = true;
            DialogResult = true;
        }
    }
}
