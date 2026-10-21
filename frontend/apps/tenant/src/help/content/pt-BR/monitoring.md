# Monitore tarefas e execuções

## O que você vai fazer

Localizar tarefas por estado e consultar a atividade e o resumo de uma execução.

## Antes de começar

Entre no painel do tenant. A permissão de leitura é suficiente.

## Funções de acesso

Leitura. O monitoramento de tarefas e execuções é somente para consulta.

## Etapas

1. Abra [Monitoramento](#/monitoring) para pesquisar tarefas, filtrar por estado e percorrer os resultados. A tabela mostra o modelo e a versão, o estado ativo ou arquivado, execuções ativas, falhas recentes e o estado do agendamento.
2. Abra uma execução na lista de execuções recentes do [Painel](#/dashboard) para consultar estado, tentativas, códigos de resultado seguros, certeza e **Linha do tempo**. Os eventos mostram quando o estado da execução mudou.
3. Use **Atualizar** quando disponível. As telas consultam dados enquanto estão visíveis e pausam quando a aba fica oculta; o horário **Atualizado** identifica a última atualização. Se a atualização falhar, o aviso informa que os resultados estão desatualizados. Confira o horário antes de agir.
4. Use os códigos de resultado apenas como resumos. O painel não exibe erros brutos, SQL, configuração nem valores secretos.

O aplicativo pode exibir dados mantidos pelo adaptador configurado. Dados em modo de memória não comprovam que o monitoramento com SQL, a persistência, os bloqueios ou uma implantação real funcionam. Sem um banco SQL configurado, o monitoramento que depende do banco pode ficar indisponível, em vez de aparecer vazio. Consulte [Workers](#/help:workers) e [Auditoria](#/help:audit) para outras telas operacionais de consulta.

## Exemplo

Pesquise `resumo-semanal` e escolha **Ativos**. Uma linha sintética pode mostrar o modelo `sample-report · 1`, `Ativas 0` e `Falhas 24h 1`; consulte a execução antes de concluir por que ela falhou.

## Verifique o resultado

Você consulta o estado atual da tarefa, as tentativas registradas e a linha do tempo da execução, sabendo quando os dados estão desatualizados ou indisponíveis.

## Se não funcionar

Se os resultados estiverem desatualizados ou indisponíveis, confira **Atualizado**, atualize e peça a um administrador que verifique a fonte configurada se o aviso persistir. Dados indisponíveis não equivalem a um resultado vazio saudável.

## Guias relacionados

- [Entenda o painel](#/help:dashboard)
- [Consulte eventos de execução](#/help:execution-events)
- [Consulte a saúde dos workers](#/help:workers)
