using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using SessaoDeMidia = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using GerenciadorDeMidia = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace MonitorPC.Services;

/// <summary>O que esta tocando agora, num instante.</summary>
public sealed record EstadoDeMidia(
    string Titulo,
    string Artista,
    string App,
    bool Tocando,
    ImageSource? Capa,
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
/// Por ali vem qualquer player que se anuncie ao sistema: Spotify, navegador
/// tocando YouTube, o player do Windows. Nao ha integracao por app, e nenhum
/// deles precisa saber que o KInfo existe.
///
/// Nao consulta nada em intervalo: o Windows avisa quando a sessao, a faixa ou
/// o estado mudam, e o servico so le nessa hora.
/// </summary>
public sealed class MidiaService : IDisposable
{
    private GerenciadorDeMidia? _gerenciador;
    private SessaoDeMidia? _sessao;
    private int _geracao;

    /// <summary>
    /// Disparado em thread qualquer, com nulo quando nada esta tocando nem
    /// pausado em player nenhum.
    /// </summary>
    public event Action<EstadoDeMidia?>? Mudou;

    public EstadoDeMidia? Atual { get; private set; }

    public async Task IniciarAsync()
    {
        try
        {
            _gerenciador = await GerenciadorDeMidia.RequestAsync();
            _gerenciador.CurrentSessionChanged += (_, _) => TrocarSessao();
            TrocarSessao();
        }
        catch (Exception ex)
        {
            // Sem o controle de midia a ilha so mostra sensores. Nao e motivo
            // para derrubar nada.
            Log.Erro("Mídia: iniciar", ex);
        }
    }

    private void TrocarSessao()
    {
        try
        {
            if (_sessao is not null)
            {
                _sessao.MediaPropertiesChanged -= Sessao_Mudou;
                _sessao.PlaybackInfoChanged -= Sessao_Mudou;
                _sessao.TimelinePropertiesChanged -= Sessao_Mudou;
            }

            _sessao = _gerenciador?.GetCurrentSession();

            if (_sessao is not null)
            {
                _sessao.MediaPropertiesChanged += Sessao_Mudou;
                _sessao.PlaybackInfoChanged += Sessao_Mudou;
                _sessao.TimelinePropertiesChanged += Sessao_Mudou;
            }

            _ = LerAsync();
        }
        catch (Exception ex)
        {
            Log.Erro("Mídia: trocar sessão", ex);
        }
    }

    private void Sessao_Mudou(SessaoDeMidia sender, object args) => _ = LerAsync();

    /// <summary>
    /// Le a sessao inteira. Os avisos chegam em rajada — trocar de faixa dispara
    /// os tres de uma vez — e cada leitura carrega a capa: a geracao faz so a
    /// ultima leitura publicar o resultado.
    /// </summary>
    private async Task LerAsync()
    {
        var minha = Interlocked.Increment(ref _geracao);

        try
        {
            var sessao = _sessao;

            if (sessao is null)
            {
                Publicar(minha, null);
                return;
            }

            var props = await sessao.TryGetMediaPropertiesAsync();
            var info = sessao.GetPlaybackInfo();
            var linha = sessao.GetTimelineProperties();

            var titulo = props?.Title?.Trim() ?? "";
            if (titulo.Length == 0)
            {
                Publicar(minha, null);
                return;
            }

            var tocando = info?.PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var capa = Atual is { } anterior && anterior.Titulo == titulo && anterior.Capa is not null
                ? anterior.Capa
                : await LerCapaAsync(props!);

            var duracao = linha is null ? TimeSpan.Zero : linha.EndTime - linha.StartTime;
            var posicao = linha is null ? TimeSpan.Zero : linha.Position - linha.StartTime;
            var lidaEm = linha?.LastUpdatedTime ?? DateTimeOffset.Now;

            // Um player que nunca atualizou a linha do tempo devolve a data
            // zero, e a posicao estimada viraria decadas.
            if (lidaEm.Year < 2000) lidaEm = DateTimeOffset.Now;

            Publicar(minha, new EstadoDeMidia(
                titulo,
                props?.Artist?.Trim() ?? "",
                NomeDoApp(sessao.SourceAppUserModelId),
                tocando,
                capa,
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
            if (_sessao is { } sessao) await acao(sessao);
        }
        catch (Exception ex)
        {
            Log.Erro("Mídia: comando", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_sessao is not null)
            {
                _sessao.MediaPropertiesChanged -= Sessao_Mudou;
                _sessao.PlaybackInfoChanged -= Sessao_Mudou;
                _sessao.TimelinePropertiesChanged -= Sessao_Mudou;
            }
        }
        catch
        {
            // Encerrando: nada a fazer com a falha.
        }

        _sessao = null;
        _gerenciador = null;
    }
}
