using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace MonitorPC.Setup;

public partial class SetupWindow : Window
{
    private readonly bool _desinstalando = App.Desinstalando;
    private string _pasta = Instalacao.PastaPadrao;

    /// <summary>O que falhou sem impedir a instalação, para a tela final dizer.</summary>
    private List<string> _ressalvas = new();

    public SetupWindow()
    {
        InitializeComponent();

        // Uma instalacao anterior manda: reinstalar por cima da mesma pasta
        // evita dois Monitores concorrendo pelo mesmo driver.
        if (Instalacao.PastaInstalada() is { Length: > 0 } anterior) _pasta = anterior;

        CaminhoDeInstalacao.Text = _pasta;
        Administrador_Alterado(this, new RoutedEventArgs());

        if (_desinstalando) PrepararDesinstalacao();
    }

    /// <summary>
    /// O cartão de aviso explica o que a escolha de administrador significa, e
    /// muda junto com ela. Deixar um texto fixo falando de privilégio elevado
    /// com a caixa desmarcada seria contar outra história.
    /// </summary>
    private void Administrador_Alterado(object sender, RoutedEventArgs e)
    {
        if (AvisoDoDriver is null) return;

        AvisoDoDriver.Text = OpcaoAdministrador.IsChecked == true
            // O texto anterior dizia que "o driver vem dentro da biblioteca de
            // sensores". Era falso: a biblioteca não embute driver nenhum desde
            // que trocou a família WinRing0 pelo PawnIO, e foi essa frase errada
            // que sustentou a ideia de que bastava elevar.
            ? "Ler temperatura de CPU e placa-mãe exige um driver em modo kernel, que só " +
              "carrega com privilégio elevado. O instalador registra uma tarefa do Windows " +
              "para o app subir elevado sem pedir confirmação toda vez. O driver em si é o " +
              "PawnIO, instalado no sistema pela opção abaixo — privilégio sem ele não lê " +
              "nada, e ele sem privilégio também não."
            : "Sem privilégio elevado o driver de sensores não carrega. O app abre e " +
              "funciona, mas temperatura de CPU, tensões, rotação de ventoinhas e tudo que " +
              "vem do chip da placa-mãe ficam indisponíveis. GPU, discos, memória e uso de " +
              "CPU continuam normais. Dá para mudar reinstalando.";
    }

    private void PrepararDesinstalacao()
    {
        Titulo.Text = "Desinstalar o KInfo";
        Subtitulo.Text = "Remove o app, a tarefa dos sensores e os atalhos.";
        BotaoInstalar.Content = "Desinstalar";

        // As opções são de instalação: na saída não há o que escolher. O cartão
        // de aviso continua, porque explica o que a tarefa do Windows era.
        BlocoDeOpcoes.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------

    private async void Instalar_Click(object sender, RoutedEventArgs e)
    {
        PassoOpcoes.Visibility = Visibility.Collapsed;
        PassoAndamento.Visibility = Visibility.Visible;
        BotaoFecharJanela.IsEnabled = false;

        var passos = 0;
        var progresso = new Progress<string>(texto =>
        {
            Etapa.Text = texto;

            // A barra anda por etapa concluída, e não por tempo: uma barra que
            // estima tempo erra sempre, e aqui as etapas são poucas e nomeadas.
            passos++;
            BarraDeProgresso.Width = Math.Min(passos / 8.0, 1) * 432;
        });

        try
        {
            if (_desinstalando) await Instalacao.Desinstalar(progresso);
            else _ressalvas = await Instalacao.Instalar(_pasta, Opcoes(), progresso);

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

    private OpcoesDeInstalacao Opcoes() => new(
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

        ResultadoTitulo.Text = _ressalvas.Count == 0 ? "Instalado" : "Instalado, com ressalvas";

        // As ressalvas vêm primeiro: o que não deu certo importa mais que o
        // texto de boas-vindas, e escondê-las no fim repetiria o erro de
        // instalar em silêncio.
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

    private void Falhar(Exception ex)
    {
        PassoAndamento.Visibility = Visibility.Collapsed;
        PassoFim.Visibility = Visibility.Visible;

        ResultadoTitulo.Text = _desinstalando ? "A desinstalação falhou" : "A instalação falhou";
        // TextBlock.ForegroundProperty, e nao o ForegroundProperty herdado de
        // Control: sao propriedades diferentes, e a do Control nao pinta nada
        // num TextBlock.
        ResultadoTitulo.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty, "AlertaTexto");
        ResultadoDetalhe.Text = ex.Message;

        BotaoAbrirApp.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------

    private void AbrirApp_Click(object sender, RoutedEventArgs e)
    {
        // Pela tarefa, e não pelo executável: assim o app já sobe elevado, com
        // os sensores de CPU e placa-mãe disponíveis desde o primeiro segundo.
        //
        // O código de saída precisa ser conferido. O schtasks inicia sempre —
        // o que falha é a tarefa lá dentro —, então Process.Start devolver um
        // processo não prova nada, e era por isso que este botão não abria
        // nada quando a tarefa não existia.
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

        Close();
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
            // Diagnóstico, não funcionalidade.
        }
    }

    private void FecharJanela_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>A janela nao tem barra de título, então o painel inteiro arrasta.</summary>
    private void Painel_Arrastar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
