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

    /// <summary>
    /// DXGI_PRESENT_TEST: o jogo so pergunta se a janela esta visivel, nada
    /// vai para a tela. Varios jogos fazem isso a cada quadro, e contar a
    /// pergunta como quadro dobrava o numero.
    /// </summary>
    private const uint SoTeste = 0x1;

    /// <summary>Janela da media: o ultimo segundo de quadros de cada cadeia.</summary>
    private const double JanelaMs = 1000;

    /// <summary>Sem quadro novo ha tanto tempo, a cadeia parou (jogo fechado ou pausado).</summary>
    private static readonly TimeSpan Validade = TimeSpan.FromSeconds(2.5);

    private const string NomeDaSessao = "KInfo-FPS";

    /// <summary>
    /// Os quadros de uma cadeia de troca (swap chain): o "lugar" onde o jogo
    /// desenha. Um processo pode ter mais de uma — o jogo e um launcher, uma
    /// sobreposicao, um segundo buffer — e somar todas dava um FPS que nao
    /// existia. Vale a mais rapida.
    /// </summary>
    private sealed class Cadeia
    {
        /// <summary>Hora de cada quadro, pelo relogio do proprio evento, em ms.</summary>
        public readonly Queue<double> Quadros = new();
        public DateTime ChegouEm;
    }

    private readonly object _trava = new();
    private readonly Dictionary<(int Pid, ulong Troca), Cadeia> _cadeias = new();
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
        var dxgi = e.ProviderGuid == Dxgi && id is DxgiPresent or DxgiPresentMultiplano;
        var d3d9 = e.ProviderGuid == D3D9 && id == D3D9Present;
        if (!dxgi && !d3d9) return;

        // Os dois eventos comecam pelo endereco da cadeia de troca; o Present
        // do DXGI traz, logo depois, as opcoes do Present.
        ulong cadeia = 0;
        try
        {
            var dados = e.EventData();
            var ponteiro = e.PointerSize;

            if (ponteiro is 4 or 8 && dados.Length >= ponteiro)
                cadeia = ponteiro == 8 ? BitConverter.ToUInt64(dados, 0) : BitConverter.ToUInt32(dados, 0);

            if (dxgi && id == DxgiPresent && dados.Length >= ponteiro + 4 &&
                (BitConverter.ToUInt32(dados, ponteiro) & SoTeste) != 0)
                return;
        }
        catch
        {
            // Formato inesperado: conta o quadro, sem separar por cadeia.
        }

        // A hora vem do evento, e nao de quando ele chegou aqui. O Windows
        // entrega os eventos em lotes, a cada fracao de segundo; contar pela
        // chegada fazia um segundo receber dois lotes e o seguinte nenhum — o
        // numero pulava de 40 para 140 com o jogo estavel em 90.
        var quando = e.TimeStampRelativeMSec;

        lock (_trava)
        {
            var chave = (e.ProcessID, cadeia);
            if (!_cadeias.TryGetValue(chave, out var c))
                _cadeias[chave] = c = new Cadeia();

            c.Quadros.Enqueue(quando);
            c.ChegouEm = DateTime.UtcNow;

            while (c.Quadros.Count > 0 && quando - c.Quadros.Peek() > JanelaMs)
                c.Quadros.Dequeue();
        }
    }

    /// <summary>
    /// Quadros por segundo de cada processo: a media do ultimo segundo de
    /// quadros, pelos intervalos entre eles — como o PresentMon mede. Chamado
    /// pela Kisland uma vez por segundo.
    /// </summary>
    public Dictionary<int, double> Colher()
    {
        var resultado = new Dictionary<int, double>();
        var agora = DateTime.UtcNow;

        lock (_trava)
        {
            foreach (var (chave, c) in _cadeias.ToList())
            {
                if (agora - c.ChegouEm > Validade)
                {
                    _cadeias.Remove(chave);
                    continue;
                }

                // Precisa de dois quadros para haver um intervalo.
                if (c.Quadros.Count < 2) continue;

                var primeiro = c.Quadros.Peek();
                var ultimo = c.Quadros.Last();
                var duracao = ultimo - primeiro;
                if (duracao <= 0) continue;

                var fps = (c.Quadros.Count - 1) * 1000.0 / duracao;

                if (!resultado.TryGetValue(chave.Pid, out var outro) || fps > outro)
                    resultado[chave.Pid] = fps;
            }
        }

        return resultado;
    }

    public void Parar()
    {
        try { _sessao?.Dispose(); }
        catch (Exception ex) { Log.Erro("FPS: parar", ex); }

        _sessao = null;

        lock (_trava) _cadeias.Clear();
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
        "NVIDIA Share", "RadeonSoftware", "AMDRSServ", "XboxPcAppFT", "GameBar", "Lively",

        // Acesso remoto e transmissao: mostram a tela de outro PC (ou a deste)
        // num ritmo de jogo, em janela cheia, e nao sao o jogo.
        "parsecd", "Parsec", "AnyDesk", "TeamViewer", "mstsc", "msrdc", "RustDesk",
        "Moonlight", "sunshine", "Steam", "steamstreamingclient",

        // Apps de chat e trabalho feitos com navegador embutido.
        "Claude", "ChatGPT", "slack", "Notion", "Figma"
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
