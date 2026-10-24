using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class ArquivosWindow : Window
{
    private readonly int _pid;
    private readonly string _nome;

    public ArquivosWindow(int pid, string nome)
    {
        InitializeComponent();
        _pid = pid;
        _nome = nome;

        Titulo.Text = nome;
        Subtitulo.Text = $"PID {pid} · arquivos abertos agora";

        Carregar();
    }

    private async void Carregar()
    {
        Vazio.Text = "Consultando…";
        Vazio.Visibility = Visibility.Visible;
        Lista.Children.Clear();
        Contagem.Text = "";

        // A consulta varre a tabela de descritores do sistema inteiro: pesada
        // demais para a interface, leve o bastante para rodar sob demanda.
        var arquivos = await Task.Run(() => OpenFilesService.Listar(_pid));

        foreach (var caminho in arquivos)
            Lista.Children.Add(MontarLinha(caminho));

        Contagem.Text = arquivos.Count switch
        {
            0 => "",
            1 => "1 arquivo",
            _ => $"{arquivos.Count} arquivos"
        };

        Vazio.Visibility = arquivos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Vazio.Text = OpenFilesService.PodeInspecionar(_pid)
            ? "Nenhum arquivo aberto neste momento. Processos do sistema e alguns programas " +
              "mantêm os arquivos abertos só durante a operação."
            : "O Windows não permite inspecionar este processo. Abrir o app como administrador " +
              "costuma resolver, exceto em processos protegidos.";

        Reposicionar();
    }

    private UIElement MontarLinha(string caminho)
    {
        var pasta = Path.GetDirectoryName(caminho) ?? "";
        var arquivo = Path.GetFileName(caminho);

        var botao = new Button
        {
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 6, 0, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = caminho,
            Tag = caminho
        };

        var textos = new StackPanel();

        var nome = new TextBlock
        {
            Text = string.IsNullOrEmpty(arquivo) ? caminho : arquivo,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        var local = new TextBlock
        {
            Text = pasta,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        local.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        textos.Children.Add(nome);
        if (!string.IsNullOrEmpty(pasta)) textos.Children.Add(local);

        botao.Content = textos;
        botao.Template = (ControlTemplate)FindResource("LinhaClicavel");
        botao.Click += Abrir_Click;

        return botao;
    }

    private void Abrir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string caminho }) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{caminho}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Erro("Abrir pasta", ex);
        }
    }

    private void Atualizar_Click(object sender, RoutedEventArgs e) => Carregar();

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();

    private void Reposicionar()
    {
        Janelas.Ancorar(this, Owner);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Reposicionar();
    }
}
