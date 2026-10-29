## Pré-requisitos

Você precisa de acesso de gerenciamento, do modelo instalado **SQL Server** e de um perfil SQL Server ativo criado em [Configurações](#/settings). O perfil deve permitir o catálogo selecionado pela tarefa.

## Função

Leitores podem consultar este guia. Criar o perfil, executar diagnósticos e criar ou ativar uma tarefa SQL exige acesso de gerenciamento.

## Etapas

1. Abra [Tarefas](#/jobs), escolha o modelo SQL Server e avance para **Configuração**.
2. Selecione um perfil pelo nome do perfil ativo e pelo servidor. As opções de catálogo vêm de `allowedCatalogs` desse perfil; escolha um desses catálogos. Selecione **Consulta** ou **Procedimento** e informe o comando.
3. Adicione cada parâmetro SQL com seu nome e `SqlDbType`. Informe exatamente uma origem para cada parâmetro: valor literal, referência lógica de segredo, chave da execução ou valor nulo. Inclua tamanho, precisão ou escala quando o tipo exigir.
4. Escolha se deseja usar uma transação local e valide a configuração. O texto SQL é parametrizado; os valores não são inseridos no texto SQL.
5. Revise a tarefa e salve um rascunho desativado. Ative-a somente quando o perfil e a configuração estiverem prontos.
6. Se necessário, abra **Configurações → Perfis SQL** e execute o diagnóstico do perfil. Ele verifica a conectividade por uma operação fixa; não executa o comando SQL da tarefa.

## Exemplo prático

No modo **Consulta**, use `SELECT * FROM dbo.Orders WHERE ReferenceDate = @Dt_Ref;` e adicione um parâmetro correspondente `Dt_Ref` do tipo `Date`. Escolha **Valor dinâmico** e associe-o a **Data de referência** no formato ISO. O parâmetro é vinculado separadamente; nunca concatene um valor de usuário ao texto SQL.

## Verificação

Valide a configuração, salve um rascunho desativado e confirme que o perfil e o catálogo estão disponíveis. Depois da ativação, verifique o resultado de uma execução. Salvar pela interface ou obter um resultado no modo de memória não comprova que o destino SQL foi executado.

## Recuperação

Para erros de validação, confira nomes e tipos SQL dos parâmetros e escolha exatamente uma origem de valor por linha. Verifique o acesso ao catálogo e o diagnóstico do perfil quando houver dúvida de conectividade. O diagnóstico testa apenas a conectividade; não executa o comando da tarefa.

## Guias relacionados

Consulte [perfis SQL](#/help:sql-profiles), [entradas de tarefas](#/help:job-inputs), [criação de tarefas](#/help:jobs) e [execuções manuais](#/help:manual-runs).

## Resultado

A tarefa armazena a referência do perfil selecionado, o catálogo, o comando e os metadados de parâmetros tipados. A revisão do perfil é fixada quando uma execução é aceita. O modo de desenvolvimento em memória pode armazenar configurações de perfil, mas não executa SQL nem comprova conectividade com o destino; a aceitação do diagnóstico nesse modo não comprova uma conexão SQL bem-sucedida. Consulte [criação de tarefas](#/help:jobs) e a referência do SQL Server no repositório do código-fonte.
