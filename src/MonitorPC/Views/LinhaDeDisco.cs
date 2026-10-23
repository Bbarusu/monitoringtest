using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using MonitorPC.Services;

namespace MonitorPC.Views;

/// <summary>
/// Uma linha da lista da aba Disco: nome em cima, detalhe embaixo, valor a
/// direita e uma barra fina indicando a proporcao. Serve tanto para processos
/// quanto para unidades.
/// </summary>
public sealed class LinhaDeDisco : StackPanel
{
    private readonly TextBlock _nome;
    private readonly TextBlock _detalhe;
    private readonly TextBlock _valor;
    private readonly ColumnDefinition _cheia;
    private readonly ColumnDefinition _vazia;

    public LinhaDeDisco()
    {
        Margin = new Thickness(0, 0, 0, 12);

        var topo = new Grid();
        topo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textos = new StackPanel();
        _nome = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeService.Pincel("Texto")
        };
        _detalhe = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeService.Pincel("TextoFraco")
        };
        textos.Children.Add(_nome);
        textos.Children.Add(_detalhe);
        Grid.SetColumn(textos, 0);
        topo.Children.Add(textos);

        _valor = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = ThemeService.Pincel("Texto")
        };
        Grid.SetColumn(_valor, 1);
        topo.Children.Add(_valor);
        Children.Add(topo);

        var trilho = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        trilho.Children.Add(new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = ThemeService.Pincel("Trilho")
        });

        _cheia = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        _vazia = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };

        var interno = new Grid();
        interno.ColumnDefinitions.Add(_cheia);
        interno.ColumnDefinitions.Add(_vazia);

        var preenchimento = new Border { Height = 4, CornerRadius = new CornerRadius(2) };
        preenchimento.SetResourceReference(Border.BackgroundProperty, "Acento");
        Grid.SetColumn(preenchimento, 0);
        interno.Children.Add(preenchimento);

        trilho.Children.Add(interno);
        Children.Add(trilho);
    }

    /// <summary>
    /// Mantem a linha ocupando o mesmo espaco quando nao ha o que mostrar, para
    /// a lista nao mudar de altura a cada leitura.
    /// </summary>
    public void Vazia(string texto)
    {
        _nome.Text = texto;
        _nome.Foreground = ThemeService.Pincel("TextoMuitoFraco");
        _detalhe.Text = " ";
        _detalhe.Visibility = Visibility.Visible;
        _valor.Text = "";
        _cheia.Width = new GridLength(0, GridUnitType.Star);
        _vazia.Width = new GridLength(100, GridUnitType.Star);
        ToolTip = null;
    }

    public void Atualizar(string nome, string detalhe, string valor, double porcentagem)
    {
        _nome.Foreground = ThemeService.Pincel("Texto");

        var p = Math.Clamp(porcentagem, 0, 100);

        _nome.Text = nome;
        _detalhe.Text = detalhe;
        _detalhe.Visibility = string.IsNullOrEmpty(detalhe) ? Visibility.Collapsed : Visibility.Visible;
        _valor.Text = valor;
        _cheia.Width = new GridLength(p, GridUnitType.Star);
        _vazia.Width = new GridLength(100 - p, GridUnitType.Star);

        ToolTip = string.IsNullOrEmpty(detalhe) ? nome : $"{nome} · {detalhe}";
    }
}
