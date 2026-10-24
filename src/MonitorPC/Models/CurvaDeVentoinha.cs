namespace MonitorPC.Models;

/// <summary>
/// O que o app ja observou de uma ventoinha: dois pontos da curva rotacao x PWM
/// e a maior rotacao bruta vista. Guardado entre sessoes, porque a curva so se
/// completa quando o PC passa por cargas diferentes — algo que leva dias, nao
/// minutos.
/// </summary>
public sealed class CurvaDeVentoinha
{
    /// <summary>Ponto de menor PWM ja observado, acima do piso de confianca.</summary>
    public double PwmBaixo { get; set; }
    public double RpmBaixo { get; set; }

    /// <summary>Ponto de maior PWM ja observado.</summary>
    public double PwmAlto { get; set; }
    public double RpmAlto { get; set; }

    /// <summary>Maior rotacao ja lida, com ou sem PWM conhecido.</summary>
    public double RpmMax { get; set; }

    /// <summary>
    /// Rotacao medida com o PWM forcado a 100% pela tela de calibracao. Quando
    /// existe, nao ha o que projetar: e a rotacao maxima real daquele ventilador,
    /// medida nele mesmo.
    /// </summary>
    public double RpmCalibrado { get; set; }

    /// <summary>Quando a calibracao foi feita, para a tela dizer se envelheceu.</summary>
    public DateTime? CalibradoEm { get; set; }
}
