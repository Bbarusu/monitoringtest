using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class NovidadesWindow : Window
{
    private const int Passos = 6;

    private readonly AppConfig _cfg;
    private readonly Action _aoMudar;
    private bool _carregando = true;
    private int _passo;

    public NovidadesWindow(AppConfig cfg, Action aoMudar, bool obrigatorio = false)
    {
        InitializeComponent();

        if (obrigatorio)
        {
            BotaoPular.IsEnabled = false;
            BotaoPular.ToolTip = "Conheça o Kdrop até o fim para fechar";
        }

        Janelas.CaberNaTela(this);
        _cfg = cfg;
        _aoMudar = aoMudar;

        ItemKdrop.IsChecked = _cfg.Kdrop && _cfg.IlhaBandeja && _cfg.IlhaAtiva;

        if (!AtualizacaoService.Elevado())
        {
            ItemKdrop.IsEnabled = false;
            DetalheKdrop.Text = "Precisa do KInfo instalado com acesso completo (rodando como administrador).";
        }

        var nome = string.IsNullOrWhiteSpace(_cfg.KdropNome) ? Environment.MachineName : _cfg.KdropNome!.Trim();
        var endereco = $"{NomeNaRede.NomePara(nome)}:{KdropService.Porta}";
        NomeDoPcPrevia.Text = nome;
        EnderecoPrevia.Text = endereco;
        EnderecoFixo.Text = endereco;

        MontarPontos();
        MostrarPasso(0);

        _carregando = false;
    }

    private void MontarPontos()
    {
        for (var i = 0; i < Passos; i++)
            Pontos.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) });
    }

    private void MostrarPasso(int passo)
    {
        _passo = Math.Clamp(passo, 0, Passos - 1);

        Passo1.Visibility = Visivel(_passo == 0);
        Passo2.Visibility = Visivel(_passo == 1);
        Passo3.Visibility = Visivel(_passo == 2);
        Passo4.Visibility = Visivel(_passo == 3);
        Passo5.Visibility = Visivel(_passo == 4);
        Passo6.Visibility = Visivel(_passo == 5);

        Contagem.Text = $"Novidades · Kdrop · {_passo + 1} de {Passos}";
        BotaoVoltar.Visibility = _passo == 0 ? Visibility.Hidden : Visibility.Visible;
        BotaoAvancar.Content = _passo == Passos - 1 ? "Pronto!" : "Próximo";

        for (var i = 0; i < Pontos.Children.Count; i++)
        {
            if (Pontos.Children[i] is not Border ponto) continue;

            ponto.Width = i == _passo ? 18 : 6;
            ponto.SetResourceReference(Border.BackgroundProperty, i == _passo ? "Acento" : "Trilho");
        }
    }

    private static Visibility Visivel(bool sim) => sim ? Visibility.Visible : Visibility.Collapsed;

    private void Voltar_Click(object sender, RoutedEventArgs e) => MostrarPasso(_passo - 1);

    private void Avancar_Click(object sender, RoutedEventArgs e)
    {
        if (_passo == Passos - 1)
        {
            Close();
            return;
        }

        MostrarPasso(_passo + 1);
    }

    private void Pular_Click(object sender, RoutedEventArgs e)
    {
        if (BotaoPular.IsEnabled) Close();
    }

    private void Painel_Arrastar(object sender, MouseButtonEventArgs e) => Janelas.ArrastarPeloFundo(this, e);

    private void Kdrop_Alterado(object sender, RoutedEventArgs e)
    {
        if (_carregando) return;

        var ligado = ItemKdrop.IsChecked == true;
        _cfg.Kdrop = ligado;
        if (ligado)
        {
            _cfg.IlhaAtiva = true;
            _cfg.IlhaBandeja = true;
        }

        _cfg.Salvar();
        _aoMudar();
    }
}
