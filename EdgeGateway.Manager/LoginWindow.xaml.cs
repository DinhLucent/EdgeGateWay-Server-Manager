using System.Windows;
using EdgeGateway.Core.Security;
using EdgeGateway.Manager.Api;

namespace EdgeGateway.Manager;

public partial class LoginWindow : Window
{
    private readonly EdgeGatewayApiClient apiClient;
    private bool bootstrapMode;

    internal LoginWindow(EdgeGatewayApiClient apiClient)
    {
        InitializeComponent();
        this.apiClient = apiClient;
        Loaded += async (_, _) => await LoadAuthStatusAsync();
    }

    private async Task LoadAuthStatusAsync()
    {
        try
        {
            EdgeGatewayAuthStatus status = await apiClient.GetAuthStatusAsync();
            if (!status.AuthRequired)
            {
                DialogResult = true;
                Close();
                return;
            }

            bootstrapMode = status.RequiresBootstrap;
            if (bootstrapMode)
            {
                TitleTextBlock.Text = "Create initial admin";
                HelpTextBlock.Text =
                    "No EdgeGateway admin exists yet. Create the initial local admin password.";
                SubmitButton.Content = "Create Admin";
            }
            else
            {
                TitleTextBlock.Text = "Sign in";
                HelpTextBlock.Text =
                    "Use an EdgeGateway account to manage service, devices, config, and orders.";
                SubmitButton.Content = "Login";
            }

            PasswordBox.Focus();
        }
        catch (Exception ex)
        {
            ErrorTextBlock.Text = ex.Message;
        }
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        SubmitButton.IsEnabled = false;
        ErrorTextBlock.Text = "";
        try
        {
            string userName = string.IsNullOrWhiteSpace(UserNameTextBox.Text)
                ? "admin"
                : UserNameTextBox.Text.Trim();
            EdgeGatewayLoginResult result = bootstrapMode
                ? await apiClient.BootstrapAdminAsync(
                    new EdgeGatewayBootstrapRequest(userName, PasswordBox.Password)
                )
                : await apiClient.LoginAsync(
                    new EdgeGatewayLoginRequest(userName, PasswordBox.Password)
                );

            apiClient.UseSessionToken(result.Token);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorTextBlock.Text = ex.Message;
            SubmitButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
