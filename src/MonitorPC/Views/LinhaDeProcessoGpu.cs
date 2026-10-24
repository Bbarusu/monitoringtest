using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorPC.Models;

// System.Windows.Shapes nao entra como using: Path colidiria com System.IO.Path,
// que vem dos usings implicitos. Os dois tipos daqui ficam escritos por extenso.
using Forma = System.Windows.Shapes.Shape;
using Desenho = System.Windows.Shapes.Path;

namespace MonitorPC.Views;

/// <summary>
/// Uma linha da lista de GPU por programa: nome, de que jeito ele usa a placa, a
/// ocupacao e o botao de fechar.
///
/// Os elementos sao reaproveitados entre as leituras — so os textos mudam —
/// porque a lista se refaz a cada segundo e recriar o botao embaixo do cursor
/// faria o clique cair no vazio.
/// </summary>
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

    /// <summary>Quem a linha mostra agora. Nulo quando ela esta vazia.</summary>
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

        // A linha pode ter sido usada como vazia antes, e ali o nome fica
        // apagado de propósito.
        _nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        _nome.Text = p.Nome;
        _valor.Text = p.UsoPct >= 0.1 ? $"{p.UsoPct:0.0}%" : "—";

        var partes = new List<string> { $"PID {p.Pid}" };
        if (p.Motor is { } motor) partes.Add(motor);
        if (p.VramMb >= 1) partes.Add($"{p.VramMb:N0} MB de VRAM");
        if (protegido) partes.Add("sistema");

        _detalhe.Text = string.Join(" · ", partes);

        // Protegidos aparecem na lista, para a conta fechar, mas sem botao:
        // fechar um deles deixaria o PC inutilizavel ou desprotegido.
        _fechar.Visibility = protegido ? Visibility.Hidden : Visibility.Visible;
        _fechar.ToolTip = $"Pedir para {p.Nome} fechar";
    }

    /// <summary>
    /// Deixa a linha ocupando o mesmo espaco quando nao ha o que mostrar, para
    /// a lista nao mudar de altura a cada leitura.
    /// </summary>
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
