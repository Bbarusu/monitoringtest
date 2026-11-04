# Passagem de contexto — onde paramos (4 nov 2026)

Arquivo para quem continuar o trabalho no KInfo (Claude Code no PC do Kahuã).
Leia junto com o `CLAUDE.md` (regras do projeto, valem sempre), o `README.md`
(como cada parte funciona por dentro) e o `DESIGN.md` (o visual de cada tela).

## Versões

- Publicadas: até a **0.1.28** (tag `v0.1.28` em `Bbarusu/kinfo-releases`).
- Em andamento: **0.1.29** — o `Directory.Build.props` já está em `0.1.29`,
  ainda **não publicada**. As notas estão em `notas-da-versao.md` e acumulam
  (regra do `CLAUDE.md`): mixer de volume, textos revisados, player que sai em
  1 minuto, ícone do app no lugar da capa, guia obrigatório com o passo do
  Kdrop.
- Incerteza: não se sabe se a 0.1.28 publicada já levava o commit `452d54d`
  (página do celular parada com a escolha de fotos aberta). Se não levava, ele
  entra na 0.1.29 e merece uma linha nas notas — perguntar ao Kahuã.
- Quem publica é o Kahuã (`publicar.cmd` e `publicar-release.cmd`). Número só
  muda quando ele pedir.

## Kdrop — o que é e o que já existe

A bandeja da Kisland aberta para a rede de casa: celular e outros PCs com KInfo
mandam e pegam arquivos. Só rede local, HTTP comum, sem serviço de fora e sem
certificado. Detalhes no `README.md`, seção Kdrop.

- `Services/KdropService.cs`
  - `HttpListener` na porta **48620** (precisa do app elevado) e anúncio UDP na
    **48621** a cada 5 s, que também leva a lista da bandeja (`ItemKdrop`) e
    sai na hora em que a bandeja muda (`BandejaMudou`).
  - Rotas: `GET /` (página do celular), `GET /api/estado`,
    `GET /api/arquivo/{id}` (pasta vai como .zip montado na hora),
    `POST /api/pedido`, `GET /api/pedido/{id}`, `POST /api/enviar/{pedido}?nome=`.
  - Todo envio recebido pede aceite na Kisland (60 s), menos de aparelho
    confiável. O envio é conferido pela **quantidade** de arquivos do pedido,
    não pelo nome (o iPhone renomeia fotos).
  - `BuscarDePc`: um PC busca item da bandeja de outro, sem aceite; pasta chega
    aberta.
  - Regra de firewall "KInfo Kdrop" só para redes privadas e de domínio.
- `Dados/kdrop.html` (recurso embutido): a página do celular.
  - Pergunta o estado a cada 2 s e para de perguntar com a escolha de arquivos
    aberta.
  - Campo de arquivo escondido sem `display:none` (o Safari não abre com ele).
  - Campos usados ficam guardados em `#guardados` e nunca são limpos (limpar
    apagava a cópia temporária das fotos do iPhone).
  - Cada arquivo de até **100 MB** é lido para a memória na escolha
    (`LIMITE_NA_MEMORIA`); acima disso vai direto do `File`.
  - Fotos com nome genérico (`image.jpg`) são renomeadas
    "Foto AAAA-MM-DD HH.MM.SS (n).ext".
- `Views/IlhaWindow.xaml(.cs)`: bandeja, círculos dos aparelhos, QR Code
  (QRCoder), cartão do pedido de aceite, "Na bandeja de (PC)".
  - Arrastar do Explorer usa `WM_DROPFILES`; o registro OLE do WPF é revogado
    (`RevokeDragDrop`), senão nada chega com o app elevado.
  - Um arrasto vindo de fora abre a Kisland só com a bandeja (botão apertado
    longe dela + cursor de arrasto).
- Guia: passo do Kdrop em `GuiaKislandWindow`; `App.VersaoDoGuia = 2` faz o
  guia abrir para todos uma vez, sem o "Pular".

## Problema aberto: fotos da galeria do iPhone

- Sintoma (vídeo do Kahuã): escolhe 4 fotos na galeria, toca no ✓ e a tela de
  escolha do iPhone **não fecha**. Arquivos (não fotos) vão normalmente.
- Nada da página roda antes do ✓; a escolha é do iOS. Hipótese mais forte:
  fotos no iCloud ("Otimizar armazenamento") sendo baixadas e convertidas
  (HEIC → JPG) no ✓, sem progresso visível; o iPhone estava com 1% de bateria
  (Modo de Pouca Energia pausa downloads do iCloud).
- Já feito: página parada durante a escolha (commit `452d54d`).
- **Teste pendente** (depende da 0.1.29 publicada): uma foto tirada na hora
  versus fotos antigas, com o celular carregando.
  - Foto nova vai e antigas não → é o iCloud, não a página.
  - Nem foto nova vai → plano B: separar o botão em "Fotos e vídeos"
    (`accept="image/*,video/*"`) e "Arquivos" (sem `accept`), e testar.
- Cuidado com `accept`: o WebKit trata `image/*` como texto literal na
  conversão de formatos (bugs 303803 e 273444); não misturar `image/*` com
  `image/heic`.

## Próxima etapa sugerida

Proposta ao Kahuã, à espera da escolha dele (recomendação: a 1):

1. **Vídeos grandes do celular.** Acima de 100 MB a página manda direto do
   `File`, e esse caminho nunca foi testado com vídeo do iPhone. Ideia: envio
   em partes (por exemplo, 8 MB por requisição) com retomada se a conexão cair.
   Precisa de rota nova ou de parâmetro de posição em `/api/enviar`, e o
   `Receber` gravando por posição em vez de só anexar.
2. **Progresso no celular ao baixar** um item da bandeja do PC, e marcar o que
   já foi baixado.
3. **Soltar arquivos do Explorer direto no círculo de um aparelho**, sem passar
   pela bandeja (hoje só arrastando um item que já está nela).
4. **Canvas**: atualizar os quadros Kdrop2 a Kdrop4 com pastas em .zip, "Na
   bandeja de outro PC" e o passo do guia. Canvas "KInfo Ilha":
   https://claude.ai/artifact/WNaevR5XpYss9Vyd5A2RaN

## Lembretes de trabalho

- Respostas e textos do app em português, informal, como o Kahuã escreve.
  Textos para quem usa ficam curtos e diretos (ele revisou todos em
  4 nov 2026); para quem usa, a ilha é "Kisland", no código continua "Ilha".
- Toda tela nova segue o visual do KInfo (paleta do `ThemeService`, cartões de
  canto 12, Inter/Segoe, números em fonte leve, cor de destaque).
- No PC do Kahuã há .NET: o build pode ser conferido localmente antes de
  entregar, se ele concordar. A regra do `CLAUDE.md` contra compilar foi
  escrita para ambientes remotos sem .NET.
- Commits sem número de versão no título, e notas acumuladas em
  `notas-da-versao.md` a cada mudança que alguém vá perceber.
