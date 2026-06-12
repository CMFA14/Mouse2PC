# Mouse2PC

Um mouse e teclado controlando dois computadores Windows como se fossem um
único desktop estendido — incluindo múltiplos monitores em cada PC — com um
painel visual para configurar onde cada tela fica.

## Como funciona

- O mesmo `Mouse2PC.exe` roda nos dois PCs, em papéis diferentes:
  - **Controlador**: o PC onde o mouse/teclado físicos estão conectados.
    Captura o input com hooks de baixo nível; quando o cursor cruza a borda
    para uma tela do outro PC, o input passa a ser enviado pela rede.
  - **Controlado**: escuta na porta TCP (padrão **24801**), informa quantas
    telas tem e injeta o mouse/teclado recebidos via `SendInput`.
- O **painel "Configurar telas..."** mostra todas as telas dos dois PCs como
  retângulos arrastáveis. As bordas encostadas definem por onde o cursor
  atravessa (direita, esquerda, cima ou baixo).
- **Área de transferência compartilhada**: texto copiado (Ctrl+C) em um PC
  pode ser colado (Ctrl+V) no outro, nas duas direções.

## Uso

1. Baixe o **Mouse2PC-win-Setup.exe** do [Release mais recente](https://github.com/CMFA14/Mouse2PC/releases/latest)
   e instale nos dois PCs. Instalado assim, o app **se atualiza sozinho**
   quando sai versão nova. (Alternativa sem instalação: o pacote
   `-Portable.zip` do mesmo Release, mas sem auto-update.)
2. **No PC controlado**: abra o app, marque "Controlado", clique **Iniciar**.
   Anote o IP exibido. Libere a porta 24801 no Firewall do Windows
   (o Windows costuma perguntar na primeira execução — aceite em "Redes privadas").
3. **No PC controlador**: marque "Controlador", digite o IP do outro PC e
   clique **Iniciar**.
4. Após conectar, clique **Configurar telas...** e arraste os retângulos para
   refletir a posição física real dos monitores. Salve.
5. Mova o mouse até a borda configurada — ele atravessa para o outro PC, e o
   teclado passa a digitar lá também. Volte movendo o mouse de volta, ou use
   **Ctrl+Alt+Home** (hotkey de emergência).

## Notas

- Apenas rede local (LAN). O tráfego não é criptografado — não exponha a
  porta à internet.
- Se a conexão cair com o cursor "do outro lado", o controle volta
  automaticamente para o PC local.
- A tela de login/UAC do Windows não recebe input injetado (limitação do
  Windows para apps sem privilégio especial).
