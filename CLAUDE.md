# Instruções para quem trabalha neste repositório

## Versões: só quando o Kahuã pedir

- **Não suba o número da versão por conta própria.** O `<Version>` do
  `Directory.Build.props` só muda quando o Kahuã pedir uma versão nova. Uma
  versão para cada ideia não serve: as mudanças se acumulam na versão em
  andamento até ele pedir para fechar.
- **Notas acumulam.** Cada mudança que alguém vá perceber entra no
  `notas-da-versao.md`, somada às que já estão lá, e não no lugar delas. Quando
  a versão for pedida, as notas já estão completas.
- **Ao fechar uma versão (só quando pedido):** confira se o número ainda não foi
  publicado, se as notas cobrem tudo o que mudou desde a última publicada, e
  deixe pronto para ele rodar `publicar.cmd` e `publicar-release.cmd`. O
  `publicar-release.ps1` recusa uma tag que já existe; número repetido é sinal
  de que outra sessão já usou aquele número.
- Commits continuam normais, sem versão no título. Nunca publique release:
  quem publica é ele, na máquina dele.

## Outras regras do projeto

- **Sem certificados nem custos.** Nada de assinatura de código, MSIX, widget
  da Xbox Game Bar ou qualquer coisa que exija certificado ou serviço pago.
- **Visual:** toda tela nova segue o estilo e a personalização do painel do
  KInfo (paleta e chaves de `ThemeService`, Inter/Segoe e números em fonte
  leve, cartões de canto 12, cor de destaque, transparência e opacidade). Para
  quem usa, a ilha do topo se chama **Kisland**; no código ela continua
  "Ilha" (`IlhaWindow`, chaves `Ilha*` do config), para não perder
  configurações salvas.
- **Não compile no lugar dele achando que pode.** Os ambientes remotos
  costumam não ter o .NET nem acesso ao NuGet; o build é feito por ele no
  Windows. Revise o código com cuidado antes de mandar, porque um erro só
  aparece quando ele rodar o `publicar.cmd`.
