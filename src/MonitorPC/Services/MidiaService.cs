using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using SessaoDeMidia = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using GerenciadorDeMidia = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace MonitorPC.Services;

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
    public TimeSpan PosicaoAgora()
    {
        if (!Tocando) return Posicao;

        var andou = DateTimeOffset.Now - PosicaoLidaEm;
        var estimada = Posicao + (andou > TimeSpan.Zero ? andou : TimeSpan.Zero);

        return Duracao > TimeSpan.Zero && estimada > Duracao ? Duracao : estimada;
    }
}

public sealed class MidiaService : IDisposable
{
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(1);

    private sealed class Registro
    {
        public required SessaoDeMidia Sessao { get; init; }
        public bool Tocando { get; set; }

        public DateTime ComecouEm { get; set; } = DateTime.MinValue;

        public DateTime TocouEm { get; set; } = DateTime.MinValue;
    }

    private readonly object _trava = new();
    private readonly Dictionary<string, Registro> _sessoes = new();
    private GerenciadorDeMidia? _gerenciador;
    private string? _escolhida;
    private int _geracao;

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
            Log.Erro("Mídia: iniciar", ex);
        }
    }

    public void Conferir() => Escolher();

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

            if (tocando || registro.Tocando) registro.TocouEm = agora;

            registro.Tocando = tocando;
        }

        if (!Escolher() && id == _escolhida) _ = LerAsync();
    }

    private void Conteudo_Mudou(SessaoDeMidia sessao, object args)
    {
        if ((sessao.SourceAppUserModelId ?? "") == _escolhida) _ = LerAsync();
    }

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
        }
    }

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

            var capa = Atual is { } anterior && anterior.Origem == origem &&
                       anterior.Titulo == titulo && anterior.Artista == artista
                ? anterior.Capa
                : await LerCapaAsync(props!);

            var duracao = linha is null ? TimeSpan.Zero : linha.EndTime - linha.StartTime;
            var posicao = linha is null ? TimeSpan.Zero : linha.Position - linha.StartTime;
            var lidaEm = linha?.LastUpdatedTime ?? DateTimeOffset.Now;

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

            var quadro = BitmapDecoder.Create(copia, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            if (quadro.PixelWidth < 64 || quadro.PixelHeight < 64) return null;
            copia.Position = 0;

            var imagem = new BitmapImage();
            imagem.BeginInit();
            imagem.CacheOption = BitmapCacheOption.OnLoad;

            imagem.DecodePixelWidth = 144;
            imagem.StreamSource = copia;
            imagem.EndInit();

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

    private static ImageSource? IconeDoAppDe(string origem)
    {
        if (string.IsNullOrWhiteSpace(origem)) return null;

        lock (Icones)
            if (Icones.TryGetValue(origem, out var guardado)) return guardado;

        var icone = IconeDoWindows.DoApp(origem) ?? IconeDoProcesso(origem);
        lock (Icones) Icones[origem] = icone;
        return icone;
    }

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

        var ponto = nome.IndexOf('.');
        var traco = ponto > 0 ? nome.IndexOf('_', ponto + 1) : -1;
        if (traco > ponto + 1) nome = nome[(ponto + 1)..traco];

        return nome;
    }

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
