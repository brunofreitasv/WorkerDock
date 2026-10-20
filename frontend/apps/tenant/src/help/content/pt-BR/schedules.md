## Pré-requisitos

Você precisa de acesso de gerenciamento e de um rascunho de tarefa. Abra [Tarefas](#/jobs) e ative **Usar um agendamento** na etapa **Agendamento**.

## Função

Leitores podem consultar este guia. Criar ou alterar um agendamento e visualizar suas ocorrências exige acesso de gerenciamento.

## Etapas

1. Escolha **Recorrente (cron)** para um agendamento recorrente ou **Uma vez** para uma data e hora local.
2. Para um agendamento recorrente, informe os cinco campos cron exibidos em **Expressão cron**. Os campos de dia do mês e dia da semana usam a semântica AND.
3. Escolha o **Fuso horário**. Para um agendamento de uma única vez, informe a **Data e hora local** e o **Deslocamento UTC** pretendido quando o horário local for ambíguo.
4. Escolha **Pré-visualizar próximas execuções** e confira os instantes UTC e deslocamentos retornados. O serviço de visualização resolve lacunas e repetições do horário de verão.
5. Em **Política**, escolha como tratar trabalho perdido: **Ignorar**, **Agrupar** ou **Recuperar**. Configure tentativas e sobreposição separadamente; a sobreposição pode enfileirar, ignorar ou permitir trabalho simultâneo, conforme a política da tarefa.
6. Salve o agendamento enquanto a tarefa ainda estiver desativada, revise-o e então ative a tarefa. A pausa interrompe futuras ocorrências no intervalo pausado; ao retomar, a contagem recomeça no horário da retomada, sem reproduzir esse intervalo.

## Resultado

O agendamento fica salvo com seu fuso horário e sua política de execução. Excluí-lo exige confirmação e mantém a tarefa disponível para execuções manuais. Execuções aceitas mantêm seu ciclo de vida mesmo após alterações ou remoção do agendamento. Consulte o [ciclo de vida da tarefa](#/help:jobs) e `docs/reference/scheduling.md` no repositório do código-fonte.
