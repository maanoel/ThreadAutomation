using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ThreadOps.Models;
using ThreadOps.Services;

namespace ThreadOps;

public partial class MainWindow : Window
{
    private const string ThreadsUrl = "https://www.threads.com/";
    private const int MaxDailyLimit = 50;
    private const int MaxTopPosts = 20;
    private const int MaxRepliesPerPost = 15;

    // Pausa entre abrir um post e o próximo, para a página carregar e você acompanhar.
    private static readonly TimeSpan PageInterval = TimeSpan.FromSeconds(2);

    // Intervalo fixo entre um "seguir" e o próximo.
    private static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(5);
    private const int MaxConsecutiveMisses = 3;

    // Perfil próprio do app: o login no Threads fica salvo aqui entre execuções.
    private static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThreadOps", "WebView2");

    private enum StatusKind { Ok, Busy, Paused, Error }

    private readonly ActivityLog _log = new();
    private readonly FollowHistory _history = new();
    private ThreadsScraper? _scraper;
    private ThreadsFollower? _follower;
    private ThreadsConversation? _conversation;
    private ThreadsPoster? _poster;
    private ThreadsAccount? _account;
    private readonly FollowHistory _unfollowHistory = new("deixou-de-seguir.log");
    private int _unfollowedToday;
    private bool _unfollowMode;

    // Quem o app seguiu há menos que isso não entra na lista de "não me segue":
    // seguir e desfazer em poucos dias é o padrão follow/unfollow que a Meta pune.
    private const int UnfollowGraceDays = 7;
    private readonly AppConfig _config;
    private readonly AgentClient _agent;
    private readonly ObservableCollection<ChatMessage> _chatMessages = new();
    private Candidate? _chatCandidate;
    private string _me = "";
    // Duas listas independentes: candidatos da busca e quem não te segue de volta.
    private readonly ObservableCollection<Candidate> _candidates = new();
    private readonly ICollectionView _candidateView;
    private readonly ObservableCollection<Candidate> _nonFollowers = new();

    private ObservableCollection<Candidate> CurrentList => _unfollowMode ? _nonFollowers : _candidates;
    private System.Collections.IEnumerable CurrentView => _unfollowMode ? _nonFollowers : _candidateView;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private Storyboard? _radar;

    private bool _loggedIn;
    private bool _busy;
    private bool _paused;
    private bool _stopRequested;
    private bool _collecting;
    private int _followedToday;
    private string _filter = "all";

    public MainWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = _log.Entries;

        try
        {
            _config = AppConfig.Load();
        }
        catch (Exception ex)
        {
            _config = new AppConfig();
            _log.Error($"config.json inválido ({ex.Message}). Usando valores padrão.");
        }
        _me = _config.MeuUsuario.TrimStart('@');
        _agent = new AgentClient(_config.Agente);
        ChatList.ItemsSource = _chatMessages;
        _chatMessages.CollectionChanged += (_, _) => ChatScroll.ScrollToEnd();
        _log.Entries.CollectionChanged += (_, _) => LogScroll.ScrollToEnd();
        LogFileText.Text = "salvo em logs/" + Path.GetFileName(_log.FilePath);

        _candidateView = CollectionViewSource.GetDefaultView(_candidates);
        _candidateView.Filter = o => o is Candidate c && _filter switch
        {
            "autor" => c.Kind == CandidateKind.Author,
            "coment" => c.Kind == CandidateKind.Commenter,
            _ => true,
        };
        CandidateList.ItemsSource = _candidateView;
        _candidates.CollectionChanged += Candidates_CollectionChanged;
        _nonFollowers.CollectionChanged += Candidates_CollectionChanged;

        _followedToday = _history.CountToday();
        _unfollowedToday = _unfollowHistory.CountToday();

        _clock.Tick +=(_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");

        Loaded += async (_, _) =>
        {
            _radar = (Storyboard)FindResource("RadarSweep");
            _radar.Begin(this, true);
            UpdateStats();
            _log.Info("Sistema iniciado.");
            await InitWebViewAsync();
        };
    }

    // ---------- WebView2 / login ----------

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
            await Web.EnsureCoreWebView2Async(env);
            _log.Ok($"WebView2 carregado (runtime {env.BrowserVersionString}).");
            var page = new PageDriver(Web.CoreWebView2);
            _scraper = new ThreadsScraper(page);
            _follower = new ThreadsFollower(page);
            _conversation = new ThreadsConversation(page);
            _poster = new ThreadsPoster(page);
            _account = new ThreadsAccount(page);

            Web.CoreWebView2.SourceChanged += (_, _) =>
            {
                UrlText.Text = Web.CoreWebView2.Source;
                WarnIfStuckOnInstagram(Web.CoreWebView2.Source);
            };
            Web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    SetStatus($"ERRO ao carregar ({e.WebErrorStatus})", StatusKind.Error);
                    _log.Error($"Falha ao carregar a página: {e.WebErrorStatus}");
                    return;
                }
                await CheckLoginAsync();
            };
            Web.CoreWebView2.Navigate(ThreadsUrl);
        }
        catch (Exception ex)
        {
            SetStatus("ERRO: " + ex.Message, StatusKind.Error);
            _log.Error("Falha ao iniciar o WebView2: " + ex.Message);
        }
    }

    /// <summary>A sessão do Threads é identificada pelo cookie "sessionid" em threads.com.</summary>
    private async Task CheckLoginAsync()
    {
        var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(ThreadsUrl);
        var loggedIn = cookies.Any(c => c.Name == "sessionid" && !string.IsNullOrEmpty(c.Value));

        if (loggedIn == _loggedIn && StatusText.Text != "INICIANDO…") return;
        _loggedIn = loggedIn;

        if (loggedIn)
        {
            if (!_busy) SetStatus("PRONTO · sessão do Threads ativa", StatusKind.Ok);
            _log.Ok("Sessão do Threads detectada.");
        }
        else
        {
            SetStatus("NÃO LOGADO · faça login no painel central", StatusKind.Error);
            _log.Warn("Nenhuma sessão do Threads. Faça login no painel central.");
            // Diagnóstico: só os nomes, nunca os valores dos cookies.
            _log.Info("Cookies presentes: " + (cookies.Count == 0 ? "(nenhum)" : string.Join(", ", cookies.Select(c => c.Name).Distinct())));
        }
        UpdateStats();
    }

    /// <summary>
    /// O login "Continuar com o Instagram" passa por instagram.com e às vezes para na home do
    /// Instagram em vez de voltar ao Threads. Fora das páginas de login/OAuth, avisa o usuário.
    /// </summary>
    private void WarnIfStuckOnInstagram(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var onInstagram = uri.Host.EndsWith("instagram.com", StringComparison.OrdinalIgnoreCase);
        var inLoginFlow = uri.AbsolutePath.StartsWith("/oauth", StringComparison.OrdinalIgnoreCase)
                       || uri.AbsolutePath.StartsWith("/accounts", StringComparison.OrdinalIgnoreCase)
                       || uri.AbsolutePath.StartsWith("/challenge", StringComparison.OrdinalIgnoreCase);
        if (onInstagram && !inLoginFlow)
            _log.Warn("Você está no Instagram. Clique em INÍCIO DO THREADS para voltar (o login do Instagram já vale para o Threads).");
    }

    // ---------- Controles ----------

    /// <summary>
    /// Para cada termo: abre a busca, pega os top N posts e, em cada post, o autor e quem comentou.
    /// Só lê páginas — nada é seguido aqui; os candidatos vão para a lista para revisão.
    /// </summary>
    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        var terms = ParseTerms();
        if (terms.Count == 0)
        {
            _log.Warn("Informe ao menos um termo de busca.");
            return;
        }
        if (_scraper is null || _busy) return;

        var top = TopPosts();
        var seenPosts = new HashSet<string>();
        var seenHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedFollowed = 0;
        var postsRead = 0;

        void AddCandidate(string handle, CandidateKind kind, string snippet, PostInfo post, string? commentUrl = null, int? ageMinutes = null)
        {
            if (!seenHandles.Add(handle)) return;
            if (_history.Contains(handle)) { skippedFollowed++; return; }
            _candidates.Add(new Candidate
            {
                Handle = handle, Kind = kind, Snippet = snippet,
                PostUrl = post.Url, PostAuthor = post.Author, PostText = post.Text, CommentUrl = commentUrl,
                AgeMinutes = ageMinutes,
            });
        }

        _candidates.Clear();
        ShowTab(unfollow: false);
        StatPosts.Text = "0";
        SetBusy(true, "coletando posts");
        _collecting = true;
        UpdateStats();
        try
        {
            foreach (var term in terms)
            {
                _log.Action($"Buscando \"{term}\" (aba Principais)…");
                var posts = await _scraper.SearchTopPostsAsync(term, top, WaitIfPausedAsync);
                if (posts.Count == 0)
                {
                    _log.Warn($"Nenhum post encontrado para \"{term}\". Diagnóstico: {await _scraper.DiagnosticsAsync()}");
                    continue;
                }
                _log.Info($"{posts.Count} posts encontrados para \"{term}\".");

                for (var i = 0; i < posts.Count; i++)
                {
                    var post = posts[i];
                    if (!seenPosts.Add(post.Url)) continue;
                    await WaitIfPausedAsync();

                    RadarText.Text = $"POST\n{i + 1}/{posts.Count}";
                    AddCandidate(post.Author, CandidateKind.Author, post.Text, post);

                    var replies = await _scraper.ReadRepliesAsync(post, MaxRepliesPerPost);
                    foreach (var r in replies)
                        AddCandidate(r.Author, CandidateKind.Commenter, r.Text, post, r.Url, r.AgeMinutes);

                    postsRead++;
                    StatPosts.Text = postsRead.ToString();
                    _log.Info($"Post {i + 1}/{posts.Count} @{post.Author} — {replies.Count} comentário(s) lido(s).");
                    await Task.Delay(PageInterval);
                }
            }

            _log.Ok($"Coleta concluída: {postsRead} posts · {_candidates.Count} candidatos. Revise e marque quem seguir.");
            if (skippedFollowed > 0)
                _log.Info($"{skippedFollowed} perfil(is) já seguido(s) pelo app foram omitidos.");
        }
        catch (OperationCanceledException)
        {
            _log.Warn($"Coleta interrompida: {postsRead} posts · {_candidates.Count} candidatos mantidos para revisão.");
        }
        catch (Exception ex)
        {
            _log.Error("Erro na coleta: " + ex.Message);
        }
        finally
        {
            _collecting = false;
            SortByRecentActivity();
            SetBusy(false);
            RadarText.Text = _candidates.Count > 0 ? "REVISÃO" : "PRONTO";
        }
    }

    /// <summary>
    /// Ordena os candidatos: os de comentário mais recente vêm primeiro (quem está ativo agora responde mais),
    /// os sem data ficam depois na ordem em que foram lidos. Move os itens em vez de recriar a lista,
    /// para não duplicar os handlers de PropertyChanged.
    /// </summary>
    private void SortByRecentActivity()
    {
        var ordered = _candidates
            .Select((c, i) => (c, i))
            .OrderBy(x => x.c.AgeMinutes.HasValue ? 0 : 1)
            .ThenBy(x => x.c.AgeMinutes ?? 0)
            .ThenBy(x => x.i)
            .Select(x => x.c)
            .ToList();

        for (var target = 0; target < ordered.Count; target++)
        {
            var from = _candidates.IndexOf(ordered[target]);
            if (from != target) _candidates.Move(from, target);
        }
    }

    private void SetBusy(bool busy, string activity = "")
    {
        _busy = busy;
        _stopRequested = false;
        _paused = false; // pausa vale só para a execução em andamento
        PauseButton.Content = "❚❚  PAUSAR";
        SetStatus(busy ? "EXECUTANDO · " + activity : "PRONTO · sessão do Threads ativa", busy ? StatusKind.Busy : StatusKind.Ok);
        UpdateStats();
    }

    /// <summary>Ponto de parada entre etapas: segura enquanto pausado e encerra se pediram PARAR.</summary>
    private async Task WaitIfPausedAsync()
    {
        while (_paused && !_stopRequested) await Task.Delay(200);
        if (_stopRequested) throw new OperationCanceledException();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) return;
        _stopRequested = true;
        _paused = false;
        PauseButton.Content = "❚❚  PAUSAR";
        StopButton.IsEnabled = false;
        _log.Warn("Parada solicitada — encerrando após a etapa atual…");
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) return;
        _paused = !_paused;
        PauseButton.Content = _paused ? "▶  RETOMAR" : "❚❚  PAUSAR";
        if (_paused)
        {
            SetStatus("PAUSADO", StatusKind.Paused);
            _log.Warn("Execução pausada pelo usuário.");
        }
        else
        {
            SetStatus(_busy ? "EXECUTANDO" : "PRONTO", _busy ? StatusKind.Busy : StatusKind.Ok);
            _log.Info("Execução retomada.");
        }
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Navigate(ThreadsUrl);
    private void BackButton_Click(object sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }
    private void ReloadButton_Click(object sender, RoutedEventArgs e) => Web.Reload();

    private void LimitBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) UpdateStats();
    }

    private void LimitCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        LimitBox.IsEnabled = LimitEnabled;
        if (LimitEnabled)
            _log.Info("Limite diário ativado.");
        else
            _log.Warn("Limite diário DESATIVADO — sem teto por dia. Isso aumenta o risco de bloqueio pelo Threads.");
        UpdateStats();
    }

    // ---------- Candidatos ----------

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        _filter = (string)((RadioButton)sender).Tag;
        _candidateView?.Refresh();
    }

    private void MarkAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in CurrentView.Cast<Candidate>().Where(c => !c.IsDone))
            c.IsChecked = true;
        _log.Warn("Visíveis marcados — revise e desmarque quem não quiser antes de confirmar.");
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in CurrentList) c.IsChecked = false;
    }

    /// <summary>
    /// Confirma a fila (com post opcional escrito por você), publica o post e depois segue,
    /// um por vez, só os perfis marcados, até o limite diário.
    /// Para a fila se o Threads não confirmar um "seguir" (possível limite da plataforma).
    /// </summary>
    private async void FollowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_unfollowMode)
        {
            await UnfollowMarkedAsync();
            return;
        }
        if (_follower is null || _poster is null) return;
        if (_busy && !_collecting) return;

        var room = DailyLimit() - _followedToday;
        var queue = _candidates.Where(c => c.IsChecked && !c.IsFollowed).Take(room).ToList();
        if (queue.Count == 0) return;

        var dialog = new PostDialog(queue.Select(c => c.Handle).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            _log.Info("Execução cancelada na confirmação.");
            return;
        }

        if (_busy)
        {
            // Durante a coleta dá para seguir: encerra a coleta e usa o que já foi marcado.
            _log.Warn("Encerrando a coleta para seguir os marcados…");
            _stopRequested = true;
            _paused = false;
            UpdateStats();
            while (_busy) await Task.Delay(200);
        }

        SetBusy(true, "seguindo marcados");
        var misses = 0;
        try
        {
            if (dialog.Choice == PostDialogChoice.PostAndFollow)
            {
                _log.Action("Publicando o seu post no perfil…");
                var posted = await _poster.PostAsync(dialog.PostText);
                if (posted != PostResult.Posted)
                {
                    _log.Error($"O post não foi publicado ({posted}). Os seguires não foram iniciados.");
                    return;
                }
                _log.Ok("Post publicado no seu perfil.");
                await Task.Delay(FollowInterval);
            }

            _log.Action($"Iniciando: {queue.Count} perfil(is) na fila.");
            for (var i = 0; i < queue.Count; i++)
            {
                await WaitIfPausedAsync();
                var c = queue[i];
                RadarText.Text = $"SEGUINDO\n{i + 1}/{queue.Count}";
                CandidateList.ScrollIntoView(c);
                _log.Info($"Abrindo perfil @{c.Handle}…");

                var result = await _follower.FollowAsync(c.Handle);
                switch (result)
                {
                    case FollowResult.Followed:
                        _history.Add(c.Handle);
                        _followedToday++;
                        c.IsChecked = false;
                        c.IsFollowed = true;
                        misses = 0;
                        _log.Ok($"Seguiu @{c.Handle}");
                        break;

                    case FollowResult.AlreadyFollowing:
                        c.IsChecked = false;
                        c.IsFollowed = true;
                        _log.Info($"Você já seguia @{c.Handle}.");
                        break;

                    case FollowResult.ButtonNotFound:
                        _log.Warn($"Botão Seguir não encontrado em @{c.Handle} — pulado.");
                        if (++misses >= MaxConsecutiveMisses)
                        {
                            _log.Error($"{MaxConsecutiveMisses} perfis seguidos sem botão. Fila parada — o layout do Threads pode ter mudado.");
                            return;
                        }
                        break;

                    case FollowResult.NotConfirmed:
                        _log.Error($"O Threads não confirmou o seguir em @{c.Handle}. Fila parada — pode ser limite de ação da plataforma; tente mais tarde.");
                        return;
                }
                UpdateStats();

                if (_followedToday >= DailyLimit())
                {
                    var left = queue.Skip(i + 1).Count(x => !x.IsFollowed);
                    _log.Warn($"Limite diário ({DailyLimit()}) atingido." + (left > 0 ? $" {left} marcado(s) ficaram para amanhã." : ""));
                    return;
                }
                if (i < queue.Count - 1) await Task.Delay(FollowInterval);
            }
            _log.Ok($"Fila concluída. Registrado em {Path.GetFileName(_history.FilePath)}.");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Fila interrompida pelo usuário. Os marcados restantes continuam na lista.");
        }
        catch (Exception ex)
        {
            _log.Error("Erro ao seguir: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            RadarText.Text = "CONCLUÍDO";
        }
    }

    private void Candidates_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Candidate c in e.NewItems) c.PropertyChanged += (_, _) => UpdateStats();
        UpdateStats();
    }

    // ---------- Quem não me segue ----------

    /// <summary>
    /// Lê Seguindo e Seguidores do seu perfil e lista quem você segue mas não te segue de volta.
    /// Só lê — ninguém é removido até você marcar e confirmar.
    /// </summary>
    private async void NonFollowersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null || _busy) return;

        _nonFollowers.Clear();
        ShowTab(unfollow: true);
        SetBusy(true, "lendo suas listas");
        try
        {
            if (string.IsNullOrEmpty(_me))
            {
                _me = await _account.DetectMeAsync();
                if (string.IsNullOrEmpty(_me))
                {
                    _log.Error("Não consegui detectar seu usuário. Preencha \"MeuUsuario\" no config.json.");
                    return;
                }
                _config.MeuUsuario = _me;
                _config.Save();
                _log.Info($"Seu usuário: @{_me} (salvo no config.json).");
            }

            _log.Action("Lendo a lista Seguindo…");
            RadarText.Text = "SEGUINDO";
            var following = await _account.ReadListAsync(_me, FollowListKind.Following, WaitIfPausedAsync,
                n => ReportListProgress("Seguindo", n));
            _log.Info($"Você segue {following.Count} perfil(is).");

            _log.Action("Lendo a lista Seguidores…");
            var followers = await _account.ReadListAsync(_me, FollowListKind.Followers, WaitIfPausedAsync,
                n => ReportListProgress("Seguidores", n));
            _log.Info($"{followers.Count} perfil(is) te seguem.");

            var cutoff = DateTime.Now.AddDays(-UnfollowGraceDays);
            var inGrace = 0;
            foreach (var h in following.Where(h => !followers.Contains(h)).OrderBy(h => h))
            {
                if (_history.When(h) is { } followedAt && followedAt > cutoff) { inGrace++; continue; }
                _nonFollowers.Add(new Candidate { Handle = h, Kind = CandidateKind.NonFollower, Snippet = "Você segue, mas não te segue de volta." });
            }

            _log.Ok($"{_nonFollowers.Count} perfil(is) não te seguem de volta. Revise e marque quem deixar de seguir.");
            if (inGrace > 0)
                _log.Info($"{inGrace} seguido(s) pelo app há menos de {UnfollowGraceDays} dias ficaram de fora (ainda podem seguir de volta).");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Leitura das listas interrompida.");
        }
        catch (Exception ex)
        {
            _log.Error("Erro ao ler suas listas: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            RadarText.Text = _nonFollowers.Count > 0 ? "REVISÃO" : "PRONTO";
        }
    }

    // ---------- Abas da lista da direita ----------

    private void ShowTab(bool unfollow)
    {
        _unfollowMode = unfollow;
        if (TabSearch.IsChecked != !unfollow) TabSearch.IsChecked = !unfollow;
        if (TabNonFollowers.IsChecked != unfollow) TabNonFollowers.IsChecked = unfollow;
        CandidateList.ItemsSource = CurrentView;
        FilterChips.Visibility = unfollow ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = unfollow
            ? "Clique em QUEM NÃO ME SEGUE para ler suas listas. Quem você segue mas não te segue de volta aparece aqui."
            : "Nenhum candidato ainda. Autores e comentaristas dos posts aparecem aqui — você revisa e marca.";
        UpdateStats();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var unfollow = sender == TabNonFollowers;
        if (unfollow != _unfollowMode) ShowTab(unfollow);
    }

    private int _lastListProgressLogged;

    /// <summary>Mostra a contagem no radar e registra no log a cada 50 perfis lidos.</summary>
    private void ReportListProgress(string list, int count)
    {
        RadarText.Text = $"{list.ToUpperInvariant()}\n{count}";
        if (count < _lastListProgressLogged) _lastListProgressLogged = 0; // nova lista
        if (count - _lastListProgressLogged >= 50)
        {
            _lastListProgressLogged = count;
            _log.Info($"{list}: {count} perfis lidos…");
        }
    }

    /// <summary>Deixa de seguir, um por vez, só os marcados, até o limite diário.</summary>
    private async Task UnfollowMarkedAsync()
    {
        if (_account is null || _busy) return;

        var room = DailyLimit() - _unfollowedToday;
        var queue = _nonFollowers.Where(c => c.IsChecked && !c.IsDone).Take(room).ToList();
        if (queue.Count == 0) return;

        var answer = MessageBox.Show(this,
            $"Deixar de seguir {queue.Count} perfil(is)?\n\n" + string.Join("  ", queue.Take(30).Select(c => "@" + c.Handle)) +
            (queue.Count > 30 ? $"  … e mais {queue.Count - 30}" : ""),
            "THREAD//OPS · confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            _log.Info("Execução cancelada na confirmação.");
            return;
        }

        SetBusy(true, "deixando de seguir");
        _log.Action($"Iniciando: {queue.Count} perfil(is) para deixar de seguir.");
        var misses = 0;
        try
        {
            for (var i = 0; i < queue.Count; i++)
            {
                await WaitIfPausedAsync();
                var c = queue[i];
                RadarText.Text = $"REMOVENDO\n{i + 1}/{queue.Count}";
                CandidateList.ScrollIntoView(c);
                _log.Info($"Abrindo perfil @{c.Handle}…");

                switch (await _account.UnfollowAsync(c.Handle))
                {
                    case UnfollowResult.Unfollowed:
                        _unfollowHistory.Add(c.Handle);
                        _unfollowedToday++;
                        c.IsChecked = false;
                        c.IsUnfollowed = true;
                        misses = 0;
                        _log.Ok($"Deixou de seguir @{c.Handle}");
                        break;

                    case UnfollowResult.NotFollowing:
                        c.IsChecked = false;
                        c.IsUnfollowed = true;
                        _log.Info($"Você já não seguia @{c.Handle}.");
                        break;

                    case UnfollowResult.ButtonNotFound:
                        _log.Warn($"Botão Seguindo/confirmação não encontrado em @{c.Handle} — pulado.");
                        if (++misses >= MaxConsecutiveMisses)
                        {
                            _log.Error($"{MaxConsecutiveMisses} perfis seguidos sem botão. Fila parada — o layout do Threads pode ter mudado.");
                            return;
                        }
                        break;

                    case UnfollowResult.NotConfirmed:
                        _log.Error($"O Threads não confirmou em @{c.Handle}. Fila parada — pode ser limite de ação da plataforma; tente mais tarde.");
                        return;
                }
                UpdateStats();

                if (_unfollowedToday >= DailyLimit())
                {
                    var left = queue.Skip(i + 1).Count(x => !x.IsDone);
                    _log.Warn($"Limite diário ({DailyLimit()}) atingido." + (left > 0 ? $" {left} marcado(s) ficaram para amanhã." : ""));
                    return;
                }
                if (i < queue.Count - 1) await Task.Delay(FollowInterval);
            }
            _log.Ok($"Fila concluída. Registrado em {Path.GetFileName(_unfollowHistory.FilePath)}.");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Fila interrompida pelo usuário.");
        }
        catch (Exception ex)
        {
            _log.Error("Erro ao deixar de seguir: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            RadarText.Text = "CONCLUÍDO";
        }
    }

    // ---------- Conversa (agente de IA sugere, você publica) ----------

    private void ChatButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // não marca/desmarca o candidato
        if (((FrameworkElement)sender).DataContext is not Candidate c || !c.CanChat) return;

        _chatCandidate = c;
        _chatMessages.Clear();
        _chatMessages.Add(new ChatMessage(c.Handle, c.Snippet, false));
        ChatHandleText.Text = "@" + c.Handle;
        ChatContextText.Text = $"No post de @{c.PostAuthor}: “{c.PostText}”";
        ChatDraftBox.Text = "";
        CandidatesPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        _log.Info($"Conversa aberta com @{c.Handle}.");
        if (!_agent.IsConfigured)
            _log.Warn($"Agente de IA não configurado: preencha \"Agente.Url\" em {AppConfig.FilePath} e reinicie o app.");
    }

    private void ChatBackButton_Click(object sender, RoutedEventArgs e)
    {
        ChatPanel.Visibility = Visibility.Collapsed;
        CandidatesPanel.Visibility = Visibility.Visible;
    }

    private async void ChatReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation is null || _chatCandidate?.CommentUrl is not { } url || _busy) return;
        SetBusy(true, "lendo conversa");
        try
        {
            var (messages, detectedMe) = await _conversation.ReadAsync(url, _chatCandidate.Handle, _me);
            if (string.IsNullOrEmpty(_me) && !string.IsNullOrEmpty(detectedMe))
            {
                _me = detectedMe;
                _log.Info($"Seu usuário detectado: @{_me} (defina \"MeuUsuario\" no config.json para fixar).");
            }

            if (messages.Count == 0)
            {
                _log.Warn("Não consegui ler a conversa nesta página.");
                return;
            }
            _chatMessages.Clear();
            foreach (var m in messages) _chatMessages.Add(m);
            _log.Ok($"Conversa lida: {messages.Count} mensagem(ns).");
        }
        catch (Exception ex)
        {
            _log.Error("Erro ao ler conversa: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ChatSuggestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCandidate is null || _busy) return;
        if (!_agent.IsConfigured)
        {
            _log.Warn($"Agente de IA não configurado: preencha \"Agente.Url\" em {AppConfig.FilePath} e reinicie o app.");
            return;
        }

        SetBusy(true, "consultando agente de IA");
        try
        {
            var c = _chatCandidate;
            _log.Action($"Pedindo sugestão ao agente para @{c.Handle}…");
            var reply = await _agent.SuggestReplyAsync(c.Handle, _me, c.PostUrl, c.PostAuthor, c.PostText, _chatMessages.ToList());
            if (string.IsNullOrWhiteSpace(reply))
            {
                _log.Warn("O agente devolveu uma resposta vazia.");
                return;
            }
            ChatDraftBox.Text = reply;
            _log.Ok("Sugestão recebida. Revise o rascunho antes de preencher.");
        }
        catch (Exception ex)
        {
            _log.Error("Erro no agente de IA: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ChatFillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation is null || _chatCandidate?.CommentUrl is not { } url || _busy) return;
        var text = ChatDraftBox.Text.Trim();
        if (text.Length == 0)
        {
            _log.Warn("Rascunho vazio.");
            return;
        }

        SetBusy(true, "preenchendo resposta");
        try
        {
            var result = await _conversation.FillReplyAsync(url, text);
            switch (result)
            {
                case FillResult.Filled:
                    _log.Ok($"Resposta para @{_chatCandidate.Handle} preenchida. Revise e clique em Publicar no Threads.");
                    break;
                case FillResult.CommentNotFound:
                    _log.Warn("Comentário não encontrado na página.");
                    break;
                case FillResult.ReplyButtonNotFound:
                    _log.Warn("Botão Responder não encontrado no comentário.");
                    break;
                case FillResult.ComposerNotFound:
                    _log.Warn("A caixa de resposta não abriu. Clique em Responder no Threads e cole o rascunho.");
                    Clipboard.SetText(text);
                    _log.Info("Rascunho copiado para a área de transferência.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Erro ao preencher resposta: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- Estado visual ----------

    private void UpdateStats()
    {
        var list = CurrentList;
        var marked = list.Count(c => c.IsChecked && !c.IsDone);
        var limit = DailyLimit();
        var doneToday = _unfollowMode ? _unfollowedToday : _followedToday; // cada ação tem seu próprio limite

        StatCandidates.Text = _candidates.Count.ToString();
        StatMarked.Text = marked.ToString();
        StatFollowed.Text = _followedToday.ToString();
        CandidateInfo.Text = list.Count > 0 ? $"{list.Count} na lista" : "—";
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var label = LimitEnabled ? "LIMITE DIÁRIO" : "SEM LIMITE DIÁRIO";
        QuotaLabel.Text = _unfollowMode ? label + " · DEIXAR DE SEGUIR" : label;
        if (LimitEnabled)
        {
            QuotaBar.Maximum = limit;
            QuotaBar.Value = Math.Min(doneToday, limit);
            QuotaText.Text = $"{doneToday}/{limit}";
        }
        else
        {
            // Sem teto: a barra fica vazia e o contador mostra só o que já foi feito hoje.
            QuotaBar.Maximum = 1;
            QuotaBar.Value = 0;
            QuotaText.Text = doneToday.ToString();
        }

        SearchButton.IsEnabled = NonFollowersButton.IsEnabled = _loggedIn && !_busy;
        StopButton.IsEnabled = _busy && !_stopRequested;
        ChatReadButton.IsEnabled = ChatSuggestButton.IsEnabled = ChatFillButton.IsEnabled = _loggedIn && !_busy;
        var canReview = !_busy || (_collecting && !_unfollowMode); // marcar/seguir também durante a coleta da busca
        PauseButton.IsEnabled = _busy;
        MarkAllButton.IsEnabled = ClearButton.IsEnabled = list.Count > 0 && canReview;
        FollowButton.Content = doneToday >= limit
            ? $"LIMITE DIÁRIO ATINGIDO ({doneToday}/{limit})"
            : _unfollowMode ? $"DEIXAR DE SEGUIR ({marked})" : $"SEGUIR MARCADOS ({marked})";
        FollowButton.IsEnabled = _loggedIn && canReview && !_stopRequested && marked > 0 && doneToday < limit;
    }

    private void SetStatus(string text, StatusKind kind)
    {
        var color = kind switch
        {
            StatusKind.Ok => (Color)ColorConverter.ConvertFromString("#7DFF6B"),
            StatusKind.Busy => (Color)ColorConverter.ConvertFromString("#FFB547"),
            _ => (Color)ColorConverter.ConvertFromString("#FF4D6D"),
        };
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
        StatusGlow.Color = color;

        // Radar acelera e acende quando há trabalho em andamento.
        var busy = kind == StatusKind.Busy;
        Sweep.Opacity = busy ? 1 : 0.25;
        _radar?.SetSpeedRatio(this, busy ? 2.7 : 1);
        RadarText.Text = kind switch
        {
            StatusKind.Busy => "EXECUTANDO",
            StatusKind.Paused => "PAUSADO",
            StatusKind.Error => _loggedIn ? "ERRO" : "SEM LOGIN",
            _ => "PRONTO",
        };
    }

    // Ligado por padrão. Desmarcado = sem teto diário (int.MaxValue faz todas as checagens de limite passarem).
    private bool LimitEnabled => LimitCheck.IsChecked == true;

    private int DailyLimit() =>
        !LimitEnabled ? int.MaxValue
        : int.TryParse(LimitBox.Text, out var n) ? Math.Clamp(n, 1, MaxDailyLimit) : 30;

    private int TopPosts() =>
        int.TryParse(TopPostsBox.Text, out var n) ? Math.Clamp(n, 1, MaxTopPosts) : 10;

    private List<string> ParseTerms() =>
        TermsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
