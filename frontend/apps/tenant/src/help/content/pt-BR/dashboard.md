## O que você vai fazer

Usar o resumo do painel para decidir o que precisa de atenção e qual tela consultar em seguida.

## Antes de começar

Entre no tenant com acesso de Leitura.

## Funções de acesso

O acesso de Leitura permite consultar as métricas do painel. Não é necessário acesso de Gerenciar.

## Explore a tela

1. Abra o [painel](#/dashboard).
2. Consulte as quantidades de tarefas e execuções para entender a carga de trabalho atual.
3. Verifique a capacidade junto com o horário da atualização. O horário ajuda a avaliar a atualidade dos dados exibidos.
4. Consulte os próximos trabalhos e as execuções recentes para identificar itens que precisam de atenção.
5. Abra uma execução recente no Painel para consultar seus detalhes ou siga [Consulte eventos de execução](#/help:execution-events) para entender as tentativas e a linha do tempo.

## Exemplo

Suponha que os cartões mostrem `Em execução 2`, `Falhas 1` e `Workers prontos 1/2`, com uma execução recente. Abra-a para consultar a linha do tempo registrada e depois confira o heartbeat em Workers. Os valores são sintéticos.

## Verifique o resultado

Você pode consultar um resumo da atividade do tenant e decidir qual tarefa ou execução verificar. Os valores do painel descrevem os dados retornados pela conexão atual do tenant.

## Se não funcionar

Se um aviso informar que os dados estão indisponíveis ou desatualizados, trate os valores como provisórios. Atualize e verifique a fonte configurada ou a saúde do serviço. Resultados em modo de memória não verificam o monitoramento com SQL nem uma implantação real; dados indisponíveis não significam atividade zero.

## Guias relacionados

- [Monitore tarefas e execuções](#/help:monitoring)
- [Consulte eventos de execução](#/help:execution-events)
- [Consulte a saúde dos workers](#/help:workers)
