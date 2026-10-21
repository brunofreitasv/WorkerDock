# Entradas de tarefas e valores dinâmicos

## Pré-requisitos

É necessário acesso de gerenciamento e um modelo com entradas declaradas. A configuração dinâmica só está disponível para campos cujo esquema do modelo instalado a habilita explicitamente.

## Permissão

Usuários com acesso de leitura podem consultar este guia. Gerentes configuram entradas, visualizam valores dinâmicos e salvam a tarefa.

![Selecione um modelo SQL ou HTTP, declare uma entrada, escolha uma origem compatível, valide, visualize e salve o rascunho.](./media/input-choice.svg)

*Figura: Associe uma origem compatível a cada entrada declarada.* O esquema do modelo define as opções disponíveis; parâmetros SQL permanecem separados do texto da consulta.

## Etapas

1. Abra [Tarefas](#/jobs), selecione um modelo e confira os campos declarados em **Configuração**.
2. Para usar associações dinâmicas em SQL, selecione a versão **1.1.0**; a versão **1.0.0** mantém o comportamento de valores fixos. Adicione um parâmetro tipado e escolha exatamente uma origem: **Valor fixo**, **Valor dinâmico**, **Segredo** ou **Enviar SQL NULL**. No modo Consulta, associe cada marcador `@nome` a uma linha de parâmetro correspondente.
3. Para valores dinâmicos HTTP, selecione a versão **1.1.0**; a versão **1.0.0** mantém o comportamento de valores fixos. Declare um parâmetro e use `{nome}` nos campos compatíveis de URL, consulta ou corpo. Substituições na URL e na consulta são codificadas para cada parte. Use referências lógicas para credenciais.
4. Para entradas dinâmicas, escolha uma origem declarada, como **Data de referência**, **Data e hora agendadas**, **Início da primeira execução**, **ID da execução** ou **Chave de idempotência**; depois escolha um formato e fuso compatíveis.
5. Use a pré-visualização do editor para inspecionar valores de exemplo. Salve o rascunho desativado, revise-o e ative quando estiver pronto.

## Exemplo prático

Associe um parâmetro SQL `Date` chamado `Dt_Ref` a **Data de referência** no formato ISO. Em uma tarefa HTTP, o parâmetro declarado `referenceDate` pode preencher um valor de consulta por meio de `{referenceDate}`. Nos dois casos, os dados são entradas vinculadas; nenhum dado do usuário é inserido no texto SQL.

## Verificação

Confirme que cada associação aponta para um campo declarado e usa tipo compatível. A pré-visualização mostra apenas um exemplo: não salva um valor fixo, executa código do modelo, resolve segredos nem acessa um destino.

## Recuperação

Se houver erro de associação, confira o caminho do destino, o nome do parâmetro, o tipo, o formato e o fuso selecionado. Valores dinâmicos não podem apontar para segredos, campos sensíveis, campos não declarados ou objetos estruturais. Se a origem não estiver disponível para o gatilho, escolha uma origem compatível e mantenha a tarefa desativada enquanto corrige a configuração. A edição não atualiza automaticamente a versão fixada do modelo; use o fluxo de revisão para alterar a versão.

## Resultado

As entradas e associações dinâmicas são validadas pela versão selecionada do modelo. Em execuções agendadas, a data de referência usa a ocorrência original e o fuso do plano, inclusive em atrasos e novas tentativas.

## Guias relacionados

Consulte [tarefas do SQL Server](#/help:sql-jobs), [tarefas HTTP](#/help:http-jobs), [execuções manuais](#/help:manual-runs) e [agendamentos](#/help:schedules).
