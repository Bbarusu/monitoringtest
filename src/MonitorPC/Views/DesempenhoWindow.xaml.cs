using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class DesempenhoWindow : Window
{
    private readonly List<(CheckBox Caixa, ProcessoFechavel Processo)> _escolhas = new();

    public DesempenhoWindow()
    {
        InitializeComponent();
        Janelas.Acompanhar(this);

        MontarProcessos();
        MedirTemporarios();
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A varredura dos temporarios percorre a pasta inteira, entao corre fora da
    /// interface para nao travar a janela ao abrir.
    /// </summary>
    private async void MedirTemporarios()
    {
        try
        {
            var (arquivos, mb) = await Task.Run(PerformanceService.MedirTemporarios);

            DetalheTemporarios.Text = arquivos == 0
                ? "Nada para limpar agora"
                : $"{arquivos:N0} arquivos · {mb:N0} MB, sem contar os que estão em uso";

            LimparTemp.IsEnabled = arquivos > 0;
            if (arquivos == 0) LimparTemp.IsChecked = false;
        }
        catch (Exception ex)
        {
            Log.Erro("Medir temporários", ex);
            DetalheTemporarios.Text = "Não foi possível calcular";
            LimparTemp.IsEnabled = false;
            LimparTemp.IsChecked = false;
        }
    }

    private void MontarProcessos()
    {
        var processos = PerformanceService.Fechaveis();

        foreach (var p in processos)
        {
            var linha = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var caixa = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Width = 18,
                Height = 18,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            caixa.Checked += Escolha_Alterada;
            caixa.Unchecked += Escolha_Alterada;
            Grid.SetColumn(caixa, 0);
            linha.Children.Add(caixa);

            var nome = new TextBlock
            {
                Text = p.Nome,
                FontSize = 13,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = p.Titulo,
                Foreground = ThemeService.Pincel("Texto")
            };
            Grid.SetColumn(nome, 1);
            linha.Children.Add(nome);

            var memoria = new TextBlock
            {
                Text = $"{p.MemoriaMb:N0} MB",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeService.Pincel("TextoFraco")
            };
            Grid.SetColumn(memoria, 2);
            linha.Children.Add(memoria);

            ListaProcessos.Children.Add(linha);
            _escolhas.Add((caixa, p));
        }

        if (processos.Count != 0) return;

        FecharProcessos.IsEnabled = false;
        DetalheProcessos.Text = "Nenhum programa aberto para fechar";
    }

    // ------------------------------------------------------------------

    private void FecharProcessos_Alterado(object sender, RoutedEventArgs e)
    {
        AreaProcessos.Visibility = FecharProcessos.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        Reposicionar();
        AtualizarTotal();
    }

    private void Escolha_Alterada(object sender, RoutedEventArgs e) => AtualizarTotal();

    private void AtualizarTotal()
    {
        var escolhidos = _escolhas.Where(x => x.Caixa.IsChecked == true).ToList();
        var memoria = escolhidos.Sum(x => x.Processo.MemoriaMb);

        TotalSelecionado.Text = escolhidos.Count == 0
            ? ""
            : $"{escolhidos.Count} selecionados · {memoria:N0} MB";

        DetalheProcessos.Text = FecharProcessos.IsChecked != true
            ? "Nenhum selecionado abaixo"
            : escolhidos.Count == 0
                ? "Escolha abaixo o que fechar"
                : $"{escolhidos.Count} programas · {memoria:N0} MB";
    }

    // ------------------------------------------------------------------

    private async void Ativar_Click(object sender, RoutedEventArgs e)
    {
        BotaoAtivar.IsEnabled = false;
        var feito = new List<string>();

        if (TrocarPlano.IsChecked == true)
            feito.Add(PowerService.Aplicar(PerfilEnergia.Desempenho)
                ? "plano trocado para Desempenho"
                : "não foi possível trocar o plano");

        if (LimparTemp.IsChecked == true)
        {
            var (arquivos, mb) = await Task.Run(PerformanceService.LimparTemporarios);
            feito.Add(arquivos == 0
                ? "nenhum temporário pôde ser apagado"
                : $"{arquivos:N0} temporários apagados ({mb:N0} MB)");
        }

        if (FecharProcessos.IsChecked == true)
        {
            var pids = _escolhas
                .Where(x => x.Caixa.IsChecked == true)
                .Select(x => x.Processo.Pid)
                .ToList();

            if (pids.Count > 0)
            {
                var (fechados, memoria) = PerformanceService.Fechar(pids);
                feito.Add(fechados == 0
                    ? "nenhum programa aceitou fechar"
                    : $"{fechados} programas fechados (~{memoria:N0} MB)");
            }
        }

        Resultado.Text = feito.Count == 0
            ? "Nada foi selecionado."
            : "Feito: " + string.Join("; ", feito) + ".";
        Resultado.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        BotaoAtivar.IsEnabled = true;
        Reposicionar();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>A janela e ancorada pela base, para crescer para cima.</summary>
    private void Reposicionar()
    {
        Janelas.Ancorar(this, Owner);
    }

}
