using System.Runtime.InteropServices;

namespace MonitorPC.Services;

/// <summary>
/// Volume geral do Windows, pelo Core Audio — a mesma interface que o controle
/// de volume da barra de tarefas usa.
///
/// A mudanca chega por aviso do proprio Windows, e nao por consulta em
/// intervalo: o servico fica parado ate alguem mexer no volume.
///
/// O aparelho de saida pode mudar (fone ligado, troca no painel de som), e o
/// aviso fica preso ao aparelho antigo. Por isso <see cref="Conferir"/> existe:
/// a ilha a chama de tempos em tempos, e trocar so acontece quando o padrao do
/// Windows mudou de fato.
/// </summary>
public sealed class VolumeService : IDisposable
{
    private IAudioEndpointVolume? _volume;
    private IAudioMeterInformation? _medidor;
    private RetornoDeVolume? _retorno;
    private string? _aparelho;
    private Guid _contexto = Guid.NewGuid();

    /// <summary>Disparado em thread qualquer, com o nivel de 0 a 100 e o mudo.</summary>
    public event Action<int, bool>? Mudou;

    public int Nivel { get; private set; }
    public bool Mudo { get; private set; }
    public bool Disponivel => _volume is not null;

    public VolumeService() => Conferir();

    /// <summary>Reabre o aparelho padrao, se ele mudou desde a ultima vez.</summary>
    public void Conferir()
    {
        try
        {
            var enumerador = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerador.GetDefaultAudioEndpoint(0 /* saida */, 1 /* multimidia */, out var aparelho) != 0)
            {
                Soltar();
                return;
            }

            aparelho.GetId(out var id);
            if (id == _aparelho && _volume is not null) return;

            Soltar();

            var iid = typeof(IAudioEndpointVolume).GUID;
            aparelho.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var objeto);

            _volume = (IAudioEndpointVolume)objeto;
            _aparelho = id;

            // O medidor de pico e opcional: sem ele a ilha so perde a prova de
            // que ha som, e volta a confiar no que o player declara.
            try
            {
                var iidMedidor = typeof(IAudioMeterInformation).GUID;
                aparelho.Activate(ref iidMedidor, 23, IntPtr.Zero, out var medidor);
                _medidor = (IAudioMeterInformation)medidor;
            }
            catch (Exception ex)
            {
                Log.Erro("Volume: medidor", ex);
                _medidor = null;
            }

            _retorno = new RetornoDeVolume(this);
            _volume.RegisterControlChangeNotify(_retorno);

            Ler();
        }
        catch (Exception ex)
        {
            // Sem aparelho de som, ou o servico de audio parado: a ilha so nao
            // mostra volume.
            Log.Erro("Volume: abrir aparelho", ex);
            Soltar();
        }
    }

    private void Ler()
    {
        if (_volume is null) return;

        _volume.GetMasterVolumeLevelScalar(out var escalar);
        _volume.GetMute(out var mudo);

        Nivel = (int)Math.Round(escalar * 100);
        Mudo = mudo;
    }

    /// <summary>
    /// Pico do que esta saindo pelo aparelho agora, de 0 a 1. Nulo sem medidor.
    /// Uma leitura e uma chamada de memoria compartilhada, sem custo de som.
    /// </summary>
    public float? Pico()
    {
        try
        {
            if (_medidor is null) return null;
            _medidor.GetPeakValue(out var pico);
            return pico;
        }
        catch
        {
            return null;
        }
    }

    public void Definir(int nivel)
    {
        try
        {
            _volume?.SetMasterVolumeLevelScalar(Math.Clamp(nivel, 0, 100) / 100f, ref _contexto);
        }
        catch (Exception ex)
        {
            Log.Erro("Volume: definir", ex);
        }
    }

    public void AlternarMudo()
    {
        try
        {
            _volume?.SetMute(!Mudo, ref _contexto);
        }
        catch (Exception ex)
        {
            Log.Erro("Volume: mudo", ex);
        }
    }

    /// <summary>
    /// O aviso traz quem mudou o volume. Mudanca feita pela propria ilha nao
    /// precisa virar aviso na ilha — o usuario esta olhando o controle.
    /// </summary>
    internal void Avisado(Guid quem, bool mudo, float escalar)
    {
        UltimaFoiMinha = quem == _contexto;
        Nivel = (int)Math.Round(escalar * 100);
        Mudo = mudo;
        Mudou?.Invoke(Nivel, Mudo);
    }

    /// <summary>Verdadeiro quando a ultima mudanca veio da propria ilha.</summary>
    public bool UltimaFoiMinha { get; private set; }

    private void Soltar()
    {
        try
        {
            if (_volume is not null && _retorno is not null)
                _volume.UnregisterControlChangeNotify(_retorno);
        }
        catch
        {
            // O aparelho pode ja ter sumido; nao ha o que desfazer.
        }

        _volume = null;
        _medidor = null;
        _retorno = null;
        _aparelho = null;
    }

    public void Dispose() => Soltar();
}

// ------------------------------------------------------------------

/// <summary>
/// Recebe o aviso do Windows. A estrutura que chega e
/// AUDIO_VOLUME_NOTIFICATION_DATA: o GUID de quem mudou (16 bytes), o mudo
/// (BOOL, 4 bytes) e o volume geral (float, 4 bytes).
/// </summary>
[ComVisible(true)]
internal sealed class RetornoDeVolume : IAudioEndpointVolumeCallback
{
    private readonly VolumeService _dono;

    public RetornoDeVolume(VolumeService dono) => _dono = dono;

    public int OnNotify(IntPtr dados)
    {
        try
        {
            var quem = Marshal.PtrToStructure<Guid>(dados);
            var mudo = Marshal.ReadInt32(dados, 16) != 0;
            var escalar = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(dados, 20));

            _dono.Avisado(quem, mudo, escalar);
        }
        catch
        {
            // Lancar daqui atravessaria a fronteira COM para dentro do
            // servico de audio do Windows.
        }

        return 0;
    }
}

// ------------------------------------------------------------------
// Core Audio. So os metodos usados tem assinatura completa; os demais
// existem para manter a ordem da tabela de metodos da interface.

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int fluxo, int estado, out IntPtr colecao);
    [PreserveSig] int GetDefaultAudioEndpoint(int fluxo, int papel, out IMMDevice aparelho);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, int contexto, IntPtr parametros,
                  [MarshalAs(UnmanagedType.IUnknown)] out object interfaceAtivada);
    void OpenPropertyStore(int acesso, out IntPtr propriedades);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int estado);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback retorno);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback retorno);
    void GetChannelCount(out uint canais);
    void SetMasterVolumeLevel(float decibeis, ref Guid contexto);
    void SetMasterVolumeLevelScalar(float nivel, ref Guid contexto);
    void GetMasterVolumeLevel(out float decibeis);
    void GetMasterVolumeLevelScalar(out float nivel);
    void SetChannelVolumeLevel(uint canal, float decibeis, ref Guid contexto);
    void SetChannelVolumeLevelScalar(uint canal, float nivel, ref Guid contexto);
    void GetChannelVolumeLevel(uint canal, out float decibeis);
    void GetChannelVolumeLevelScalar(uint canal, out float nivel);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mudo, ref Guid contexto);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mudo);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr dados);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    void GetPeakValue(out float pico);
}
