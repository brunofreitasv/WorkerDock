# Execuções manuais

## Pré-requisitos

A tarefa precisa estar ativada e válida. É necessário acesso de gerenciamento para solicitar uma execução; a tarefa deve ter uma revisão salva e capacidade disponível.

## Permissão

Usuários com acesso de leitura podem consultar o histórico de execuções. Gerentes podem iniciar ou interromper execuções.

## Etapas

1. Abra [Tarefas](#/jobs), selecione uma tarefa ativada e confira a configuração e o status atuais.
2. Escolha **Executar agora**. Se a tarefa tiver uma associação dinâmica de **Data de referência**, informe opcionalmente uma data de substituição no diálogo.
3. Confirme a solicitação e guarde o ID da execução. Se a resposta de rede for incerta, repita a mesma solicitação com a mesma chave.
4. Abra [Monitoramento](#/help:monitoring) ou os detalhes de execução da tarefa para acompanhar o trabalho. Use **Interromper execução** para solicitar o cancelamento cooperativo de uma execução ativa.

## Exemplo prático

Para um relatório diário ativado com associação a `ReferenceDate`, escolha **Executar agora** e informe `2026-10-19` para testar essa data uma vez. A substituição vale só para esta execução; não altera a tarefa nem o agendamento.

## Verificação

Use o ID para localizar a execução aceita e confira o status, as tentativas e os eventos. Uma solicitação aceita confirma a admissão, não a conclusão bem-sucedida nem um efeito externo.

## Recuperação

Se a admissão for rejeitada, confirme que a tarefa está ativada, que a revisão atual passa pela validação e que há capacidade. A substituição precisa ser uma data válida para o fuso configurado. Repetir a mesma chave e a mesma data retorna a solicitação aceita; outra data exige uma nova chave.

## Resultado

Execuções manuais entram no ciclo normal sem aguardar um agendamento. A origem dinâmica **Data e hora agendadas** não está disponível; a data de referência usa a substituição explícita ou a data de admissão da solicitação.

## Guias relacionados

Consulte [criação de tarefas](#/help:jobs), [entradas de tarefas](#/help:job-inputs), [políticas de execução](#/help:execution-policies) e [monitoramento](#/help:monitoring).
