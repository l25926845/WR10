using System.Windows;

namespace FullScreenStart;

/// <summary>轻量文本输入对话框，用于新建分组等命名场景。</summary>
public partial class NamePrompt : Window
{
    private NamePrompt(string title, string initial)
    {
        InitializeComponent();
        Title = title;
        Input.Text = initial;

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>弹出输入框；取消或空输入返回 null。</summary>
    public static string? Ask(Window owner, string title, string initial)
    {
        var prompt = new NamePrompt(title, initial) { Owner = owner };
        if (prompt.ShowDialog() != true)
        {
            return null;
        }

        string value = prompt.Input.Text.Trim();
        return value.Length > 0 ? value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
