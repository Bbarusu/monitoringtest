using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MonitorPC.Setup;

public partial class SetupWindow : Window
{
    private readonly bool _desinstalando = App.Desinstalando;
    private string _pasta = Instalacao.PastaPadrao;

    private bool _atualizando;
    private OpcoesDeInstalacao? _anteriores;

    private List<string> _ressalvas = new();

    public SetupWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => CaberNaTela();
        Loaded += (_, _) => CaberNaTela();
        SizeChanged += (_, _) => CaberNaTela();
        DpiChanged += (_, _) => CaberNaTela();

        if (Instalacao.PastaInstalada() is { Length: > 0 } anterior) _pasta = anterior;

        CaminhoDeInstalacao.Text = _pasta;
        Administrador_Alterado(this, new RoutedEventArgs());

        if (_desinstalando) PrepararDesinstalacao();
        else if (App.Atualizando && Instalacao.PastaInstalada() is { Length: > 0 }) PrepararAtualizacao();
    }

    private void PrepararAtualizacao()
    {
        _atualizando = true;

        Titulo.Text = "Atualizando o KInfo";
        var anterior = Instalacao.VersaoInstalada();
        Subtitulo.Text = anterior is { Length: > 0 } && anterior != Comum.Versao.Texto
            ? $"Da {anterior} para a {Comum.Versao.Texto}"
            : $"Versão {Comum.Versao.Texto}";

        if (Notas() is { } notas)
        {
            NotasDaVersao.Text = notas;
            BlocoNotas.Visibility = Visibility.Visible;
        }

        PassoOpcoes.Visibility = Visibility.Collapsed;
        PassoAndamento.Visibility = Visibility.Visible;
        Etapa.Text = "Conferindo a instalação atual…";

        Loaded += async (_, _) =>
        {
            _anteriores = await Task.Run(() => Instalacao.OpcoesAnteriores());

            if (_anteriores is null)
            {
                _atualizando = false;
                Titulo.Text = "KInfo";
                PassoAndamento.Visibility = Visibility.Collapsed;
                PassoOpcoes.Visibility = Visibility.Visible;
                return;
            }

            await Rodar();
        };
    }

    private static string? Notas()
    {
        try
        {
            using var fluxo = Assembly.GetExecutingAssembly().GetManifestResourceStream("notas.md");
            if (fluxo is null) return null;

            using var leitor = new StreamReader(fluxo, Encoding.UTF8);
            var texto = leitor.ReadToEnd().Replace("\r\n", "\n");

            var detalhes = System.Text.RegularExpressions.Regex.Match(texto, @"(?im)^##\s*Detalhes");
            if (detalhes.Success) texto = texto[..detalhes.Index];

            texto = string.Join("\n", texto.Split('\n').Where(l => !l.TrimStart().StartsWith('#'))).Trim();

            return texto.Length == 0 || texto.StartsWith('(') ? null : texto;
        }
        catch
        {
            return null;
        }
    }

    private void Administrador_Alterado(object sender, RoutedEventArgs e)
    {
        if (AvisoDoDriver is null) return;

        AvisoDoDriver.Text = OpcaoAdministrador.IsChecked == true
            ? "Ler temperatura de CPU e placa-mãe exige um driver em modo kernel, que só carrega em modo administrador. O instalador registra uma tarefa do Windows para o app subir como admin sem pedir confirmação toda vez. O driver em si é o PawnIO, instalado no sistema pela opção abaixo."
            : "Sem modo admin o driver de sensores não carrega. O app abre e funciona, mas temperatura de CPU, tensões, rotação de ventoinhas e tudo que vem do chip da placa-mãe ficam indisponíveis. GPU, discos, memória e uso de CPU continuam normais. Dá para mudar reinstalando.";
    }

    private void PrepararDesinstalacao()
    {
        Titulo.Text = "Desinstalar o KInfo";
        Subtitulo.Text = "Remove o app, a tarefa dos sensores e os atalhos.";
        BotaoInstalar.Content = "Desinstalar";

        BlocoDeOpcoes.Visibility = Visibility.Collapsed;
    }

    private async void Instalar_Click(object sender, RoutedEventArgs e) => await Rodar();

    private async Task Rodar()
    {
        PassoOpcoes.Visibility = Visibility.Collapsed;
        PassoAndamento.Visibility = Visibility.Visible;
        BotaoFecharJanela.IsEnabled = false;

        var passos = 0;
        var progresso = new Progress<string>(texto =>
        {
            Etapa.Text = texto;

            passos++;
            BarraDeProgresso.Width = Math.Min(passos / 8.0, 1) * 432;
        });

        try
        {
            if (_desinstalando) await Instalacao.Desinstalar(progresso);
            else _ressalvas = await Instalacao.Instalar(_pasta, Opcoes(), progresso, _atualizando);

            Concluir();
        }
        catch (Exception ex)
        {
            Falhar(ex);
        }
        finally
        {
            BotaoFecharJanela.IsEnabled = true;
        }
    }

    private OpcoesDeInstalacao Opcoes() => _anteriores ?? new(
        OpcaoAdministrador.IsChecked == true,
        OpcaoPawnIo.IsChecked == true,
        OpcaoAtalho.IsChecked == true,
        OpcaoLogon.IsChecked == true,
        OpcaoDefender.IsChecked == true);

    private void Concluir()
    {
        PassoAndamento.Visibility = Visibility.Collapsed;
        PassoFim.Visibility = Visibility.Visible;

        if (_desinstalando)
        {
            ResultadoTitulo.Text = "Desinstalado";
            ResultadoDetalhe.Text =
                "O app, a tarefa dos sensores e os atalhos foram removidos. " +
                "As configurações e o registro de consumo continuam em " +
                @"%APPDATA%\MonitorPC, caso você queira reinstalar depois.";

            BotaoAbrirApp.Visibility = Visibility.Collapsed;
            return;
        }

        if (_atualizando)
        {
            ConcluirAtualizacao();
            return;
        }

        ResultadoTitulo.Text = _ressalvas.Count == 0 ? "Instalado" : "Instalado, com ressalvas";

        var texto = _ressalvas.Count > 0 ? string.Join("\n\n", _ressalvas) + "\n\n" : "";

        ResultadoDetalhe.Text = texto + (OpcaoAdministrador.IsChecked == true
            ? "O painel abre agora e, daqui em diante, pelo ícone na bandeja do " +
              "Windows. O app sobe com acesso completo aos sensores, sem pedir " +
              "confirmação toda vez."
            : "O painel abre agora e, daqui em diante, pelo ícone na bandeja do " +
              "Windows. Sem o acesso elevado, as medidas do chip da placa-mãe — " +
              "temperatura de CPU, tensões e ventoinhas — vão aparecer vazias.");

        if (_ressalvas.Count > 0)
            ResultadoTitulo.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty, "AlertaTexto");
    }

    private void ConcluirAtualizacao()
    {
        if (_anteriores is { AcessoCompletoAosSensores: true } && !Instalacao.DriverPresente())
            _ressalvas.Add("O PawnIO, o driver dos sensores, não está instalado. Sem ele, " +
                           "temperatura de CPU e ventoinhas ficam vazias. Baixe em pawnio.eu " +
                           "ou rode este instalador de novo e marque a opção dele.");

        ResultadoTitulo.Text = _ressalvas.Count == 0
            ? $"Atualizado para a {Comum.Versao.Texto}"
            : "Atualizado, com ressalvas";

        var texto = _ressalvas.Count > 0 ? string.Join("\n\n", _ressalvas) + "\n\n" : "";
        ResultadoDetalhe.Text = texto +
            "O KInfo já foi reaberto, com as mesmas opções da instalação anterior. " +
            "Para mudar alguma delas, rode o instalador direto.";

        if (_ressalvas.Count > 0)
            ResultadoTitulo.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty, "AlertaTexto");

        BotaoAbrirApp.Visibility = Visibility.Collapsed;
        AbrirOApp();

        if (_ressalvas.Count > 0) return;

        var fechar = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        fechar.Tick += (_, _) =>
        {
            fechar.Stop();
            Close();
        };
        fechar.Start();
    }

    private void Falhar(Exception ex)
    {
        PassoAndamento.Visibility = Visibility.Collapsed;
        PassoFim.Visibility = Visibility.Visible;

        ResultadoTitulo.Text = _desinstalando ? "A desinstalação falhou"
                             : _atualizando ? "A atualização falhou"
                             : "A instalação falhou";
        ResultadoTitulo.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty, "AlertaTexto");
        ResultadoDetalhe.Text = ex.Message;

        BotaoAbrirApp.Visibility = Visibility.Collapsed;
    }

    private void AbrirApp_Click(object sender, RoutedEventArgs e)
    {
        AbrirOApp();
        Close();
    }

    private void AbrirOApp()
    {
        if (!Rodou("schtasks.exe", $"/Run /TN \"{Instalacao.NomeDaTarefa}\""))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(_pasta, "KInfo.exe"),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }
    }

    private static bool Rodou(string programa, string argumentos)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = programa,
                Arguments = argumentos,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (p is null) return false;

            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log(ex);
            return false;
        }
    }

    private static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Instalacao.Registro,
                $"[{DateTime.Now:HH:mm:ss}] abrir o app: {ex.Message}\n");
        }
        catch
        {
        }
    }

    private void FecharJanela_Click(object sender, RoutedEventArgs e) => Close();

    private void CaberNaTela()
    {
        var area = AreaDoMonitor();

        if (area.Height < 240) return;

        if (Math.Abs(MaxHeight - area.Height) > 0.5) MaxHeight = area.Height;

        if (ActualHeight > 0 && !double.IsNaN(Top))
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    private Rect AreaDoMonitor()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return SystemParameters.WorkArea;

            var monitor = MonitorFromWindow(handle, MonitorMaisProximo);
            var info = new InfoDoMonitor { Tamanho = System.Runtime.InteropServices.Marshal.SizeOf<InfoDoMonitor>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;

            var escala = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var x = escala.DpiScaleX <= 0 ? 1 : escala.DpiScaleX;
            var y = escala.DpiScaleY <= 0 ? 1 : escala.DpiScaleY;

            return new Rect(
                info.AreaUtil.Esquerda / x,
                info.AreaUtil.Topo / y,
                (info.AreaUtil.Direita - info.AreaUtil.Esquerda) / x,
                (info.AreaUtil.Baixo - info.AreaUtil.Topo) / y);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    private const int MonitorMaisProximo = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Retangulo
    {
        public int Esquerda;
        public int Topo;
        public int Direita;
        public int Baixo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct InfoDoMonitor
    {
        public int Tamanho;
        public Retangulo Area;
        public Retangulo AreaUtil;
        public uint Sinalizadores;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr janela, int padrao);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref InfoDoMonitor info);

    private void Painel_Arrastar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
