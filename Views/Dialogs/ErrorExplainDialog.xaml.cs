using System.Windows;

namespace LenSa.Core.Views.Dialogs
{
    public partial class ErrorExplainDialog : Window
    {
        public string CodeText => CodeTextBox.Text;
        public string ErrorText => ErrorTextBox.Text;
        public string ModulePath => ModulePathBox.Text;

        public ErrorExplainDialog()
        {
            InitializeComponent();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
