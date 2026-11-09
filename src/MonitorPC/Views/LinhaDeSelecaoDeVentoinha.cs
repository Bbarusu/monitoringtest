using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MonitorPC.Views;

internal sealed class LinhaDeSelecaoDeVentoinha : Border
{
    private readonly CheckBox _marca;
    private readonly TextBlock _rotacao;

    public string Chave { get; }

    public string Nome { get; }

    public bool Marcada => _marca.IsChecked == true;

    public LinhaDeSelecaoDeVentoinha(string chave, string nome, bool marcada)
    {
        Chave = chave;
        Nome = nome;

        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(2, 7, 2, 7);

        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        _marca = new CheckBox
        {
            IsChecked = marcada,
            VerticalAlignment = VerticalAlignment.Center,

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
