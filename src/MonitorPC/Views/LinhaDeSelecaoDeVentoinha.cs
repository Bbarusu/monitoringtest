using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MonitorPC.Views;

/// <summary>
/// Uma ventoinha na lista de Ajustes: caixa de selecao, nome e a rotacao de
/// agora.
///
/// A rotacao ao vivo e o ponto da linha. "Fan #3" nao diz nada sobre qual
/// conector e aquele; o numero que sobe quando o usuario encosta a mao na
/// ventoinha diz. Sem isso, escolher quais mostrar seria chutar.
/// </summary>
internal sealed class LinhaDeSelecaoDeVentoinha : Border
{
    private readonly CheckBox _marca;
    private readonly TextBlock _rotacao;

    /// <summary>Identidade estavel da ventoinha, que vai para a configuracao.</summary>
    public string Chave { get; }

    /// <summary>Nome do sensor, que e como a leitura chega no snapshot.</summary>
    public string Nome { get; }

    public bool Marcada => _marca.IsChecked == true;

    public LinhaDeSelecaoDeVentoinha(string chave, string nome, bool marcada)
    {
        Chave = chave;
        Nome = nome;

        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(2, 7, 2, 7);

        // Sem um fundo, um Border nao recebe clique: a linha inteira precisa
        // ser alvo, e nao so os vinte pixels da caixa.
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        _marca = new CheckBox
        {
            IsChecked = marcada,
            VerticalAlignment = VerticalAlignment.Center,

            // Quem trata o clique e a linha. Deixar a caixa tambem tratar
            // alternaria duas vezes no mesmo clique.
            IsHitTestVisible = false
        };
        _marca.SetResourceReference(StyleProperty, "CaixaDeSelecao");

        var rotulo = new TextBlock
        {
            Text = nome,
            FontSize = 13,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        rotulo.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        _rotacao = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MinWidth = 64
        };
        _rotacao.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        var grade = new Grid();
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_marca, 0);
        Grid.SetColumn(rotulo, 1);
        Grid.SetColumn(_rotacao, 2);

        grade.Children.Add(_marca);
        grade.Children.Add(rotulo);
        grade.Children.Add(_rotacao);

        Child = grade;

        MouseLeftButtonUp += (_, _) => _marca.IsChecked = !Marcada;
        MouseEnter += (_, _) => SetResourceReference(BackgroundProperty, "Chip");
        MouseLeave += (_, _) => Background = Brushes.Transparent;
    }

    public void Atualizar(float? rpm) =>
        _rotacao.Text = rpm is > 0 ? $"{rpm:F0} rpm" : "parada";
}
