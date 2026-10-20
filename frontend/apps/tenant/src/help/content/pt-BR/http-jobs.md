## Pré-requisitos

Você precisa de acesso de gerenciamento, do modelo instalado **HTTP request** e de uma política de saída que permita o host e a porta de destino. Abra [Configurações](#/settings) para consultar a política de tarefas do tenant.

## Função

Leitores podem consultar este guia. Criar a tarefa, alterar a política de saída e ativar a tarefa exige acesso de gerenciamento.

## Etapas

1. Abra [Tarefas](#/jobs), escolha **Requisição HTTP** e avance para **Configuração**.
2. Escolha o método e a URL de destino. Configure valores de consulta, cabeçalhos comuns, parâmetros e um modo de resposta limitado conforme necessário.
3. Adicione credenciais somente como referências lógicas em **Cabeçalhos secretos**. Não coloque tokens na URL ou em valores de cabeçalhos comuns; o executor resolve as referências e os valores secretos não são exibidos no formulário.
4. Configure os status de sucesso e o tempo limite conforme necessário e valide. Os marcadores usam o mapa de parâmetros configurado; a chave da execução fica disponível como `{execution.idempotencyKey}` quando o destino oferece suporte a idempotência.
5. Peça a um gerente para conferir **Configurações → Política de saída** se o host, a porta ou o protocolo HTTP não estiver permitido. Destinos privados precisam de uma entrada explícita de CIDR privado; destinos bloqueados permanentemente continuam bloqueados.
6. Revise a tarefa, salve um rascunho desativado e ative-a quando a política de destino e a configuração estiverem prontas.

## Resultado

A tarefa contém a configuração da requisição e referências lógicas de segredo, sem valores secretos. O executor verifica a política de saída e não segue redirecionamentos. O resumo da resposta não inclui o texto retornado. Consulte [criação de tarefas](#/help:jobs) e a referência HTTP no repositório do código-fonte.
