using System.Windows;
using GalgameManager.Application.Community;
using GalgameManager.Infrastructure;

namespace GalgameManager.Desktop;

public partial class AccountWindow : Window
{
    private readonly CommunityClient client;
    private readonly CancellationTokenSource lifetime = new();
    private bool register;

    public AccountWindow(CommunityClient client, bool register)
    {
        this.client = client;
        InitializeComponent();
        SetMode(register);
        Loaded += (_, _) => EmailBox.Focus();
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            lifetime.Dispose();
            PasswordInput.Clear();
            ConfirmInput.Clear();
        };
    }

    private void SwitchMode(object sender, RoutedEventArgs e)
    {
        Status.Text = "";
        SetMode(!register);
    }

    private void SetMode(bool registration)
    {
        register = registration;
        Heading.Text = registration ? "注册论坛账号" : "登录论坛";
        SubmitButton.Content = registration ? "注册并登录" : "登录";
        ModeButton.Content = registration ? "已有账号" : "注册账号";
        NicknameField.Visibility = ConfirmationField.Visibility = registration ? Visibility.Visible : Visibility.Collapsed;
        PasswordInput.Clear();
        ConfirmInput.Clear();
    }

    private async void Submit(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var password = PasswordInput.Password;
        if (email.Length == 0 || password.Length == 0)
        {
            Status.Text = "请填写邮箱和密码。";
            return;
        }
        if (register && (NicknameBox.Text.Trim().Length == 0 ||
            password.Length < CommunityLimits.PasswordMin || password != ConfirmInput.Password))
        {
            Status.Text = "请填写昵称并确认两次密码一致，密码至少 10 个字符。";
            return;
        }

        var token = lifetime.Token;
        Fields.IsEnabled = ModeButton.IsEnabled = SubmitButton.IsEnabled = false;
        Status.Text = register ? "正在注册…" : "正在登录…";
        try
        {
            if (register)
            {
                await client.RegisterAsync(new Registration(email, NicknameBox.Text.Trim(), password), token);
                token.ThrowIfCancellationRequested();
                SetMode(false);
                Status.Text = "账号已创建，正在登录…";
            }
            await client.SignInAsync(new Login(email, password), token);
            if (IsVisible)
                DialogResult = true;
        }
        catch (CommunityException ex)
        {
            if (IsVisible)
                Status.Text = ex.Message;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (IsVisible)
                Fields.IsEnabled = ModeButton.IsEnabled = SubmitButton.IsEnabled = true;
        }
    }
}
