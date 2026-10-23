using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using MonitorPC.Services;

namespace MonitorPC.Views;

/// <summary>
/// Uma linha da grade de nucleos: rotulo, barra de carga e porcentagem.
/// Montada em codigo porque a quantidade vem do hardware, e so os valores
/// mudam a cada leitura — os elementos sao reaproveitados.
/// </summary>
public sealed class BarraDeNucleo : Grid
{
    private readonly TextBlock _rotulo;
    private readonly TextBlock _valor;
    private readonly ColumnDefinition _cheia;
    private readonly ColumnDefinition _vazia;

    public BarraDeNucleo()
    {
        Margin = new Thickness(0, 0, 0, 8);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        _rotulo = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeService.Pincel("TextoMuitoFraco")
        };
        SetColumn(_rotulo, 0);
        Children.Add(_rotulo);

        var trilho = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = ThemeService.Pincel("Trilho"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        SetColumn(trilho, 1);
        Children.Add(trilho);

        // Duas colunas proporcionais em vez de largura em pixels: acompanha o
        // painel e nunca produz um valor negativo.
        _cheia = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        _vazia = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };

        var interno = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        interno.ColumnDefinitions.Add(_cheia);
        interno.ColumnDefinitions.Add(_vazia);

        var preenchimento = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center
        };
        preenchimento.SetResourceReference(Border.BackgroundProperty, "Acento");
        SetColumn(preenchimento, 0);
        interno.Children.Add(preenchimento);

        SetColumn(interno, 1);
        Children.Add(interno);

        _valor = new TextBlock
        {
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeService.Pincel("Texto")
        };
        SetColumn(_valor, 2);
        Children.Add(_valor);
    }

    public void Atualizar(string rotulo, double porcentagem, float? clockMhz, string? origem = null)
    {
        var valor = Math.Clamp(porcentagem, 0, 100);

        _rotulo.Text = rotulo;
        _valor.Text = $"{valor:F0}%";
        _cheia.Width = new GridLength(valor, GridUnitType.Star);
        _vazia.Width = new GridLength(100 - valor, GridUnitType.Star);

        var partes = new List<string> { $"{rotulo} · {valor:F0}%" };
        if (clockMhz is > 0) partes.Add($"{clockMhz / 1000:0.00} GHz");
        if (origem is not null) partes.Add(origem);
        ToolTip = string.Join(" · ", partes);
    }
}
