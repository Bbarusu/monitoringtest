using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using MonitorPC.Services;

namespace MonitorPC.Views;

public sealed class LinhaDeLegenda : Grid
{
    private readonly Border _cor;
    private readonly TextBlock _nome;
    private readonly TextBlock _valor;
    private readonly TextBlock _origem;

    public LinhaDeLegenda()
    {
        Margin = new Thickness(0, 0, 0, 8);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        _cor = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetColumn(_cor, 0);
        Children.Add(_cor);

        _nome = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(8, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        SetColumn(_nome, 1);
        Children.Add(_nome);

        _valor = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _valor.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        SetColumn(_valor, 2);
        Children.Add(_valor);

        _origem = new TextBlock
        {
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _origem.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
        SetColumn(_origem, 3);
        Children.Add(_origem);
    }

    public void Atualizar(string cor, string nome, string valor, string origem)
    {
        if (cor.StartsWith('#'))
            _cor.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cor)!);
        else
            _cor.SetResourceReference(Border.BackgroundProperty, cor);

        _nome.Text = nome;
        _valor.Text = valor;
        _origem.Text = origem;
    }
}
