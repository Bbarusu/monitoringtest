using System.Windows;

namespace MonitorPC.Setup;

public partial class App : Application
{
    /// <summary>
    /// Passado pelo atalho de desinstalacao e pelo registro do Windows. Sem
    /// argumento, o instalador instala.
    /// </summary>
    public const string ArgumentoDeDesinstalacao = "--desinstalar";

    public static bool Desinstalando =>
        Environment.GetCommandLineArgs()
            .Any(a => a.Equals(ArgumentoDeDesinstalacao, StringComparison.OrdinalIgnoreCase));
}
