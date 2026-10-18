using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ThreadOps.Models;

public enum CandidateKind { Author, Commenter, NonFollower }

/// <summary>Perfil encontrado nos posts, aguardando a revisão do usuário.</summary>
public sealed class Candidate : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isFollowed;
    private bool _isUnfollowed;

    public required string Handle { get; init; }
    public required CandidateKind Kind { get; init; }

    /// <summary>O que a pessoa escreveu (post ou comentário), para o usuário decidir.</summary>
    public required string Snippet { get; init; }

    /// <summary>Post onde a pessoa foi encontrada (autor ou comentário).</summary>
    public string PostUrl { get; init; } = "";
    public string PostAuthor { get; init; } = "";
    public string PostText { get; init; } = "";

    /// <summary>Link do próprio comentário; habilita abrir uma conversa a partir dele.</summary>
    public string? CommentUrl { get; init; }

    /// <summary>Idade do comentário em minutos (null se o Threads não mostrou). Usado para ordenar a lista.</summary>
    public int? AgeMinutes { get; init; }

    public bool CanChat => Kind == CandidateKind.Commenter && !string.IsNullOrEmpty(CommentUrl);

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; OnChanged(); } }
    }

    public bool IsFollowed
    {
        get => _isFollowed;
        set
        {
            if (_isFollowed == value) return;
            _isFollowed = value;
            OnChanged();
            OnChanged(nameof(CanToggle));
        }
    }

    public bool IsUnfollowed
    {
        get => _isUnfollowed;
        set
        {
            if (_isUnfollowed == value) return;
            _isUnfollowed = value;
            OnChanged();
            OnChanged(nameof(CanToggle));
        }
    }

    /// <summary>A ação da lista (seguir ou deixar de seguir) já foi feita para este perfil.</summary>
    public bool IsDone => IsFollowed || IsUnfollowed;

    public bool CanToggle => !IsDone;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
