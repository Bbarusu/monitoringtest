using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using SessaoDeMidia = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using GerenciadorDeMidia = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace MonitorPC.Services;

/// <summary>O que esta tocando agora, num instante.</summary>
public sealed record EstadoDeMidia(
    string Origem,
    string Titulo,
    string Artista,
    string App,
    bool Tocando,
    ImageSource? Capa,
    ImageSource? IconeDoApp,
    TimeSpan Posicao,
    TimeSpan Duracao,
    DateTimeOffset PosicaoLidaEm)
{
    /// <summary>
    /// Posicao estimada agora. O Windows so atualiza a linha do tempo quando o
    /// player avisa, o que alguns fazem a cada poucos segundos e outros so ao
    /// pausar: tocando, a posicao anda pelo relogio desde a ultima leitura.
    /// </summary>
    public TimeSpan PosicaoAgora()
    {
        if (!Tocando) return Posicao;

        var andou = DateTimeOffset.Now - PosicaoLidaEm;
        var estimada = Posicao + (andou > TimeSpan.Zero ? andou : TimeSpan.Zero);

        return Duracao > TimeSpan.Zero && estimada > Duracao ? Duracao : estimada;
    }
}

/// <summary>
/// Le e controla a midia pelo controle de transporte do proprio Windows — o
/// mesmo que poe a musica no painel de volume e na tela de bloqueio.
///
/// Acompanha todas as sessoes, e nao so a que o Windows chama de "atual". A
/// atual e a ultima que se anunciou, e o Windows a mantem ali mesmo pausada:
/// uma mensagem de audio parada no WhatsApp continuava sendo "o que esta
/// tocando" por horas, com a mesma foto, enquanto o som vinha de outro app.
/// Agora a escolha e de quem toca de fato; ninguem tocando, de quem tocou por
/// ultimo — e so ate um minuto depois de parar.
/// </summary>
public sealed class MidiaService : IDisposable
{
    /// <summary>Parado ha mais que isto, o player deixa de ser "o que esta tocando".</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(1);

    /// <summary>O que se sabe de cada sessao, pelo id do app.</summary>
    private sealed class Registro
    {
        public required SessaoDeMidia Sessao { get; init; }
        public bool Tocando { get; set; }

        /// <summary>Quando comecou a tocar desta vez: entre dois tocando, vence o mais recente.</summary>
        public DateTime ComecouEm { get; set; } = DateTime.MinValue;

        /// <summary>A ultima vez em que estava tocando. Parado ha muito, sai da disputa.</summary>
        public DateTime TocouEm { get; set; } = DateTime.MinValue;
    }

    private readonly object _trava = new();
    private readonly Dictionary<string, Registro> _sessoes = new();
    private GerenciadorDeMidia? _gerenciador;
    private string? _escolhida;
    private int _geracao;

    /// <summary>
    /// Disparado em thread qualquer, com nulo quando nada toca nem tocou nos
    /// ultimos minutos.
    /// </summary>
    public event Action<EstadoDeMidia?>? Mudou;

    public EstadoDeMidia? Atual { get; private set; }

    public async Task IniciarAsync()
    {
        try
        {
            _gerenciador = await GerenciadorDeMidia.RequestAsync();
            _gerenciador.SessionsChanged += (_, _) => Sincronizar();
            Sincronizar();
        }
        catch (Exception ex)
        {
            // Sem o controle de midia a ilha so mostra sensores. Nao e motivo
            // para derrubar nada.
            Log.Erro("Mídia: iniciar", ex);
        }
    }

    /// <summary>
    /// Chamada a cada segundo pela ilha. E o que tira da tela um player que
    /// parou ha mais de um minuto: nenhum aviso do Windows chega nessa
    /// hora, porque nada mudou — so o tempo passou.
    /// </summary>
    public void Conferir() => Escolher();

    // ------------------------------------------------------------------
    // Sessoes

    private void Sincronizar()
    {
        try
        {
            var atuais = _gerenciador?.GetSessions();
            if (atuais is null) return;

            lock (_trava)
            {
                var vistas = new HashSet<string>();

                foreach (var sessao in atuais)
                {
                    var id = sessao.SourceAppUserModelId ?? "";
                    if (!vistas.Add(id)) continue;
                    if (_sessoes.ContainsKey(id)) continue;

                    var tocando = EstaTocando(sessao);
                    var agora = DateTime.UtcNow;

                    _sessoes[id] = new Registro
                    {
                        Sessao = sessao,
                        Tocando = tocando,

                        // Pausada quando o app a encontrou: sem como saber ha
                        // quanto tempo, conta como antiga. Quem abre o PC nao
                        // quer ver a musica que pausou ontem.
                        ComecouEm = tocando ? agora : DateTime.MinValue,
                        TocouEm = tocando ? agora : DateTime.MinValue
                    };

                    sessao.PlaybackInfoChanged += Estado_Mudou;
                    sessao.MediaPropertiesChanged += Conteudo_Mudou;
                    sessao.TimelinePropertiesChanged += Conteudo_Mudou;
                }

                foreach (var id in _sessoes.Keys.Where(k => !vistas.Contains(k)).ToList())
                {
                    Desligar(_sessoes[id].Sessao);
                    _sessoes.Remove(id);
                }
            }

            Escolher();
        }
        catch (Exception ex)
        {
            Log.Erro("Mídia: sessões", ex);
        }
    }

    private static bool EstaTocando(SessaoDeMidia sessao)
    {
        try
        {
            return sessao.GetPlaybackInfo()?.PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private void Estado_Mudou(SessaoDeMidia sessao, object args)
    {
        var id = sessao.SourceAppUserModelId ?? "";
        var tocando = EstaTocando(sessao);
        var agora = DateTime.UtcNow;

        lock (_trava)
        {
            if (!_sessoes.TryGetValue(id, out var registro)) return;

            if (tocando && !registro.Tocando) registro.ComecouEm = agora;

            // Tocando agora, ou parando agora: nos dois casos, este foi o
            // ultimo instante em que estava tocando.
            if (tocando || registro.Tocando) registro.TocouEm = agora;

            registro.Tocando = tocando;
        }

        // Mudou de estado a que esta na tela: relê para o icone de play e
        // pausa acompanharem. Outra sessao: pode ter passado a frente.
        if (!Escolher() && id == _escolhida) _ = LerAsync();
    }

    private void Conteudo_Mudou(SessaoDeMidia sessao, object args)
    {
        if ((sessao.SourceAppUserModelId ?? "") == _escolhida) _ = LerAsync();
    }

    /// <summary>
    /// Quem aparece: entre as que tocam, a que comecou por ultimo; nenhuma
    /// tocando, a que tocou por ultimo, se faz menos de um minuto.
    /// Devolve verdadeiro quando a escolha mudou (e ja mandou ler).
    /// </summary>
    private bool Escolher()
    {
        string? nova;
        var agora = DateTime.UtcNow;

        lock (_trava)
        {
            var tocando = _sessoes
                .Where(p => p.Value.Tocando)
                .OrderByDescending(p => p.Value.ComecouEm)
                .Select(p => p.Key)
                .FirstOrDefault();

            nova = tocando ?? _sessoes
                .Where(p => agora - p.Value.TocouEm < Validade)
                .OrderByDescending(p => p.Value.TocouEm)
                .Select(p => p.Key)
                .FirstOrDefault();

            if (nova == _escolhida) return false;
            _escolhida = nova;
        }

        _ = LerAsync();
        return true;
    }

    private SessaoDeMidia? SessaoEscolhida()
    {
        lock (_trava)
        {
            return _escolhida is not null && _sessoes.TryGetValue(_escolhida, out var r) ? r.Sessao : null;
        }
    }

    private void Desligar(SessaoDeMidia sessao)
    {
        try
        {
            sessao.PlaybackInfoChanged -= Estado_Mudou;
            sessao.MediaPropertiesChanged -= Conteudo_Mudou;
            sessao.TimelinePropertiesChanged -= Conteudo_Mudou;
        }
        catch
        {
            // Sessao que ja sumiu do lado do Windows.
        }
    }

    // ------------------------------------------------------------------
    // Leitura

    /// <summary>
    /// Le a sessao escolhida inteira. Os avisos chegam em rajada — trocar de
    /// faixa dispara os tres de uma vez — e cada leitura carrega a capa: a
    /// geracao faz so a ultima leitura publicar o resultado.
    /// </summary>
    private async Task LerAsync()
    {
        var minha = Interlocked.Increment(ref _geracao);

        try
        {
            var sessao = SessaoEscolhida();

            if (sessao is null)
            {
                Publicar(minha, null);
                return;
            }

            var origem = sessao.SourceAppUserModelId ?? "";
            var props = await sessao.TryGetMediaPropertiesAsync();
            var info = sessao.GetPlaybackInfo();
            var linha = sessao.GetTimelineProperties();

            var titulo = props?.Title?.Trim() ?? "";
            if (titulo.Length == 0)
            {
                Publicar(minha, null);
                return;
            }

            var artista = props?.Artist?.Trim() ?? "";
            var tocando = info?.PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // A capa so e reaproveitada se for do mesmo app e da mesma faixa.
            // Pelo titulo apenas, a foto de um app ia parar em outro.
            var capa = Atual is { } anterior && anterior.Origem == origem &&
                       anterior.Titulo == titulo && anterior.Artista == artista
                ? anterior.Capa
                : await LerCapaAsync(props!);

            var duracao = linha is null ? TimeSpan.Zero : linha.EndTime - linha.StartTime;
            var posicao = linha is null ? TimeSpan.Zero : linha.Position - linha.StartTime;
            var lidaEm = linha?.LastUpdatedTime ?? DateTimeOffset.Now;

            // Um player que nunca atualizou a linha do tempo devolve a data
            // zero, e a posicao estimada viraria decadas.
            if (lidaEm.Year < 2000) lidaEm = DateTimeOffset.Now;

            Publicar(minha, new EstadoDeMidia(
                origem,
                titulo,
                artista,
                NomeDoApp(origem),
                tocando,
                capa,
                capa is null ? IconeDoAppDe(origem) : null,
                posicao < TimeSpan.Zero ? TimeSpan.Zero : posicao,
                duracao < TimeSpan.Zero ? TimeSpan.Zero : duracao,
                lidaEm));
        }
        catch (Exception ex)
        {
            // Player fechando no meio da leitura e o caso comum aqui.
            Log.Erro("Mídia: ler", ex);
        }
    }

    private void Publicar(int geracao, EstadoDeMidia? estado)
    {
        if (geracao != Volatile.Read(ref _geracao)) return;

        Atual = estado;
        Mudou?.Invoke(estado);
    }

    private static async Task<ImageSource?> LerCapaAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        try
        {
            if (props.Thumbnail is null) return null;

            using var fluxo = await props.Thumbnail.OpenReadAsync();
            using var origem = fluxo.AsStreamForRead();

            var copia = new MemoryStream();
            await origem.CopyToAsync(copia);
            copia.Position = 0;

            // Miniatura pequena nao e capa: e o icone do site (o navegador
            // manda o favicon quando a pagina nao tem arte, e numa guia
            // anonima ele nao repassa a arte da pagina). Esticada para 72 px,
            // virava um borrao qualquer; sem capa, entra o icone do app.
            var quadro = BitmapDecoder.Create(copia, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            if (quadro.PixelWidth < 64 || quadro.PixelHeight < 64) return null;
            copia.Position = 0;

            var imagem = new BitmapImage();
            imagem.BeginInit();
            imagem.CacheOption = BitmapCacheOption.OnLoad;

            // A capa aparece com 72 px no maximo; decodificar a imagem inteira
            // guardaria um bitmap de 1000 px na memoria a troco de nada.
            imagem.DecodePixelWidth = 144;
            imagem.StreamSource = copia;
            imagem.EndInit();

            // Criada fora da thread da interface: sem congelar, a interface nao
            // poderia usa-la.
            imagem.Freeze();
            return imagem;
        }
        catch (Exception ex)
        {
            Log.Erro("Mídia: capa", ex);
            return null;
        }
    }

    private static readonly Dictionary<string, ImageSource?> Icones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O icone do app que esta tocando, para quando nao ha capa: o Chrome numa
    /// guia anonima, um video sem arte. Pela lista de apps do Windows (a do
    /// menu Iniciar), que conhece tanto os apps da loja quanto os instalados
    /// pelo jeito antigo; sem ele la, pelo .exe do processo. Guardado por id.
    /// </summary>
    private static ImageSource? IconeDoAppDe(string origem)
    {
        if (string.IsNullOrWhiteSpace(origem)) return null;

        lock (Icones)
            if (Icones.TryGetValue(origem, out var guardado)) return guardado;

        var icone = IconeDoWindows.DoApp(origem) ?? IconeDoProcesso(origem);
        lock (Icones) Icones[origem] = icone;
        return icone;
    }

    /// <summary>"chrome.exe" ou "Spotify": o icone do .exe de um processo aberto com esse nome.</summary>
    private static ImageSource? IconeDoProcesso(string origem)
    {
        try
        {
            var nome = origem;
            var exclamacao = nome.IndexOf('!');
            if (exclamacao > 0) nome = nome[..exclamacao];
            nome = Path.GetFileNameWithoutExtension(nome);
            if (nome.Length == 0) return null;

            foreach (var processo in System.Diagnostics.Process.GetProcessesByName(nome))
            {
                using (processo)
                {
                    try
                    {
                        if (processo.MainModule?.FileName is { } caminho) return AtalhoService.Icone(caminho);
                    }
                    catch
                    {
                        // Processo protegido: tenta o proximo.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Mídia: ícone do processo", ex);
        }

        return null;
    }

    /// <summary>
    /// O Windows identifica o player pelo id de app, que para quem usa e ruido:
    /// "Spotify.exe", "MSEdge", "308046B0AF4A39CB". Os conhecidos ganham nome;
    /// o resto perde so o ".exe".
    /// </summary>
    private static string NomeDoApp(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";

        var minusculo = id.ToLowerInvariant();

        if (minusculo.Contains("spotify")) return "Spotify";
        if (minusculo.Contains("whatsapp")) return "WhatsApp";
        if (minusculo.Contains("telegram")) return "Telegram";
        if (minusculo.Contains("discord")) return "Discord";
        if (minusculo.Contains("msedge")) return "Edge";
        if (minusculo.Contains("chrome")) return "Chrome";
        if (minusculo.Contains("firefox") || minusculo == "308046b0af4a39cb") return "Firefox";
        if (minusculo.Contains("opera")) return "Opera";
        if (minusculo.Contains("brave")) return "Brave";
        if (minusculo.Contains("zunemusic") || minusculo.Contains("media player")) return "Media Player";
        if (minusculo.Contains("vlc")) return "VLC";
        if (minusculo.Contains("deezer")) return "Deezer";

        var nome = id;
        var exclamacao = nome.IndexOf('!');
        if (exclamacao > 0) nome = nome[..exclamacao];
        if (nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) nome = nome[..^4];

        // Ids de pacote da loja ("Fabricante.App_hash"): fica o meio.
        var ponto = nome.IndexOf('.');
        var traco = ponto > 0 ? nome.IndexOf('_', ponto + 1) : -1;
        if (traco > ponto + 1) nome = nome[(ponto + 1)..traco];

        return nome;
    }

    // ------------------------------------------------------------------
    // Controles. Cada player decide se aceita; recusar nao e erro.

    public Task TocarPausar() => Comando(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task Proxima() => Comando(s => s.TrySkipNextAsync().AsTask());
    public Task Anterior() => Comando(s => s.TrySkipPreviousAsync().AsTask());

    private async Task Comando(Func<SessaoDeMidia, Task<bool>> acao)
    {
        try
        {
            if (SessaoEscolhida() is { } sessao) await acao(sessao);
        }
        catch (Exception ex)
        {
            Log.Erro("Mídia: comando", ex);
        }
    }

    public void Dispose()
    {
        lock (_trava)
        {
            foreach (var registro in _sessoes.Values) Desligar(registro.Sessao);
            _sessoes.Clear();
            _escolhida = null;
        }

        _gerenciador = null;
    }
}
