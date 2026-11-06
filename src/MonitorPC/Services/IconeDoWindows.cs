using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MonitorPC.Services;

/// <summary>
/// O icone de um app como o menu Iniciar mostra, pelo id de app do Windows
/// (o mesmo que o controle de midia informa: "Chrome", "MSEdge",
/// "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify").
///
/// Pelo IShellItemImageFactory, que desenha o icone de qualquer item do
/// shell — inclusive apps da loja, que nao tem .exe para tirar icone. O
/// bitmap vem com transparencia; e copiado pixel a pixel (GetDIBits), porque
/// o caminho pronto do WPF (CreateBitmapSourceFromHBitmap) a perde e o icone
/// sai com fundo preto.
/// </summary>
public static class IconeDoWindows
{
    /// <returns>O icone em 64 px, congelado; nulo se o Windows nao conhece o app.</returns>
    /// <remarks>
    /// O shell prefere ser chamado de uma thread STA; quem chama daqui pode
    /// estar em qualquer thread, entao a leitura roda numa propria, com prazo.
    /// </remarks>
    public static ImageSource? DoApp(string idDoApp, int tamanho = 64)
    {
        if (string.IsNullOrWhiteSpace(idDoApp)) return null;

        return NumaThreadSta(() => Ler(@"shell:AppsFolder\" + idDoApp, tamanho, SoIcone | MaiorServe), TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// A miniatura de uma foto ou video, como o Explorer mostra (o mesmo
    /// cache do Windows). Nulo se o Windows nao sabe fazer miniatura do tipo
    /// (HEIC sem a extensao da loja, por exemplo) — quem chama fica com o icone.
    /// </summary>
    public static ImageSource? Miniatura(string caminho, int tamanho = 96)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !System.IO.File.Exists(caminho)) return null;

        // Video grande pode levar alguns segundos na primeira vez.
        return NumaThreadSta(() => Ler(caminho, tamanho, SoMiniatura | MaiorServe), TimeSpan.FromSeconds(8));
    }

    private static ImageSource? NumaThreadSta(Func<ImageSource?> ler, TimeSpan prazo)
    {
        ImageSource? resultado = null;
        var thread = new Thread(() => resultado = ler()) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(prazo) ? resultado : null;
    }

    private static ImageSource? Ler(string nome, int tamanho, int opcoes)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(nome, IntPtr.Zero, ref iid, out var fabrica) != 0 ||
                fabrica is null)
                return null;

            try
            {
                if (fabrica.GetImage(new Tamanho { Largura = tamanho, Altura = tamanho },
                                     opcoes, out bitmap) != 0 || bitmap == IntPtr.Zero)
                    return null;
            }
            finally
            {
                Marshal.ReleaseComObject(fabrica);
            }

            return Copiar(bitmap);
        }
        catch (Exception ex)
        {
            Log.Erro("Ícone do app", ex);
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }

    private static BitmapSource? Copiar(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<InfoDoBitmap>(), out var info) == 0) return null;

        var largura = info.Largura;
        var altura = Math.Abs(info.Altura);
        if (largura <= 0 || altura <= 0) return null;

        var cabecalho = new CabecalhoDib
        {
            Tamanho = 40, // sizeof(BITMAPINFOHEADER), sem o espaco de cores
            Largura = largura,
            Altura = -altura, // negativo: linhas de cima para baixo
            Planos = 1,
            Bits = 32
        };

        var pixels = new byte[largura * altura * 4];
        var tela = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(tela, bitmap, 0, (uint)altura, pixels, ref cabecalho, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, tela);
        }

        // Miniatura de foto costuma vir sem transparencia, com o canal alfa
        // todo zerado: lida como transparente, sumiria. Sem nenhum pixel com
        // alfa, a imagem e opaca.
        var temAlfa = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0) continue;
            temAlfa = true;
            break;
        }

        if (!temAlfa)
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

        var imagem = BitmapSource.Create(largura, altura, 96, 96, PixelFormats.Pbgra32, null, pixels, largura * 4);
        imagem.Freeze();
        return imagem;
    }

    private const int MaiorServe = 0x1; // SIIGBF_BIGGERSIZEOK
    private const int SoIcone = 0x4;    // SIIGBF_ICONONLY
    private const int SoMiniatura = 0x8; // SIIGBF_THUMBNAILONLY

    [StructLayout(LayoutKind.Sequential)]
    private struct Tamanho
    {
        public int Largura;
        public int Altura;
    }

    /// <summary>BITMAP: so os campos lidos aqui, na ordem do Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InfoDoBitmap
    {
        public int Tipo;
        public int Largura;
        public int Altura;
        public int BytesPorLinha;
        public ushort Planos;
        public ushort BitsPorPixel;
        public IntPtr Bits;
    }

    /// <summary>BITMAPINFOHEADER, seguido do espaco de cores (que 32 bits sem compressao nao usa).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CabecalhoDib
    {
        public int Tamanho;
        public int Largura;
        public int Altura;
        public ushort Planos;
        public ushort Bits;
        public uint Compressao;
        public uint TamanhoDaImagem;
        public int PixelsPorMetroX;
        public int PixelsPorMetroY;
        public uint CoresUsadas;
        public uint CoresImportantes;
        public uint Cores0;
        public uint Cores1;
        public uint Cores2;
        public uint Cores3;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Tamanho tamanho, int opcoes, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string caminho, IntPtr contexto, ref Guid iid,
                                                          [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr objeto, int tamanho, out InfoDoBitmap info);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint primeira, uint linhas, byte[] pixels,
                                        ref CabecalhoDib info, uint uso);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objeto);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr janela);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr janela, IntPtr dc);
}
