using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace TinyBoss;

public partial class RenameDialog : Window
{
    private readonly TextBox _aliasInput;

    public string? AliasResult { get; private set; }

    public RenameDialog(string currentAlias = "")
    {
        AvaloniaXamlLoader.Load(this);

        _aliasInput = this.FindControl<TextBox>("AliasInput")!;
        _aliasInput.Text = currentAlias;

        var okButton = this.FindControl<Button>("OkButton")!;
        var cancelButton = this.FindControl<Button>("CancelButton")!;

        okButton.Click += (_, _) => Accept();
        cancelButton.Click += (_, _) => Close();

        KeyDown += OnKeyDown;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _aliasInput.Focus();
        _aliasInput.SelectAll();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Accept();
        else if (e.Key == Key.Escape) Close();
    }

    private void Accept()
    {
        var text = _aliasInput.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
            AliasResult = text;
        Close();
    }
}
