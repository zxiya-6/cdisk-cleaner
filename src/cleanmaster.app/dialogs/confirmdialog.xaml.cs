using System.Windows;

namespace CleanMaster.App.Dialogs;

public partial class ConfirmDialog : Window
{
    private bool _requireAck;

    public bool Confirmed { get; private set; }

    public ConfirmDialog(string title, string message, IReadOnlyList<string>? details = null,
        string? ackText = null, string okText = "确认", bool danger = true)
    {
        InitializeComponent();
        Title = title;
        TxtMessage.Text = message;
        BtnOk.Content = okText;
        if (danger) BtnOk.Style = (Style)FindResource("DangerButton");

        if (details is { Count: > 0 })
        {
            DetailsBox.Visibility = Visibility.Visible;
            foreach (var d in details) ListDetails.Items.Add(d);
        }

        if (!string.IsNullOrEmpty(ackText))
        {
            _requireAck = true;
            ChkRequired.Visibility = Visibility.Visible;
            ChkRequired.Content = ackText;
            ChkRequired.Checked += (_, _) => BtnOk.IsEnabled = true;
            ChkRequired.Unchecked += (_, _) => BtnOk.IsEnabled = false;
            BtnOk.IsEnabled = false;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (_requireAck && ChkRequired.IsChecked != true) return;
        Confirmed = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}
