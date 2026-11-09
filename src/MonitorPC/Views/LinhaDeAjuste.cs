using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorPC.Services;

namespace MonitorPC.Views;

public sealed class LinhaDeAjuste : Border
{
    private readonly TextBlock _explicacao;
    private readonly TextBlock? _numero;
    private readonly TextBox? _campo;
    private readonly Action<double> _aoMudar;
    private readonly double _minimo;
    private readonly double _maximo;

    private double _valor;
    private bool _silencioso;

    public enum Modo
    {
        Contador,

        Numero
    }

    public LinhaDeAjuste(
        Modo modo,
        string titulo,
        string explicacao,
        double valor,
        double minimo,
        double maximo,
        Action<double> aoMudar,
        string sufixo = "")
    {
        _valor = valor;
        _minimo = minimo;
        _maximo = maximo;
        _aoMudar = aoMudar;

        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 10, 12, 10);
        Margin = new Thickness(0, 0, 0, 8);
        SetResourceReference(BackgroundProperty, "Cartao");
        SetResourceReference(BorderBrushProperty, "Borda");

        var grade = new Grid();
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var tituloTexto = new TextBlock
        {
            Text = titulo,
            FontSize = 13,
            FontWeight = FontWeights.Medium
        };
        tituloTexto.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        _explicacao = new TextBlock
        {
            Text = explicacao,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _explicacao.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        textos.Children.Add(tituloTexto);
        textos.Children.Add(_explicacao);
        Grid.SetColumn(textos, 0);
        grade.Children.Add(textos);

        var direita = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (modo == Modo.Contador)
        {
            direita.Children.Add(Botao("−", -1, $"Diminuir {titulo}"));

            _numero = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                MinWidth = 28,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            _numero.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            direita.Children.Add(_numero);

            direita.Children.Add(Botao("+", 1, $"Aumentar {titulo}"));
        }
        else
        {
            _campo = new TextBox
            {
                Width = 64,
                Height = 34,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Right,
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1)
            };
            _campo.SetResourceReference(Control.BackgroundProperty, "Chip");
            _campo.SetResourceReference(Control.BorderBrushProperty, "Trilho");
            _campo.SetResourceReference(Control.ForegroundProperty, "Texto");
            _campo.LostFocus += (_, _) => LerCampo();
            _campo.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) LerCampo();
            };
            direita.Children.Add(_campo);

            if (!string.IsNullOrEmpty(sufixo))
            {
                var unidade = new TextBlock
                {
                    Text = sufixo,
                    FontSize = 12,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                unidade.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
                direita.Children.Add(unidade);
            }
        }

        Grid.SetColumn(direita, 1);
        grade.Children.Add(direita);
        Child = grade;

        Mostrar();
    }

    public double Valor => _valor;

    public void Explicar(string texto) => _explicacao.Text = texto;

    public void Definir(double valor)
    {
        _silencioso = true;
        _valor = Math.Clamp(valor, _minimo, _maximo);
        Mostrar();
        _silencioso = false;
    }

    private Button Botao(string rotulo, int passo, string descricao)
    {
        var botao = new Button
        {
            Width = 34,
            Height = 34,
            Content = rotulo,
            FontSize = 16,
            FontWeight = FontWeights.Medium,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
            ToolTip = descricao
        };
        botao.SetResourceReference(Control.BackgroundProperty, "Chip");
        botao.SetResourceReference(Control.BorderBrushProperty, "Trilho");
        botao.SetResourceReference(Control.ForegroundProperty, "Texto");
        botao.Click += (_, _) => Mudar(_valor + passo);
        return botao;
    }

    private void LerCampo()
    {
        if (_campo is null) return;

        var texto = _campo.Text.Replace(',', '.');

        if (double.TryParse(texto, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            Mudar(v);
        else
            Mostrar();
    }

    private void Mudar(double novo)
    {
        _valor = Math.Clamp(novo, _minimo, _maximo);
        Mostrar();

        if (!_silencioso) _aoMudar(_valor);
    }

    private void Mostrar()
    {
        if (_numero is not null) _numero.Text = $"{_valor:0}";
        if (_campo is not null) _campo.Text = _valor % 1 == 0 ? $"{_valor:0}" : $"{_valor:0.0}";
    }
}
