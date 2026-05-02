using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TinyBoss.Core;

namespace TinyBoss;

public partial class ResumeHistoryWindow : Window
{
    private readonly string _historyText;

    public ResumeHistoryWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _historyText = ResumeSessionHistory.FormatLatest();
        this.FindControl<TextBox>("HistoryBox")!.Text = _historyText;
        this.FindControl<TextBlock>("PathText")!.Text = ResumeSessionHistory.HistoryPath;

        this.FindControl<Button>("CopyButton")!.Click += async (_, _) =>
        {
            if (Clipboard is not null)
                await Clipboard.SetTextAsync(_historyText);
        };
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }
}
