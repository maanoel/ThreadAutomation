using System.Windows;

namespace ThreadOps;

public enum PostDialogChoice { Cancel, FollowOnly, PostAndFollow }

/// <summary>Confirma a fila de "seguir" e, opcionalmente, recebe um post escrito pelo usuário.</summary>
public partial class PostDialog : Window
{
    public PostDialogChoice Choice { get; private set; } = PostDialogChoice.Cancel;
    public string PostText => PostBox.Text.Trim();

    public PostDialog(IReadOnlyList<string> handles)
    {
        InitializeComponent();
        SummaryText.Text = $"Seguir {handles.Count} perfil(is) marcado(s)?";
        HandlesText.Text = string.Join("  ", handles.Select(h => "@" + h));
        Loaded += (_, _) => PostBox.Focus();
    }

    private void PostBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        CounterText.Text = $"{PostBox.Text.Length}/{PostBox.MaxLength}";
        PostAndFollowButton.IsEnabled = PostText.Length > 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(PostDialogChoice.Cancel);
    private void FollowOnly_Click(object sender, RoutedEventArgs e) => Finish(PostDialogChoice.FollowOnly);
    private void PostAndFollow_Click(object sender, RoutedEventArgs e) => Finish(PostDialogChoice.PostAndFollow);

    private void Finish(PostDialogChoice choice)
    {
        Choice = choice;
        DialogResult = choice != PostDialogChoice.Cancel;
    }
}
