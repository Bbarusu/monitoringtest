using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorPC.Models;

using Forma = System.Windows.Shapes.Shape;
using Desenho = System.Windows.Shapes.Path;

namespace MonitorPC.Views;

internal sealed class LinhaDeProcessoGpu : Border
{
    private readonly TextBlock _nome = new()
    {
        FontSize = 13,
        FontWeight = FontWeights.Medium,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private readonly TextBlock _detalhe = new()
    {
        FontSize = 11,
        Margin = new Thickness(0, 2, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private readonly TextBlock _valor = new()
    {
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 10, 0)
    };

    private readonly Button _fechar;

    public ProcessoGpu? Processo { get; private set; }

    public event Action<ProcessoGpu>? PediuParaFechar;

    public LinhaDeProcessoGpu()
    {
        Padding = new Thickness(0, 8, 0, 8);
        Background = Brushes.Transparent;

        _nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        _detalhe.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
        _valor.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        var marca = new Desenho
        {
            Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
            StrokeThickness = 1.8,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform
        };
        marca.SetResourceReference(Forma.StrokeProperty, "TextoFraco");

        _fechar = new Button
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Content = marca,
            Visibility = Visibility.Hidden
        };
        _fechar.SetResourceReference(StyleProperty, "BotaoIcone");
        _fechar.Click += (_, _) =>
        {
            if (Processo is { } p) PediuParaFechar?.Invoke(p);
        };

        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textos.Children.Add(_nome);
        textos.Children.Add(_detalhe);

        var grade = new Grid();
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        Grid.SetColumn(textos, 0);
        Grid.SetColumn(_valor, 1);
        Grid.SetColumn(_fechar, 2);

        grade.Children.Add(textos);
        grade.Children.Add(_valor);
        grade.Children.Add(_fechar);

        Child = grade;
    }

    public void Atualizar(ProcessoGpu p, bool protegido)
    {
        Processo = p;

        _nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        _nome.Text = p.Nome;
        _valor.Text = p.UsoPct >= 0.1 ? $"{p.UsoPct:0.0}%" : "—";

        var partes = new List<string> { $"PID {p.Pid}" };
        if (p.Motor is { } motor) partes.Add(motor);
        if (p.VramMb >= 1) partes.Add($"{p.VramMb:N0} MB de VRAM");
        if (protegido) partes.Add("sistema");

        _detalhe.Text = string.Join(" · ", partes);

        _fechar.Visibility = protegido ? Visibility.Hidden : Visibility.Visible;
        _fechar.ToolTip = $"Pedir para {p.Nome} fechar";
    }

    public void Vazia(string texto)
    {
        Processo = null;

        _nome.Text = texto;
        _nome.SetResourceReference(TextBlock.ForegroundProperty, "TextoMuitoFraco");
        _detalhe.Text = " ";
        _valor.Text = "";
        _fechar.Visibility = Visibility.Hidden;
        ToolTip = null;
    }
}
