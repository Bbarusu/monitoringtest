using System.Windows;
using System.Windows.Controls;
using MonitorPC.Models;

namespace MonitorPC.Views;

public sealed class BlocoDeSensores : StackPanel
{
    private readonly List<TextBlock> _valores = new();
    private readonly List<string> _nomes = new();
    private readonly string _componente;

    public BlocoDeSensores(GrupoDeSensores grupo)
    {
        _componente = grupo.Nome;
        Margin = new Thickness(0, 4, 0, 12);

        var titulo = new TextBlock
        {
            Text = grupo.Nome,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 6)
        };
        titulo.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        Children.Add(titulo);

        foreach (var leitura in grupo.Leituras)
        {
            _nomes.Add(leitura.Nome);

            var linha = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            linha.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            var nome = new TextBlock
            {
                Text = leitura.Nome,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = $"{leitura.Nome} · {leitura.Tipo}"
            };
            nome.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            Grid.SetColumn(nome, 0);
            linha.Children.Add(nome);

            var valor = new TextBlock
            {
                Text = Formatar(leitura),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Right
            };
            valor.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            Grid.SetColumn(valor, 1);
            linha.Children.Add(valor);

            _valores.Add(valor);
            Children.Add(linha);
        }
    }

    public bool Corresponde(GrupoDeSensores grupo) =>
        grupo.Nome == _componente &&
        grupo.Leituras.Count == _nomes.Count &&
        !grupo.Leituras.Where((l, i) => l.Nome != _nomes[i]).Any();

    public void Atualizar(GrupoDeSensores grupo)
    {
        for (var i = 0; i < grupo.Leituras.Count && i < _valores.Count; i++)
            _valores[i].Text = Formatar(grupo.Leituras[i]);
    }

    private static string Formatar(LeituraDeSensor l) => l.Unidade switch
    {
        "V" => $"{l.Valor:0.000} V",
        "°C" => $"{l.Valor:0.0} °C",
        "%" => $"{l.Valor:0.0} %",
        "W" => $"{l.Valor:0.0} W",
        "MHz" => $"{l.Valor:0} MHz",
        "rpm" => $"{l.Valor:0} rpm",
        "GB" => $"{l.Valor:0.00} GB",
        "MB" => $"{l.Valor:0} MB",
        "B/s" => l.Valor >= 1_048_576
            ? $"{l.Valor / 1_048_576:0.0} MB/s"
            : $"{l.Valor / 1024:0} KB/s",
        "" => $"{l.Valor:0.##}",
        _ => $"{l.Valor:0.##} {l.Unidade}"
    };
}
