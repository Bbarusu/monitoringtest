using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace MonitorPC.Services;

/// <summary>Um jogo rodando agora, com o FPS do ultimo segundo.</summary>
public sealed record Jogo(int Pid, string Nome, int Fps, IntPtr Janela);

/// <summary>
/// Conta os quadros que cada programa entrega, pelo rastreamento de eventos do
/// Windows — o mesmo caminho do PresentMon, que e a referencia para isso.
///
/// Cada vez que um programa Direct3D mostra um quadro, o DXGI (DirectX 10 a 12)
/// ou o D3D9 publica um evento "Present". Contar esses eventos por processo, a
/// cada segundo, e o FPS. Nao injeta nada no jogo e nao desenha por cima dele:
/// quem mostra o numero e a Kisland.
///
/// Limites conhecidos:
/// - Precisa de privilegio elevado: sessoes de rastreamento sao do sistema. O
///   app instalado com acesso completo aos sensores ja roda assim.
/// - Jogos so em Vulkan ou OpenGL nao passam por esses dois provedores e podem
///   nao ser contados.
///
/// A sessao so existe com o contador ligado e a Kisland ativa. Desligado, nao
/// ha rastreamento nenhum.
/// </summary>
public sealed class FpsService : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    // Present_Start no DXGI, e a variante de sobreposicao multiplano.
    private const int DxgiPresent = 42;
    private const int DxgiPresentMultiplano = 55;
    private const int D3D9Present = 1;

    private const string NomeDaSessao = "KInfo-FPS";

    private readonly object _trava = new();
    private readonly Stopwatch _relogio = new();
    private Dictionary<int, int> _contagem = new();
    private TraceEventSession? _sessao;

    /// <summary>Por que o contador nao esta rodando, quando nao esta.</summary>
    public string? Motivo { get; private set; }

    public bool Rodando => _sessao is not null;

    public bool Iniciar()
    {
        if (_sessao is not null) return true;

        if (TraceEventSession.IsElevated() != true)
        {
            Motivo = "O contador de FPS precisa do KInfo rodando com acesso completo aos sensores.";
            return false;
        }

        try
        {
            // Uma sessao com o mesmo nome sobra quando o app fecha a forca.
            // Sem parar a antiga, a nova falharia por nome repetido.
            TraceEventSession.GetActiveSession(NomeDaSessao)?.Stop(true);

            var sessao = new TraceEventSession(NomeDaSessao) { StopOnDispose = true };
            sessao.EnableProvider(Dxgi, TraceEventLevel.Verbose);
            sessao.EnableProvider(D3D9, TraceEventLevel.Verbose);
            sessao.Source.AllEvents += Evento;

            _sessao = sessao;
            _relogio.Restart();
            Motivo = null;

            // Process() so volta quando a sessao para: thread propria, de
            // fundo, para nao segurar o encerramento do app.
            new Thread(() =>
            {
                try { sessao.Source.Process(); }
                catch (Exception ex) { Log.Erro("FPS: leitura", ex); }
            })
            { IsBackground = true, Name = "KInfo FPS" }.Start();

            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("FPS: iniciar", ex);
            Motivo = "O Windows não deixou iniciar a contagem de FPS.";
            Parar();
            return false;
        }
    }

    private void Evento(TraceEvent e)
    {
        var id = (int)e.ID;
        var quadro = (e.ProviderGuid == Dxgi && id is DxgiPresent or DxgiPresentMultiplano)
                     || (e.ProviderGuid == D3D9 && id == D3D9Present);
        if (!quadro) return;

        lock (_trava)
        {
            _contagem.TryGetValue(e.ProcessID, out var n);
            _contagem[e.ProcessID] = n + 1;
        }
    }

    /// <summary>
    /// Quadros por segundo de cada processo desde a ultima colheita. Chamado
    /// pela Kisland uma vez por segundo.
    /// </summary>
    public Dictionary<int, double> Colher()
    {
        Dictionary<int, int> contagem;
        double segundos;

        lock (_trava)
        {
            contagem = _contagem;
            _contagem = new Dictionary<int, int>();
            segundos = Math.Max(0.25, _relogio.Elapsed.TotalSeconds);
            _relogio.Restart();
        }

        return contagem.ToDictionary(p => p.Key, p => p.Value / segundos);
    }

    public void Parar()
    {
        try { _sessao?.Dispose(); }
        catch (Exception ex) { Log.Erro("FPS: parar", ex); }

        _sessao = null;

        lock (_trava) _contagem = new Dictionary<int, int>();
    }

    public void Dispose() => Parar();
}

/// <summary>
/// Decide qual dos programas que entregam quadros e um jogo.
///
/// Entregar quadros nao basta: o navegador, o Discord e o proprio Windows
/// fazem isso o tempo todo. Jogo e o que entrega quadros num ritmo de jogo e
/// tem uma janela ocupando a tela — em tela cheia, sem borda ou maximizada,
/// em qualquer monitor, com ou sem foco. Programas conhecidos por desenhar
/// muito sem serem jogos ficam de fora pelo nome.
/// </summary>
public static class DetectorDeJogo
{
    /// <summary>Abaixo disto ninguem esta jogando: e uma janela piscando o cursor.</summary>
    private const double FpsMinimo = 10;

    private static readonly HashSet<string> NaoSaoJogos = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "explorer", "KInfo", "chrome", "msedge", "firefox", "opera", "opera_gx", "brave",
        "vivaldi", "Discord", "Teams", "ms-teams", "Spotify", "steamwebhelper", "EpicGamesLauncher",
        "obs64", "obs32", "Code", "devenv", "WhatsApp", "Telegram", "SearchHost",
        "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ApplicationFrameHost",
        "vlc", "mpc-hc64", "mpc-be64", "PotPlayerMini64", "Microsoft.Media.Player", "Video.UI",
        "Photos", "PhotosApp", "SnippingTool", "ScreenClippingHost", "Widgets", "msedgewebview2",
        "NVIDIA Share", "RadeonSoftware", "AMDRSServ", "XboxPcAppFT", "GameBar", "Lively"
    };

    private static readonly Dictionary<int, string> Nomes = new();

    public static Jogo? Escolher(Dictionary<int, double> fpsPorProcesso)
    {
        var proprio = Environment.ProcessId;

        foreach (var (pid, fps) in fpsPorProcesso.OrderByDescending(p => p.Value))
        {
            if (fps < FpsMinimo) break;
            if (pid == proprio || pid <= 4) continue;

            var nome = NomeDoProcesso(pid);
            if (nome is null || NaoSaoJogos.Contains(nome)) continue;

            var janela = JanelaQueOcupaATela(pid);
            if (janela == IntPtr.Zero) continue;

            return new Jogo(pid, NomeAmigavel(pid, nome), (int)Math.Round(fps), janela);
        }

        return null;
    }

    private static string? NomeDoProcesso(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"Cyberpunk 2077" em vez de "Cyberpunk2077", quando o executavel diz.</summary>
    private static string NomeAmigavel(int pid, string processo)
    {
        if (Nomes.TryGetValue(pid, out var guardado)) return guardado;

        var nome = processo;
        try
        {
            using var p = Process.GetProcessById(pid);
            var produto = p.MainModule?.FileVersionInfo.ProductName?.Trim();
            if (!string.IsNullOrEmpty(produto) && produto.Length <= 40) nome = produto;
        }
        catch
        {
            // Processo protegido ou de 32 bits: fica o nome do executavel.
        }

        if (Nomes.Count > 64) Nomes.Clear();
        Nomes[pid] = nome;
        return nome;
    }

    /// <summary>
    /// Uma janela visivel do processo que cobre o monitor (tela cheia ou sem
    /// borda), esta maximizada, ou ocupa pelo menos metade dele. Janela
    /// minimizada nao conta: o jogo pausado no fundo nao e o que se esta
    /// jogando.
    /// </summary>
    private static IntPtr JanelaQueOcupaATela(int pid)
    {
        var achada = IntPtr.Zero;

        EnumWindows((janela, _) =>
        {
            GetWindowThreadProcessId(janela, out var dono);
            if (dono != pid || !IsWindowVisible(janela) || IsIconic(janela)) return true;

            if (TelaCheia.CobreOMonitor(janela, out _) || IsZoomed(janela) ||
                TelaCheia.FracaoDoMonitor(janela) >= 0.5)
            {
                achada = janela;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return achada;
    }

    private delegate bool CadaJanela(IntPtr janela, IntPtr parametro);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(CadaJanela retorno, IntPtr parametro);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr janela, out int processo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr janela);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr janela);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr janela);
}
