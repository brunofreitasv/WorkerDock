# Consulte eventos de execução

## O que você vai fazer

Rastrear uma execução pelas tentativas e pela linha do tempo de estado usando os detalhes seguros mostrados pela interface do tenant.

![Ciclo de execução: aceita, enfileirada, em execução e estado final; tentativas e linha do tempo mostram evidências registradas.](./media/execution-lifecycle.svg)

*Figura: Acompanhe os estados registrados.* As tentativas descrevem o trabalho realizado; a linha do tempo lista mudanças de estado registradas pelo serviço.

## Antes de começar

O acesso de Leitura é suficiente. Comece por uma execução recente no [Painel](#/dashboard). A interface mostra resumos, não textos de erro brutos, corpos de solicitação, SQL, configuração ou segredos.

## Etapas

1. Abra uma execução recente no painel. A tela mostra a tarefa e o modelo, o tipo de acionamento, o horário de criação, o identificador de correlação, a certeza e o estado atual.
2. Consulte **Tentativas** em ordem. Cada item pode mostrar o estado, o código de resultado seguro, o horário de início e se efeitos podem ter ocorrido.
3. Leia a **Linha do tempo** em ordem cronológica para ver as mudanças de estado registradas. Compare os horários com os detalhes das tentativas; a ausência de um evento na linha do tempo não comprova que um sistema externo não realizou ações.
4. Se o resultado for incerto ou uma parada tiver sido solicitada, use o identificador de correlação ao pedir que um administrador rastreie a operação.

## Exemplo

![Detalhes da execução com uma indicação numerada junto à linha do tempo, mostrando eventos sintéticos de enfileirada, em execução e concluída.](./media/execution-timeline.png)

*Figura: Leia a linha do tempo junto às tentativas.* O exemplo usa dados sintéticos; um sucesso registrado não comprova um efeito externo.

Uma execução sintética pode mostrar `Queued → Running → Failed` (na fila, em execução, falhou), tentativa 1 com código seguro `Timeout` e certeza `Unknown` (desconhecida). Isso significa que a interface não confirma o resultado remoto; confira o sistema de destino antes de repetir um trabalho que possa duplicar efeitos.

## Verifique o resultado

Você consegue identificar as mudanças de estado registradas, a quantidade de tentativas, o código seguro de resultado e se efeitos podem ter ocorrido. Uma execução aceita ou uma resposta bem-sucedida da interface não comprova um commit SQL externo nem a entrega de um webhook.

## Se não funcionar

Se os detalhes estiverem indisponíveis ou desatualizados, confira **Atualizado**, atualize e tente novamente. Se continuarem indisponíveis, peça a um administrador que verifique a fonte de monitoramento. Eventos em modo de memória não comprovam histórico com SQL nem o comportamento de uma implantação real.

## Guias relacionados

- [Entenda o painel](#/help:dashboard)
- [Monitore tarefas e execuções](#/help:monitoring)
- [Consulte o histórico de auditoria](#/help:audit)
