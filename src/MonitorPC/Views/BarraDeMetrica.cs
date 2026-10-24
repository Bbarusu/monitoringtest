using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using MonitorPC.Services;

namespace MonitorPC.Views;

/// <summary>
/// Uma barra rotulada com valor a direita, como as de VRAM, potencia e
/// ventilador na aba GPU. O preenchimento usa colunas proporcionais, entao
/// acompanha a largura do painel sem conta em pixels.
/// </summary>
public sealed class BarraDeMetrica : StackPanel
{
    private readonly TextBlock _rotulo;
    private readonly TextBlock _valor;
    private readonly ColumnDefinition _cheia;
    private readonly ColumnDefinition _vazia;

    public BarraDeMetrica()
    {
        Margin = new Thickness(0, 0, 0, 10);

        var topo = new Grid();

        // SetResourceReference, e nao ThemeService.Pincel: o pincel pego no
        // construtor congela o tema daquele instante, e estas barras sao criadas
        // na primeira vez que a aba aparece. Trocar para o tema claro depois
        // disso deixava o trilho preto e o rotulo quase invisivel.
        _rotulo = new TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _rotulo.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        _valor = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _valor.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        topo.Children.Add(_rotulo);
        topo.Children.Add(_valor);
        Children.Add(topo);

        var trilho = new Grid { Margin = new Thickness(0, 5, 0, 0) };

        var fundoDoTrilho = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
        fundoDoTrilho.SetResourceReference(Border.BackgroundProperty, "Trilho");
        trilho.Children.Add(fundoDoTrilho);

        _cheia = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        _vazia = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };

        var interno = new Grid();
        interno.ColumnDefinitions.Add(_cheia);
        interno.ColumnDefinitions.Add(_vazia);

        var preenchimento = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
        preenchimento.SetResourceReference(Border.BackgroundProperty, "Acento");
        Grid.SetColumn(preenchimento, 0);
        interno.Children.Add(preenchimento);

        trilho.Children.Add(interno);
        Children.Add(trilho);
    }

    public void Atualizar(string rotulo, string valor, double porcentagem, string? explicacao = null)
    {
        var p = Math.Clamp(porcentagem, 0, 100);

        _rotulo.Text = rotulo;
        _valor.Text = valor;
        _cheia.Width = new GridLength(p, GridUnitType.Star);
        _vazia.Width = new GridLength(100 - p, GridUnitType.Star);

        ToolTip = explicacao;
    }
}
