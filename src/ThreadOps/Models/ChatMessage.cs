namespace ThreadOps.Models;

/// <summary>Uma mensagem da conversa num comentário do Threads.</summary>
public sealed record ChatMessage(string Author, string Text, bool IsMine);
