namespace MonitorPC.Models;

public sealed class CurvaDeVentoinha
{
    public double PwmBaixo { get; set; }
    public double RpmBaixo { get; set; }

    public double PwmAlto { get; set; }
    public double RpmAlto { get; set; }

    public double RpmMax { get; set; }

    public double RpmCalibrado { get; set; }

    public DateTime? CalibradoEm { get; set; }
}
