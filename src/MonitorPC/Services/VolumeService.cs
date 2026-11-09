using System.Runtime.InteropServices;

namespace MonitorPC.Services;

public sealed record SaidaDeSom(string Id, string Nome, string NomeCurto, bool Padrao);

public sealed class VolumeService : IDisposable
{
    private IAudioEndpointVolume? _volume;
    private IAudioMeterInformation? _medidor;
    private RetornoDeVolume? _retorno;
    private string? _aparelho;
    private Guid _contexto = Guid.NewGuid();
    private readonly int _fluxo;

    public event Action<int, bool>? Mudou;

    public int Nivel { get; private set; }
    public bool Mudo { get; private set; }
    public bool Disponivel => _volume is not null;

    public VolumeService(bool microfone = false)
    {
        _fluxo = microfone ? 1 : 0;
        Conferir();
    }

    public void Conferir()
    {
        try
        {
            var enumerador = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerador.GetDefaultAudioEndpoint(_fluxo, 1, out var aparelho) != 0)
            {
                Soltar();
                return;
            }

            aparelho.GetId(out var id);
            if (id == _aparelho && _volume is not null) return;

            Soltar();

            var iid = typeof(IAudioEndpointVolume).GUID;
            aparelho.Activate(ref iid, 23, IntPtr.Zero, out var objeto);

            _volume = (IAudioEndpointVolume)objeto;
            _aparelho = id;

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

    public IReadOnlyList<SaidaDeSom> Saidas()
    {
        var lista = new List<SaidaDeSom>();

        try
        {
            var enumerador = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            string? padrao = null;
            if (enumerador.GetDefaultAudioEndpoint(_fluxo, 1, out var atual) == 0)
                atual.GetId(out padrao);

            if (enumerador.EnumAudioEndpoints(_fluxo, 1, out var colecao) != 0)
                return lista;

            colecao.GetCount(out var quantos);
            var lidas = new List<(string Id, string Nome, string Curto)>();
            for (uint i = 0; i < quantos; i++)
            {
                colecao.Item(i, out var aparelho);
                aparelho.GetId(out var id);

                var nome = Ler(aparelho, NomeAmigavel) ?? (_fluxo == 1 ? "Microfone" : "Saída de som");
                var curto = Curto(Ler(aparelho, Descricao) ?? nome);
                lidas.Add((id, nome, curto));
            }

            foreach (var (id, nome, curto) in lidas)
            {
                var repetido = lidas.Count(l => string.Equals(l.Curto, curto, StringComparison.OrdinalIgnoreCase)) > 1;
                lista.Add(new SaidaDeSom(id, nome, repetido ? nome : curto, id == padrao));
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Volume: listar saídas", ex);
        }

        return lista;
    }

    private static string Curto(string nome)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(nome.Trim(), @"^\d+\s*-\s*", "");
        return t.Length == 0 ? nome : t;
    }

    private static string? Ler(IMMDevice aparelho, ChaveDePropriedade qual)
    {
        try
        {
            aparelho.OpenPropertyStore(0, out var propriedades);

            var chave = qual;
            if (propriedades.GetValue(ref chave, out var valor) != 0) return null;

            try
            {
                return valor.Tipo == TextoUnicode ? Marshal.PtrToStringUni(valor.Ponteiro) : null;
            }
            finally
            {
                PropVariantClear(ref valor);
            }
        }
        catch
        {
            return null;
        }
    }

    public bool UsarSaida(string id)
    {
        try
        {
            var politica = (IPolicyConfig)new PolicyConfigClient();

            var ok = true;
            for (var papel = 0; papel < 3; papel++)
                ok &= politica.SetDefaultEndpoint(id, papel) == 0;

            Conferir();
            return ok;
        }
        catch (Exception ex)
        {
            Log.Erro("Volume: trocar saída", ex);
            return false;
        }
    }

    private const ushort TextoUnicode = 31;

    private static readonly ChaveDePropriedade NomeAmigavel =
        new() { Formato = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
    private static readonly ChaveDePropriedade Descricao =
        new() { Formato = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 2 };

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant valor);

    internal void Avisado(Guid quem, bool mudo, float escalar)
    {
        UltimaFoiMinha = quem == _contexto;
        Nivel = (int)Math.Round(escalar * 100);
        Mudo = mudo;
        Mudou?.Invoke(Nivel, Mudo);
    }

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
        }

        _volume = null;
        _medidor = null;
        _retorno = null;
        _aparelho = null;
    }

    public void Dispose() => Soltar();
}

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
        }

        return 0;
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int fluxo, int estado, out IMMDeviceCollection colecao);
    [PreserveSig] int GetDefaultAudioEndpoint(int fluxo, int papel, out IMMDevice aparelho);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, int contexto, IntPtr parametros,
                  [MarshalAs(UnmanagedType.IUnknown)] out object interfaceAtivada);
    void OpenPropertyStore(int acesso, out IPropertyStore propriedades);
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

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    void GetCount(out uint quantos);
    void Item(uint indice, out IMMDevice aparelho);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ChaveDePropriedade
{
    public Guid Formato;
    public int Id;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Tipo;
    [FieldOffset(8)] public IntPtr Ponteiro;
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint quantos);
    [PreserveSig] int GetAt(uint indice, out ChaveDePropriedade chave);
    [PreserveSig] int GetValue(ref ChaveDePropriedade chave, out PropVariant valor);
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClient { }

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
    [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
    [PreserveSig] int ResetDeviceFormat(IntPtr a);
    [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
    [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
    [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int papel);
    [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
}
